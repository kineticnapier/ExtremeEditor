using System.Text;

namespace ExtremeEditor.Wpf;

internal static class LooseAdoFaiJson
{
    internal static string CreateNormalizedTempCopy(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        byte[] bytes = File.ReadAllBytes(sourcePath);
        byte[] normalized = NormalizeToUtf8(bytes);

        string directory = Path.Combine(Path.GetTempPath(), "ExtremeEditor", "normalized-json");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}.adofai");
        File.WriteAllBytes(path, normalized);
        return path;
    }

    private static byte[] NormalizeToUtf8(byte[] bytes)
    {
        byte[] utf8;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            utf8 = bytes.AsSpan(3).ToArray();
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            utf8 = Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            utf8 = Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
        }
        else
        {
            utf8 = bytes;
        }

        return EscapeRawStringControls(utf8);
    }

    private static byte[] EscapeRawStringControls(byte[] utf8)
    {
        bool inString = false;
        bool escaped = false;
        bool needsRewrite = false;

        foreach (byte value in utf8)
        {
            if (!inString)
            {
                if (value == (byte)'\"')
                    inString = true;
                continue;
            }

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (value == (byte)'\\')
            {
                escaped = true;
                continue;
            }

            if (value == (byte)'\"')
            {
                inString = false;
                continue;
            }

            if (value < 0x20)
            {
                needsRewrite = true;
                break;
            }
        }

        if (!needsRewrite)
            return utf8;

        using var output = new MemoryStream(utf8.Length + 256);
        inString = false;
        escaped = false;

        foreach (byte value in utf8)
        {
            if (!inString)
            {
                output.WriteByte(value);
                if (value == (byte)'\"')
                    inString = true;
                continue;
            }

            if (escaped)
            {
                output.WriteByte(value);
                escaped = false;
                continue;
            }

            if (value == (byte)'\\')
            {
                output.WriteByte(value);
                escaped = true;
                continue;
            }

            if (value == (byte)'\"')
            {
                output.WriteByte(value);
                inString = false;
                continue;
            }

            switch (value)
            {
                case 0x08:
                    WriteAscii(output, "\\b");
                    break;
                case 0x09:
                    WriteAscii(output, "\\t");
                    break;
                case 0x0A:
                    WriteAscii(output, "\\n");
                    break;
                case 0x0C:
                    WriteAscii(output, "\\f");
                    break;
                case 0x0D:
                    WriteAscii(output, "\\r");
                    break;
                default:
                    if (value < 0x20)
                        WriteAscii(output, $"\\u{value:X4}");
                    else
                        output.WriteByte(value);
                    break;
            }
        }

        return output.ToArray();
    }

    private static void WriteAscii(Stream output, string value)
    {
        foreach (char ch in value)
            output.WriteByte((byte)ch);
    }
}
