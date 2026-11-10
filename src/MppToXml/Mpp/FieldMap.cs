using System.Text.Json;
using static MppToXml.Mpp.BinaryData;

namespace MppToXml.Mpp;

internal sealed record FieldDefinition(string Name, string Type, int Id, string Location, int Block, int Offset, int Key);

// Ported from MPXJ FieldMap/FieldMap12/FieldMap14. See THIRD-PARTY-NOTICES.md.
internal static class FieldMap
{
    private static readonly Dictionary<string, Dictionary<string, FieldDefinition[]>> Defaults = Load();
    private static Dictionary<string, Dictionary<string, FieldDefinition[]>> Load()
    {
        using var stream = typeof(FieldMap).Assembly.GetManifestResourceStream("MppToXml.Mpp.FieldMaps.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, FieldDefinition[]>>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    internal static FieldDefinition[] Read(int version, string entity, Dictionary<int, byte[]> props)
    {
        var defaults = Defaults[version.ToString(System.Globalization.CultureInfo.InvariantCulture)][entity];
        int suffix = entity == "Task" ? 20 : entity == "Resource" ? 21 : 23;
        if (!props.TryGetValue(131072 + suffix, out var data) && !props.TryGetValue(50331648 + suffix, out data)) return defaults;
        if (data.Length % 28 != 0) throw new InvalidDataException("Invalid MPP field map length.");
        var byId = defaults.Where(f => f.Id >= 0).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<FieldDefinition>();
        int last = 0, block = 0;
        for (int p = 0; p < data.Length; p += 28)
        {
            int offset = U16(data, p + 4), id = I32(data, p + 12) & 0xffff, category = U16(data, p + 20);
            int key = version == 14 ? id : data[p + 6];
            string location = category is 0x0b or 0x64 ? "META_DATA" : offset == 65535 ? "VAR_DATA" : "FIXED_DATA";
            if (location == "FIXED_DATA") { if (offset < last) block++; last = offset; }
            if (byId.TryGetValue(id, out var field)) result.Add(field with { Location = location, Offset = offset, Block = block, Key = key });
            else result.Add(new($"UNKNOWN_{id}", "BINARY", id, location, block, offset, key));
        }
        return result.ToArray();
    }
    internal static int Size(string type) => type switch
    {
        "INTEGER" or "DURATION" or "DATE" => 4,
        "CURRENCY" or "UNITS" or "RATE" or "WORK" or "NUMERIC" => 8,
        "GUID" => 16,
        "WORK_UNITS" => 1,
        "STRING" or "BINARY" or "NOTES" => 0,
        _ => 2
    };
    internal static string? Decode(FieldDefinition f, byte[] data, int offset, bool variable)
    {
        if (variable && (f.Type is "STRING" or "DATE" or "NUMERIC") && data.Length >= 2 && (U16(data) is 0x701 or 0x401))
            return null; // Lookup-table references require the custom field table reader.
        switch (f.Type)
        {
            case "STRING": return Unicode(data, offset);
            case "DATE": return Date(data, offset);
            case "GUID": Check(data, offset, 16); return new Guid(data.AsSpan(offset, 16)).ToString("D");
            case "INTEGER": return I32(data, offset).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "DURATION": return I32(data, offset) == -1 ? null : Duration(I32(data, offset) / 10.0);
            case "WORK": return Duration(Double(data, offset) / 1000.0);
            case "CURRENCY": return Number(Double(data, offset) / 100.0);
            case "UNITS": return Number(Double(data, offset) / 10000.0);
            case "NUMERIC": case "RATE": return Number(Double(data, offset));
            case "BOOLEAN": return U16(data, offset) == 0 ? "0" : "1";
            case "TASK_TYPE": return U16(data, offset) switch { 0 => "0", 1 => "1", 2 => "2", _ => null };
            case "WORK_UNITS": Check(data, offset, 1); return data[offset].ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "BINARY": case "NOTES": case "ASCII_STRING": case "RELATION_LIST": case "RESOURCE_TYPE": return null;
            default: return U16(data, offset).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
