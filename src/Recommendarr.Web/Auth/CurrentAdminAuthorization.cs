using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Web.Auth;

public sealed class CurrentAdminRequirement : IAuthorizationRequirement;

public sealed class CurrentAdminAuthorization(IServiceScopeFactory scopes)
{
    public async Task<bool> IsAdminAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!user.HasClaim(AuthConstants.StageClaim, AuthConstants.ProfileStage) ||
            !user.IsInRole(AuthConstants.AdminRole) ||
            user.PlexUserId() is not { } plexUserId)
            return false;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        return await db.Users.AsNoTracking().AnyAsync(
            candidate => candidate.PlexUserId == plexUserId && candidate.IsAdmin,
            ct);
    }

    public Task<bool> CanActForAsync(ClaimsPrincipal user, string plexUserId, CancellationToken ct = default) =>
        user.PlexUserId() == plexUserId ? Task.FromResult(true) : IsAdminAsync(user, ct);
}

public sealed class CurrentAdminAuthorizationHandler(CurrentAdminAuthorization authorization)
    : AuthorizationHandler<CurrentAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CurrentAdminRequirement requirement)
    {
        if (await authorization.IsAdminAsync(context.User))
            context.Succeed(requirement);
    }
}
