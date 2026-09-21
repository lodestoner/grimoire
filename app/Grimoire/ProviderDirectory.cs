namespace Spellbook;

/// <summary>Ephemeral provider working files, removed on success, failure, and cancellation.</summary>
internal sealed class ProviderDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Brand.Name, "operations", Guid.NewGuid().ToString("N"));
    public ProviderDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (Exception error) { Log.Error("provider-cleanup", error); }
    }
}
