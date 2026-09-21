using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Spellbook;

/// <summary>A bounded current-user shutdown channel, isolated by full executable path.</summary>
internal sealed class QuitServer : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly Task _worker;

    static string PipeName(string path)
    {
        string identity = Environment.UserDomainName + "\\" + Environment.UserName + "|" + Path.GetFullPath(path).ToUpperInvariant();
        return "Grimoire.quit." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public QuitServer(string executablePath, Action quit)
    {
        _worker = ServeAsync(PipeName(executablePath), quit, _stop.Token);
    }

    static async Task ServeAsync(string name, Action quit, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var command = new byte[1];
                if (await pipe.ReadAsync(command, deadline.Token).ConfigureAwait(false) != 1 || command[0] != 1) continue;
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                quit();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(100).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) { break; }
        }
    }

    public static async Task<bool> RequestAsync(string executablePath, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(executablePath), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            var reply = new byte[1];
            return await pipe.ReadAsync(reply, deadline.Token).ConfigureAwait(false) == 1 && reply[0] == 1;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
        { return false; }
    }

    internal static bool IsAvailable(string path)
    {
        if (!File.Exists(path)) return true;
        // --quit itself runs from the same executable. Ignore only this command
        // process; all other matching paths must have exited before success.
        if (string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { return false; }
                }
            }
            return true;
        }
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Never launches the target; legacy instances simply time out and require manual exit.</summary>
    public static int QuitAndWait(string executablePath)
    {
        try
        {
            string path = Path.GetFullPath(executablePath);
            if (IsAvailable(path)) return 0;
            RequestAsync(path, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (IsAvailable(path)) return 0;
                Thread.Sleep(100);
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
        return 1;
    }

    public void Dispose() => _stop.Cancel();
}
