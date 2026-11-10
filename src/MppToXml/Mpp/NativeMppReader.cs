using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Serialization;
using MppToXml.Compound;
using MppToXml.Mspdi;
using static MppToXml.Mpp.BinaryData;

namespace MppToXml.Mpp;

// Native port of the MPP12/MPP14 record pipeline; see THIRD-PARTY-NOTICES.md.
internal sealed class NativeMppReader(CompoundFile storage)
{
    private static readonly XNamespace Ns = MspdiDocument.Namespace;
    private readonly List<ConversionDiagnostic> diagnostics = [];
    private readonly Dictionary<int, int> resourceCalendars = [];
    private int version, application;
    private string directory = "";
    private Dictionary<int, byte[]> props = [];
    private readonly XElement project = E("Project");
    internal ConversionResult Read()
    {
        var comp = storage.Read("\u0001CompObj"); int p = 28;
        string app = ReadAnsi(comp, ref p), format = ReadAnsi(comp, ref p);
        version = format switch { "MSProject.MPP12" => 12, "MSProject.MPP14" => 14, _ => throw new NotSupportedException($"Unsupported project format: {format}. Expected MPP12 or MPP14.") };
        var match = Regex.Match(app, @"(\d+)\.0");
        application = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : version;
        directory = version == 12 ? "   112/" : "   114/";
        var rootProps = Properties(storage.Read("Props" + version));
        if (rootProps.TryGetValue(893386752, out var protection) && protection.Any(b => b != 0))
            throw new NotSupportedException("Password-protected MPP files are not supported by the native reader.");
        props = Properties(storage.Read(directory + "Props"));
        Add(project, "SaveVersion", "14");
        ReadProperties();
        project.Add(ReadCalendars());
        project.Add(ReadEntities("Task", "TBkndTask", 47));
        project.Add(ReadEntities("Resource", "TBkndRsc", 37));
        project.Add(ReadEntities("Assignment", "TBkndAssn", 34));
        ReadRelations();
        UpdateTaskStructure();
        Order(project, typeof(MspdiProject));
        Warn("COVERAGE", "This native port does not yet guarantee all-field fidelity: timephased data, enterprise/lookup custom fields, recurring tasks, subprojects, rate tables, and some metadata require further porting and validation.");
        return new(new MspdiDocument(new XDocument(new XDeclaration("1.0", "utf-8", "yes"), project)), format, diagnostics);
    }

