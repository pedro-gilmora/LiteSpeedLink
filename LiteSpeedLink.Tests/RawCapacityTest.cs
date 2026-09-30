using FluentAssertions;
using Xunit;

namespace LiteSpeedLink.Tests;

public class RawCapacityTest
{
    /// <summary>6b: MemoryPack escribe strings en UTF-8 con cabecera de 8; la cota generada (8 + 3/char) nunca obliga a crecer.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("pedro")]
    [InlineData("ñandú €")]
    [InlineData("日本語")]
    public void StringFitsGeneratedCapacity(string value)
    {
        var bytes = MemoryPack.MemoryPackSerializer.Serialize(value);

        bytes.Length.Should().BeLessThanOrEqualTo(8 + value.Length * 3);
    }
}
