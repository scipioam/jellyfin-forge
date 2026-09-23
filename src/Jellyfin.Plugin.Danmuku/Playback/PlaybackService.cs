using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Danmuku.Configuration;
using Jellyfin.Plugin.Danmuku.Storage;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Playback;

public sealed record PlaybackIdentity(string UserId, string SessionHash, string MediaId, long? DurationMs);
public sealed record RenderLimits(int Low, int Medium, int High, int Overlap);
public sealed record PlaybackDisplay(string RenderVersion, RenderLimits Limits);
public sealed record PlaybackPayload(string Status, string MediaId, string? FileId, string AlgorithmVersion, int LoadLimit,
    int SelectedCount, PlaybackDisplay Display, long ExpiresAtUtcMs, IReadOnlyList<PlaybackComment> Items, string SourceKind = "file", string? PlanId = null, long? PlanVersion = null, long? CandidateCount = null, long? ScannedCandidates = null);
public sealed record PlaybackBudgets(long Bytes = 256L * 1024 * 1024, int Collections = 128, int Requests = 100000);
public interface IPlaybackSessionLookup { Task<bool> IsActiveAsync(string userId, string sessionHash); }

/// <summary>One bounded creation at a time; retry bodies live on disk, never in a process-wide object cache.</summary>
public sealed class PlaybackService(ISqliteConnectionFactory factory, ISqliteWriteCoordinator writes, DanmukuDataPaths paths,
    TimeProvider clock, IPlaybackSessionLookup sessions, PlaybackBudgets budgets) : IDisposable
{
    public const string RenderVersion = "m2-speed-v1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _live = new(StringComparer.Ordinal);
    private bool _initialized;
    private string _pruneUser = "", _pruneSession = "";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string SessionHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<Stream> GetAsync(string id, PlaybackIdentity identity, PluginConfiguration config, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(id, "N", out _) && !Guid.TryParseExact(id, "D", out _)) throw new ImportOperationException("InvalidPlaybackId", 422, "Use a new UUID for each playback.");
        if (!config.WebEnabled) return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { status = "Disabled", items = Array.Empty<object>() }, Json));
        config.Validate();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                // Old metadata deliberately survives restart. No old identifier can create a replacement collection.
                foreach (var file in Directory.EnumerateFiles(paths.PlaybackCachePath, "*.json")) System.IO.File.Delete(file);
                await writes.EnqueueAsync((db, _) => { Execute(db, null, "UPDATE PlaybackRequests SET Status='Expired' WHERE Status='Active'"); return Task.CompletedTask; }, ct).ConfigureAwait(false);
                _initialized = true;
            }
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var expiredIds = _live.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToArray();
            foreach (var expired in expiredIds)
            { System.IO.File.Delete(_live[expired].Path); _live.Remove(expired); }
            if (expiredIds.Length > 0)
                await writes.EnqueueAsync((db, _) => { Execute(db, null, "UPDATE PlaybackRequests SET Status='Expired' WHERE Status='Active' AND ExpiresAtUtcMs<=$now", ("$now", now)); return Task.CompletedTask; }, ct).ConfigureAwait(false);
            using (var c = factory.CreateOpenConnection())
            using (var cmd = Command(c, null, "SELECT UserId,SessionHash,MediaId FROM PlaybackRequests WHERE PlaybackId=$id", ("$id", id)))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    if (r.GetString(0) != identity.UserId || r.GetString(1) != identity.SessionHash || r.GetString(2) != identity.MediaId)
                        throw new ImportOperationException("PlaybackIdConflict", 409, "This identifier belongs to another playback.");
                    if (!_live.TryGetValue(id, out var cached) || !System.IO.File.Exists(cached.Path)) throw new ImportOperationException("PlaybackExpired", 410, "Re-enter playback to load a new collection.");
                    return Open(cached.Path);
                }
            }
            if (_live.Count >= budgets.Collections) throw Capacity();
            // Reclaim only metadata whose authentication session is reliably invalid.
            await PruneInactiveAsync(ct).ConfigureAwait(false);
            var available = budgets.Bytes - _live.Values.Sum(e => e.Bytes);
            var path = Path.Combine(paths.PlaybackCachePath, Guid.NewGuid().ToString("N") + ".json");
            long expiry = 0;
            try
            {
                await writes.EnqueueAsync(async (c, token) =>
                {
                    using var t = c.BeginTransaction();
                    if (Long(c, t, "SELECT COUNT(*) FROM PlaybackRequests") >= budgets.Requests) throw Capacity();
                    var file = Text(c, t, "SELECT f.FileId FROM MediaState m JOIN Files f ON f.FileId=m.ActiveFileId WHERE m.MediaId=$media AND m.IsDeactivated=0 AND f.Status='Published'", ("$media", identity.MediaId));
                    var planId = Text(c, t, "SELECT ActivePlanId FROM MediaState WHERE MediaId=$media AND IsDeactivated=0", ("$media", identity.MediaId));
                    var plan = planId is null ? null : CombinePlanService.Read(c, t, identity.MediaId, planId);
                    var limit = config.LoadLimit(identity.DurationMs);
                    var combined = plan is null ? null : CombinePlaybackSelector.Select(c, t, plan, identity.DurationMs, limit, token);
                    var items = combined?.Items ?? (file is null ? Array.Empty<PlaybackComment>() : PlaybackSelector.Select(c, t, identity.MediaId, file, identity.DurationMs, limit));
                    var algorithm = plan is null ? PlaybackSelector.Algorithm : CombinePlaybackSelector.Algorithm;
                    var sourceKind = plan is not null ? "plan" : file is not null ? "file" : "disabled";
                    var created = clock.GetUtcNow().ToUnixTimeMilliseconds();
                    expiry = created + 600000;
                    var payload = new PlaybackPayload(items.Count == 0 ? "Empty" : "Ready", identity.MediaId, file, algorithm, limit, items.Count,
                        new(RenderVersion, new(config.LowRenderLimit, config.MediumRenderLimit, config.HighRenderLimit, config.OverlapRenderLimit)), expiry, items, sourceKind, plan?.PlanId, plan?.Version, combined?.CandidateCount, combined?.ScannedCandidates);
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                        await JsonSerializer.SerializeAsync(output, payload, Json, token).ConfigureAwait(false);
                    if (new FileInfo(path).Length > available) throw Capacity();
                    Execute(c, t, """
                        INSERT INTO PlaybackRequests(PlaybackId,SessionHash,UserId,MediaId,FileId,AlgorithmVersion,LoadLimit,ConfigSnapshot,Status,CreatedAtUtcMs,ExpiresAtUtcMs,SourceKind,PlanId,PlanVersion)
                        VALUES($id,$session,$user,$media,$file,$algorithm,$limit,$config,'Active',$now,$expires,$kind,$plan,$planVersion)
                        """, ("$id", id), ("$session", identity.SessionHash), ("$user", identity.UserId), ("$media", identity.MediaId), ("$file", file),
                        ("$algorithm", algorithm), ("$kind", sourceKind), ("$plan", plan?.PlanId), ("$planVersion", plan?.Version), ("$limit", limit), ("$config", JsonSerializer.Serialize(payload.Display, Json)), ("$now", created), ("$expires", expiry));
                    t.Commit();
                }, ct).ConfigureAwait(false);
                _live.Add(id, new(path, new FileInfo(path).Length, expiry));
                return Open(path);
            }
            catch { System.IO.File.Delete(path); throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task PruneInactiveAsync(CancellationToken ct)
    {
        // Bounded page; invoked only near the metadata cap, avoiding an authentication query on every new playback.
        using var c = factory.CreateOpenConnection();
        if (Long(c, null, "SELECT COUNT(*) FROM PlaybackRequests") < budgets.Requests) return;
        var candidates = new List<(string User, string Hash)>();
        using (var cmd = Command(c, null, "SELECT UserId,SessionHash FROM PlaybackRequests WHERE UserId>$user OR (UserId=$user AND SessionHash>$session) GROUP BY UserId,SessionHash ORDER BY UserId,SessionHash LIMIT 100", ("$user", _pruneUser), ("$session", _pruneSession)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) candidates.Add((r.GetString(0), r.GetString(1)));
        // Continue on later requests instead of repeatedly scanning the same active sessions.
        (_pruneUser, _pruneSession) = candidates.Count == 100 ? candidates[^1] : ("", "");
        foreach (var item in candidates)
            if (!await sessions.IsActiveAsync(item.User, item.Hash).ConfigureAwait(false))
                await writes.EnqueueAsync((db, _) => { Execute(db, null, "DELETE FROM PlaybackRequests WHERE UserId=$user AND SessionHash=$hash", ("$user", item.User), ("$hash", item.Hash)); return Task.CompletedTask; }, ct).ConfigureAwait(false);
    }
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static ImportOperationException Capacity() => new("PlaybackCapacity", 503, "Playback cache capacity is temporarily exhausted; retry later.");
    private sealed record CacheEntry(string Path, long Bytes, long Expires);
    public void Dispose() => _gate.Dispose();
}