    private void ReadProperties()
    {
        var fields = new (int Key, string Name, string Type)[]
        {
            (37748777,"GUID","GUID"),(37748738,"StartDate","DATE"),(37748739,"FinishDate","DATE"),
            (37748805,"StatusDate","DATE"),(37748765,"MinutesPerDay","INTEGER"),(37748766,"MinutesPerWeek","INTEGER"),
            (37748754,"CurrencyDigits","SHORT"),(37748752,"CurrencySymbol","STRING"),(37753787,"CurrencyCode","STRING"),
            (37748785,"DefaultTaskType","TASK_TYPE"),(37748753,"CurrencySymbolPosition","SHORT"),
            (37748780,"FYStartDate","SHORT"),(37748801,"FiscalYearStart","BOOLEAN"),(37753743,"DaysPerMonth","SHORT"),
            (37748802,"EditableActualCosts","BOOLEAN"),(37748793,"MultipleCriticalPaths","BOOLEAN"),
            (37753800,"NewTasksAreManual","BOOLEAN"),(37748762,"SplitsInProgressTasks","BOOLEAN"),
            (37748761,"TaskUpdatesResource","BOOLEAN"),(37748756,"CriticalSlackLimit","INTEGER"),
            (37748810,"HyperlinkBase","STRING"),(37748744,"Title","STRING"),(37748745,"Subject","STRING"),
            (37748748,"Author","STRING"),(37748736,"Company","STRING"),(37748737,"Manager","STRING"),
            (37748809,"Category","STRING"),(37748767,"DefaultStandardRate","RATE"),(37748768,"DefaultOvertimeRate","RATE")
        };
        foreach (var (key, name, type) in fields)
            if (props.TryGetValue(key, out var data)) Add(project, name, FieldMap.Decode(new(name, type, 0, "", 0, 0, 0), data, 0, false));
        if (props.TryGetValue(37748740, out var schedule)) Add(project, "ScheduleFromStart", U16(schedule) == 0 ? "0" : "1");
        if (props.TryGetValue(37748794, out var honor)) Add(project, "HonorConstraints", U16(honor) == 0 ? "1" : "0");
        if (props.TryGetValue(37748773, out var week)) Add(project, "WeekStartDay", U16(week).ToString(CultureInfo.InvariantCulture));
        if (props.TryGetValue(37748757, out var units)) Add(project, "DurationFormat", (U16(units) & 31).ToString(CultureInfo.InvariantCulture));
        if (props.TryGetValue(37748764, out var start)) Add(project, "DefaultStartTime", Clock(U16(start) * 6));
        if (props.TryGetValue(37748769, out var end)) Add(project, "DefaultFinishTime", Clock(U16(end) * 6));
        ReadSummary("\u0005SummaryInformation", new Dictionary<int, string> { [2] = "Title", [3] = "Subject", [4] = "Author" });
        ReadSummary("\u0005DocumentSummaryInformation", new Dictionary<int, string> { [2] = "Category", [14] = "Manager", [15] = "Company" });
    }
    private void ReadSummary(string path, Dictionary<int, string> names)
    {
        var data = storage.TryRead(path); if (data == null) return;
        Check(data, 0, 48); int section = I32(data, 44), count = I32(data, section + 4);
        Check(data, section + 8, checked(count * 8));
        for (int i = 0; i < count; i++)
        {
            int id = I32(data, section + 8 + i * 8), pos = checked(section + I32(data, section + 12 + i * 8));
            if (!names.TryGetValue(id, out var name)) continue;
            int type = I32(data, pos), length = I32(data, pos + 4);
            if (type == 31) { Check(data, pos + 8, checked(length * 2)); Add(project, name, Unicode(data.AsSpan(pos + 8, length * 2).ToArray())); }
            else if (type == 30) { Check(data, pos + 8, length); var value = data.AsSpan(pos + 8, length); if (value.IndexOfAnyInRange((byte)128, (byte)255) >= 0) { Warn("CODEPAGE", "Non-ASCII summary property requires code-page decoding."); continue; } Add(project, name, Encoding.ASCII.GetString(value).TrimEnd('\0')); }
        }
    }

