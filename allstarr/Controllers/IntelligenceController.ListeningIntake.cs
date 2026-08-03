using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public sealed partial class IntelligenceController
{
    [HttpGet("listening-apps")]
    public async Task<IActionResult> ListListeningApps(
        [FromQuery] IntelligenceScopeRequest request,
        [FromServices] ListeningIntakeTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        return Ok(new { items = await tokens.ListAsync(scope, cancellationToken) });
    }

    [HttpPost("listening-apps")]
    public async Task<IActionResult> CreateListeningApp(
        [FromBody] ListeningIntakeTokenRequest request,
        [FromServices] ListeningIntakeTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        if (!await IntelligencePolicyService.Query(db, scope).AsNoTracking()
                .AnyAsync(item => item.Enabled, cancellationToken))
            return Conflict(new { error = "listening_history_disabled" });
        var created = await tokens.CreateAsync(scope, request.SendToConnectedServices, cancellationToken);
        return Created($"api/admin/intelligence/listening-apps/{created.Id}", created);
    }

    [HttpDelete("listening-apps/{id:guid}")]
    public async Task<IActionResult> RevokeListeningApp(
        Guid id,
        [FromBody] IntelligenceScopeRequest request,
        [FromServices] ListeningIntakeTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        return await tokens.RevokeAsync(scope, id, cancellationToken) ? NoContent() : NotFound();
    }
}

public sealed class ListeningIntakeTokenRequest : IntelligenceScopeRequest
{
    public bool SendToConnectedServices { get; set; }
}
