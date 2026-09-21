using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Data.Sqlite;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Storage;

public enum MediaPresence { Exists, Missing, CheckFailed }

public interface IMediaPresenceLookup
{
    Task<MediaPresence> CheckAsync(string mediaId, CancellationToken cancellationToken = default);
}

public sealed class JellyfinMediaPresenceLookup(ILibraryManager library) : IMediaPresenceLookup
{
    public Task<MediaPresence> CheckAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(mediaId, out var id)) return Task.FromResult(MediaPresence.Missing);
        try
        {
            return Task.FromResult(library.GetItemById(id) is Movie or Episode ? MediaPresence.Exists : MediaPresence.Missing);
        }
        catch (Exception) { return Task.FromResult(MediaPresence.CheckFailed); }
    }
}

public sealed record MediaBindingSnapshot(string MediaId, long Version, string? ActiveFileId, bool IsDeactivated,
    IReadOnlyList<string> FileIds, string CheckStatus);

public sealed record BindingCheckJob(string JobId, string Status, long Processed, long Missing, long Failed);

public sealed class MediaBindingService(ISqliteConnectionFactory factory, ISqliteWriteCoordinator writes,
    IMediaPresenceLookup lookup, TimeProvider clock)
{
    public async Task<MediaPresence> CheckAsync(string mediaId, CancellationToken ct = default)
    {
        var result = await lookup.CheckAsync(mediaId, ct).ConfigureAwait(false);
        await writes.EnqueueAsync((c, _) =>
        {
            Execute(c, null, """
                INSERT INTO MediaState(MediaId,CheckStatus,CheckedAtUtcMs) VALUES($id,$status,$now)
                ON CONFLICT(MediaId) DO UPDATE SET CheckStatus=excluded.CheckStatus,CheckedAtUtcMs=excluded.CheckedAtUtcMs;
                """, ("$id", mediaId), ("$status", result.ToString()), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        return result;
    }

    public async Task RequireExistingAsync(string mediaId, CancellationToken ct = default)
    {
        var result = await CheckAsync(mediaId, ct).ConfigureAwait(false);
        if (result != MediaPresence.Exists)
            throw new ImportOperationException(result == MediaPresence.Missing ? "MediaMissing" : "MediaCheckFailed",
                result == MediaPresence.Missing ? 404 : 503, "The target media is missing or could not be checked.");
    }

    public async Task<MediaBindingSnapshot> ReadAsync(string mediaId, CancellationToken ct = default)
    {
        await CheckAsync(mediaId, ct).ConfigureAwait(false);
        using var c = factory.CreateOpenConnection();
        return Read(c, null, mediaId);
    }

    public async Task<MediaBindingSnapshot> UpdateAsync(string mediaId, long expectedVersion,
        IReadOnlyList<string> fileIds, string? activeFileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Distinct(StringComparer.Ordinal).Count() != fileIds.Count || (activeFileId is not null && !fileIds.Contains(activeFileId)))
            throw new ImportOperationException("InvalidBindings", 422, "Bindings must be unique and include the active file.");
        var presence = await CheckAsync(mediaId, ct).ConfigureAwait(false);
        return await writes.EnqueueAsync((c, _) =>
        {
            using var t = c.BeginTransaction();
            var before = Read(c, t, mediaId);
            RequireVersion(c, t, mediaId, expectedVersion);
            if (presence != MediaPresence.Exists && (activeFileId is not null || fileIds.Except(before.FileIds).Any()))
                throw new ImportOperationException("MediaUnavailable", presence == MediaPresence.Missing ? 404 : 503, "Cannot bind or activate unavailable media.");
            foreach (var id in fileIds)
            {
                if (Text(c, t, "SELECT Status FROM Files WHERE FileId=$id", ("$id", id)) != "Published")
                    throw new ImportOperationException("FileUnavailable", 409, "Only published files can be bound.");
                Execute(c, t, "INSERT OR IGNORE INTO MediaBindings VALUES($media,$file,$now)",
                    ("$media", mediaId), ("$file", id), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
            }

            Execute(c, t, """
                UPDATE MediaState SET ActiveFileId=$active, IsDeactivated=$off, Version=Version+1
                WHERE MediaId=$media AND Version=$version
                """, ("$active", activeFileId), ("$off", activeFileId is null ? 1 : 0), ("$media", mediaId), ("$version", expectedVersion));
            foreach (var id in before.FileIds.Except(fileIds))
                Execute(c, t, "DELETE FROM MediaBindings WHERE MediaId=$media AND FileId=$file", ("$media", mediaId), ("$file", id));
            var result = Read(c, t, mediaId);
            t.Commit();
            return Task.FromResult(result);
        }, ct).ConfigureAwait(false);
    }

    internal static void RequireVersion(SqliteConnection c, SqliteTransaction t, string mediaId, long expected)
    {
        if (expected < 0 || Long(c, t, "SELECT COALESCE((SELECT Version FROM MediaState WHERE MediaId=$id),0)", ("$id", mediaId)) != expected)
            throw new ImportOperationException("MediaVersionConflict", 409, "Refresh the media bindings before continuing.");
    }

    internal static MediaBindingSnapshot Read(SqliteConnection c, SqliteTransaction? t, string mediaId)
    {
        var files = new List<string>();
        using (var cmd = Command(c, t, "SELECT FileId FROM MediaBindings WHERE MediaId=$id ORDER BY FileId", ("$id", mediaId)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) files.Add(r.GetString(0));
        using var command = Command(c, t, "SELECT Version,ActiveFileId,IsDeactivated,CheckStatus FROM MediaState WHERE MediaId=$id", ("$id", mediaId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(mediaId, reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2), files, reader.GetString(3))
            : new(mediaId, 0, null, false, files, "Unchecked");
    }

    public Task<string> StartCheckAllAsync(CancellationToken ct = default) => writes.EnqueueAsync((c, _) =>
    {
        var active = Text(c, null, "SELECT JobId FROM BindingCheckJobs WHERE Status IN ('Queued','Processing')");
        if (active is not null) return Task.FromResult(active);
        var id = Guid.NewGuid().ToString("N");
        Execute(c, null, "INSERT INTO BindingCheckJobs(JobId,Status,CreatedAtUtcMs) VALUES($id,'Queued',$now)",
            ("$id", id), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        return Task.FromResult(id);
    }, ct);

    public BindingCheckJob GetCheckJob(string id)
    {
        using var c = factory.CreateOpenConnection();
        using var cmd = Command(c, null, "SELECT Status,Processed,Missing,Failed FROM BindingCheckJobs WHERE JobId=$id", ("$id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Binding check job not found.");
        return new(id, r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    public async Task ProcessCheckJobAsync(CancellationToken ct = default)
    {
        var id = await writes.EnqueueAsync((c, _) =>
        {
            var job = Text(c, null, "SELECT JobId FROM BindingCheckJobs WHERE Status='Queued' LIMIT 1");
            if (job is not null) Execute(c, null, "UPDATE BindingCheckJobs SET Status='Processing' WHERE JobId=$id", ("$id", job));
            return Task.FromResult(job);
        }, ct).ConfigureAwait(false);
        if (id is null) return;
        string cursor = "";
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? media;
                using (var c = factory.CreateOpenConnection())
                    media = Text(c, null, "SELECT MediaId FROM MediaBindings WHERE MediaId>$cursor ORDER BY MediaId LIMIT 1", ("$cursor", cursor));
                if (media is null) break;
                cursor = media;
                var presence = await CheckAsync(media, ct).ConfigureAwait(false);
                await writes.EnqueueAsync((c, _) =>
                {
                    Execute(c, null, "UPDATE BindingCheckJobs SET Processed=Processed+1,Missing=Missing+$missing,Failed=Failed+$failed WHERE JobId=$id",
                        ("$missing", presence == MediaPresence.Missing ? 1 : 0), ("$failed", presence == MediaPresence.CheckFailed ? 1 : 0), ("$id", id));
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }
            await FinishCheckAsync(id, "Completed").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { await FinishCheckAsync(id, "Interrupted").ConfigureAwait(false); throw; }
        catch { await FinishCheckAsync(id, "Failed").ConfigureAwait(false); throw; }
    }

    private Task FinishCheckAsync(string id, string status) => writes.EnqueueAsync((c, _) =>
    {
        Execute(c, null, "UPDATE BindingCheckJobs SET Status=$status,FinishedAtUtcMs=$now WHERE JobId=$id",
            ("$status", status), ("$id", id), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        return Task.CompletedTask;
    });
}