    private XElement ReadEntities(string entity, string folder, int metaSize)
    {
        var container = E(entity == "Resource" ? "Resources" : entity + "s");
        string path = directory + folder + "/";
        if (!storage.Contains(path + "FixedMeta")) return container;
        var fields = FieldMap.Read(version, entity, props);
        var meta = Metadata(storage.Read(path + "FixedMeta"), metaSize);
        int maximum = fields.Where(f => f.Location == "FIXED_DATA" && f.Block == 0).Select(f => f.Offset + FieldMap.Size(f.Type)).DefaultIfEmpty(0).Max();
        var fixedBytes = storage.Read(path + "FixedData");
        var records = Records(meta, fixedBytes, maximum);
        byte[][] meta2 = [], records2 = [];
        var secondMeta = storage.TryRead(path + "Fixed2Meta");
        var secondData = storage.TryRead(path + "Fixed2Data");
        if (secondMeta != null && secondData != null)
        {
            int[] candidates = entity == "Task" ? version == 12 ? [86] : [92, 93, 94, 95, 96] : entity == "Resource" ? version == 12 ? [49] : [50, 51] : [53];
            int size = candidates.FirstOrDefault(s => (secondMeta.Length - 16) % s == 0 && (secondMeta.Length - 16) / s == meta.Length);
            if (size == 0) size = candidates.FirstOrDefault(s => (secondMeta.Length - 16) % s == 0);
            if (size != 0) { meta2 = Metadata(secondMeta, size); records2 = Records(meta2, secondData); }
            else Warn("METADATA", $"Unrecognized {entity} secondary metadata layout.");
        }
        var variables = Variables(storage.Read(path + "VarMeta"), storage.Read(path + "Var2Data"));
        var uidField = fields.FirstOrDefault(f => f.Name == "UNIQUE_ID") ?? throw new InvalidDataException($"Missing {entity} UID mapping.");
        var modelType = entity switch { "Task" => typeof(MspdiProjectTasksTask), "Resource" => typeof(MspdiProjectResourcesResource), _ => typeof(MspdiProjectAssignmentsAssignment) };
        var xmlNames = modelType.GetProperties().Where(x => x.PropertyType == typeof(string)).ToDictionary(x => Normalize(x.Name), x => x.Name);
        var seen = new HashSet<int>();
        var elements = new List<XElement>();
        for (int i = records.Length - 1; i >= (entity == "Task" ? 3 : 0); i--)
        {
            byte[] data = records[i];
            if (data.Length < uidField.Offset + 4) continue;
            int id = I32(data, uidField.Offset);
            if (id < 0) continue;
            if ((meta[i][0] & 2) != 0) { seen.Add(id); continue; }
            if (entity == "Assignment" && meta[i][0] != 0) continue;
            if (data.Length < Math.Min(maximum, 20) || !seen.Add(id)) continue;
            byte[] data2 = i < records2.Length ? records2[i] : [];
            // Assignment second records are packed in the same index order as 110-byte fixed records.
            if (entity == "Assignment" && secondData != null && version == 14)
            {
                int recordIndex = I32(meta[i], 4) / 110;
                if (recordIndex >= 0 && recordIndex < secondData.Length / 48) data2 = secondData.AsSpan(recordIndex * 48, 48).ToArray();
            }
            var element = E(entity); var decoded = new Dictionary<string, string>();
            foreach (var field in fields)
            {
                byte[]? source; int offset;
                if (field.Location == "FIXED_DATA") { source = field.Block == 0 ? data : field.Block == 1 ? data2 : null; offset = field.Offset; }
                else if (field.Location == "VAR_DATA") { variables.TryGetValue((id, field.Key), out source); offset = 0; }
                else continue;
                if (source == null || source.Length == 0) continue;
                if (field.Type == "BINARY" || field.Type == "NOTES")
                {
                    if (field.Location == "VAR_DATA") Warn("UNDECODED", $"{entity}.{field.Name} contains data not yet exported.");
                    continue;
                }
                if (offset + FieldMap.Size(field.Type) > source.Length) { Warn("SHORT_RECORD", $"{entity}.{field.Name} is truncated or uses an unsupported layout."); continue; }
                string? value = FieldMap.Decode(field, source, offset, field.Location == "VAR_DATA");
                if (value == null) continue;
                // MSPDI regular cost elements use hundredths of currency units. Direct
                // custom costs and assignment baseline costs use currency units instead.
                bool customCost = Regex.IsMatch(field.Name, @"^COST\d+$");
                bool assignmentBaseline = entity == "Assignment" && field.Name.StartsWith("BASELINE", StringComparison.Ordinal);
                if (field.Type == "CURRENCY" && !customCost && !assignmentBaseline) value = Number(Double(source, offset));
                if (field.Type == "DURATION" && (field.Name.EndsWith("_SLACK", StringComparison.Ordinal) || field.Name is "START_VARIANCE" or "FINISH_VARIANCE" or "LEVELING_DELAY"))
                    value = I32(source, offset).ToString(CultureInfo.InvariantCulture);
                if (field.Name == "WORK_VARIANCE") value = Number(I32(source, offset) * 100.0);
                decoded[field.Name] = value;
                if (field.Name.StartsWith("BASELINE", StringComparison.Ordinal)) { WriteBaseline(element, field.Name, value); continue; }
                string key = field.Name.Replace("UNIQUE_ID", "UID", StringComparison.Ordinal);
                key = key switch { "ACCRUE_AT" => "ACCRUEAT", "TASK_MODE" => "MANUAL", "ACTUAL_DURATION_UNITS" => "DURATIONFORMAT", "EMAIL_ADDRESS" => "EMAILADDRESS", _ => key };
                if (xmlNames.TryGetValue(Normalize(key), out var xmlName)) Add(element, xmlName, value);
                else if (Regex.IsMatch(field.Name, @"^(TEXT|NUMBER|COST|DATE|START|FINISH|DURATION|FLAG)\d+$") && field.Id >= 0)
                {
                    int fieldId = (entity == "Resource" ? 205520896 : entity == "Assignment" ? 255852544 : 188743680) + field.Id;
                    element.Add(E("ExtendedAttribute", E("FieldID", fieldId), E("Value", value)));
                    var attributes = project.Element(Ns + "ExtendedAttributes"); if (attributes == null) { attributes = E("ExtendedAttributes"); project.Add(attributes); }
                    if (!attributes.Elements().Any(e => e.Element(Ns + "FieldID")?.Value == fieldId.ToString(CultureInfo.InvariantCulture))) attributes.Add(E("ExtendedAttribute", E("FieldID", fieldId), E("FieldName", field.Name)));
                }
            }
            Add(element, "UID", id.ToString(CultureInfo.InvariantCulture));
            if (entity == "Task")
            {
                ReadTaskFlags(element, meta[i], i < meta2.Length ? meta2[i] : []);
                foreach (string name in new[] { "START", "FINISH", "DURATION" })
                    if (decoded.TryGetValue("SCHEDULED_" + name, out var scheduled) && (element.Element(Ns + "Manual")?.Value != "1" || !decoded.ContainsKey(name))) Add(element, char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant(), scheduled);
                if (decoded.TryGetValue("ACTUAL_DURATION_UNITS", out var durationUnits))
                {
                    int units = int.Parse(durationUnits, CultureInfo.InvariantCulture);
                    Add(element, "DurationFormat", (units & 63).ToString(CultureInfo.InvariantCulture));
                    Add(element, "Estimated", (units & 32) != 0 ? "1" : "0");
                }
                if (decoded.TryGetValue("PARENT_TASK_UNIQUE_ID", out var parent)) element.SetAttributeValue("parent", parent);
            }
            else if (entity == "Resource")
            {
                if (resourceCalendars.TryGetValue(id, out int calendar)) Add(element, "CalendarUID", calendar.ToString(CultureInfo.InvariantCulture));
                byte[] secondary = i < meta2.Length ? meta2[i] : [];
                if (secondary.Length > 8) { Add(element, "Type", (meta[i][9] & 2) != 0 ? "1" : (secondary[8] & 16) != 0 ? "2" : "0"); Add(element, "IsBudget", (secondary[8] & (version == 14 && application > 14 ? 64 : 32)) != 0 ? "1" : "0"); }
            }
            Order(element, modelType); elements.Add(element);
        }
        container.Add(elements.OrderBy(e => int.TryParse(e.Element(Ns + "ID")?.Value ?? e.Element(Ns + "UID")?.Value, out int id) ? id : 0));
        return container;
    }

