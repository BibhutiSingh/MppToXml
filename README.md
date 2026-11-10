# MppToXml — native C# port in progress

This repository contains a .NET 10 class library, written in C#, for reading
Microsoft Project's MPP12 and MPP14 binary formats and writing MSPDI XML.
It has no runtime dependency on Microsoft Project, COM, Java, IKVM, or MPXJ.
MPXJ's Java source is the reference for the ported record layouts and field maps.

**The original all-versions/all-fields objective is not yet met.** This is an
initial native implementation, not a completed replacement for MPXJ. Do not use
its output as a lossless project archive. The .NET 10 solution builds with zero
warnings and zero errors. A prior revision passed all 27 xUnit tests, including
the five version fixtures and paired XML comparisons. An added baseline
regression then exposed a cost scaling error, which has been corrected in source.

**Current validation limit:** Windows Application Control intermittently blocks
newly built library DLLs with error 0x800711C7. The latest 28-test run was blocked
when loading the library, so the final currency fix is not runtime-verified.
No Windows security settings were changed. The latest local report is
`TestResults/net10.trx`, with console output in `TestResults/net10-test.log`.

## Build and test

Requires the .NET 10 SDK. From this directory in PowerShell:

```powershell
dotnet restore tests/MppToXml.Tests/MppToXml.Tests.csproj
dotnet build MppToXml.sln
dotnet test tests/MppToXml.Tests/MppToXml.Tests.csproj --no-restore
```

The projects inherit `net10.0` from `Directory.Build.props`. Library code uses
only the .NET base class library; NuGet dependencies are confined to xUnit and
the test SDK. Node.js is needed only to regenerate checked-in reference models.

## API

Full-fidelity mode is the default. Until the native port is complete, it throws
`IncompleteConversionException`, containing the decoded `Result`, before writing
output. Every result currently includes a `COVERAGE` diagnostic; `IsComplete`
therefore remains false. Diagnostics describe additional detected omissions but
are not an exhaustive inventory of every field omitted from a particular file.

To explicitly inspect a partial conversion:

```csharp
using MppToXml;

var converter = new MppConverter();
var result = converter.Convert(
    "schedule.mpp",
    "schedule.xml",
    new MppReadOptions { AllowPartialConversion = true });

foreach (var issue in result.Diagnostics)
    Console.WriteLine($"{issue.Code}: {issue.Message}");
```

`Read` and `Convert` also accept streams. They read from the current input
position and leave caller-owned streams open. The path overload creates a new
output file and refuses to overwrite an existing file. Inputs are buffered in
memory with a configurable 256 MiB default input limit; decoded data requires
additional memory. Encrypted/password-protected files are rejected.

## MSPDI model and sample

`MspdiDocument` preserves an XML document in the Microsoft Project namespace.
It provides safe XML loading (DTDs prohibited), UTF-8 writing, and conversion to
and from `MspdiProject`. The generated object model has 52 classes and 712
properties, including nested calendars, tasks, resources, assignments,
baselines, extended attributes, and timephased XML records. Scalar values use
XML lexical strings to preserve representations and distinguish absence from
zero. The model does not itself validate XSD types or required fields.

```csharp
using MppToXml.Mspdi;

using var input = File.OpenRead("export.xml");
var document = MspdiDocument.Load(input);
MspdiProject model = document.ToModel();
model.Title = "Updated title";
using var output = File.Create("updated.xml");
MspdiDocument.FromModel(model).Save(output);
```

The supplied `export.xml` remains unchanged. Tests use it to check model
round-tripping. XML serialization support must not be confused with decoding
those same fields from MPP binaries.

## Coverage

| Area | Implementation status |
| --- | --- |
| OLE compound storage | Native FAT, DIFAT, MiniFAT and directory traversal; bounds and cycle checks |
| File variants | MPP12 and MPP14 detection; fixtures from Project 2007, 2010, 2013, 2016, 2019 |
| Binary records | Properties, fixed/variable records, embedded field maps |
| Common entities | Initial task/resource/assignment scalar decoding; task hierarchy and links |
| Calendars | Initial standard week and exception date/hour decoding |
| Baselines/custom values | Initial scalar baseline and direct custom-value export |
| MSPDI serialization | Broad schema model plus raw XML preservation; no XSD conformance claim |
| Complete fidelity | **Not implemented or validated** |

Further work is required for timephased binary data, lookup tables and enterprise
fields, custom-field definitions/formulas, recurring calendar exceptions and work
weeks, recurring tasks, subprojects/resource pools, RTF notes, rate tables,
availability tables, remaining flags and properties, and variant-specific
postprocessing. Corrupt or unusual layouts may be rejected. Future Project
releases and all post-2007 versions are not certified by these fixtures.

There is no guarantee of byte-identical XML to Microsoft Project's export:
MSPDI is a subset of MPP data, and formatting, defaults and calculated fields
also differ between exporters. The acceptance criterion for further porting
should be semantic comparisons against paired MPP/XML exports, not only that
an XML document can be written.

## Source layout

- `src/MppToXml/Compound`: read-only compound-file storage.
- `src/MppToXml/Mpp`: native binary decoding and MPXJ-derived field maps.
- `src/MppToXml/Mspdi`: generated schema classes and XML API.
- `tests/MppToXml.Tests`: xUnit tests and upstream binary/XML fixtures.
