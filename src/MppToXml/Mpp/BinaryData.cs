using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MppToXml.Mpp;

internal static class BinaryData
{
    internal static int I32(byte[] b, int p = 0) { Check(b, p, 4); return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); }
    internal static ushort U16(byte[] b, int p = 0) { Check(b, p, 2); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); }
    internal static double Double(byte[] b, int p = 0) { Check(b, p, 8); return BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(p)); }
    internal static void Check(byte[] b, int p, int count)
    {
        if (p < 0 || count < 0 || p > b.Length - count) throw new InvalidDataException("Truncated MPP data record.");
    }
    internal static string Unicode(byte[] b, int p = 0) { Check(b, p, 0); return Encoding.Unicode.GetString(b, p, (b.Length - p) & ~1).Split('\0')[0]; }
    internal static string? Date(byte[] b, int p = 0)
    {
        int days = U16(b, p + 2), ticks = U16(b, p);
        if (days <= 1 || days == 65535) return null;
        if (ticks == 65535) ticks = 0;
        var date = new DateTime(1983, 12, 31).AddDays(days).AddSeconds(ticks * 6);
        return days < 100 && date.Second != 0 ? null : date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }
    internal static string Duration(double minutes) => System.Xml.XmlConvert.ToString(TimeSpan.FromMinutes(minutes));
    internal static string Number(double value) => double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : throw new InvalidDataException("Non-finite MPP numeric value.");
    internal static Dictionary<int, byte[]> Properties(byte[] b)
    {
        Check(b, 0, 16); int count = U16(b, 12), p = 16;
        var result = new Dictionary<int, byte[]>();
        for (int i = 0; i < count; i++)
        {
            int size = I32(b, p), key = I32(b, p + 4); p += 12;
            Check(b, p, size); result[key] = b.AsSpan(p, size).ToArray(); p += size + (size & 1);
        }
        return result;
    }
    internal static Dictionary<(int Id, int Key), byte[]> Variables(byte[] meta, byte[] data)
    {
        Check(meta, 0, 24);
        if (I32(meta) != 0 && I32(meta) != unchecked((int)0xfadfadba)) throw new InvalidDataException("Invalid variable-data header.");
        int count = I32(meta, 8); if (count < 0 || count > (meta.Length - 24) / 12) throw new InvalidDataException("Truncated variable-data index.");
        var result = new Dictionary<(int, int), byte[]>();
        for (int i = 0, p = 24; i < count; i++, p += 12)
        {
            int id = I32(meta, p), offset = I32(meta, p + 4), key = U16(meta, p + 8);
            int size = I32(data, offset); Check(data, offset + 4, size);
            result[(id, key)] = data.AsSpan(offset + 4, size).ToArray();
        }
        return result;
    }
    internal static byte[][] Metadata(byte[] b, int size)
    {
        Check(b, 0, 16);
        if (I32(b) != unchecked((int)0xfadfadba)) throw new InvalidDataException("Invalid fixed-data header.");
        if ((b.Length - 16) % size != 0) throw new InvalidDataException("Invalid metadata record size.");
        return Enumerable.Range(0, (b.Length - 16) / size).Select(i => b.AsSpan(16 + i * size, size).ToArray()).ToArray();
    }
    internal static byte[][] Records(byte[][] meta, byte[] data, int maxSize = 0)
    {
        var result = new byte[meta.Length][];
        for (int i = 0; i < meta.Length; i++)
        {
            int start = I32(meta[i], 4);
            if (start < 0 || start > data.Length) { result[i] = []; continue; }
            int end = i + 1 < meta.Length ? I32(meta[i + 1], 4) : data.Length;
            if (end < start || end > data.Length) end = data.Length;
            if (maxSize > 0) end = Math.Min(end, start + maxSize);
            result[i] = data.AsSpan(start, end - start).ToArray();
        }
        return result;
    }
}