    private static void WriteBaseline(XElement element, string field, string value)
    {
        var match = Regex.Match(field, @"^BASELINE(\d*)_(?:ESTIMATED_)?(START|FINISH|DURATION|WORK|COST|BCWS|BCWP)$");
        if (!match.Success) return;
        string number = match.Groups[1].Value is "" ? "0" : match.Groups[1].Value;
        var baseline = element.Elements(Ns + "Baseline").FirstOrDefault(e => e.Element(Ns + "Number")?.Value == number);
        if (baseline == null) { baseline = E("Baseline", E("Number", number)); element.Add(baseline); }
        string name = match.Groups[2].Value;
        name = name.StartsWith("BC", StringComparison.Ordinal) ? name : name[0] + name[1..].ToLowerInvariant();
        Add(baseline, name, value);
    }

    private void ReadTaskFlags(XElement task, byte[] meta, byte[] second)
    {
        bool modern = version == 14 && application > 14;
        var flags = modern
            ? new (string, int, int)[] { ("Milestone", 10, 2), ("Marked", 12, 2), ("Rollup", 12, 4), ("HideBar", 12, 128), ("EffortDriven", 13, 8), ("LevelAssignments", 16, 4), ("LevelingCanSplit", 16, 2), ("IgnoreResourceCalendar", 17, 32) }
            : [("Milestone", 8, 32), ("Marked", 9, 64), ("Rollup", 10, 8), ("HideBar", 10, 128), ("EffortDriven", 11, 16), ("LevelAssignments", 13, 4), ("LevelingCanSplit", 13, 2), ("IgnoreResourceCalendar", 10, 2)];
        foreach (var (name, pos, mask) in flags) if (pos < meta.Length) Add(task, name, (meta[pos] & mask) != 0 ? "1" : "0");
        if (version == 14 && second.Length > 8) { Add(task, "Manual", (second[8] & (modern ? 128 : 8)) != 0 ? "1" : "0"); Add(task, "Active", (second[8] & (modern ? 64 : 4)) != 0 ? "1" : "0"); }
    }

