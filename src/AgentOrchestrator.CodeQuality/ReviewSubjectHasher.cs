using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public static class ReviewSubjectHasher
{
    public static async ValueTask<string> ComputeFileContentHashAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var normalizedBytes = Encoding.UTF8.GetBytes(normalized);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(normalizedBytes));
    }

    public static string ComputeManifestHash(string unitId, IReadOnlyList<SubjectInputHash> inputs)
    {
        var sortedInputs = inputs.OrderBy(input => input.Path, StringComparer.Ordinal)
            .ThenBy(input => input.Selector, StringComparer.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("domain", "quality-studio/reviewed-subject/v1");
            writer.WritePropertyName("inputs");
            writer.WriteStartArray();
            foreach (var input in sortedInputs)
            {
                writer.WriteStartObject();
                writer.WriteString("contentHash", input.ContentHash);
                writer.WriteString("path", input.Path);
                writer.WriteString("selector", input.Selector);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("unitId", unitId);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            preambleLength = Encoding.UTF8.Preamble.Length;
            return new UTF8Encoding(false, true);
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            preambleLength = Encoding.Unicode.Preamble.Length;
            return new UnicodeEncoding(false, false, true);
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            preambleLength = Encoding.BigEndianUnicode.Preamble.Length;
            return new UnicodeEncoding(true, false, true);
        }

        preambleLength = 0;
        return new UTF8Encoding(false, true);
    }
}

public sealed record SubjectInputHash(string Path, string Selector, string ContentHash);
