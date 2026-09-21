using System.Text;

namespace Spellbook;

/// <summary>Append-only diagnostics in the data folder (grimoire.log, capped at 1 MB).</summary>
internal static class Log
{
    static readonly object Gate = new();

    public static string Path_ => Path.Combine(Paths.DataDir, Brand.LogFile);

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                var p = Path_;
                if (File.Exists(p) && new FileInfo(p).Length > 1_000_000) File.WriteAllText(p, "");
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public static void Error(string where, Exception e)
    {
        // Categories are allowlisted: callers can otherwise pass spell names or private paths.
        var category = where switch
        {
            "Busy" or "action" or "pipe" or "provider-cleanup" or "file-batch" => where,
            _ => "operation",
        };
        var type = e switch
        {
            OperationCanceledException => nameof(OperationCanceledException), TimeoutException => nameof(TimeoutException),
            System.ComponentModel.Win32Exception => nameof(System.ComponentModel.Win32Exception), HttpRequestException => nameof(HttpRequestException),
            IOException => nameof(IOException), UnauthorizedAccessException => nameof(UnauthorizedAccessException),
            ArgumentException => nameof(ArgumentException), InvalidOperationException => nameof(InvalidOperationException), _ => nameof(Exception),
        };
        Write($"[x] {category}: {type} code=0x{e.HResult:X8}");
    }

    public static readonly bool TraceOn = Environment.GetEnvironmentVariable(Brand.EnvPrefix + "TRACE") == "1";

    public static void Trace(string msg) { if (TraceOn) Write("  . " + msg); }
}
