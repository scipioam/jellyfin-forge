using System.Text;
using Microsoft.Data.Sqlite;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>Bounded plan operations share the import/delete write queue and commit atomically.</summary>
public sealed class CombinePlanService(ISqliteConnectionFactory factory, ISqliteWriteCoordinator writes,
    MediaBindingService bindings, TimeProvider clock)
{
    public const long MaximumTimeMs = 9_007_199_254_740_991;

    public IReadOnlyList<CombinePlan> List(string mediaId)
    {
        using var c = factory.CreateOpenConnection();
        using var t = c.BeginTransaction(deferred: true);
        var ids = new List<string>();
        using (var cmd = Command(c, t, "SELECT PlanId FROM CombinePlans WHERE MediaId=$media ORDER BY Name COLLATE BINARY", ("$media", mediaId)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        return ids.Select(id => Read(c, t, mediaId, id)).ToArray();
    }

    public CombinePlan Get(string mediaId, string planId)
    {
        using var c = factory.CreateOpenConnection();
        using var t = c.BeginTransaction(deferred: true);
        return Read(c, t, mediaId, planId);
    }

    public string NextName(string mediaId)
    {
        var names = List(mediaId).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++) if (!names.Contains($"combine-{i}")) return $"combine-{i}";
    }

    public async Task<CombinePreview> PreviewAsync(string mediaId, IReadOnlyList<CombineSegment> segments,
        long? durationMs, CancellationToken ct = default)
    {
        ValidateSegments(segments);
        await bindings.RequireExistingAsync(mediaId, ct).ConfigureAwait(false);
        return await writes.EnqueueAsync((c, token) =>
        {
            using var t = c.BeginTransaction(deferred: true);
            return Task.FromResult(Statistics(c, t, segments, durationMs, token));
        }, ct).ConfigureAwait(false);
    }

    public async Task<CombinePlan> SaveAsync(string mediaId, string planId, string name,
        IReadOnlyList<CombineSegment> segments, long? expectedPlanVersion, bool activate = false,
        long? expectedMediaVersion = null, CancellationToken ct = default)
    {
        if (!Guid.TryParse(planId, out var guid)) throw Invalid("InvalidPlanId", "Plan ID must be a UUID.");
        planId = guid.ToString("N");
        name = name?.Trim() ?? "";
        if (name.EnumerateRunes().Count() is < 1 or > 100 || name.Contains('\0'))
            throw Invalid("InvalidPlanName", "Name must contain 1–100 Unicode code points.");
        ValidateSegments(segments);
        // Freeze caller-owned collections before entering asynchronous work.
        segments = segments.ToArray();
        await bindings.RequireExistingAsync(mediaId, ct).ConfigureAwait(false);
        return await writes.EnqueueAsync((c, token) =>
        {
            using var t = c.BeginTransaction();
            Execute(c, t, "INSERT OR IGNORE INTO MediaState(MediaId) VALUES($media)", ("$media", mediaId));
            var exists = Long(c, t, "SELECT COUNT(*) FROM CombinePlans WHERE PlanId=$id", ("$id", planId)) != 0;
            CombinePlan? previous = null;
            if (expectedPlanVersion is null)
            {
                if (exists) throw Conflict("PlanIdConflict", "This plan ID already exists; read it before retrying.");
                if (Long(c, t, "SELECT COUNT(*) FROM CombinePlans WHERE MediaId=$media", ("$media", mediaId)) >= 20)
                    throw Invalid("PlanLimit", "A media item can have at most 20 plans.");
            }
            else
            {
                previous = Read(c, t, mediaId, planId);
                RequirePlanVersion(previous.Version, expectedPlanVersion);
            }
            if (activate)
            {
                if (expectedMediaVersion is null) throw Invalid("ExpectedMediaVersionRequired", "Activating requires a media version.");
                MediaBindingService.RequireVersion(c, t, mediaId, expectedMediaVersion.Value);
            }
            if (Long(c, t, "SELECT COUNT(*) FROM CombinePlans WHERE MediaId=$media AND Name=$name COLLATE BINARY AND PlanId<>$id",
                ("$media", mediaId), ("$name", name), ("$id", planId)) != 0)
                throw Conflict("PlanNameConflict", "The name is already used; keep your input and choose another name.");
            var preview = Statistics(c, t, segments, null, token);
            if (preview.TotalCount == 0) throw Invalid("EmptyPlan", "At least one comment must be selected.");
            var version = checked((previous?.Version ?? 0) + 1);
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            Execute(c, t, """
                INSERT INTO CombinePlans(PlanId,MediaId,Name,Version,CreatedAtUtcMs,UpdatedAtUtcMs)
                VALUES($id,$media,$name,$version,$now,$now)
                ON CONFLICT(PlanId) DO UPDATE SET Name=excluded.Name,Version=excluded.Version,UpdatedAtUtcMs=excluded.UpdatedAtUtcMs;
                """, ("$id", planId), ("$media", mediaId), ("$name", name), ("$version", version), ("$now", now));
            Execute(c, t, "DELETE FROM CombineSegments WHERE PlanId=$id", ("$id", planId));
            for (var i = 0; i < segments.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = segments[i];
                Execute(c, t, "INSERT INTO CombineSegments VALUES($id,$ordinal,$file,$start,$end,$target)",
                    ("$id", planId), ("$ordinal", i), ("$file", row.FileId), ("$start", row.SourceStartMs),
                    ("$end", row.SourceEndMs), ("$target", row.TargetStartMs));
            }
            if (activate)
                Execute(c, t, "UPDATE MediaState SET ActiveFileId=NULL,ActivePlanId=$id,IsDeactivated=0,Version=Version+1 WHERE MediaId=$media",
                    ("$id", planId), ("$media", mediaId));
            else
                Execute(c, t, "UPDATE MediaState SET Version=Version+1 WHERE MediaId=$media AND ActivePlanId=$id", ("$id", planId), ("$media", mediaId));
            t.Commit();
            return Task.FromResult(new CombinePlan(planId, mediaId, name, version, segments));
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string mediaId, string planId, long expectedPlanVersion, long expectedMediaVersion,
        MediaSelection? replacement = null, CancellationToken ct = default)
    {
        var presence = await bindings.CheckAsync(mediaId, ct).ConfigureAwait(false);
        await writes.EnqueueAsync((c, token) =>
        {
            using var t = c.BeginTransaction();
            var plan = Read(c, t, mediaId, planId);
            RequirePlanVersion(plan.Version, expectedPlanVersion);
            MediaBindingService.RequireVersion(c, t, mediaId, expectedMediaVersion);
            var state = MediaBindingService.Read(c, t, mediaId);
            if (state.ActivePlanId == planId)
            {
                if (replacement is null || (replacement.Kind == "plan" && replacement.Id == planId))
                    throw Invalid("ReplacementRequired", "Choose another selection or explicitly disable danmuku.");
                MediaBindingService.ApplySelection(c, t, mediaId, state.FileIds, replacement, presence);
                Execute(c, t, "UPDATE MediaState SET Version=Version+1 WHERE MediaId=$media", ("$media", mediaId));
            }
            else if (replacement is not null) throw Invalid("UnexpectedReplacement", "Deleting an inactive plan must preserve the current selection.");
            Execute(c, t, "DELETE FROM CombinePlans WHERE PlanId=$id AND MediaId=$media", ("$id", planId), ("$media", mediaId));
            token.ThrowIfCancellationRequested();
            t.Commit();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    internal static CombinePlan Read(SqliteConnection c, SqliteTransaction? t, string mediaId, string planId)
    {
        string name; long version;
        using (var cmd = Command(c, t, "SELECT Name,Version FROM CombinePlans WHERE PlanId=$id AND MediaId=$media", ("$id", planId), ("$media", mediaId)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new KeyNotFoundException("Plan not found.");
            name = r.GetString(0); version = r.GetInt64(1);
        }
        var rows = new List<CombineSegment>();
        using (var cmd = Command(c, t, "SELECT FileId,SourceStartMs,SourceEndMs,TargetStartMs FROM CombineSegments WHERE PlanId=$id ORDER BY Ordinal", ("$id", planId)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(new(r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetInt64(3)));
        return new(planId, mediaId, name, version, rows);
    }

    internal static void RequirePlanVersion(long actual, long? expected)
    {
        if (expected is null || expected < 0 || actual != expected)
            throw Conflict("PlanVersionConflict", "The plan changed; read the latest version before continuing.");
    }

    internal static void ValidateSegments(IReadOnlyList<CombineSegment> segments)
    {
        if (segments is null || segments.Count is < 1 or > 50) throw Invalid("SegmentLimit", "A plan needs 1–50 segments.");
        var unique = new HashSet<CombineSegment>();
        foreach (var row in segments)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.FileId)) throw Invalid("InvalidSegment", "A source file is required.");
            if (row.SourceStartMs is < 0 or > MaximumTimeMs || row.TargetStartMs is < 0 or > MaximumTimeMs ||
                row.SourceEndMs is < 0 or > MaximumTimeMs || row.SourceEndMs <= row.SourceStartMs)
                throw Invalid("InvalidTime", "Times must be safe integer milliseconds with end after start.");
            if (row.SourceEndMs is long end) _ = Map(row, end);
            if (!unique.Add(row)) throw Invalid("DuplicateSegment", "Remove identical segment rows.");
        }
    }

    internal static long Map(CombineSegment row, long source)
    {
        if (source is < 0 or > MaximumTimeMs) throw Invalid("InvalidTime", "Selected source time exceeds exact integer range.");
        var mapped = checked(row.TargetStartMs + (source - row.SourceStartMs));
        if (mapped is < 0 or > MaximumTimeMs) throw Invalid("InvalidTime", "Mapped time exceeds exact integer range.");
        return mapped;
    }

    private static CombinePreview Statistics(SqliteConnection c, SqliteTransaction t,
        IReadOnlyList<CombineSegment> rows, long? durationMs, CancellationToken ct)
    {
        var results = new List<CombineSegmentStatistics>();
        long total = 0, outside = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (Text(c, t, "SELECT Status FROM Files WHERE FileId=$id", ("$id", row.FileId)) != "Published")
                throw Conflict("SourceUnavailable", "Every source must be a published file.");
            using var cmd = Command(c, t, """
                SELECT COUNT(*),MIN(TimeMs),MAX(TimeMs),COALESCE(SUM(CASE WHEN $duration IS NOT NULL
                    AND TimeMs - $start > $duration - $target THEN 1 ELSE 0 END),0)
                FROM Comments WHERE FileId=$file AND TimeMs >= $start AND ($end IS NULL OR TimeMs < $end)
                """, ("$file", row.FileId), ("$start", row.SourceStartMs), ("$end", row.SourceEndMs),
                ("$target", row.TargetStartMs), ("$duration", durationMs));
            using var cancellation = ct.Register(cmd.Cancel);
            using var r = cmd.ExecuteReader(); r.Read();
            var count = r.GetInt64(0);
            total = checked(total + count);
            if (total > 1_000_000) throw Invalid("CommentLimit", "A plan can select at most 1,000,000 comments, including duplicates.");
            long? first = count == 0 ? null : Map(row, r.GetInt64(1));
            long? last = count == 0 ? null : Map(row, r.GetInt64(2));
            var beyond = r.GetInt64(3); outside += beyond;
            results.Add(new(count, first, last, row.SourceEndMs is long end ? Map(row, end) : null, beyond));
        }
        ct.ThrowIfCancellationRequested();
        return new(results, total, outside, durationMs is null);
    }

    private static ImportOperationException Invalid(string code, string message) => new(code, 422, message);
    private static ImportOperationException Conflict(string code, string message) => new(code, 409, message);
}
