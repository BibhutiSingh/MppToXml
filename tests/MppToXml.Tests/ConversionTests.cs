using System.Xml.Linq;
using MppToXml.Compound;
using MppToXml.Mspdi;

namespace MppToXml.Tests;

public sealed class ConversionTests
{
    private static readonly XNamespace Ns = MspdiDocument.Namespace;
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("2007-mpp12")]
    [InlineData("2010-mpp14")]
    [InlineData("2013-mpp14")]
    [InlineData("2016-mpp14")]
    [InlineData("2019-mpp14")]
    public void ReadsRealMppFiles(string version)
    {
        var result = new MppConverter().Read(Fixture($"calendar-{version}.mpp"), new() { AllowPartialConversion = true });
        Assert.Equal(version.EndsWith("mpp12") ? "MSProject.MPP12" : "MSProject.MPP14", result.SourceFormat);
        Assert.Contains(result.Document.Xml.Descendants(Ns + "Calendar"), c => c.Element(Ns + "Name")?.Value == "Standard");
        Assert.NotEmpty(result.Document.Xml.Descendants(Ns + "Task"));
        using var output = new MemoryStream(); result.Document.Save(output); output.Position = 0;
        Assert.Equal(Ns + "Project", MspdiDocument.Load(output).Xml.Root!.Name);
    }

    [Fact]
    public void SampleXmlRoundTripsEveryElementAndValue()
    {
        using var input = File.OpenRead(Fixture("export.xml")); var document = MspdiDocument.Load(input);
        var restored = MspdiDocument.FromModel(document.ToModel());
        Assert.Equal(Leaves(document.Xml), Leaves(restored.Xml));
    }

    [Theory]
    [InlineData("assignment-assignments-2019.xml")]
    [InlineData("task-baselines-2019.xml")]
    [InlineData("task-text-2019.xml")]
    public void UpstreamXmlRoundTripsThroughSchemaModel(string file)
    {
        using var input = File.OpenRead(Fixture(file));
        var document = MspdiDocument.Load(input);
        var restored = MspdiDocument.FromModel(document.ToModel());
        Assert.Equal(Leaves(document.Xml), Leaves(restored.Xml));
    }

    [Fact]
    public void AssignmentIdsMatchPairedProjectExport()
    {
        var actual = new MppConverter().Read(Fixture("assignment-assignments-2019.mpp"), new() { AllowPartialConversion = true }).Document.Xml;
        var expected = XDocument.Load(Fixture("assignment-assignments-2019.xml"));
        static string[] Ids(XDocument doc) => doc.Descendants(Ns + "Assignment")
            .Select(a => $"{a.Element(Ns + "UID")?.Value}:{a.Element(Ns + "TaskUID")?.Value}:{a.Element(Ns + "ResourceUID")?.Value}")
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(Ids(expected));
        Assert.Equal(Ids(expected), Ids(actual));
    }

    [Fact]
    public void DirectTaskTextValuesMatchPairedProjectExport()
    {
        var actual = new MppConverter().Read(Fixture("task-text-2019.mpp"), new() { AllowPartialConversion = true }).Document.Xml;
        var expected = XDocument.Load(Fixture("task-text-2019.xml"));
        Assert.NotEmpty(expected.Descendants(Ns + "Task").Elements(Ns + "ExtendedAttribute"));
        foreach (var task in expected.Descendants(Ns + "Task"))
        {
            string uid = task.Element(Ns + "UID")!.Value;
            var found = Assert.Single(actual.Descendants(Ns + "Task"), t => t.Element(Ns + "UID")?.Value == uid);
            foreach (var attribute in task.Elements(Ns + "ExtendedAttribute"))
            {
                var value = Assert.Single(found.Elements(Ns + "ExtendedAttribute"), a => a.Element(Ns + "FieldID")?.Value == attribute.Element(Ns + "FieldID")?.Value);
                Assert.Equal(attribute.Element(Ns + "Value")?.Value, value.Element(Ns + "Value")?.Value);
            }
        }
    }

    [Fact]
    public void BaselinesMatchPairedProjectExport()
    {
        var actual = new MppConverter().Read(Fixture("task-baselines-2019.mpp"), new() { AllowPartialConversion = true }).Document.Xml;
        var expected = XDocument.Load(Fixture("task-baselines-2019.xml"));
        Assert.NotEmpty(expected.Descendants(Ns + "Task").Elements(Ns + "Baseline"));
        foreach (var task in expected.Descendants(Ns + "Task"))
        {
            var found = Assert.Single(actual.Descendants(Ns + "Task"), t => t.Element(Ns + "UID")?.Value == task.Element(Ns + "UID")?.Value);
            foreach (var baseline in task.Elements(Ns + "Baseline"))
            {
                var result = Assert.Single(found.Elements(Ns + "Baseline"), b => b.Element(Ns + "Number")?.Value == baseline.Element(Ns + "Number")?.Value);
                foreach (string name in new[] { "Start", "Finish", "Duration", "Work", "Cost" })
                {
                    string? expectedValue = baseline.Element(Ns + name)?.Value;
                    if (expectedValue == null) continue;
                    string actualValue = Assert.IsType<string>(result.Element(Ns + name)?.Value);
                    if (name is "Duration" or "Work") Assert.Equal(System.Xml.XmlConvert.ToTimeSpan(expectedValue), System.Xml.XmlConvert.ToTimeSpan(actualValue));
                    else if (name == "Cost") Assert.Equal(decimal.Parse(expectedValue, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(actualValue, System.Globalization.CultureInfo.InvariantCulture));
                    else Assert.Equal(expectedValue, actualValue);
                }
            }
        }
    }

    [Fact]
    public void CalendarFixtureMatchesMicrosoftProjectExport()
    {
        var actual = new MppConverter().Read(Fixture("calendar-2019-mpp14.mpp"), new() { AllowPartialConversion = true }).Document.Xml;
        var expected = XDocument.Load(Fixture("calendar-2019.xml"));
        var expectedCalendars = expected.Descendants(Ns + "Calendar").Where(c => c.Element(Ns + "IsBaseCalendar")?.Value == "1").ToArray();
        foreach (var calendar in expectedCalendars)
        {
            var found = Assert.Single(actual.Descendants(Ns + "Calendar"), c => c.Element(Ns + "UID")?.Value == calendar.Element(Ns + "UID")?.Value);
            Assert.Equal(calendar.Element(Ns + "Name")?.Value, found.Element(Ns + "Name")?.Value);
            Assert.Equal(Leaves(calendar.Element(Ns + "WeekDays")!), Leaves(found.Element(Ns + "WeekDays")!));
        }
    }

    [Fact]
    public void DefaultModeRefusesToClaimCompleteCoverage()
    {
        var error = Assert.Throws<IncompleteConversionException>(() => new MppConverter().Read(Fixture("calendar-2019-mpp14.mpp")));
        Assert.False(error.Result.IsComplete);
        Assert.Contains(error.Result.Diagnostics, d => d.Code == "COVERAGE");
    }

    [Fact]
    public void StrictFailureDoesNotWriteOutputOrCloseStreams()
    {
        using var input = File.OpenRead(Fixture("calendar-2019-mpp14.mpp")); using var output = new MemoryStream();
        Assert.Throws<IncompleteConversionException>(() => new MppConverter().Convert(input, output));
        Assert.Equal(0, output.Length); Assert.True(input.CanRead); Assert.True(output.CanWrite);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(512)]
    public void RejectsNonCompoundInput(int length)
    {
        using var input = new MemoryStream(new byte[length]);
        Assert.Throws<InvalidDataException>(() => new MppConverter().Read(input));
    }

    [Fact]
    public void EnforcesInputSizeLimit()
    {
        using var input = File.OpenRead(Fixture("calendar-2019-mpp14.mpp"));
        Assert.Throws<InvalidDataException>(() => new MppConverter().Read(input, new() { MaximumInputBytes = 512 }));
    }

    [Fact]
    public void RejectsCyclicDirectoryChain()
    {
        var bytes = File.ReadAllBytes(Fixture("calendar-2019-mpp14.mpp"));
        int sectorSize = 1 << BitConverter.ToUInt16(bytes, 30); int directory = BitConverter.ToInt32(bytes, 48);
        // Make the root's child point back to the root.
        BitConverter.GetBytes(0).CopyTo(bytes, (directory + 1) * sectorSize + 76);
        Assert.Throws<InvalidDataException>(() => new CompoundFile(bytes));
    }

    [Fact]
    public void XmlRejectsExternalEntities()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE Project [<!ENTITY e SYSTEM 'file:///secret'>]><Project xmlns='http://schemas.microsoft.com/project'>&e;</Project>"));
        Assert.Throws<System.Xml.XmlException>(() => MspdiDocument.Load(stream));
    }

    private static string[] Leaves(XContainer xml) => xml.Descendants().Where(e => !e.HasElements)
        .Select(e => string.Join("/", e.AncestorsAndSelf().Reverse().Select(a => a.Name.LocalName)) + "=" + e.Value).ToArray();
}
