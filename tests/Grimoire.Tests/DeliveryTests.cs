using System.Diagnostics;
using System.IO.Pipes;
using Spellbook;

namespace Spellbook.Tests;

internal static class DeliveryTests
{
    public static void QuitProtocol() => QuitProtocolAsync().GetAwaiter().GetResult();

    static async Task QuitProtocolAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Grimoire.exe");
        int requests = 0;
        using var server = new QuitServer(path, () => Interlocked.Increment(ref requests));
        var watch = Stopwatch.StartNew();
        if (await QuitServer.RequestAsync(path + ".other", TimeSpan.FromMilliseconds(150)))
            throw new Exception("A different executable path accepted a quit request.");
        if (requests != 0 || watch.Elapsed > TimeSpan.FromSeconds(3))
            throw new Exception("Wrong-path quit was not bounded and isolated.");
        if (!await QuitServer.RequestAsync(path, TimeSpan.FromSeconds(2)))
            throw new Exception("Own-path quit was not acknowledged.");
        for (int i = 0; i < 50 && requests == 0; i++) await Task.Delay(10);
        if (requests != 1) throw new Exception("Quit callback did not run exactly once.");
    }

    public static void ManualUpdates()
    {
        var settings = new Settings { UpdateCheck = true, LastUpdateCheck = "synthetic-marker" };
        Updater.AutoCheck(settings);
        if (settings.LastUpdateCheck != "synthetic-marker") throw new Exception("Source candidate changed update-check state.");
        if (Updater.ReleasesUrl != Brand.Homepage + "/releases")
            throw new Exception("Release action did not point at the public releases page.");
        if (Updater.Current != new Version(2, 2, 0)) throw new Exception("RC numeric version failed to parse.");
    }
}
