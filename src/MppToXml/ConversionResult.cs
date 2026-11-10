using MppToXml.Mspdi;

namespace MppToXml;

public sealed record ConversionDiagnostic(string Code, string Message);

public sealed class ConversionResult
{
    public MspdiDocument Document { get; }
    public string SourceFormat { get; }
    public IReadOnlyList<ConversionDiagnostic> Diagnostics { get; }
    public bool IsComplete => Diagnostics.Count == 0;
    internal ConversionResult(MspdiDocument document, string sourceFormat, IEnumerable<ConversionDiagnostic> diagnostics)
    { Document = document; SourceFormat = sourceFormat; Diagnostics = Array.AsReadOnly(diagnostics.Distinct().ToArray()); }
}

public sealed class MppReadOptions
{
    /// <summary>Maximum input bytes. The reader buffers the input and decoded streams in memory.</summary>
    public int MaximumInputBytes { get; init; } = 256 * 1024 * 1024;
    /// <summary>Explicitly accept the native port's documented incomplete coverage.</summary>
    public bool AllowPartialConversion { get; init; }
}

public sealed class IncompleteConversionException : NotSupportedException
{
    public ConversionResult Result { get; }
    internal IncompleteConversionException(ConversionResult result)
        : base("The native reader cannot guarantee complete field coverage. Inspect Result.Diagnostics; set AllowPartialConversion only if the documented omissions are acceptable.") => Result = result;
}
