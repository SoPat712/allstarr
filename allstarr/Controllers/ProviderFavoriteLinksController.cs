using allstarr.Core.Favorites;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/provider-favorite-links")]
[ServiceFilter(typeof(AdminPortFilter), Order = -4000)]
[RequestSizeLimit(4096)]
[TypeFilter(typeof(FavoriteLinkAuthorizationFilter), Order = -3500)]
[TypeFilter(typeof(FavoriteLinkInputFilter), Order = -3000)]
public sealed class ProviderFavoriteLinksController(FavoriteLinkService service) : ControllerBase
{
    [HttpGet("members")]
    public Task<IActionResult> Members(string? protocol = null, string? cursor = null, int limit = 100, CancellationToken token = default) =>
        ExecuteAsync(async session => Ok(await service.MembersAsync(session, protocol, cursor, limit, token)));
    [HttpGet]
    public Task<IActionResult> List(string? cursor = null, int limit = 50, CancellationToken token = default) =>
        ExecuteAsync(async session => Ok(await service.ListAsync(session, cursor, limit, token)));
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken token) => ExecuteAsync(async session => Ok(await service.DetailAsync(session, id, token)));
    [HttpPost("preview")]
    public Task<IActionResult> Preview([FromBody] FavoriteLinkPair request, CancellationToken token) => ExecuteAsync(async session => Ok(await service.PreviewAsync(session, request, token)));
    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateFavoriteLinkRequest request, CancellationToken token) => ExecuteAsync(async session =>
    {
        var receipt = await service.CreateAsync(session, request, token);
        return StatusCode(receipt.Created ? 201 : 200, receipt);
    });
    [HttpPost("{id:guid}/retry")]
    public Task<IActionResult> Retry(Guid id, [FromBody] RetryFavoriteLinkRequest request, CancellationToken token) => ExecuteAsync(async session =>
    {
        var receipt = await service.RetryAsync(session, id, request, token);
        return StatusCode(receipt.Replayed ? 200 : 202, receipt);
    });
    private async Task<IActionResult> ExecuteAsync(Func<AdminAuthSession, Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized(new { code = "authentication_required", error = "Sign in again to continue." });
        try { await service.RequireAdministratorAsync(session, HttpContext.RequestAborted); return await action(session); }
        catch (FavoriteLinkException error) { return StatusCode(error.StatusCode, new { code = error.Code, error = error.Message }); }
    }
}

public sealed class FavoriteLinkAuthorizationFilter(FavoriteLinkService service) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "private, no-store";
        if (context.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
        { context.Result = new UnauthorizedObjectResult(new { code = "authentication_required", error = "Sign in again to continue." }); return; }
        try { await service.RequireAdministratorAsync(session, context.HttpContext.RequestAborted); }
        catch (FavoriteLinkException error) { context.Result = new ObjectResult(new { code = error.Code, error = error.Message }) { StatusCode = error.StatusCode }; return; }
        await next();
    }
}
public sealed class FavoriteLinkInputFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid) context.Result = new BadRequestObjectResult(new { code = "favorite_link_invalid", error = "The shared provider hearts request is invalid." });
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