    private XElement ReadCalendars()
    {
        string path = directory + "TBkndCal/"; var calendars = E("Calendars");
        var meta = Metadata(storage.Read(path + "FixedMeta"), 10);
        var records = Records(meta, storage.Read(path + "FixedData"), 12);
        var variables = Variables(storage.Read(path + "VarMeta"), storage.Read(path + "Var2Data"));
        props.TryGetValue(37753736, out var defaultHours);
        string? defaultName = props.TryGetValue(37748750, out var nameBytes) ? Unicode(nameBytes) : null;
        var ids = new HashSet<int>(); bool modern = version == 14 && application > 14;
        foreach (var data in records)
        {
            if (data.Length < 12) continue;
            int id = I32(data, modern ? 8 : 0), parent = I32(data, modern ? 0 : 4), resource = I32(data, modern ? 4 : 8);
            if (id <= 0 || !ids.Add(id)) continue;
            bool isBase = parent <= 0 || parent == id;
            string? name = variables.TryGetValue((id, 1), out var n) ? Unicode(n) : null;
            var calendar = E("Calendar", E("UID", id));
            Add(calendar, "Name", name); Add(calendar, "IsBaseCalendar", isBase ? "1" : "0"); Add(calendar, "BaseCalendarUID", isBase ? "-1" : parent.ToString(CultureInfo.InvariantCulture));
            if (resource > 0) resourceCalendars[resource] = id;
            if (name == defaultName) Add(project, "CalendarUID", id.ToString(CultureInfo.InvariantCulture));
            variables.TryGetValue((id, 8), out var hours); if (hours == null && isBase) hours = defaultHours;
            calendar.Add(ReadWeek(hours, defaultHours, isBase));
            if (hours?.Length > 420) ReadCalendarExceptions(calendar, hours);
            Order(calendar, typeof(MspdiProjectCalendarsCalendar)); calendars.Add(calendar);
        }
        return calendars;
    }
    private static XElement ReadWeek(byte[]? hours, byte[]? defaults, bool isBase)
    {
        var week = E("WeekDays");
        for (int day = 0; day < 7; day++)
        {
            int p = day * 60; byte[]? source = hours;
            bool inherit = source == null || U16(source, p) == 1;
            if (inherit && !isBase) continue;
            if (inherit) source = defaults;
            var weekday = E("WeekDay", E("DayType", day + 1));
            var times = E("WorkingTimes");
            if (source == null || U16(source, p) == 1)
            {
                if (day > 0 && day < 6) times.Add(E("WorkingTime", E("FromTime", "08:00:00"), E("ToTime", "12:00:00")), E("WorkingTime", E("FromTime", "13:00:00"), E("ToTime", "17:00:00")));
            }
            else
            {
                int count = U16(source, p + 2); if (count > 5) throw new InvalidDataException("Too many working periods in calendar day.");
                for (int i = 0; i < count; i++) { int start = U16(source, p + 8 + i * 2) * 6; int duration = U16(source, p + 20 + i * 4) * 6; times.Add(E("WorkingTime", E("FromTime", Clock(start)), E("ToTime", Clock(start + duration)))); }
            }
            weekday.Add(E("DayWorking", times.HasElements ? 1 : 0)); if (times.HasElements) weekday.Add(times); week.Add(weekday);
        }
        return week;
    }
    private void ReadCalendarExceptions(XElement calendar, byte[] data)
    {
        int count = U16(data, 420), p = 424; var exceptions = E("Exceptions");
        for (int i = 0; i < count; i++)
        {
            Check(data, p, 92); int nameLength = I32(data, p + 88); Check(data, p + 92, nameLength);
            var exception = E("Exception", E("TimePeriod", E("FromDate", Day(U16(data, p))), E("ToDate", Day(U16(data, p + 2)))));
            if (nameLength > 0) exception.Add(E("Name", Unicode(data.AsSpan(p + 92, nameLength).ToArray())));
            int periods = U16(data, p + 14); if (periods > 5) throw new InvalidDataException("Too many exception working periods.");
            exception.Add(E("DayWorking", periods == 0 ? 0 : 1));
            var times = E("WorkingTimes");
            for (int j = 0; j < periods; j++) { int start = U16(data, p + 20 + j * 2) * 6; int duration = U16(data, p + 32 + j * 4) * 6; times.Add(E("WorkingTime", E("FromTime", Clock(start)), E("ToTime", Clock(start + duration)))); }
            if (times.HasElements) exception.Add(times);
            exceptions.Add(exception); p += 92 + checked((nameLength + 3) / 4 * 4);
        }
        if (count > 0) { calendar.Add(exceptions); Warn("CALENDAR_RECURRENCE", "Calendar exception dates/hours are exported; recurrence rules are not yet decoded."); }
        if (p + 4 <= data.Length && I32(data, p) != 0) Warn("WORK_WEEKS", "Additional calendar work weeks are not yet decoded.");
    }

