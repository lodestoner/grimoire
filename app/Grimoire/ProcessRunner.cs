using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Spellbook;

/// <summary>Bounded, shell-free launch. A Windows job owns the process before its first instruction,
/// including descendants that outlive their parent or retain redirected pipe handles.</summary>
internal static class ProcessRunner
{
    public const int MaxOutputChars = 4 * 1024 * 1024;

    public static (string stdout, string stderr, int code) Run(ProcessStartInfo psi, string? input, int timeoutMs)
    {
        var caller = OperationContext.Token;
        using var deadline = OperationContext.Deadline(timeoutMs);
        // No caller/UI synchronization context may be captured by the synchronous facade.
        var token = deadline.Token;
        var task = Task.Run(() => RunAsync(psi, input, token));
        try { return task.WaitAsync(deadline.Token).GetAwaiter().GetResult(); }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            // Allow termination, pipe drain, and reaping to finish before temporary files are removed.
            // A stalled native startup cannot extend the public deadline indefinitely.
            Task.WhenAny(task, Task.Delay(5000)).GetAwaiter().GetResult();
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            caller.ThrowIfCancellationRequested();
            throw new TimeoutException("The command exceeded its time limit. Try a smaller input or check the provider.");
        }
    }

    static async Task<(string, string, int)> RunAsync(ProcessStartInfo psi, string? input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var child = new WindowsChild(psi);
        using var cancel = token.Register(child.Kill);
        token.ThrowIfCancellationRequested();
        // Pipes are synchronous Win32 handles; independent workers prevent a blocked stdin write
        // from blocking either output drain. Killing the entire job unblocks all three workers.
        var stdout = Task.Factory.StartNew(() => ReadBounded(child.Output, child), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var stderr = Task.Factory.StartNew(() => ReadBounded(child.Error, child), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var stdin = Task.Factory.StartNew(() =>
        {
            try { if (input != null) child.Input.Write(input); child.Input.Flush(); }
            catch (IOException error) when (IsClosedPipe(error)) { /* An exited child need not consume stdin. */ }
            finally { child.CloseInput(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var exited = Task.Factory.StartNew(child.Wait, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await Task.WhenAll(stdout, stderr, stdin, exited).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var code = child.ExitCode;
            if (code != 0)
                throw new InvalidOperationException($"Command exited with code {code}. " +
                    (string.IsNullOrWhiteSpace(stderr.Result) ? "Check provider setup and supported CLI options." : Util.Trunc(stderr.Result.Trim(), 300)));
            return (stdout.Result, stderr.Result, code);
        }
        finally { child.Kill(); } // Also close any descendants that detached from their parent's pipes.
    }

    static bool IsClosedPipe(IOException error) => (error.HResult & 0xffff) is 109 or 232 or 233;

    static string ReadBounded(StreamReader reader, WindowsChild child)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (result.Length + count > MaxOutputChars)
            {
                child.Kill();
                throw new InvalidOperationException("Command output exceeded the 4 Mi-character safety limit. No partial result was used.");
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    internal static string QuoteArgument(string value)
    {
        var b = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') b.Append('\\', slashes * 2 + 1).Append(c);
            else b.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return b.Append('\\', slashes * 2).Append('"').ToString();
    }

    sealed class WindowsChild : IDisposable
    {
        SafeFileHandle? _job, _process;
        public StreamWriter Input { get; private set; } = null!;
        public StreamReader Output { get; private set; } = null!;
        public StreamReader Error { get; private set; } = null!;
        public int ExitCode { get { Check(GetExitCodeProcess(_process!, out var code)); return unchecked((int)code); } }

        public WindowsChild(ProcessStartInfo psi)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The contained command runner requires Windows 10 or newer.");
            if (psi.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || psi.FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Resolve a vendor shim to its executable entry point before launching it.");
            if (psi.UseShellExecute || !psi.RedirectStandardInput || !psi.RedirectStandardOutput || !psi.RedirectStandardError)
                throw new ArgumentException("Commands require redirected input/output and shell execution disabled.");
            SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null, errRead = null, errWrite = null;
            IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero, environment = IntPtr.Zero;
            bool attributesReady = false;
            try
            {
                _job = CreateJobObjectW(IntPtr.Zero, null); Check(!_job.IsInvalid);
                var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE; no breakaway.
                Check(SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
                var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
                Check(CreatePipe(out inRead, out inWrite, ref sa, 0));
                Check(CreatePipe(out outRead, out outWrite, ref sa, 0));
                Check(CreatePipe(out errRead, out errWrite, ref sa, 0));
                Check(SetHandleInformation(inWrite, 1, 0)); Check(SetHandleInformation(outRead, 1, 0)); Check(SetHandleInformation(errRead, 1, 0));
                nuint size = 0;
                InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                attributes = Marshal.AllocHGlobal(checked((int)size));
                Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size)); attributesReady = true;
                handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(handles, 0, inRead.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, IntPtr.Size, outWrite.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errWrite.DangerousGetHandle());
                Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero));
                jobs = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobs, _job.DangerousGetHandle());
                Check(UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                var startup = new StartupInfoEx
                {
                    Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                        Input = inRead.DangerousGetHandle(), Output = outWrite.DangerousGetHandle(), Error = errWrite.DangerousGetHandle() },
                    Attributes = attributes,
                };
                var arguments = psi.ArgumentList.Count > 0 ? string.Join(" ", psi.ArgumentList.Select(QuoteArgument)) : psi.Arguments;
                var command = new StringBuilder(QuoteArgument(psi.FileName) + " " + arguments);
                var env = string.Join('\0', psi.Environment.Where(x => x.Value != null).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "=" + x.Value)) + "\0\0";
                environment = Marshal.StringToHGlobalUni(env);
                // Resolve only executable paths; no shell, associations, or cmd parsing for vendor prompts.
                var executable = Path.IsPathRooted(psi.FileName) ? psi.FileName : Providers.FindOnPath(psi.FileName)
                    ?? Path.Combine(Environment.SystemDirectory, psi.FileName);
                Check(CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x08000000 | 0x00080000 | 0x00000400, environment,
                    string.IsNullOrWhiteSpace(psi.WorkingDirectory) ? null : psi.WorkingDirectory, ref startup, out var info));
                _process = new SafeFileHandle(info.Process, true);
                using var thread = new SafeFileHandle(info.Thread, true);
                Input = new StreamWriter(new FileStream(inWrite, FileAccess.Write), psi.StandardInputEncoding ?? new UTF8Encoding(false)); inWrite = null;
                Output = new StreamReader(new FileStream(outRead, FileAccess.Read), psi.StandardOutputEncoding ?? Encoding.UTF8); outRead = null;
                Error = new StreamReader(new FileStream(errRead, FileAccess.Read), psi.StandardErrorEncoding ?? Encoding.UTF8); errRead = null;
            }
            catch { Dispose(); throw; }
            finally
            {
                inRead?.Dispose(); inWrite?.Dispose(); outRead?.Dispose(); outWrite?.Dispose(); errRead?.Dispose(); errWrite?.Dispose();
                if (attributesReady) DeleteProcThreadAttributeList(attributes);
                foreach (var ptr in new[] { attributes, handles, jobs, environment }) if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            }
        }
        public void Wait() { if (WaitForSingleObject(_process!, uint.MaxValue) == uint.MaxValue) throw new Win32Exception(); }
        public void Kill() { if (_job is { IsClosed: false, IsInvalid: false }) TerminateJobObject(_job, 1); }
        public void CloseInput()
        {
            // StreamWriter.Dispose flushes again: early child exit can break that flush too.
            try { Input?.Dispose(); } catch (IOException error) when (IsClosedPipe(error)) { }
        }
        public void Dispose()
        {
            Kill();
            if (_process is { IsClosed: false, IsInvalid: false }) WaitForSingleObject(_process, 5000);
            try { CloseInput(); }
            finally { Output?.Dispose(); Error?.Dispose(); _process?.Dispose(); _job?.Dispose(); }
        }
        static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits
    { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeFileHandle handle, out uint code);
}
