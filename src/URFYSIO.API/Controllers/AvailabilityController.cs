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

    public AvailabilityController(IAvailabilityService availabilityService) =>
        _availabilityService = availabilityService;

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

    [HttpPost]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Create([FromBody] CreateAvailabilitySlotDto dto)
    {
        var slot = await _availabilityService.CreateAsync(dto.ToEntity());
        return CreatedAtAction(nameof(GetById), new { id = slot.Id }, slot.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAvailabilitySlotDto dto)
    {
        var slot = await _availabilityService.GetByIdAsync(id);
        if (slot is null) return NotFound();

        slot.StartTime = dto.StartTime;
        slot.EndTime = dto.EndTime;

        await _availabilityService.UpdateAsync(slot);
        return Ok(slot.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _availabilityService.DeleteAsync(id);
        return result ? NoContent() : NotFound();
    }
}
