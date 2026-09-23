using Jellyfin.Plugin.Danmuku.Storage;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Api;

public sealed record Page<T>(IReadOnlyList<T> Items, long TotalCount, int StartIndex, int Limit);

/// <summary>Only fixed projections are exposed; staging paths and normal comment bodies never leave this service.</summary>
public sealed class ManagementQueries(ISqliteConnectionFactory factory, IPublishFileStore files)
{
    public static void ValidatePage(int startIndex, int limit)
    {
        if (startIndex < 0 || limit is < 1 or > 100)
            throw new ImportOperationException("InvalidPagination", 422, "startIndex must be nonnegative and limit must be 1–100.");
    }

    public Page<Dictionary<string, object?>> Files(string? search, bool unbound, int start, int limit)
    {
        var result = Query(
        "Files f", "f.FileId,f.OriginalFileName,f.StoredFileName,f.DisplayName,f.Format,f.ContentHash,f.ImportedAtUtcMs,f.CommentCount,f.LastCommentTimeMs,f.ParseDataVersion,f.Status,(SELECT COUNT(*) FROM MediaBindings b WHERE b.FileId=f.FileId) AS BindingCount,(SELECT COUNT(DISTINCT s.PlanId) FROM CombineSegments s WHERE s.FileId=f.FileId) AS PlanReferenceCount",
        "($search IS NULL OR instr(lower(f.StoredFileName),lower($search))>0) AND ($unbound=0 OR (NOT EXISTS(SELECT 1 FROM MediaBindings b WHERE b.FileId=f.FileId) AND NOT EXISTS(SELECT 1 FROM CombineSegments s WHERE s.FileId=f.FileId)))",
        "f.ImportedAtUtcMs DESC,f.FileId", start, limit, ("$search", search), ("$unbound", unbound ? 1 : 0));
        foreach (var item in result.Items)
        {
            var path = files.GetOriginalPath((string)item["StoredFileName"]!);
            item["SizeBytes"] = System.IO.File.Exists(path) ? new FileInfo(path).Length : null;
        }
        return result;
    }

    public Dictionary<string, object?> File(string id)
    {
        var result = Query("Files", "FileId,OriginalFileName,StoredFileName,DisplayName,Format,ContentHash,ImportedAtUtcMs,CommentCount,LastCommentTimeMs,ParseDataVersion,Status",
            "FileId=$id", "FileId", 0, 1, ("$id", id)).Items.SingleOrDefault() ?? throw new KeyNotFoundException("File not found.");
        var path = files.GetOriginalPath((string)result["StoredFileName"]!);
        result["SizeBytes"] = System.IO.File.Exists(path) ? new FileInfo(path).Length : null;
        return result;
    }

    public Page<Dictionary<string, object?>> Bindings(string id, int start, int limit)
    {
        _ = File(id);
        return Query("MediaBindings b JOIN MediaState m ON m.MediaId=b.MediaId", "b.MediaId,b.BoundAtUtcMs,m.ActiveFileId,m.Version,m.CheckStatus",
            "b.FileId=$id", "b.MediaId", start, limit, ("$id", id));
    }

    public Page<Dictionary<string, object?>> References(string id, int start, int limit)
    {
        _ = File(id);
        return Query("""
            (SELECT 'file' AS Kind,b.MediaId,NULL AS PlanId,NULL AS Name,0 AS SegmentCount
                FROM MediaBindings b WHERE b.FileId=$id
             UNION ALL
             SELECT 'plan',p.MediaId,p.PlanId,p.Name,COUNT(*) FROM CombineSegments s
                JOIN CombinePlans p ON p.PlanId=s.PlanId WHERE s.FileId=$id GROUP BY p.PlanId) refs
             LEFT JOIN MediaState m ON m.MediaId=refs.MediaId
            """, "refs.Kind,refs.MediaId,refs.PlanId,refs.Name,refs.SegmentCount,m.CheckStatus,m.Version",
            "1=1", "refs.MediaId,refs.Kind,refs.PlanId", start, limit, ("$id", id));
    }

