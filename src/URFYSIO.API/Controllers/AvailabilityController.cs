using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using URFYSIO.API.Mapping;
using URFYSIO.Core.Interfaces;
using URFYSIO.Shared.DTOs.Availability;

namespace URFYSIO.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AvailabilityController : BaseApiController
{
    private readonly IAvailabilityService _availabilityService;
    private readonly IUserService _userService;

    public AvailabilityController(IAvailabilityService availabilityService, IUserService userService)
    {
        _availabilityService = availabilityService;
        _userService = userService;
    }

    /// <summary>
    /// Write-ownership rule for slots: admins may manage ANY physiotherapist's
    /// availability (the admin dashboard manages slots on a physio's behalf);
    /// a physiotherapist may only manage their OWN. Without this check the DTO's
    /// PhysiotherapistProfileId was trusted as-is, letting one physio write slots
    /// onto a colleague's calendar.
    /// </summary>
    private async Task<bool> CallerMayManageAsync(Guid physioProfileId)
    {
        if (IsAdmin()) return true;
        var userId = GetCurrentUserId();
        if (!userId.HasValue) return false;
        var user = await _userService.GetByIdAsync(userId.Value);
        return user?.PhysiotherapistProfile?.Id == physioProfileId;
    }

    /// <summary>Get available (un-booked) slots. Accessible to all authenticated users.</summary>
    [HttpGet("available")]
    public async Task<IActionResult> GetAvailable(
        [FromQuery] Guid? physiotherapistProfileId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var slots = await _availabilityService.GetAvailableSlotsAsync(physiotherapistProfileId, from, to);
        return Ok(slots.Select(s => s.ToDto()));
    }

    /// <summary>Get all slots for a specific physiotherapist (including booked).</summary>
    [HttpGet("physiotherapist/{physioProfileId:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> GetByPhysiotherapist(
        Guid physioProfileId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var slots = await _availabilityService.GetByPhysiotherapistProfileIdAsync(physioProfileId, from, to);
        return Ok(slots.Select(s => s.ToDto()));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var slot = await _availabilityService.GetByIdAsync(id);
        return slot is null ? NotFound() : Ok(slot.ToDto());
    }

    // Validation failures (overlap, past slot, booked slot, start ≥ end) come back as
    // failed ServiceResults and become 400s with the reason in "message" — the app's
    // error extraction reads that field. No DomainException is thrown on these paths,
    // so the debugger no longer breaks on routine validation.
    [HttpPost]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Create([FromBody] CreateAvailabilitySlotDto dto)
    {
        if (!await CallerMayManageAsync(dto.PhysiotherapistProfileId))
            return Forbidden();

        var result = await _availabilityService.CreateAsync(dto.ToEntity());
        if (!result.Success)
            return BadRequest(new { message = result.Error });
        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAvailabilitySlotDto dto)
    {
        var slot = await _availabilityService.GetByIdAsync(id);
        if (slot is null) return NotFound();

        if (!await CallerMayManageAsync(slot.PhysiotherapistProfileId))
            return Forbidden();

        slot.StartTime = dto.StartTime;
        slot.EndTime = dto.EndTime;

        var result = await _availabilityService.UpdateAsync(slot);
        if (!result.Success)
            return BadRequest(new { message = result.Error });
        return Ok(result.Value!.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var slot = await _availabilityService.GetByIdAsync(id);
        if (slot is null) return NotFound();

        if (!await CallerMayManageAsync(slot.PhysiotherapistProfileId))
            return Forbidden();

        if (slot.IsBooked)
            return BadRequest(new { message = "Cannot delete a booked availability slot." });

        var deleted = await _availabilityService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
