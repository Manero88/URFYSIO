using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace URFYSIO.API.Controllers;

[ApiController]
public abstract class BaseApiController : ControllerBase
{
    protected Guid? GetCurrentUserId()
    {
        return HttpContext.Items["LocalUserId"] as Guid?;
    }

    // Reads the standard role claim (ClaimTypes.Role) which Auth0UserSyncMiddleware
    // populates from the local DB on every authenticated request. This is deliberately
    // NOT the Auth0 "https://urfysio.nl/roles" claim — that one reflects the stale
    // role baked into the JWT at login time, not the current DB value.
    protected string? GetCurrentUserRole()
    {
        return User.FindFirst(ClaimTypes.Role)?.Value;
    }

    protected bool IsAdmin()
    {
        return User.IsInRole("Admin");
    }

    protected bool IsOwnerOrAdmin(Guid resourceOwnerId)
    {
        if (IsAdmin()) return true;
        var currentUserId = GetCurrentUserId();
        return currentUserId.HasValue && currentUserId.Value == resourceOwnerId;
    }

    protected IActionResult Forbidden()
    {
        return StatusCode(StatusCodes.Status403Forbidden, new { message = "You do not have permission to access this resource." });
    }
}
