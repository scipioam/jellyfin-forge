using Jellyfin.Plugin.Danmuku.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Danmuku.Api;

[ApiController, Route("Danmuku/Media/{itemId}/CombinePlans"), Authorize(Policy = Policies.RequiresElevation), DanmukuErrors]
[RequestSizeLimit(128 * 1024)]
public sealed class CombinePlansController(CombinePlanService plans, MediaBindingService bindings,
    ILibraryManager library) : ControllerBase
{
    private static string Id(string value) => Guid.TryParse(value, out var id) ? id.ToString("N") : throw new ArgumentException("Invalid ID.");
    private long? Duration(string media) => library.GetItemById(Guid.Parse(media))?.RunTimeTicks is > 0 and var ticks
        ? ticks / TimeSpan.TicksPerMillisecond : null;

    [HttpGet]
    public async Task<object> List(string itemId, CancellationToken ct)
    {
        var media = Id(itemId);
        var state = await bindings.ReadAsync(media, ct).ConfigureAwait(false);
        // Unavailable media must remain manageable without another library lookup.
        var duration = state.CheckStatus == "Exists" ? Duration(media) : null;
        return new { items = plans.List(media), state, defaultName = plans.NextName(media), durationMs = duration };
    }
    [HttpGet("{planId}")]
    public async Task<object> Get(string itemId, string planId, CancellationToken ct) =>
        new { plan = plans.Get(Id(itemId), Id(planId)), state = await bindings.ReadAsync(Id(itemId), ct).ConfigureAwait(false) };
    [HttpPost("Preview")]
    public async Task<CombinePreview> Preview(string itemId, PreviewPlan body, CancellationToken ct)
    {
        var media = Id(itemId);
        await bindings.RequireExistingAsync(media, ct).ConfigureAwait(false);
        return await plans.PreviewAsync(media, body.Segments, Duration(media), ct).ConfigureAwait(false);
    }
    [HttpPost]
    public Task<CombinePlan> Create(string itemId, CreatePlan body, CancellationToken ct) =>
        plans.SaveAsync(Id(itemId), Id(body.PlanId), body.Name, body.Segments, null, body.Activate, body.ExpectedMediaVersion, ct);
    [HttpPut("{planId}")]
    public Task<CombinePlan> Update(string itemId, string planId, UpdatePlan body, CancellationToken ct) =>
        plans.SaveAsync(Id(itemId), Id(planId), body.Name, body.Segments, body.ExpectedPlanVersion, body.Activate, body.ExpectedMediaVersion, ct);
    [HttpPost("{planId}/Delete")]
    public async Task<IActionResult> Delete(string itemId, string planId, DeletePlan body, CancellationToken ct)
    {
        await plans.DeleteAsync(Id(itemId), Id(planId), body.ExpectedPlanVersion, body.ExpectedMediaVersion, body.Replacement, ct).ConfigureAwait(false);
        return NoContent();
    }
}

public sealed record PreviewPlan(IReadOnlyList<CombineSegment> Segments);
public sealed record CreatePlan(string PlanId, string Name, IReadOnlyList<CombineSegment> Segments, bool Activate = false, long? ExpectedMediaVersion = null);
public sealed record UpdatePlan(string Name, IReadOnlyList<CombineSegment> Segments, long ExpectedPlanVersion, bool Activate = false, long? ExpectedMediaVersion = null);
public sealed record DeletePlan(long ExpectedPlanVersion, long ExpectedMediaVersion, MediaSelection? Replacement = null);
