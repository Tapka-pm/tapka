using System.Text;
using Tapka;

public sealed class ShimBinaryTests
{
    private const int FieldSize = 32768 * 2;
    private static ReadOnlySpan<byte> Marker => "TAPKA_SHIM_CONFIG_V1:0e94b57a3d\0"u8;
    internal static byte[] ReadTemplate() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "shim.exe"));

    [Theory]
    [InlineData("")]
    [InlineData(@"shims\deps\пример")]
    public void PatchesOnlyTheThreeFields(string prefix)
    {
        var template = ReadTemplate();
        var expected = template.ToArray();
        var settings = template.AsSpan().IndexOf(Marker) + Marker.Length;
        const string root = @"..\..";
        const string target = @"packages\пример\x.exe";
        Encoding.Unicode.GetBytes(root).CopyTo(expected, settings);
        Encoding.Unicode.GetBytes(target).CopyTo(expected, settings + FieldSize);
        Encoding.Unicode.GetBytes(prefix).CopyTo(expected, settings + 2 * FieldSize);

        var patched = ShimBinary.Create(template, root, target, prefix);

        Assert.Equal(expected, patched);
        Assert.Equal(ReadTemplate(), template);
        Assert.Equal(patched, ShimBinary.Create(template, root, target, prefix));
        Assert.True(ShimBinary.IsManaged(patched));
        Assert.False(ShimBinary.IsManaged(template));
    }

    [Fact]
    public void ReservesOneCodeUnitForTheTerminator()
    {
        var template = ReadTemplate();
        var maximum = new string('x', 32767);
        Assert.True(ShimBinary.IsManaged(ShimBinary.Create(template, maximum, maximum, maximum)));
        Assert.Throws<InvalidDataException>(() => ShimBinary.Create(template, "..", maximum + "x", ""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x\0y")]
    [InlineData(@"C:\tool.exe")]
    [InlineData(@"C:tool.exe")]
    [InlineData(@"\tool.exe")]
    public void RejectsInvalidRequiredPath(string target)
    {
        Assert.Throws<InvalidDataException>(() => ShimBinary.Create(ReadTemplate(), "..", target, ""));
    }

    [Fact]
    public void RejectsMissingDuplicateAndTruncatedSettings()
    {
        var template = ReadTemplate();
        var marker = template.AsSpan().IndexOf(Marker);
        Assert.True(marker >= 0);
        var missing = template.ToArray();
        missing[marker] = 0;
        var duplicate = template.Concat(Marker.ToArray()).ToArray();
        var truncated = template[..(marker + Marker.Length + 3 * FieldSize - 1)];
        foreach (var invalid in new[] { missing, duplicate, truncated })
        {
            Assert.Throws<InvalidDataException>(() => ShimBinary.Create(invalid, "..", "tool.exe", ""));
            Assert.False(ShimBinary.IsManaged(invalid));
        }
    }

    [Fact]
    public void RejectsManagedImageWithAnUnterminatedField()
    {
        var image = ShimBinary.Create(ReadTemplate(), "..", "tool.exe", "");
        var offset = image.AsSpan().IndexOf(Marker) + Marker.Length;
        image.AsSpan(offset, FieldSize).Fill(0x41);
        Assert.False(ShimBinary.IsManaged(image));
    }
}
