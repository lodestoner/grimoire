using System.Text.RegularExpressions;

namespace Spellbook;

/// <summary>Watches Downloads and offers to file each finished download into the notes folder.</summary>
internal sealed class DownloadRouter : IDisposable
{
    readonly Settings _s;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    readonly Dictionary<string, long> _seen = new(StringComparer.OrdinalIgnoreCase);
    bool _busy;

    static readonly Regex Partial = new(@"\.(crdownload|tmp|partial|part)$", RegexOptions.IgnoreCase);

    public DownloadRouter(Settings s)
    {
        _s = s;
        _timer.Tick += (_, _) => Poll();
    }

    public void Refresh()
    {
        _seen.Clear();
        if (Directory.Exists(_s.DownloadsDir))
            foreach (var f in Directory.GetFiles(_s.DownloadsDir)) _seen[f] = -1;   // existing files never trigger
        _timer.Enabled = _s.RouterEnabled;
    }

    void Poll()
    {
        if (!_s.RouterEnabled || _busy || !Directory.Exists(_s.DownloadsDir)) return;
        foreach (var f in Directory.GetFiles(_s.DownloadsDir))
        {
            if (Partial.IsMatch(f)) continue;
            long size;
            try { size = new FileInfo(f).Length; } catch { continue; }
            if (!_seen.TryGetValue(f, out var prev)) { _seen[f] = size; continue; }   // first sighting -> pending
            if (prev == -1) continue;
            if (prev == size)                                                        // size stable -> complete
            {
                _seen[f] = -1;
                _busy = true;
                try { Pick(f); } finally { _busy = false; }
                return;
            }
            _seen[f] = size;
        }
    }

    public List<string> Folders()
    {
        var list = new List<string>();
        var root = _s.NextcloudRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return list;
        foreach (var area in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var an = Path.GetFileName(area);
            if (an.StartsWith('.')) continue;
            list.Add(an);
            foreach (var sub in Directory.GetDirectories(area).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var sn = Path.GetFileName(sub);
                if (!sn.StartsWith('.')) list.Add(an + "\\" + sn);
            }
        }
        return list;
    }

    public void Pick(string file)
    {
        var folders = Folders();
        if (folders.Count == 0) { Toast.Show("Set the notes folder in Settings first", 4000); return; }
        var rel = RoutePickerForm.Show(Path.GetFileName(file), folders);
        if (rel == null) return;
        try
        {
            var destDir = Path.Combine(_s.NextcloudRoot, rel);
            Directory.CreateDirectory(destDir);
            var name = Path.GetFileNameWithoutExtension(file);
            var ext = Path.GetExtension(file);
            var dest = Path.Combine(destDir, name + ext);
            int i = 1;
            while (File.Exists(dest)) dest = Path.Combine(destDir, $"{name} ({i++}){ext}");
            File.Move(file, dest);
            Toast.Show("Filed to " + rel, 3500);
        }
        catch (Exception e) { Toast.Show("Move failed: " + Util.Trunc(e.Message, 100), 5000); }
    }

    public void RouteNewest()
    {
        if (!Directory.Exists(_s.DownloadsDir)) { Toast.Show("No Downloads folder", 3000); return; }
        var newest = Directory.GetFiles(_s.DownloadsDir).Where(f => !Partial.IsMatch(f))
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (newest == null) { Toast.Show("No downloads found", 3000); return; }
        _busy = true;
        try { Pick(newest.FullName); } finally { _busy = false; }
    }

    public void Dispose() => _timer.Dispose();
}
