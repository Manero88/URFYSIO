using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using URFYSIO.API.Mapping;
using URFYSIO.Core.Interfaces;
using URFYSIO.Shared.DTOs.Registration;

namespace URFYSIO.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RegistrationController : BaseApiController
{
    private readonly IRegistrationService _registrationService;

    public RegistrationController(IRegistrationService registrationService) =>
        _registrationService = registrationService;

    /// <summary>Public endpoint — allows potential clients to submit a registration request.</summary>
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("public")]
    public async Task<IActionResult> Create([FromBody] CreateRegistrationRequestDto dto)
    {
        var request = await _registrationService.CreateAsync(dto.ToEntity());
        return Created($"api/registration/{request.Id}", request.ToDto());
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAll([FromQuery] Shared.Enums.RegistrationStatus? status)
    {
        var coreStatus = status.HasValue ? (Core.Enums.RegistrationStatus)(int)status.Value : (Core.Enums.RegistrationStatus?)null;
        var requests = await _registrationService.GetAllAsync(coreStatus);
        return Ok(requests.Select(r => r.ToDto()));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var request = await _registrationService.GetByIdAsync(id);
        return request is null ? NotFound() : Ok(request.ToDto());
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Approve(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        // ApproveAsync creates the Auth0 account, the local user, assigns the role and
        // sends the password-setup email. On any hard failure it throws a DomainException
        // which ExceptionHandlingMiddleware turns into a ProblemDetails the admin app
        // surfaces — so there's no partial "approved but can't log in" state.
        var request = await _registrationService.ApproveAsync(id, userId.Value);
        return Ok(request.ToDto());
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reject(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        var request = await _registrationService.RejectAsync(id, userId.Value);
        return Ok(request.ToDto());
    }
}
