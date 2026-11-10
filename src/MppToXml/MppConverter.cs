using MppToXml.Compound;
using MppToXml.Mpp;

namespace MppToXml;

/// <summary>Native C# MPP12/MPP14 reader. No Microsoft Project, COM, Java, or IKVM dependency.</summary>
public sealed class MppConverter
{
    public ConversionResult Read(string inputPath, MppReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        using var input = File.OpenRead(inputPath);
        return Read(input, options);
    }

    /// <summary>Reads from the current stream position; leaves the caller's stream open.</summary>
    public ConversionResult Read(Stream input, MppReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new();
        if (!input.CanRead) throw new ArgumentException("Input stream must be readable.", nameof(input));
        if (options.MaximumInputBytes < 512) throw new ArgumentOutOfRangeException(nameof(options), "MaximumInputBytes must be at least 512.");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = input.Read(chunk, 0, (int)Math.Min(chunk.Length, (long)options.MaximumInputBytes - buffer.Length + 1))) > 0)
        {
            if (buffer.Length + count > options.MaximumInputBytes) throw new InvalidDataException("MPP input exceeds MaximumInputBytes.");
            buffer.Write(chunk, 0, count);
        }
        var result = new NativeMppReader(new CompoundFile(buffer.ToArray())).Read();
        if (!options.AllowPartialConversion && !result.IsComplete) throw new IncompleteConversionException(result);
        return result;
    }

    /// <summary>Reads fully before writing. Does not close either stream.</summary>
    public ConversionResult Convert(Stream input, Stream output, MppReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (ReferenceEquals(input, output)) throw new ArgumentException("Input and output must be different streams.", nameof(output));
        if (!output.CanWrite) throw new ArgumentException("Output stream must be writable.", nameof(output));
        var result = Read(input, options);
        result.Document.Save(output);
        return result;
    }

    /// <summary>Writes a new output file. Existing output files are never overwritten.</summary>
    public ConversionResult Convert(string inputPath, string outputPath, MppReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var result = Read(inputPath, options);
        // Serialize before creating the destination, so XML errors cannot leave partial output.
        using var buffer = new MemoryStream(); result.Document.Save(buffer); buffer.Position = 0;
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        buffer.CopyTo(output);
        return result;
    }
}
