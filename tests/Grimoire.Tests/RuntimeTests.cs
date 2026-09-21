using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Spellbook;

namespace Spellbook.Tests;

internal static class RuntimeTests
{
    public static int Fixture(string[] args)
    {
        var mode = args[1];
        if (mode == "echo") { Console.Write(args[2]); return 0; }
        if (mode == "stdin") { Console.Write(Console.In.ReadToEnd()); return 0; }
        if (mode == "nonzero") { Console.Write("partial output"); Console.Error.Write("synthetic failure"); return 7; }
        if (mode == "output" || mode == "error")
        {
            var writer = mode == "output" ? Console.Out : Console.Error;
            for (var i = 0; i < 2048; i++) writer.Write(new string('x', 8192));
            return 0;
        }
        if (mode == "spawn" || mode == "orphan")
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var arg in new[] { "--fixture", "sleep", args[2] }) psi.ArgumentList.Add(arg);
            using var descendant = Process.Start(psi)!;
            // Wait until the descendant has opened its output handle and published its PID.
            var watch = Stopwatch.StartNew();
            while (!File.Exists(args[2]) && watch.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            if (mode == "orphan") return 0;
            Thread.Sleep(30_000); return 0;
        }
        if (args.Length > 2) File.WriteAllText(args[2], Environment.ProcessId.ToString());
        Console.Out.Flush();
        Thread.Sleep(30_000); // sleep and never-read-stdin both keep all inherited handles open.
        return 0;
    }

    static ProcessStartInfo Child(string mode, params string[] arguments)
    {
        var psi = Providers.Psi(Environment.ProcessPath!);
        psi.ArgumentList.Add("--fixture"); psi.ArgumentList.Add(mode);
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        return psi;
    }
    static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    static void Bounded(Action work, int limitMs = 10_000)
    {
        var timer = Stopwatch.StartNew(); work();
        Check(timer.ElapsedMilliseconds < limitMs, "Operation exceeded bounded test duration");
    }

    public static void OperationLifecycleCases()
    {
        var lifecycle = new OperationLifecycle();
        var first = lifecycle.TryBegin() ?? throw new InvalidOperationException("First operation was rejected");
        Check(lifecycle.TryBegin() == null, "Overlapping operation acquired active slot");
        lifecycle.CancelActive();
        Check(first.Token.IsCancellationRequested, "Cancel did not reach active operation");
        first.Dispose();
        using (var next = lifecycle.TryBegin())
            Check(next != null && !next.Token.IsCancellationRequested, "Cancellation leaked into next operation");
        try
        {
            using var failedSetup = lifecycle.TryBegin();
            throw new InvalidOperationException("synthetic progress setup failure");
        }
        catch (InvalidOperationException) { }
        var last = lifecycle.TryBegin() ?? throw new InvalidOperationException("Failed setup left operation slot occupied");
        var token = last.Token;
        var stopped = lifecycle.StopAsync();
        Check(token.IsCancellationRequested, "Quit did not cancel active work");
        Check(!stopped.IsCompleted, "Quit returned before operation cleanup");
        Check(lifecycle.TryBegin() == null, "Operation started while quitting");
        last.Dispose();
        Check(stopped.IsCompletedSuccessfully, "Quit did not complete after cleanup");
        Check(lifecycle.TryBegin() == null, "Operation started after quitting");
        Task.Run(() => Throws<InvalidOperationException>(() => lifecycle.TryBegin())).GetAwaiter().GetResult();
    }

    public static void ProcessCases()
    {
        var text = "synthetic & | > < %PATH% !quoted! \"word\" \\ ending\\";
        Check(Providers.RunProcess(Child("echo", text), null, 5000).stdout == text, "Executable arguments changed or passed through shell");
        Check(Providers.RunProcess(Child("stdin"), text, 5000).stdout == text, "stdin changed");
        Check(Providers.RunProcess(Child("echo", "early exit"), new string('x', 128 * 1024), 5000).stdout == "early exit", "Early stdin close replaced successful process result");
        Bounded(() => Throws<InvalidOperationException>(() => Providers.RunProcess(Child("nonzero"), new string('x', 128 * 1024), 5000)));
        Bounded(() => Throws<TimeoutException>(() => Providers.RunProcess(Child("never-read-stdin"), new string('x', 8 * 1024 * 1024), 1000)));
        foreach (var stream in new[] { "output", "error" })
            Bounded(() => Throws<InvalidOperationException>(() => Providers.RunProcess(Child(stream), null, 8000)), 12_000);
        using var cancel = new CancellationTokenSource(500);
        using (OperationContext.Push(cancel.Token))
            Bounded(() => Throws<OperationCanceledException>(() => Providers.RunProcess(Child("sleep"), null, 10_000)));
        Check(!OperationContext.Token.IsCancellationRequested, "Cancellation scope leaked");
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RejectPostContext());
            Check(Providers.RunProcess(Child("echo", "context"), null, 5000).stdout == "context", "UI context process failed");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    sealed class RejectPostContext : SynchronizationContext
    { public override void Post(SendOrPostCallback callback, object? state) => throw new InvalidOperationException("Runner captured UI synchronization context"); }

    public static void ProcessTrees()
    {
        foreach (var mode in new[] { "spawn", "orphan" })
        {
            var pidFile = Path.Combine(Paths.DataDir, mode + ".pid");
            Bounded(() => Throws<TimeoutException>(() => Providers.RunProcess(Child(mode, pidFile), null, 2000)));
            Check(File.Exists(pidFile), "Descendant fixture did not start");
            var pid = int.Parse(File.ReadAllText(pidFile));
            try
            {
                using var process = Process.GetProcessById(pid);
                Check(process.WaitForExit(3000), "Descendant survived cancellation or parent exit");
            }
            catch (ArgumentException) { /* Already reaped. */ }
        }
    }

    public static void CliSetup()
    {
        using var directory = new ProviderDirectory();
        var request = new AiRequest("system fixture", "prompt & | %PATH%", "text", null, "synthetic-model");
        var codex = CodexCliProvider.BuildStartInfo("synthetic.exe", request, directory.Path, Path.Combine(directory.Path, "result.txt"));
        foreach (var flag in new[] { "--sandbox", "read-only", "--ephemeral", "--ignore-user-config", "--skip-git-repo-check", "--output-last-message" }) Check(codex.ArgumentList.Contains(flag), "Missing Codex restriction " + flag);
        Check(codex.WorkingDirectory == directory.Path, "Codex inherited working directory");
        var claude = ClaudeCliProvider.BuildStartInfo("synthetic.exe", request, directory.Path);
        foreach (var flag in new[] { "--safe-mode", "--tools", "", "--no-session-persistence", "--strict-mcp-config" }) Check(claude.ArgumentList.Contains(flag), "Missing Claude restriction " + flag);
        var toolsIndex = claude.ArgumentList.IndexOf("--tools");
        Check(toolsIndex >= 0 && claude.ArgumentList[toolsIndex + 1] == "", "Claude no-tools value is not a distinct empty argument");
        Check(!claude.ArgumentList.Contains("--bare"), "Claude subscription authentication disabled");
        var image = Path.Combine(Paths.DataDir, "fixture.png"); File.WriteAllBytes(image, [1, 2, 3]);
        var vision = ClaudeCliProvider.BuildStartInfo("synthetic.exe", request with { ImagePath = image }, directory.Path);
        Check(vision.ArgumentList.Contains("Read") && vision.ArgumentList.Any(a => a.StartsWith("Read(")), "Vision read scope missing");
        Check(File.Exists(Path.Combine(directory.Path, "selected-image.png")), "Selected image was not isolated");
        Check(!vision.ArgumentList.Contains("Bash") && !vision.ArgumentList.Contains("Edit"), "Vision enabled write tools");

        var shim = Path.Combine(directory.Path, "codex.cmd"); File.WriteAllText(shim, "@echo unsafe-shim-must-not-run");
        var node = Path.Combine(directory.Path, "node.exe"); File.Copy(Environment.ProcessPath!, node);
        var entry = Path.Combine(directory.Path, "node_modules", "@openai", "codex", "bin", "codex.js");
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!); File.WriteAllText(entry, "// synthetic entry point");
        var resolved = Providers.Psi(shim);
        Check(resolved.FileName == node && resolved.ArgumentList[0] == entry, "npm shim did not resolve directly to Node entry point");
        Check(!resolved.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase), "npm shim invoked shell");
        Throws<InvalidOperationException>(() => Providers.Psi(Path.Combine(directory.Path, "unknown.cmd")));
        // Windows CI accepted a native .cmd launch; require our own explicit guard against shell shims.
        var direct = Providers.Psi(Environment.ProcessPath!); direct.FileName = shim;
        Throws<InvalidOperationException>(() => Providers.RunProcess(direct, null, 5000));
    }

    public static void TempAndDiagnostics()
    {
        string directory;
        try
        {
            using var operation = new ProviderDirectory(); directory = operation.Path;
            File.WriteAllText(Path.Combine(directory, "result.txt"), "synthetic content");
            throw new TestCleanupException(directory);
        }
        catch (TestCleanupException exception) { Check(!Directory.Exists(exception.Directory), "Provider temporary files survived failure"); }
        using (var first = new ProviderDirectory())
        using (var second = new ProviderDirectory()) Check(first.Path != second.Path, "Provider operations shared a directory");
        const string secret = "synthetic-sensitive-message";
        const string path = @"X:\SyntheticPrivate\fixture.txt";
        Log.Error(path, new InvalidOperationException(secret + path));
        var log = File.ReadAllText(Log.Path_);
        Check(!log.Contains(secret) && !log.Contains(path), "Diagnostic exception persisted payload or path");
        Check(log.Contains(nameof(InvalidOperationException)) && log.Contains("code=0x"), "Safe diagnostic category missing");
    }
    sealed class TestCleanupException(string directory) : Exception { public string Directory { get; } = directory; }

    public static void HttpCancellation()
    {
        // Loopback only; server deliberately never sends headers or closes its connection.
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/synthetic";
        foreach (var api in new[] { true, false })
        {
            using var stop = new CancellationTokenSource();
            var accepted = Task.Run(async () =>
            {
                using var client = await server.AcceptTcpClientAsync(stop.Token);
                await Task.Delay(10_000, stop.Token);
            });
            try
            {
                using var cancel = new CancellationTokenSource(300);
                using var scope = OperationContext.Push(cancel.Token);
                Bounded(() => Throws<OperationCanceledException>(() =>
                {
                    if (api) Providers.PostJson(url, new JsonObject { ["fixture"] = "synthetic" }, _ => { }, 5000);
                    else Spells.RunText(new Spell("HTTP fixture", "http", "", "show", "none", "quick", "", url, "POST", "", "{text}", true, false), "synthetic");
                }));
            }
            finally { stop.Cancel(); try { accepted.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
        }
    }

    public static void CommandSpell()
    {
        var spell = new Spell("Command fixture", "command", "", "show", "none", "quick", "echo synthetic", "", "POST", "", "", true, false);
        Check(Spells.RunText(spell, "input & does-not-become-a-command").Trim() == "synthetic", "Command spell invocation failed");
        Throws<InvalidOperationException>(() => Spells.RunText(spell with { Command = "echo partial & exit /b 7" }, "synthetic"));
    }
}
