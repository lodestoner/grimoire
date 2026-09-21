using System.Buffers.Binary;

namespace Spellbook;

/// <summary>Windows clipboard history and cloud-sync format handling.</summary>
internal static class ClipboardPrivacy
{
    // DWORD formats need four bytes; the fifth byte detects an oversized payload.
    const int MaxMarkerBytes = 4;
    internal const string Exclude = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string History = "CanIncludeInClipboardHistory";
    internal const string Cloud = "CanUploadToCloudClipboard";

    static readonly string[] Markers = [Exclude, History, Cloud];

    internal static bool IsExcluded(IDataObject source)
    {
        try
        {
            if (source.GetDataPresent(Exclude, false)) return true;
            return Denies(source, History) || Denies(source, Cloud);
        }
        catch { return true; } // An unreadable privacy preference must not enter local history.
    }

    static bool Denies(IDataObject source, string format)
    {
        if (!source.GetDataPresent(format, false)) return false;
        return !TryReadDword(source.GetData(format, false), out uint value) || value != 1;
    }

    static bool TryReadDword(object? data, out uint value)
    {
        value = 0;
        if (data is uint unsigned) { value = unsigned; return true; }
        if (data is int signed) { value = unchecked((uint)signed); return true; }
        if (!TryBytes(data, out var bytes) || bytes.Length != 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    internal static bool CopyMarkers(IDataObject source, DataObject target)
    {
        bool any = false;
        foreach (var format in Markers)
        {
            try
            {
                if (!source.GetDataPresent(format, false)) continue;
                var data = source.GetData(format, false);
                if (TryBytes(data, out var bytes))
                {
                    if (format == Exclude && bytes.Length == 0) AddExclusion(target);
                    else
                    {
                        target.SetData(format, false, new MemoryStream(bytes));
                        if (format != Exclude && bytes.Length != 4) AddExclusion(target);
                    }
                }
                else if (data is uint unsigned)
                    target.SetData(format, false, new MemoryStream(BitConverter.GetBytes(unsigned)));
                else if (data is int signed)
                    target.SetData(format, false, new MemoryStream(BitConverter.GetBytes(signed)));
                else
                    AddExclusion(target);
                any = true;
            }
            catch
            {
                AddExclusion(target);
                any = true;
            }
        }
        return any;
    }

    internal static void AddExclusion(DataObject target)
    {
        target.SetData(Exclude, false, new MemoryStream([1]));
        target.SetData(History, false, new MemoryStream(new byte[4]));
        target.SetData(Cloud, false, new MemoryStream(new byte[4]));
    }

    static bool TryBytes(object? data, out byte[] bytes)
    {
        if (data is byte[] array)
        {
            if (array.Length > MaxMarkerBytes) { bytes = []; return false; }
            bytes = (byte[])array.Clone();
            return true;
        }
        if (data is MemoryStream memory)
        {
            if (memory.Length > MaxMarkerBytes) { bytes = []; return false; }
            bytes = memory.ToArray();
            return true;
        }
        if (data is Stream stream)
        {
            long position = 0;
            bool restore = stream.CanSeek;
            if (restore) { position = stream.Position; stream.Position = 0; }
            try
            {
                Span<byte> probe = stackalloc byte[MaxMarkerBytes + 1];
                int count = 0;
                while (count < probe.Length)
                {
                    int read = stream.Read(probe[count..]);
                    if (read == 0) break;
                    count += read;
                }
                if (count > MaxMarkerBytes) { bytes = []; return false; }
                bytes = probe[..count].ToArray();
                return true;
            }
            finally { if (restore) stream.Position = position; }
        }
        bytes = [];
        return false;
    }
}
