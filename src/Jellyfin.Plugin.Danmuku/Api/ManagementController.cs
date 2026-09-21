using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Danmuku.Api;

public sealed class DanmukuErrorsAttribute : ActionFilterAttribute, IExceptionFilter
{
    public DanmukuErrorsAttribute() { Order = -3000; }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = new ObjectResult(new { code = "InvalidInput", message = "The request fields are invalid." }) { StatusCode = 422 };
    }

    public void OnException(ExceptionContext context)
    {
        var (status, code, message) = context.Exception switch
        {
            ImportOperationException e => (e.StatusCode, e.Code, e.Message),
            KeyNotFoundException => (404, "NotFound", "The requested object does not exist."),
            ArgumentException => (422, "InvalidInput", "The request fields are invalid."),
            _ => (500, "InternalError", "The operation failed; inspect server logs before retrying.")
        };
        if (status == 500)
            (context.HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger("Danmuku.Api")
                .LogError(context.Exception, "Danmuku request failed.");
        context.Result = new ObjectResult(new { code, message }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}

[ApiController, Route("Danmuku"), Authorize(Policy = Policies.RequiresElevation), DanmukuErrors]
public sealed class ManagementController(ManagementQueries queries, ImportService imports, MediaBindingService bindings,
    IFileDeletionCoordinator deletion, IPublishFileStore files, ILibraryManager library) : ControllerBase
{
    private static string MediaId(string value) => Guid.TryParse(value, out var id) ? id.ToString("N") : throw new ArgumentException("Invalid media id.");

    [HttpGet("Files")]
    public object Files(string? search = null, bool unbound = false, int startIndex = 0, int limit = 50) => queries.Files(search, unbound, startIndex, limit);
    [HttpGet("Files/{fileId}")]
    public object FileDetails(string fileId) => queries.File(fileId);
    [HttpGet("Files/{fileId}/Bindings")]
    public object FileBindings(string fileId, int startIndex = 0, int limit = 50) => queries.Bindings(fileId, startIndex, limit);
    [HttpGet("Files/{fileId}/Original")]
    public IActionResult Original(string fileId)
    {
        var row = queries.File(fileId);
        if ((string)row["Status"]! != "Published") throw new ImportOperationException("FileUnavailable", 409, "File deletion is pending.");
        var path = files.GetOriginalPath((string)row["StoredFileName"]!);
        try { return File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), "application/octet-stream", (string)row["StoredFileName"]!); }
        catch (FileNotFoundException) { return NotFound(new { code = "OriginalMissing", message = "Original file is missing." }); }
    }
    [HttpDelete("Files/{fileId}")]
    public async Task<IActionResult> Delete(string fileId, CancellationToken ct)
    {
        var result = await deletion.MarkForDeletionAsync(fileId, ct).ConfigureAwait(false);
        if (result.Status == FileDeletionMarkStatus.NotFound) throw new KeyNotFoundException();
        if (result.Status == FileDeletionMarkStatus.Referenced) throw new ImportOperationException("FileReferenced", 409, "Unbind all media before deleting this file.");
        await deletion.CleanupAsync(ct).ConfigureAwait(false);
        return Ok(new { status = "DeletionRequested" });
    }
    [HttpGet("Media")]
    public object Media(string? search = null, Guid? parentId = null, bool abnormal = false, int startIndex = 0, int limit = 50)
    {
        ManagementQueries.ValidatePage(startIndex, limit);
        if (abnormal) return queries.AbnormalMedia(startIndex, limit);
        var result = library.GetItemsResult(new InternalItemsQuery
        {
            SearchTerm = search, ParentId = parentId ?? Guid.Empty, Recursive = parentId is null,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series, BaseItemKind.Season],
            StartIndex = startIndex, Limit = limit, EnableTotalRecordCount = true
        });
        return new { items = result.Items.Select(i => new { id = i.Id.ToString("N"), name = i.Name, type = i.GetType().Name, canBind = i is MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Episode }), totalCount = result.TotalRecordCount, startIndex, limit };
    }
    [HttpGet("Media/{itemId}/Bindings")]
    public Task<MediaBindingSnapshot> ReadBindings(string itemId, CancellationToken ct) => bindings.ReadAsync(MediaId(itemId), ct);
    [HttpPut("Media/{itemId}/Bindings")]
    public Task<MediaBindingSnapshot> UpdateBindings(string itemId, BindingUpdate body, CancellationToken ct) => bindings.UpdateAsync(MediaId(itemId), body.ExpectedVersion, body.FileIds, body.ActiveFileId, ct);
    [HttpPost("BindingChecks")]
    public async Task<object> StartCheck(CancellationToken ct) => new { taskId = await bindings.StartCheckAllAsync(ct).ConfigureAwait(false) };
    [HttpGet("BindingChecks/{taskId}")]
    public object Check(string taskId) => bindings.GetCheckJob(taskId);
    [HttpPost("ImportBatches")]
    public Task<ImportBatchSnapshot> Create(ImportBatchRequest body, CancellationToken ct) => imports.CreateBatchAsync(body with { MediaId = MediaId(body.MediaId) }, ct);
    [HttpPost("ImportBatches/{batchId}/Files/{slot}"), DisableRequestSizeLimit]
    public async Task<IActionResult> Upload(string batchId, int slot, CancellationToken ct)
    {
        var result = await imports.ReceiveAsync(batchId, slot, Request.Body, Request.ContentLength, ct).ConfigureAwait(false);
        if (result.Status == "Failed") throw new ImportOperationException(result.ErrorCode ?? "UploadFailed", result.ErrorCode == "FileTooLarge" ? 413 : 422, "The upload failed; create a new batch to retry.");
        return StatusCode(result.TaskId is null ? 409 : 202, result);
    }
    [HttpGet("Imports")]
    public async Task<object> Imports(string? batchId = null, int startIndex = 0, int limit = 50, CancellationToken ct = default)
    {
        var batch = batchId is null ? null : await imports.GetBatchAsync(batchId, ct).ConfigureAwait(false);
        var page = queries.Imports(batchId, startIndex, limit);
        return new { page.Items, page.TotalCount, page.StartIndex, page.Limit, batch };
    }
    [HttpGet("Imports/{taskId}")]
    public object Import(string taskId) => queries.Task(taskId);
    [HttpGet("Imports/{taskId}/Errors")]
    public object Errors(string taskId, int startIndex = 0, int limit = 50) => queries.Errors(taskId, startIndex, limit);
    [HttpPost("Imports/{taskId}/ConfirmSkip")]
    public async Task<object> Confirm(string taskId, CancellationToken ct) { await imports.ConfirmSkipAsync(taskId, ct).ConfigureAwait(false); return queries.Task(taskId); }
    [HttpPost("Imports/{taskId}/Cancel")]
    public async Task<object> Cancel(string taskId, CancellationToken ct) { await imports.CancelTaskAsync(taskId, ct).ConfigureAwait(false); return queries.Task(taskId); }
    [HttpPost("Imports/{taskId}/Resume")]
    public async Task<object> Resume(string taskId, ResumeRequest body, CancellationToken ct) { await imports.ResumeAsync(taskId, body.ExpectedVersion, ct).ConfigureAwait(false); return queries.Task(taskId); }
    [HttpPost("ImportBatches/{batchId}/ConfirmSkip")]
    public async Task<object> ConfirmBatch(string batchId, CancellationToken ct) { await imports.ConfirmBatchSkipAsync(batchId, ct).ConfigureAwait(false); return await imports.GetBatchAsync(batchId, ct).ConfigureAwait(false); }
    [HttpPost("ImportBatches/{batchId}/Cancel")]
    public async Task<object> CancelBatch(string batchId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CancelBatchRequest? body, CancellationToken ct) { await imports.CancelBatchAsync(batchId, body?.Slots, ct).ConfigureAwait(false); return await imports.GetBatchAsync(batchId, ct).ConfigureAwait(false); }
    [HttpGet("WebIntegration")]
    public object WebIntegration([FromServices] Configuration.PluginConfiguration config, [FromServices] MediaBrowser.Common.Configuration.IApplicationPaths paths)
    {
        var status = "Unavailable";
        try
        {
            var path = Path.Combine(paths.WebPath, "index.html");
            if (System.IO.File.Exists(path) && new FileInfo(path).Length <= 2 * 1024 * 1024)
                status = System.IO.File.ReadAllText(path).Contains("data-danmuku-entry=\"m1-v1\"", StringComparison.Ordinal) ? "MarkerDetected" : "NotDeployed";
        }
        catch (IOException) { status = "ReadFailed"; }
        catch (UnauthorizedAccessException) { status = "ReadFailed"; }
        return new { enabled = config.WebEnabled, resourceVersion = WebResourceController.ResourceVersion, entryStatus = status,
            browserStatus = "RefreshAndVerify", message = "Prepare a matching entry and recreate the container when missing. Saving this switch does not deploy an entry; refresh open playback pages after disabling." };
    }
}

public sealed record BindingUpdate(long ExpectedVersion, IReadOnlyList<string> FileIds, string? ActiveFileId);
public sealed record ResumeRequest(long ExpectedVersion);
public sealed record CancelBatchRequest(IReadOnlyList<int>? Slots = null);
