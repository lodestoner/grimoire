using System.Runtime.InteropServices;

namespace Spellbook;

/// <summary>Remembers recent text clipboards (in memory only) so any of them can be pasted back.</summary>
internal sealed class ClipboardHistory
{
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    readonly List<string> _items = new();
    readonly IntPtr _hwnd;
    public int Max = 30;
    public bool Enabled = true;

    /// <summary>Set by Clip while it is writing the clipboard itself, so its own round-trips are not recorded.</summary>
    public static DateTime IgnoreUntil;

    public ClipboardHistory(IntPtr hwnd) { _hwnd = hwnd; if (hwnd != IntPtr.Zero) AddClipboardFormatListener(hwnd); }

    public IReadOnlyList<string> Items => _items;

    /// <summary>Call from the listener window's WndProc on WM_CLIPBOARDUPDATE.</summary>
    public void OnUpdate()
    {
        if (!Enabled || DateTime.Now < IgnoreUntil) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data != null) Capture(data);
        }
        catch { }
    }

    internal void Capture(IDataObject data)
    {
        if (ClipboardPrivacy.IsExcluded(data)) return;
        if (!data.GetDataPresent(DataFormats.UnicodeText)) return;
        if (data.GetData(DataFormats.UnicodeText) is not string t) return;
        if (string.IsNullOrWhiteSpace(t) || t.Length > 200_000) return;
        _items.RemoveAll(x => x == t);
        _items.Insert(0, t);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
    }

    public void Clear() => _items.Clear();

    public static string Label(string s)
    {
        var one = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");
        return one.Length > 60 ? one[..60] + "…" : one;
    }

    public void Dispose() { if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd); }
}
