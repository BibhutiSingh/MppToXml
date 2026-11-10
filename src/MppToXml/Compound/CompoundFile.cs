using System.Buffers.Binary;
using System.Text;

namespace MppToXml.Compound;

// Native, read-only implementation of OLE compound storage (512/4096 byte sectors).
internal sealed class CompoundFile
{
    private const uint End = 0xfffffffe, Free = 0xffffffff;
    private readonly byte[] bytes;
    private readonly int sectorSize;
    private readonly uint[] fat;
    private readonly uint[] miniFat;
    private readonly byte[] miniStream;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    internal IEnumerable<string> Paths => entries.Keys;
    private sealed record Entry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, ulong Size);

    internal CompoundFile(byte[] bytes)
    {
        this.bytes = bytes;
        if (bytes.Length < 512 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            throw new InvalidDataException("Input is not an OLE compound file (.mpp).");
        var major = U16(bytes, 26);
        var shift = U16(bytes, 30);
        if (U16(bytes, 28) != 0xfffe || U16(bytes, 32) != 6 ||
            !((major == 3 && shift == 9) || (major == 4 && shift == 12)) || U32(bytes, 56) != 4096)
            throw new InvalidDataException("Unsupported compound-file header.");
        sectorSize = 1 << shift;
        if (bytes.Length % sectorSize != 0) throw new InvalidDataException("Truncated compound-file sector.");
        int sectorCount = bytes.Length / sectorSize - 1;
        uint fatCount = U32(bytes, 44), difatCount = U32(bytes, 72);
        if (fatCount > sectorCount || difatCount > sectorCount) throw new InvalidDataException("Invalid allocation table size.");
        var fatIds = new List<uint>();
        for (int p = 76; p < 512; p += 4) { uint id = U32(bytes, p); if (id != Free) fatIds.Add(id); }
        uint next = U32(bytes, 68);
        var seen = new HashSet<uint>();
        for (int i = 0; i < difatCount; i++)
        {
            if (!seen.Add(next)) throw new InvalidDataException("Cyclic DIFAT.");
            byte[] block = Sector(next);
            for (int p = 0; p < sectorSize - 4; p += 4) { uint id = U32(block, p); if (id != Free) fatIds.Add(id); }
            next = U32(block, sectorSize - 4);
        }
        if (fatIds.Count != fatCount || fatIds.Distinct().Count() != fatIds.Count) throw new InvalidDataException("Invalid FAT sector list.");
        fat = fatIds.SelectMany(id => Words(Sector(id))).ToArray();
        var directory = ReadChain(U32(bytes, 48), fat, sectorSize, Sector, null);
        var nodes = new List<Entry>();
        for (int p = 0; p + 128 <= directory.Length; p += 128)
        {
            int n = U16(directory, p + 64);
            byte type = directory[p + 66];
            if (type != 0 && (n < 2 || n > 64 || n % 2 != 0)) throw new InvalidDataException("Invalid directory name.");
            nodes.Add(new(type == 0 ? "" : Encoding.Unicode.GetString(directory, p, n - 2), type,
                U32(directory, p + 68), U32(directory, p + 72), U32(directory, p + 76), U32(directory, p + 116),
                major == 3 ? U32(directory, p + 120) : BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(p + 120, 8))));
        }
        if (nodes.Count == 0 || nodes[0].Type != 5) throw new InvalidDataException("Missing compound-file root.");
        miniStream = ReadChain(nodes[0].Start, fat, sectorSize, Sector, CheckedSize(nodes[0].Size));
        uint miniFatCount = U32(bytes, 64);
        if (miniFatCount > sectorCount) throw new InvalidDataException("Invalid MiniFAT size.");
        miniFat = Words(ReadChain(U32(bytes, 60), fat, sectorSize, Sector, checked((int)miniFatCount * sectorSize))).ToArray();
        var pending = new Stack<(uint Id, string Parent)>();
        pending.Push((nodes[0].Child, ""));
        seen.Clear();
        while (pending.TryPop(out var item))
        {
            if (item.Id == Free) continue;
            if (item.Id >= nodes.Count || !seen.Add(item.Id)) throw new InvalidDataException("Invalid or cyclic directory tree.");
            var node = nodes[(int)item.Id];
            if (node.Type is not (1 or 2)) throw new InvalidDataException("Invalid directory entry type.");
            string path = item.Parent + node.Name;
            if (!entries.TryAdd(path, node)) throw new InvalidDataException("Duplicate compound-file path.");
            pending.Push((node.Left, item.Parent));
            pending.Push((node.Right, item.Parent));
            if (node.Type == 1) pending.Push((node.Child, path + "/"));
        }
    }

    internal bool Contains(string path) => entries.TryGetValue(path, out var e) && e.Type == 2;
    internal byte[] Read(string path)
    {
        if (!entries.TryGetValue(path, out var entry) || entry.Type != 2) throw new InvalidDataException($"Missing MPP stream: {path}.");
        int size = CheckedSize(entry.Size);
        return size < 4096 ? ReadChain(entry.Start, miniFat, 64, MiniSector, size) : ReadChain(entry.Start, fat, sectorSize, Sector, size);
    }
    internal byte[]? TryRead(string path) => Contains(path) ? Read(path) : null;
    private int CheckedSize(ulong size) => size <= (ulong)bytes.Length ? (int)size : throw new InvalidDataException("Stream length exceeds file size.");
    private byte[] Sector(uint id)
    {
        long offset = ((long)id + 1) * sectorSize;
        if (offset < sectorSize || offset + sectorSize > bytes.Length) throw new InvalidDataException("Sector is outside the file.");
        return bytes.AsSpan((int)offset, sectorSize).ToArray();
    }
    private byte[] MiniSector(uint id)
    {
        long offset = (long)id * 64;
        if (offset + 64 > miniStream.Length) throw new InvalidDataException("Mini-sector is outside the root stream.");
        return miniStream.AsSpan((int)offset, 64).ToArray();
    }
    private byte[] ReadChain(uint start, uint[] table, int size, Func<uint, byte[]> read, int? length)
    {
        if (length == 0) return [];
        using var output = new MemoryStream();
        var seen = new HashSet<uint>();
        for (uint id = start; id != End; id = table[id])
        {
            if (id >= table.Length || !seen.Add(id)) throw new InvalidDataException("Invalid or cyclic sector chain.");
            if (output.Length + size > bytes.Length) throw new InvalidDataException("Sector chain exceeds input size.");
            if (length.HasValue && output.Length >= length.Value) throw new InvalidDataException("Sector chain exceeds declared length.");
            output.Write(read(id));
        }
        if (length.HasValue && output.Length < length.Value) throw new InvalidDataException("Truncated stream chain.");
        return output.GetBuffer().AsSpan(0, length ?? (int)output.Length).ToArray();
    }
    private static IEnumerable<uint> Words(byte[] b) { for (int p = 0; p < b.Length; p += 4) yield return U32(b, p); }
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
}
