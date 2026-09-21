using System.Windows.Forms;
using Spellbook;

namespace Spellbook.Tests;

internal static class ClipboardTests
{
    internal const string Exclude = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string History = "CanIncludeInClipboardHistory";
    internal const string Cloud = "CanUploadToCloudClipboard";

    public static void SnapshotPreservesPrivacy(string format, byte[] payload)
    {
        var source = new DataObject();
        source.SetData(DataFormats.UnicodeText, false, "synthetic protected text");
        source.SetData(format, false, new MemoryStream(payload));

        var snapshot = Clip.CopySnapshot(source);
        Check(snapshot != null, $"snapshot missing for {format}");
        Check(((IDataObject)snapshot!).GetData(DataFormats.UnicodeText, false) is string text && text == "synthetic protected text", $"text missing for {format}");
        Check(snapshot.GetDataPresent(format, false), $"privacy marker {format} was dropped");
        Check(ReadBytes(((IDataObject)snapshot).GetData(format, false)).SequenceEqual(payload), $"privacy marker {format} changed");
    }

    public static void TemporaryTextIsPrivate()
    {
        var data = Clip.TemporaryText("synthetic temporary paste");
        Check(((IDataObject)data).GetData(DataFormats.UnicodeText, false) is string text && text == "synthetic temporary paste", "temporary text changed");
        Check(data.GetDataPresent(Exclude, false), "temporary text can enter Windows history or cloud sync");
        Check(data.GetDataPresent(History, false) && ReadBytes(((IDataObject)data).GetData(History, false)).SequenceEqual(new byte[4]), "temporary text lacks history opt-out DWORD");
        Check(data.GetDataPresent(Cloud, false) && ReadBytes(((IDataObject)data).GetData(Cloud, false)).SequenceEqual(new byte[4]), "temporary text lacks cloud opt-out DWORD");
    }

    public static void HistorySkipsProtectedText(string format)
    {
        var source = new DataObject();
        source.SetData(DataFormats.UnicodeText, false, "synthetic protected text");
        source.SetData(format, false, new MemoryStream(format == Exclude ? new byte[] { 1 } : new byte[4]));
        var local = new ClipboardHistory(IntPtr.Zero);
        try
        {
            local.Capture(source);
            Check(local.Items.Count == 0, $"local history captured {format} text");
        }
        finally { local.Dispose(); }
    }

    public static void HistoryCapturesOrdinaryText()
    {
        var ordinary = new DataObject(DataFormats.UnicodeText, "synthetic ordinary text");
        CheckCaptured(ordinary, "synthetic ordinary text");
    }

    public static void HistoryCapturesAllowedText()
    {
        var allowed = new DataObject();
        allowed.SetData(DataFormats.UnicodeText, false, "synthetic allowed text");
        allowed.SetData(History, false, new MemoryStream(new byte[] { 1, 0, 0, 0 }));
        allowed.SetData(Cloud, false, new MemoryStream(new byte[] { 1, 0, 0, 0 }));
        CheckCaptured(allowed, "synthetic allowed text");
    }

    public static void HistorySkipsMalformedMarker()
    {
        var malformed = new DataObject();
        malformed.SetData(DataFormats.UnicodeText, false, "synthetic malformed text");
        malformed.SetData(History, false, new MemoryStream(new byte[] { 1 }));
        var local = new ClipboardHistory(IntPtr.Zero);
        try
        {
            local.Capture(malformed);
            Check(local.Items.Count == 0, "local history captured text with malformed history marker");
        }
        finally { local.Dispose(); }
    }

    public static void SnapshotBoundsMarkerStream()
    {
        var oversized = new CountingMarkerStream(4096);
        var source = ProtectedSource(oversized);
        var snapshot = Clip.CopySnapshot(source);
        Check(snapshot != null && snapshot.GetDataPresent(Exclude, false), "oversized marker snapshot lost fail-closed exclusion");
        Check(oversized.BytesRead <= 5, $"snapshot read {oversized.BytesRead} marker bytes; four plus one is enough");
    }

    public static void HistoryBoundsMarkerStream()
    {
        var oversized = new CountingMarkerStream(4096);
        var local = new ClipboardHistory(IntPtr.Zero);
        try
        {
            local.Capture(ProtectedSource(oversized));
            Check(local.Items.Count == 0, "history captured text with oversized marker");
            Check(oversized.BytesRead <= 5, $"history read {oversized.BytesRead} marker bytes; four plus one is enough");
        }
        finally { local.Dispose(); }
    }

    public static void MarkerReadFailuresFailClosed()
    {
        var source = new ThrowingMarkerDataObject(ProtectedSource(new MemoryStream(new byte[4])));
        var snapshot = Clip.CopySnapshot(source);
        Check(snapshot != null && snapshot.GetDataPresent(Exclude, false), "unreadable marker snapshot lost fail-closed exclusion");
        var local = new ClipboardHistory(IntPtr.Zero);
        try
        {
            local.Capture(source);
            Check(local.Items.Count == 0, "history captured text with unreadable marker");
        }
        finally { local.Dispose(); }
    }

    static DataObject ProtectedSource(Stream marker)
    {
        var source = new DataObject();
        source.SetData(DataFormats.UnicodeText, false, "synthetic protected text");
        source.SetData(History, false, marker);
        return source;
    }

    sealed class CountingMarkerStream(int length) : Stream
    {
        int _position;
        internal int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, length - _position);
            for (int i = 0; i < count; i++) buffer[i] = _position + i == 0 ? (byte)1 : (byte)0;
            _position += count;
            BytesRead += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

#pragma warning disable WFO1001 // Synthetic IDataObject only exercises the legacy clipboard contract.
    sealed class ThrowingMarkerDataObject(IDataObject inner) : IDataObject
    {
        public object? GetData(string format, bool autoConvert) => format == History ? throw new IOException("synthetic unreadable marker") : inner.GetData(format, autoConvert);
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => inner.GetData(format);
        public bool GetDataPresent(string format, bool autoConvert) => inner.GetDataPresent(format, autoConvert);
        public bool GetDataPresent(string format) => inner.GetDataPresent(format);
        public bool GetDataPresent(Type format) => inner.GetDataPresent(format);
        public string[] GetFormats(bool autoConvert) => inner.GetFormats(autoConvert);
        public string[] GetFormats() => inner.GetFormats();
        public void SetData(string format, bool autoConvert, object? data) => inner.SetData(format, autoConvert, data);
        public void SetData(string format, object? data) => inner.SetData(format, data);
        public void SetData(Type format, object? data) => inner.SetData(format, data);
        public void SetData(object? data) => inner.SetData(data);
    }
#pragma warning restore WFO1001

    static void CheckCaptured(DataObject source, string text)
    {
        var allowed = new ClipboardHistory(IntPtr.Zero);
        try
        {
            allowed.Capture(source);
            Check(allowed.Items.Count == 1 && allowed.Items[0] == text, "allowed text was not captured");
        }
        finally { allowed.Dispose(); }
    }

    static byte[] ReadBytes(object? value) => value switch
    {
        MemoryStream stream => stream.ToArray(),
        byte[] bytes => bytes,
        _ => throw new InvalidOperationException($"unexpected marker payload type: {value?.GetType().FullName ?? "null"}"),
    };

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
