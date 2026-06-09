using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using URFYSIO.API.Mapping;
using URFYSIO.Core.Interfaces;
using URFYSIO.Shared.DTOs.Appointments;

namespace URFYSIO.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AppointmentsController : BaseApiController
{
    private readonly IAppointmentService _appointmentService;
    private readonly IUserService _userService;

    public AppointmentsController(IAppointmentService appointmentService, IUserService userService)
    {
        _appointmentService = appointmentService;
        _userService = userService;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAll([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var appointments = await _appointmentService.GetAllAsync(from, to);
        return Ok(appointments.Select(a => a.ToDto()));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist,Client")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var appointment = await _appointmentService.GetByIdAsync(id);
        if (appointment is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user is null) return Unauthorized();

            var isOwner = user.ClientProfile?.Id == appointment.ClientProfileId
                       || user.PhysiotherapistProfile?.Id == appointment.PhysiotherapistProfileId;
            if (!isOwner) return Forbidden();
        }

        return Ok(appointment.ToDto());
    }

    [HttpGet("client/{clientProfileId:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist,Client")]
    public async Task<IActionResult> GetByClient(Guid clientProfileId)
    {
        if (!IsAdmin() && GetCurrentUserRole() == "Client")
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.ClientProfile?.Id != clientProfileId)
                return Forbidden();
        }

        var appointments = await _appointmentService.GetByClientProfileIdAsync(clientProfileId);
        return Ok(appointments.Select(a => a.ToDto()));
    }

    [HttpGet("physiotherapist/{physioProfileId:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist,Client")]
    public async Task<IActionResult> GetByPhysiotherapist(Guid physioProfileId)
    {
        if (!IsAdmin() && GetCurrentUserRole() == "Physiotherapist")
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != physioProfileId)
                return Forbidden();
        }

        var appointments = await _appointmentService.GetByPhysiotherapistProfileIdAsync(physioProfileId);
        return Ok(appointments.Select(a => a.ToDto()));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Physiotherapist,Client")]
    public async Task<IActionResult> Create([FromBody] CreateAppointmentDto dto)
    {
        var appointment = await _appointmentService.CreateAsync(dto.ToEntity());
        return CreatedAtAction(nameof(GetById), new { id = appointment.Id }, appointment.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAppointmentDto dto)
    {
        var appointment = await _appointmentService.GetByIdAsync(id);
        if (appointment is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != appointment.PhysiotherapistProfileId)
                return Forbidden();
        }

        if (dto.Notes is not null) appointment.Notes = dto.Notes;
        if (dto.Status.HasValue) appointment.Status = (Core.Enums.AppointmentStatus)(int)dto.Status.Value;

        await _appointmentService.UpdateAsync(appointment);
        return Ok(appointment.ToDto());
    }

    // Cancel/Reschedule are physio/admin only — a client must contact their
    // physiotherapist to change an appointment (the app shows them an info notice
    // to that effect). The Client role is deliberately absent from [Authorize] so
    // even a hand-crafted request from a client's token is rejected with 403.
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var appointment = await _appointmentService.GetByIdAsync(id);
        if (appointment is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user is null) return Unauthorized();

            // Only the owning physiotherapist may cancel (admins bypass via IsAdmin).
            if (user.PhysiotherapistProfile?.Id != appointment.PhysiotherapistProfileId)
                return Forbidden();
        }

        var result = await _appointmentService.CancelAsync(id);
        return result ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/reschedule")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleAppointmentDto dto)
    {
        var appointment = await _appointmentService.GetByIdAsync(id);
        if (appointment is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user is null) return Unauthorized();

            if (user.PhysiotherapistProfile?.Id != appointment.PhysiotherapistProfileId)
                return Forbidden();
        }

        var rescheduled = await _appointmentService.RescheduleAsync(id, dto.NewAvailabilitySlotId);
        return Ok(rescheduled.ToDto());
    }

    private async Task<Core.Entities.User?> GetCurrentUserWithProfiles()
    {
        var userId = GetCurrentUserId();
        return userId.HasValue ? await _userService.GetByIdAsync(userId.Value) : null;
    }
}
