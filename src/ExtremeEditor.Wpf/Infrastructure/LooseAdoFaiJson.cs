using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Wpf;

internal static class LooseAdoFaiJson
{
    internal static bool HasUtf16Bom(string sourcePath)
    {
        using var stream = File.OpenRead(sourcePath);
        int first = stream.ReadByte();
        int second = stream.ReadByte();
        return (first == 0xFF && second == 0xFE) || (first == 0xFE && second == 0xFF);
    }

    internal static JsonObject ParseObject(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return ParseNormalizedObject(NormalizeToUtf8(File.ReadAllBytes(sourcePath)));
    }

    private static JsonObject ParseNormalizedObject(byte[] normalized)
    {
        return JsonNode.Parse(
                   normalized,
                   new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                   new JsonDocumentOptions
                   {
                       AllowTrailingCommas = true,
                       CommentHandling = JsonCommentHandling.Skip
                   }) as JsonObject
               ?? throw new InvalidDataException("ADOFAI root must be a JSON object.");
    }

    // Returns one property of the root object without materializing the rest
    // of the chart. The reader still validates every skipped value, so input
    // that ParseObject rejects is rejected here with the same exception. Rare
    // shapes (a non-object root, a repeated property) defer to ParseObject so
    // their result is exactly the whole-document one.
    internal static JsonNode? ParseRootProperty(string sourcePath, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return ParseRootProperty(File.ReadAllBytes(sourcePath), propertyName);
    }

    internal static JsonNode? ParseRootProperty(byte[] fileBytes, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        ArgumentException.ThrowIfNullOrEmpty(propertyName);

        byte[] normalized = NormalizeToUtf8(fileBytes);
        var reader = new Utf8JsonReader(normalized, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return ParseNormalizedObject(normalized)[propertyName];

        int start = -1;
        int length = 0;
        int matches = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool match = reader.ValueTextEquals(propertyName);
            reader.Read();
            int valueStart = checked((int)reader.TokenStartIndex);
            reader.Skip();
            if (match)
            {
                matches++;
                start = valueStart;
                length = checked((int)reader.BytesConsumed) - valueStart;
            }
        }
        // Anything but whitespace or comments after the root value throws, as
        // it does for the whole-document parse.
        reader.Read();

        if (matches == 0)
            return null;
        if (matches > 1)
            return ParseNormalizedObject(normalized)[propertyName];
        return JsonNode.Parse(
            normalized.AsSpan(start, length),
            new JsonNodeOptions { PropertyNameCaseInsensitive = false },
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
    }

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

    internal static byte[] NormalizeToUtf8(byte[] bytes)
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
