namespace Spellbook;

/// <summary>Updates are manual; this candidate only opens the public releases page.</summary>
internal static class Updater
{
    public static Version Current => Version.Parse(Brand.Version.Split('-')[0]);
    public const string ReleasesUrl = Brand.Homepage + "/releases";

    public static bool IsInstalled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Brand.InstallerAppId + "_is1");
            return string.Equals(key?.GetValue("InstallLocation") as string,
                AppContext.BaseDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void ViewReleases() => Util.OpenUrl(ReleasesUrl);

    // Preserve the historical setting for compatibility. No network, download,
    // executable launch, update status claim or settings write occurs here.
    public static void AutoCheck(Settings settings) { }
}
