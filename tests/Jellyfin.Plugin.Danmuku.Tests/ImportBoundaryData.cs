using System.Text;

namespace Jellyfin.Plugin.Danmuku.Tests;

/// <summary>Synthetic, disk-backed fixtures; never retain a whole large document in memory.</summary>
internal sealed class ImportBoundaryData : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "danmuku-boundary-" + Guid.NewGuid().ToString("N"));

    public ImportBoundaryData() => Directory.CreateDirectory(_directory);

    public string SourcePath => Path.Combine(_directory, "source.dat");

    public string StagedPath => Path.Combine(_directory, "staged.dat");

    public StreamWriter CreateWriter() => new(SourcePath, false, new UTF8Encoding(false), 65536);

    public void WriteEntries(bool json, int normal, int abnormal = 0)
    {
        using var writer = CreateWriter();
        writer.Write(json ? "[" : "<i>");
        for (var index = 0; index < normal + abnormal; index++)
        {
            if (json && index != 0) writer.Write(',');
            writer.Write(json
                ? index < normal ? "{\"progress\":1,\"content\":\"synthetic\"}" : "{\"content\":\"synthetic\"}"
                : index < normal ? "<d p=\"1\">synthetic</d>" : "<d p=\"\">synthetic</d>");
        }

        writer.Write(json ? "]" : "</i>");
    }

    public void WriteByteBoundary(bool json, long bytes)
    {
        // Long text in separate entries exercises bounded parsing, without exceeding the
        // JSON element budget or the source entry limit. Keep one playable entry.
        using (var writer = CreateWriter())
        {
            var prefix = json ? "[{\"progress\":1,\"content\":\"synthetic\"}" : "<i><d p=\"1\">synthetic</d>";
            var opening = json ? ",{\"progress\":2,\"content\":\"" : "<d p=\"2\">";
            var closing = json ? "\"}" : "</d>";
            var suffix = json ? "]" : "</i>";
            writer.Write(prefix);
            var remaining = bytes - prefix.Length - suffix.Length;
            while (remaining > opening.Length + closing.Length)
            {
                var length = (int)Math.Min(131072, remaining - opening.Length - closing.Length);
                writer.Write(opening);
                WriteRepeated(writer, 'x', length);
                writer.Write(closing);
                remaining -= opening.Length + length + closing.Length;
            }

            WriteRepeated(writer, ' ', remaining);
            writer.Write(suffix);
        }

        if (new FileInfo(SourcePath).Length != bytes) throw new InvalidOperationException("Fixture byte size mismatch.");
    }

    public static void WriteRepeated(TextWriter writer, char value, long count)
    {
        var chunk = new string(value, 8192);
        while (count > 0)
        {
            var length = (int)Math.Min(count, chunk.Length);
            writer.Write(chunk.AsSpan(0, length));
            count -= length;
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
