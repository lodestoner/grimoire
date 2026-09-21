using System.Diagnostics;
using static Spellbook.Native;

namespace Spellbook;

/// <summary>Clipboard round-trips: copy the selection out, paste a result in, restore what was there.</summary>
internal static class Clip
{
    static readonly string[] KeepFormats =
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html,
        DataFormats.FileDrop, DataFormats.Bitmap, DataFormats.Dib,
    };

    static void Quiet() => ClipboardHistory.IgnoreUntil = DateTime.Now.AddMilliseconds(1500);

    public static DataObject? Snapshot()
    {
        Quiet();
        try
        {
            var src = Clipboard.GetDataObject();
            return src == null ? null : CopySnapshot(src);
        }
        catch { return null; }
    }

    internal static DataObject? CopySnapshot(IDataObject src)
    {
        var copy = new DataObject();
        bool any = false;
        foreach (var f in src.GetFormats(false))
        {
            if (!KeepFormats.Contains(f)) continue;
            try
            {
                var data = src.GetData(f, false);
                if (data != null) { copy.SetData(f, false, data); any = true; }
            }
            catch { }
        }
        any |= ClipboardPrivacy.CopyMarkers(src, copy);
        return any ? copy : null;
    }

    public static void Restore(DataObject? snap)
    {
        Quiet();
        try
        {
            if (snap == null) Clipboard.Clear();
            else Clipboard.SetDataObject(snap, true, 6, 40);
        }
        catch { }
    }

    public static void SetText(string text)
    {
        Quiet();
        try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true, 6, 40); }
        catch (Exception e) { Log.Error("SetText", e); }
    }

    static void SetTemporaryText(string text)
    {
        Quiet();
        try { Clipboard.SetDataObject(TemporaryText(text), true, 6, 40); }
        catch (Exception e) { Log.Error("SetTemporaryText", e); }
    }

    internal static DataObject TemporaryText(string text)
    {
        var data = new DataObject(DataFormats.UnicodeText, text);
        ClipboardPrivacy.AddExclusion(data);
        return data;
    }

    /// <summary>Copy the current selection (Ctrl+C) and hand it back, leaving the clipboard as it was.</summary>
    public static string GetSelection(int timeoutMs = 700)
    {
        var snap = Snapshot();
        // Raw Win32 from here on: no COM marshaling, so it works while this thread is not pumping messages.
        RawEmpty();
        uint seq0 = GetClipboardSequenceNumber();
        Log.Trace($"GetSelection: emptied, seq={seq0}, sending Ctrl+C");
        Input.CtrlC();
        var sw = Stopwatch.StartNew();
        string text = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(20);
            if (GetClipboardSequenceNumber() == seq0) continue;
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) continue;
            text = RawReadUnicode();
            if (text.Length > 0) break;
        }
        Log.Write($"GetSelection: {text.Length} chars in {sw.ElapsedMilliseconds}ms fg=0x{Native.GetForegroundWindow():X}");
        Restore(snap);
        return text;
    }

    /// <summary>Paste text over the current selection, then restore the previous clipboard.</summary>
    public static void PasteText(string text, IntPtr target = default)
    {
        if (target != IntPtr.Zero) Input.ForceForeground(target);
        var snap = Snapshot();
        SetTemporaryText(text);
        Input.CtrlV();
        Thread.Sleep(220);
        Log.Write($"PasteText: {text.Length} chars fg=0x{Native.GetForegroundWindow():X}");
        Restore(snap);
    }

    /// <summary>Paste the clipboard as plain text (formatting stripped).</summary>
    public static void PastePlain(IntPtr target = default)
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { text = ""; }
        if (text.Length == 0) return;
        if (target != IntPtr.Zero) Input.ForceForeground(target);
        var snap = Snapshot();
        SetTemporaryText(text);
        Input.CtrlV();
        Thread.Sleep(160);
        Restore(snap);
    }

    static bool RawOpen()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    static void RawEmpty()
    {
        if (!RawOpen()) return;
        try { EmptyClipboard(); } finally { CloseClipboard(); }
    }

    static string RawReadUnicode()
    {
        if (!RawOpen()) return "";
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return "";
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return "";
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(p) ?? ""; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static bool WaitForImage(int timeoutSec)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSec)
        {
            if (IsClipboardFormatAvailable(CF_BITMAP) || IsClipboardFormatAvailable(CF_DIB)) return true;
            Application.DoEvents();
            Thread.Sleep(250);
        }
        return false;
    }

    public static Image? GetImage()
    {
        try { return Clipboard.GetImage(); } catch { return null; }
    }
}