    public Page<Dictionary<string, object?>> Imports(string? batch, int start, int limit) => Query("ImportTasks", TaskProjection,
        "($batch IS NULL OR BatchId=$batch)", "CreatedAtUtcMs DESC,TaskId", start, limit, ("$batch", batch));

    public Dictionary<string, object?> Task(string id) => Query("ImportTasks", TaskProjection, "TaskId=$id", "TaskId", 0, 1, ("$id", id)).Items.SingleOrDefault()
        ?? throw new KeyNotFoundException("Import task not found.");

    public Page<Dictionary<string, object?>> Errors(string id, int start, int limit)
    {
        _ = Task(id);
        var result = Query("ImportErrors", "ErrorId,SourceOrdinal,TextSummary,ReasonCodes", "TaskId=$id", "SourceOrdinal,ErrorId", start, limit, ("$id", id));
        foreach (var error in result.Items)
        {
            var codes = ((string)error["ReasonCodes"]!).Split(',', StringSplitOptions.RemoveEmptyEntries);
            error["Description"] = string.Join("; ", codes.Select(code => code switch
            {
                "MissingTime" => "Time field is missing", "InvalidTime" => "Time must be a nonnegative millisecond value",
                "BlankText" => "Text is empty", "InvalidText" => "Text must be a string", "TextTooLong" => "Text exceeds 100 Unicode code points",
                "InvalidMode" => "Mode must be a positive integer", "UnsupportedMode" => "Only modes 1, 4 and 5 are supported",
                "InvalidFontSize" => "Font size must be a positive integer", "InvalidColor" => "Color must be an integer from 0 to 16777215",
                "EntryTooLarge" => "Entry exceeds the bounded parser buffer", _ => code
            }));
        }
        return result;
    }

    public Page<Dictionary<string, object?>> AbnormalMedia(int start, int limit) => Query("MediaState", "MediaId,ActiveFileId,ActivePlanId,IsDeactivated,Version,CheckStatus,CheckedAtUtcMs",
        "CheckStatus IN ('Missing','CheckFailed') AND (EXISTS(SELECT 1 FROM MediaBindings b WHERE b.MediaId=MediaState.MediaId) OR EXISTS(SELECT 1 FROM CombinePlans p WHERE p.MediaId=MediaState.MediaId))", "MediaId", start, limit);

    private const string TaskProjection = "TaskId,BatchId,Slot,Status,Stage,StagePercent,TotalComments,ProcessedComments,NormalComments,AbnormalComments,ImportedComments,SkippedComments,CreatedAtUtcMs,FinishedAtUtcMs,DeadlineAtUtcMs,ErrorCode,ResultCode,FileId,(SELECT Operation FROM ImportBatches b WHERE b.BatchId=ImportTasks.BatchId) AS Operation,(SELECT MediaId FROM ImportBatches b WHERE b.BatchId=ImportTasks.BatchId) AS MediaId,(SELECT OriginalFileName FROM ImportSlots s WHERE s.BatchId=ImportTasks.BatchId AND s.Slot=ImportTasks.Slot) AS OriginalFileName";

    private Page<Dictionary<string, object?>> Query(string table, string projection, string where, string order, int start, int limit, params (string, object?)[] args)
    {
        ValidatePage(start, limit);
        using var c = factory.CreateOpenConnection();
        using var t = c.BeginTransaction(deferred: true);
        var total = Long(c, t, $"SELECT COUNT(*) FROM {table} WHERE {where}", args);
        using var cmd = Command(c, t, $"SELECT {projection} FROM {table} WHERE {where} ORDER BY {order} LIMIT $limit OFFSET $start", [.. args, ("$limit", limit), ("$start", start)]);
        using var r = cmd.ExecuteReader();
        var items = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetName(i) == "ErrorId" ? r.GetInt64(i).ToString(System.Globalization.CultureInfo.InvariantCulture) : r.GetValue(i);
            items.Add(row);
        }
        return new(items, total, start, limit);
    }
}
