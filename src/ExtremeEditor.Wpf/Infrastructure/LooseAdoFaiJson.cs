using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Wpf;

// A comma the loader inserted between two values, at 1-based line/column of
// the value end it follows in the UTF-8 chart text.
internal readonly record struct JsonRepair(int Line, int Column, long ByteOffset);

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

    internal static string CreateNormalizedTempCopy(string sourcePath) =>
        CreateNormalizedTempCopy(sourcePath, out _);

    internal static string CreateNormalizedTempCopy(string sourcePath, out IReadOnlyList<JsonRepair> repairs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        byte[] bytes = File.ReadAllBytes(sourcePath);
        byte[] normalized = NormalizeToUtf8(bytes, out repairs);

        string directory = Path.Combine(Path.GetTempPath(), "ExtremeEditor", "normalized-json");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}.adofai");
        File.WriteAllBytes(path, normalized);
        return path;
    }

    internal static byte[] NormalizeToUtf8(byte[] bytes) => NormalizeToUtf8(bytes, out _);

    // Converts to UTF-8, supplies missing commas between values (see
    // FindMissingCommas) and escapes raw control bytes inside strings. A
    // strictly valid UTF-8 chart is returned as the same array.
    internal static byte[] NormalizeToUtf8(byte[] bytes, out IReadOnlyList<JsonRepair> repairs)
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

        List<JsonRepair> found = FindMissingCommas(utf8, out bool hasRawStringControls);
        repairs = found;
        if (found.Count > 0)
            utf8 = InsertCommas(utf8, found);
        return found.Count > 0 || hasRawStringControls ? EscapeRawStringControls(utf8) : utf8;
    }

    // Stock ADOFAI parses levels with GDMiniJSON, which skips commas between
    // array elements and object members instead of requiring them. Supply a
    // comma only in the unambiguous case: outside strings and comments, a
    // value has ended (an object, an array or a string) and, after only
    // whitespace or comments, the next token opens an object, an array or a
    // string. Adjacent numbers or literals are left alone. The result is
    // still parsed strictly, so a comma that does not complete valid JSON
    // leaves the chart rejected.
    private static List<JsonRepair> FindMissingCommas(byte[] utf8, out bool hasRawStringControls)
    {
        var repairs = new List<JsonRepair>();
        hasRawStringControls = false;
        bool inString = false;
        bool escaped = false;
        bool valueEnded = false;
        long valueEnd = -1;
        int valueEndLine = 0;
        int valueEndColumn = 0;
        int line = 1;
        long lineStart = 0;

        void MarkValueEnd(long index)
        {
            valueEnded = true;
            valueEnd = index;
            valueEndLine = line;
            valueEndColumn = checked((int)(index - lineStart)) + 1;
        }

        for (long i = 0; i < utf8.Length; i++)
        {
            byte value = utf8[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (value == (byte)'\\')
                {
                    escaped = true;
                }
                else if (value == (byte)'"')
                {
                    inString = false;
                    MarkValueEnd(i);
                }
                else if (value < 0x20)
                {
                    hasRawStringControls = true;
                    if (value == (byte)'\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }
                }
                continue;
            }

            switch (value)
            {
                case (byte)'\n':
                    line++;
                    lineStart = i + 1;
                    break;
                case (byte)' ':
                case (byte)'\t':
                case (byte)'\r':
                    break;
                case (byte)'/' when i + 1 < utf8.Length && utf8[i + 1] == (byte)'/':
                    // Line comment: skip to the newline, which the loop counts.
                    while (i + 1 < utf8.Length && utf8[i + 1] != (byte)'\n')
                        i++;
                    break;
                case (byte)'/' when i + 1 < utf8.Length && utf8[i + 1] == (byte)'*':
                    i += 2;
                    while (i < utf8.Length && !(utf8[i] == (byte)'*' && i + 1 < utf8.Length && utf8[i + 1] == (byte)'/'))
                    {
                        if (utf8[i] == (byte)'\n')
                        {
                            line++;
                            lineStart = i + 1;
                        }
                        i++;
                    }
                    i++;
                    break;
                case (byte)'{':
                case (byte)'[':
                case (byte)'"':
                    if (valueEnded)
                        repairs.Add(new JsonRepair(valueEndLine, valueEndColumn, valueEnd + 1));
                    valueEnded = false;
                    inString = value == (byte)'"';
                    break;
                case (byte)'}':
                case (byte)']':
                    MarkValueEnd(i);
                    break;
                default:
                    valueEnded = false;
                    break;
            }
        }
        return repairs;
    }

    private static byte[] InsertCommas(byte[] utf8, IReadOnlyList<JsonRepair> repairs)
    {
        var output = new byte[utf8.Length + repairs.Count];
        long source = 0;
        long target = 0;
        foreach (JsonRepair repair in repairs)
        {
            long count = repair.ByteOffset - source;
            Array.Copy(utf8, source, output, target, count);
            target += count;
            output[target++] = (byte)',';
            source = repair.ByteOffset;
        }
        Array.Copy(utf8, source, output, target, utf8.Length - source);
        return output;
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
