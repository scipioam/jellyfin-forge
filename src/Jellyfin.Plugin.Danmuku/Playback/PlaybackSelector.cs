using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Playback;

public sealed record PlaybackComment(string Id, long TimeMs, string Text, int Mode, int Color, int FontSize);

public static class PlaybackSelector
{
    public const string Algorithm = "m1-v1";

    public static IReadOnlyList<PlaybackComment> Select(SqliteConnection c, SqliteTransaction t, string media, string file, long? durationMs, int limit)
    {
        if (limit is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(limit));
        // Length-prefixed UTF-8 fields, then signed 64-bit little-endian integers.
        using var seedStream = new MemoryStream();
        using (var writer = new BinaryWriter(seedStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var text in new[] { Algorithm, media, file }) { var bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
            writer.Write(durationMs ?? -1); writer.Write((long)limit);
        }
        var seed = SHA256.HashData(seedStream.ToArray());
        var prefix = Fnv(14695981039346656037UL, seed);
        var buckets = new List<Bucket>();
        using (var cmd = Command(c, t, "SELECT TimeMs/60000,COUNT(*) FROM Comments WHERE FileId=$file AND ($duration IS NULL OR TimeMs<=$duration) GROUP BY TimeMs/60000 ORDER BY TimeMs/60000", ("$file", file), ("$duration", durationMs)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) buckets.Add(new(r.GetInt64(0), r.GetInt32(1), Hash(prefix, r.GetInt64(0), -1)));
        var remaining = Math.Min(limit, buckets.Sum(b => b.Count));
        while (remaining > 0)
        {
            var eligible = buckets.Where(b => b.Quota < b.Count).ToArray();
            var each = remaining / eligible.Length;
            if (each == 0)
            {
                foreach (var b in eligible.OrderBy(b => b.Hash).ThenBy(b => b.Minute).Take(remaining)) b.Quota++;
                break;
            }
            foreach (var b in eligible) { var add = Math.Min(each, b.Count - b.Quota); b.Quota += add; remaining -= add; }
        }
        var byMinute = buckets.Where(b => b.Quota > 0).ToDictionary(b => b.Minute);
        var selected = new List<Candidate>(limit);
        // Query a bucket at a time. Candidate storage is bounded by the selected count,
        // and no unselected text is read into managed memory.
        foreach (var b in byMinute.Values)
        {
            var heap = new PriorityQueue<Candidate, (ulong, long)>(Comparer<(ulong, long)>.Create((x, y) => y.CompareTo(x)));
            using var cmd = Command(c, t, "SELECT CommentId,SourceOrdinal,TimeMs FROM Comments WHERE FileId=$file AND TimeMs>=$from AND TimeMs<=$to ORDER BY TimeMs,SourceOrdinal",
                ("$file", file), ("$from", b.Minute * 60000), ("$to", Math.Min(b.Minute > (long.MaxValue - 59999) / 60000 ? long.MaxValue : b.Minute * 60000 + 59999, durationMs ?? long.MaxValue)));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var ordinal = r.GetInt64(1);
                var priority = (Hash(prefix, ordinal, id), id);
                // Read the time only for candidates which actually enter the bounded heap.
                if (heap.Count < b.Quota) heap.Enqueue(new(id, ordinal, r.GetInt64(2)), priority);
                else if (heap.TryPeek(out _, out var worst) && priority.CompareTo(worst) < 0)
                    heap.EnqueueDequeue(new(id, ordinal, r.GetInt64(2)), priority);
            }
            selected.AddRange(heap.UnorderedItems.Select(x => x.Element));
        }
        var ordered = selected.OrderBy(i => i.Time).ThenBy(i => i.Ordinal).ThenBy(i => i.Id).ToArray();
        var positions = ordered.Select((item, index) => (item.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var comments = new PlaybackComment[ordered.Length];
        var read = 0;
        // Fetch only selected text, in bounded batches instead of one SQLite command per comment.
        foreach (var batch in ordered.Chunk(500))
        {
            using var get = c.CreateCommand();
            get.Transaction = t;
            // These are typed Int64 database keys, never user-supplied SQL fragments.
            // Numeric literals avoid the quadratic named-parameter binding cost of a large IN list.
            get.CommandText = "SELECT CommentId,Text,Mode,Color,FontSize FROM Comments WHERE CommentId IN ("
                + string.Join(',', batch.Select(item => item.Id.ToString(CultureInfo.InvariantCulture))) + ")";
            using var r = get.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var index = positions[id];
                comments[index] = new(id.ToString(CultureInfo.InvariantCulture), ordered[index].Time, r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
                read++;
            }
        }
        if (read != comments.Length) throw new InvalidOperationException("Selected comment disappeared inside the read snapshot.");
        return comments;
    }

    private static ulong Fnv(ulong hash, ReadOnlySpan<byte> input)
    {
        foreach (var value in input) hash = unchecked((hash ^ value) * 1099511628211UL);
        return hash;
    }

    private static ulong Hash(ulong prefix, long first, long second)
    {
        // Stable sampling only, not a security primitive. SHA-256
        // derives the seed; original-content and authentication-session hashes also remain SHA-256.
        Span<byte> input = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(input, first);
        BinaryPrimitives.WriteInt64LittleEndian(input[8..], second);
        var hash = Fnv(prefix, input);
        // MurmurHash3 fmix64 finalizer by Austin Appleby (public domain):
        // https://github.com/aappleby/smhasher/blob/master/src/MurmurHash3.cpp
        hash ^= hash >> 33;
        hash = unchecked(hash * 0xff51afd7ed558ccdUL);
        hash ^= hash >> 33;
        hash = unchecked(hash * 0xc4ceb9fe1a85ec53UL);
        return hash ^ (hash >> 33);
    }
    private sealed class Bucket(long minute, int count, ulong hash)
    {
        public long Minute { get; } = minute;
        public int Count { get; } = count;
        public ulong Hash { get; } = hash;
        public int Quota { get; set; }
    }
    private readonly record struct Candidate(long Id, long Ordinal, long Time);
}
