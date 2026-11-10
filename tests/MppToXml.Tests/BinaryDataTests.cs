using MppToXml.Mpp;

namespace MppToXml.Tests;

public sealed class BinaryDataTests
{
    [Fact]
    public void TimestampUsesProjectEpochAndSixSecondTicks()
    {
        byte[] record = [100, 0, 100, 0];
        Assert.Equal("1984-04-09T00:10:00", BinaryData.Date(record));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void NaDateSentinelsAreAbsent(int days)
    {
        byte[] record = [0, 0, (byte)(days & 255), (byte)(days >> 8)];
        Assert.Null(BinaryData.Date(record));
    }

    [Fact]
    public void DatesRejectTruncatedData() => Assert.Throws<InvalidDataException>(() => BinaryData.Date([0, 0, 0]));

    [Fact]
    public void UnicodeDecodingPreservesNonAsciiNames()
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes("工程 परीक्षण\0ignored");
        Assert.Equal("工程 परीक्षण", BinaryData.Unicode(bytes));
    }

    [Fact]
    public void MppCurrencyUsesHundredths()
    {
        var field = new FieldDefinition("COST", "CURRENCY", 5, "FIXED_DATA", 0, 0, 5);
        Assert.Equal("1234.56", FieldMap.Decode(field, BitConverter.GetBytes(123456.0), 0, false));
    }
}
