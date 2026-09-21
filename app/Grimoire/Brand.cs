namespace Spellbook;

/// <summary>Everything edition-specific in one place. The public edition changes only this file.</summary>
internal static class Brand
{
    public const string Name = "Grimoire";
    public const string Tagline = "cast transforms on highlighted text, anywhere";
    public static readonly string Version = typeof(Brand).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
    public const string Exe = Name + ".exe";
    public const string SettingsFile = "grimoire-settings.ini";
    public const string LogFile = "grimoire.log";
    public const string Mutex = @"Local\Grimoire.SingleInstance";
    public const string EnvPrefix = "GRIMOIRE_";
    public const string RunKeyName = Name;
    public const string ReminderTaskPrefix = Name + "-Reminder-";
    public const string DataDirName = Name;            // %APPDATA%\<DataDirName> when not in repo mode

    /// <summary>Public source repository. Its releases page may be empty.</summary>
    public const string UpdateRepo = "lodestoner/grimoire";
    public const string Homepage = "https://github.com/" + UpdateRepo;

    /// <summary>Inno Setup AppId (must match installer/Grimoire.iss).</summary>
    public const string InstallerAppId = "{B6E0C3A2-7D3F-4C7E-9B7A-2F5A6C1D8E90}";

    /// <summary>Icon disc colour (ARGB). Grimoire: spellbook purple.</summary>
    public const int AccentArgb = unchecked((int)0xFF5C3EA0);
}