    private void ReadRelations()
    {
        string path = directory + "TBkndCons/"; if (!storage.Contains(path + "FixedMeta")) return;
        var meta = Metadata(storage.Read(path + "FixedMeta"), 10); var data = storage.Read(path + "FixedData");
        var tasks = project.Element(Ns + "Tasks")!.Elements().ToDictionary(e => e.Element(Ns + "UID")!.Value);
        foreach (var row in meta)
        {
            if (U16(row) != 0) continue; int p = I32(row, 4); Check(data, p, 20);
            string predecessor = I32(data, p + 4).ToString(CultureInfo.InvariantCulture), successor = I32(data, p + 8).ToString(CultureInfo.InvariantCulture);
            if (predecessor == successor || predecessor == "0" || successor == "0" || !tasks.ContainsKey(predecessor) || !tasks.TryGetValue(successor, out var task)) continue;
            bool modern = version == 14 && application > 14;
            task.Add(E("PredecessorLink", E("PredecessorUID", predecessor), E("Type", U16(data, p + 12)), E("CrossProject", 0), E("LinkLag", I32(data, p + (modern ? 14 : 16))), E("LagFormat", U16(data, p + (modern ? 18 : 14)) & 31)));
        }
    }
    private void UpdateTaskStructure()
    {
        var tasks = project.Element(Ns + "Tasks")!.Elements().ToList();
        var parents = tasks.Select(t => t.Attribute("parent")?.Value).Where(p => p != null).ToHashSet();
        var outline = new List<int>();
        foreach (var task in tasks)
        {
            string? uid = task.Element(Ns + "UID")?.Value;
            Add(task, "Summary", parents.Contains(uid) ? "1" : "0");
            int level = int.TryParse(task.Element(Ns + "OutlineLevel")?.Value, out int parsed) ? parsed : uid == "0" ? 0 : 1;
            if (level < 0 || level > 1000) throw new InvalidDataException("Invalid task outline level.");
            while (outline.Count > level) outline.RemoveAt(outline.Count - 1);
            while (outline.Count < level) outline.Add(0);
            if (level > 0) outline[level - 1]++;
            Add(task, "OutlineNumber", level == 0 ? "0" : string.Join(".", outline));
            task.Attribute("parent")?.Remove();
        }
    }
    private void Warn(string code, string message) => diagnostics.Add(new(code, message));
    private static string ReadAnsi(byte[] data, ref int p) { int length = I32(data, p); p += 4; Check(data, p, length); string value = Encoding.ASCII.GetString(data, p, length).TrimEnd('\0'); p += length; return value; }
    private static string Clock(int seconds) => TimeSpan.FromSeconds(seconds % 86400).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    private static string Day(int days) => new DateTime(1983, 12, 31).AddDays(days).ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture);
    private static string Normalize(string name) => name.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
    private static XElement E(string name, params object[] content) => new(Ns + name, content);
    private static void Add(XElement parent, string name, string? value) { if (value != null) parent.SetElementValue(Ns + name, value); }
    // Use the complete schema model's element sequence, including nested baselines/links.
    private static void Order(XElement element, Type type)
    {
        var properties = type.GetProperties().Select(p => (Property: p, Xml: p.GetCustomAttribute<XmlElementAttribute>())).Where(p => p.Xml != null).ToDictionary(p => p.Xml!.ElementName);
        foreach (var child in element.Elements())
        {
            if (!properties.TryGetValue(child.Name.LocalName, out var property)) continue;
            Type childType = property.Property.PropertyType; if (childType.IsGenericType) childType = childType.GetGenericArguments()[0];
            if (childType != typeof(string)) Order(child, childType);
        }
        var children = element.Elements().OrderBy(e => properties.TryGetValue(e.Name.LocalName, out var property) ? property.Xml!.Order : int.MaxValue).ToArray();
        element.ReplaceNodes(children);
    }
}
