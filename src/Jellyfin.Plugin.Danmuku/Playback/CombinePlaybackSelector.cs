using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Playback;

public sealed record CombineSelection(IReadOnlyList<PlaybackComment> Items, long CandidateCount, long ScannedCandidates);

/// <summary>Two numeric streaming scans; heap capacity never exceeds the final load limit.</summary>
public static class CombinePlaybackSelector
{
    public const string Algorithm = "m2-combine-v1";

    public static CombineSelection Select(SqliteConnection c, SqliteTransaction t, CombinePlan plan,
        long? durationMs, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(limit));
        CombinePlanService.ValidateSegments(plan.Segments);
        foreach (var file in plan.Segments.Select(r => r.FileId).Distinct(StringComparer.Ordinal))
            if (Text(c, t, "SELECT Status FROM Files WHERE FileId=$id", ("$id", file)) != "Published")
                throw new ImportOperationException("SourceUnavailable", 409, "A combine source is unavailable.");
        using var seedStream = new MemoryStream();
        using (var writer = new BinaryWriter(seedStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { Algorithm, plan.MediaId, plan.PlanId })
            { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            writer.Write(plan.Version); writer.Write(durationMs ?? -1); writer.Write((long)limit);
        }
        var prefix = PlaybackSelector.Fnv(14695981039346656037UL, SHA256.HashData(seedStream.ToArray()));
        var buckets = new Dictionary<long, Bucket>();
        long scanned = 0, count = 0;
        Scan(candidate =>
        {
            count++;
            var minute = candidate.Time / 60000;
            if (!buckets.TryGetValue(minute, out var bucket))
                buckets.Add(minute, bucket = new(minute, PlaybackSelector.Hash(prefix, minute, -1)));
            bucket.Count++;
        });
        var remaining = (int)Math.Min(limit, count);
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var eligible = buckets.Values.Where(b => b.Quota < b.Count).ToArray();
            var each = remaining / eligible.Length;
            if (each == 0)
            {
                foreach (var b in eligible.OrderBy(b => b.Hash).ThenBy(b => b.Minute).Take(remaining)) b.Quota++;
                break;
            }
            foreach (var b in eligible) { var add = Math.Min(each, b.Count - b.Quota); b.Quota += add; remaining -= add; }
        }
        // Drop zero-quota buckets before the second scan; text is still not resident.
        var selectedBuckets = buckets.Values.Where(b => b.Quota > 0).ToDictionary(b => b.Minute);
        buckets.Clear();
        foreach (var b in selectedBuckets.Values)
            b.Heap = new PriorityQueue<Candidate, (ulong, int, long)>(Comparer<(ulong, int, long)>.Create((x, y) => y.CompareTo(x)));
        Scan(candidate =>
        {
            if (!selectedBuckets.TryGetValue(candidate.Time / 60000, out var b)) return;
            var priority = (PlaybackSelector.Hash(PlaybackSelector.Hash(prefix, candidate.Row, candidate.Ordinal), candidate.Id, candidate.Time), candidate.Row, candidate.Id);
            if (b.Heap!.Count < b.Quota) b.Heap.Enqueue(candidate, priority);
            else if (b.Heap.TryPeek(out _, out var worst) && priority.CompareTo(worst) < 0) b.Heap.EnqueueDequeue(candidate, priority);
        });
        var ordered = selectedBuckets.Values.SelectMany(b => b.Heap!.UnorderedItems.Select(i => i.Element))
            .OrderBy(i => i.Time).ThenBy(i => i.Row).ThenBy(i => i.Ordinal).ThenBy(i => i.Id).ToArray();
        var texts = new Dictionary<long, PlaybackComment>();
        foreach (var batch in ordered.Select(i => i.Id).Distinct().Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = Command(c, t, "SELECT CommentId,Text,Mode,Color,FontSize FROM Comments WHERE CommentId IN (" +
                string.Join(',', batch.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ")");
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) texts.Add(reader.GetInt64(0), new("", 0, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        }
        var items = ordered.Select(i => texts.TryGetValue(i.Id, out var text)
            ? text with { Id = FormattableString.Invariant($"{i.Row}:{i.Id}"), TimeMs = i.Time }
            : throw new InvalidOperationException("Selected source data disappeared inside the snapshot.")).ToArray();
        return new(items, count, scanned);

        void Scan(Action<Candidate> visit)
        {
            // One bounded UNION of at most 50 parameterized ranges per scan, never one scan per minute.
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            var clauses = new List<string>();
            for (var i = 0; i < plan.Segments.Count; i++)
            {
                var row = plan.Segments[i];
                clauses.Add($"SELECT {i} AS SegmentOrdinal,CommentId,SourceOrdinal,TimeMs FROM Comments WHERE FileId=$file{i} AND TimeMs >= $start{i} AND ($end{i} IS NULL OR TimeMs < $end{i})");
                cmd.Parameters.AddWithValue($"$file{i}", row.FileId);
                cmd.Parameters.AddWithValue($"$start{i}", row.SourceStartMs);
                cmd.Parameters.AddWithValue($"$end{i}", (object?)row.SourceEndMs ?? DBNull.Value);
            }
            cmd.CommandText = string.Join(" UNION ALL ", clauses);
            using var cancellation = ct.Register(cmd.Cancel);
            using var reader = cmd.ExecuteReader();
            long passCount = 0;
            while (reader.Read())
            {
                if ((passCount++ & 1023) == 0) ct.ThrowIfCancellationRequested();
                scanned++;
                if (passCount > 1_000_000) throw new ImportOperationException("CommentLimit", 422, "The plan exceeds its candidate limit.");
                var row = reader.GetInt32(0);
                var time = CombinePlanService.Map(plan.Segments[row], reader.GetInt64(3));
                if (durationMs is null || time <= durationMs)
                    visit(new(reader.GetInt64(1), reader.GetInt64(2), row, time));
            }
        }
    }

    private sealed class Bucket(long minute, ulong hash)
    {
        public long Minute { get; } = minute;
        public ulong Hash { get; } = hash;
        public int Count { get; set; }
        public int Quota { get; set; }
        public PriorityQueue<Candidate, (ulong, int, long)>? Heap { get; set; }
    }
    private readonly record struct Candidate(long Id, long Ordinal, int Row, long Time);
}
