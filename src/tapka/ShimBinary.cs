using System.Buffers.Binary;
using System.Text;

namespace Tapka;

internal static class ShimBinary
{
    // The C marker is padded to 32 bytes. Each field includes its terminator.
    private const int FieldCapacity = 32768;
    private const int FieldSize = FieldCapacity * sizeof(char);
    private static ReadOnlySpan<byte> Marker => "TAPKA_SHIM_CONFIG_V2:0e94b57a3d\0"u8;
    private static ReadOnlySpan<byte> LegacyMarker => "TAPKA_SHIM_CONFIG_V1:0e94b57a3d\0"u8;
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    public static byte[] Create(ReadOnlySpan<byte> template, string rootRelative, string targetExe, string pathPrefix,
        string dotNetRoot = "")
    {
        var offset = FindSettings(template, Marker, 4);
        if (offset < 0 || template.Slice(offset, 4 * FieldSize).ContainsAnyExcept((byte)0))
        {
            throw new InvalidDataException("The native shim template has invalid embedded settings.");
        }

        var result = template.ToArray();
        WriteField(result.AsSpan(offset, FieldSize), rootRelative, nameof(rootRelative));
        WriteField(result.AsSpan(offset + FieldSize, FieldSize), targetExe, nameof(targetExe));
        WriteField(result.AsSpan(offset + 2 * FieldSize, FieldSize), pathPrefix, nameof(pathPrefix), optional: true);
        WriteField(result.AsSpan(offset + 3 * FieldSize, FieldSize), dotNetRoot, nameof(dotNetRoot), optional: true);
        return result;
    }

    public static bool IsManaged(ReadOnlySpan<byte> executable)
    {
        var offset = FindSettings(executable, Marker, 4);
        if (offset >= 0)
        {
            return HasValidSettings(executable, offset) &&
                IsValidField(executable.Slice(offset + 3 * FieldSize, FieldSize), optional: true);
        }
        offset = FindSettings(executable, LegacyMarker, 3);
        return offset >= 0 && HasValidSettings(executable, offset);
    }

    private static bool HasValidSettings(ReadOnlySpan<byte> executable, int offset)
    {
        return IsValidField(executable.Slice(offset, FieldSize)) &&
            IsValidField(executable.Slice(offset + FieldSize, FieldSize)) &&
            IsValidField(executable.Slice(offset + 2 * FieldSize, FieldSize), optional: true);
    }

    private static int FindSettings(ReadOnlySpan<byte> image, ReadOnlySpan<byte> signature, int fields)
    {
        const int peOffsetLocation = 0x3c;
        if (image.Length < peOffsetLocation + sizeof(int) || !image.StartsWith("MZ"u8))
        {
            return -1;
        }
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(image[peOffsetLocation..]);
        if (peOffset < peOffsetLocation + sizeof(int) || peOffset > image.Length - 4 ||
            !image[peOffset..].StartsWith("PE\0\0"u8))
        {
            return -1;
        }

        var marker = image.IndexOf(signature);
        if (marker < peOffset + 4 || image[(marker + 1)..].IndexOf(signature) >= 0)
        {
            return -1;
        }
        var offset = marker + signature.Length;
        return offset <= image.Length - fields * FieldSize ? offset : -1;
    }

    private static void WriteField(Span<byte> field, string value, string name, bool optional = false)
    {
        if (value.Length >= FieldCapacity || !IsRelative(value.AsSpan(), optional))
        {
            throw new InvalidDataException($"Invalid relative path for the shim's {name} setting.");
        }
        try
        {
            Utf16.GetBytes(value.AsSpan(), field);
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException($"Invalid UTF-16 in the shim's {name} setting.", error);
        }
    }

    private static bool IsValidField(ReadOnlySpan<byte> field, bool optional = false)
    {
        for (var end = 0; end < field.Length; end += sizeof(char))
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(field[end..]) == 0)
            {
                try
                {
                    return !field[end..].ContainsAnyExcept((byte)0) &&
                        IsRelative(Utf16.GetString(field[..end]).AsSpan(), optional);
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
            }
        }
        return false;
    }

    private static bool IsRelative(ReadOnlySpan<char> path, bool optional) =>
        path.IsEmpty ? optional : path[0] is not ('\\' or '/') && !path.ContainsAny('\0', ':');
}
