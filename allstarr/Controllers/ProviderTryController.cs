using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[ProviderTryInputValidation]
[Route("api/admin/providers")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ProviderTryController(IProviderRegistry providers, ProviderTryService service,
    IDbContextFactory<AllstarrDbContext> database, ILogger<ProviderTryController> logger) : ControllerBase
{
    private AdminAuthSession? Administrator => HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value)
        && value is AdminAuthSession { IsAdministrator: true } session ? session : null;

    [HttpGet("{provider}/try")]
    public Task<IActionResult> Describe(string provider, [FromQuery] Guid? accountId = null)
    {
        if (Administrator == null) return Task.FromResult<IActionResult>(StatusCode(403, new { error = "Administrator permissions required." }));
        var catalog = Catalog(provider, accountId);
        return Task.FromResult<IActionResult>(catalog == null ? NotFound() : Ok(catalog));
    }

    private ProviderTryCatalog? Catalog(string provider, Guid? accountId)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (ProviderTryService.IsScrobbling(provider))
            return new(provider, [new("scrobbling", "required", [new("check", [])])]);
        return providers.TryGet(provider, out var descriptor) ? ProviderTryOperations.Describe(descriptor!, accountId) : null;
    }

    [HttpPost("{provider}/try")]
    public async Task<IActionResult> Execute(string provider, [FromBody] ProviderTryRequest request)
    {
        var session = Administrator;
        if (session == null) return StatusCode(403, new { error = "Administrator permissions required." });
        if (request.Validate() is { } error) return BadRequest(new { error });
        var catalog = Catalog(provider, request.AccountId);
        var operation = catalog?.Capabilities.SingleOrDefault(item => item.Capability == request.Capability)?.Operations
            .SingleOrDefault(item => item.Id == request.Operation);
        if (operation == null) return NotFound();
        if (request.Operation == "search" && !operation.Kinds!.Contains(request.Kind ?? "tracks")) return NotFound();
        if (session.AllstarrUserId is not { } userId) return StatusCode(403, new { error = "A verified administrator identity is required." });
        var token = HttpContext.RequestAborted;
        await using var db = await database.CreateDbContextAsync(token);
        var identity = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId && item.Enabled && item.IsAdmin &&
            item.BackendType == session.BackendType.ToLowerInvariant() && item.BackendInstanceId == session.BackendInstanceId &&
            item.BackendPrincipalId == session.UserId, token);
        if (identity == null) return StatusCode(403, new { error = "A verified administrator identity is required." });
        var actor = new ProviderActorContext(ProviderActorKind.Administrator, userId,
            new(identity.BackendType, identity.BackendInstanceId, identity.BackendPrincipalId));
        var correlation = string.IsNullOrWhiteSpace(HttpContext.TraceIdentifier) ? Guid.NewGuid().ToString("N") :
            HttpContext.TraceIdentifier[..Math.Min(100, HttpContext.TraceIdentifier.Length)];
        var outcome = await service.ExecuteAsync(actor, catalog!.Provider, request, correlation, token);
        if (outcome.StatusCode == 503 && outcome.ReasonCode == LocalCapacityException.Code)
        {
            LocalCapacityException.MarkResponse(Response);
            return StatusCode(503, new { error = "Allstarr is at capacity. Try again shortly.", reasonCode = LocalCapacityException.Code });
        }
        if (outcome.StatusCode == 429)
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(429, new { retryAfterSeconds = 5 });
        }
        if (outcome.StatusCode == 409) return Conflict(new { reasonCode = "sign_in_needed" });
        if (outcome.StatusCode == 400) return BadRequest(new
        {
            error = outcome.ReasonCode == "account_not_used"
            ? "This capability does not use a provider account." : "Select a provider account for this capability."
        });
        if (outcome.StatusCode == 404) return NotFound();
        if (outcome.StatusCode == 403) return StatusCode(403, new { error = "The selected provider account is not authorized." });
        logger.LogInformation("Provider try {Provider}/{Capability}/{Operation} {Outcome} {ElapsedMs}ms", catalog.Provider,
            request.Capability, request.Operation, outcome.Result!.Succeeded ? "success" : outcome.Result.ReasonCode, outcome.Result.ElapsedMs);
        return Ok(outcome.Result);
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class ProviderTryInputValidationAttribute : Attribute, IActionFilter, IOrderedFilter
{
    public int Order => -3000;
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = new BadRequestObjectResult(new { error = "Request inputs are invalid." });
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
