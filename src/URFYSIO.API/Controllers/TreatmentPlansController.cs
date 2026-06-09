using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using URFYSIO.API.Mapping;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Interfaces;
using URFYSIO.Shared.DTOs.TreatmentPlans;

namespace URFYSIO.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TreatmentPlansController : BaseApiController
{
    private readonly ITreatmentPlanService _planService;
    private readonly IUserService _userService;

    public TreatmentPlansController(ITreatmentPlanService planService, IUserService userService)
    {
        _planService = planService;
        _userService = userService;
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var plan = await _planService.GetByIdAsync(id);
        if (plan is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user is null) return Unauthorized();

            var isOwner = user.ClientProfile?.Id == plan.ClientProfileId
                       || user.PhysiotherapistProfile?.Id == plan.PhysiotherapistProfileId;
            if (!isOwner) return Forbidden();
        }

        return Ok(plan.ToDto());
    }

    [HttpGet("client/{clientProfileId:guid}")]
    public async Task<IActionResult> GetByClient(Guid clientProfileId)
    {
        if (!IsAdmin() && GetCurrentUserRole() == "Client")
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.ClientProfile?.Id != clientProfileId)
                return Forbidden();
        }

        var plans = await _planService.GetByClientProfileIdAsync(clientProfileId);
        return Ok(plans.Select(p => p.ToDto()));
    }

    [HttpGet("physiotherapist/{physioProfileId:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> GetByPhysiotherapist(Guid physioProfileId)
    {
        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != physioProfileId)
                return Forbidden();
        }

        var plans = await _planService.GetByPhysiotherapistProfileIdAsync(physioProfileId);
        return Ok(plans.Select(p => p.ToDto()));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Create([FromBody] CreateTreatmentPlanDto dto)
    {
        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != dto.PhysiotherapistProfileId)
                return Forbidden();
        }

        var plan = await _planService.CreateAsync(dto.ToEntity());
        return CreatedAtAction(nameof(GetById), new { id = plan.Id }, plan.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTreatmentPlanDto dto)
    {
        var plan = await _planService.GetByIdAsync(id);
        if (plan is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != plan.PhysiotherapistProfileId)
                return Forbidden();
        }

        plan.Title = dto.Title;
        plan.Description = dto.Description;

        await _planService.UpdateAsync(plan);
        return Ok(plan.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var plan = await _planService.GetByIdAsync(id);
        if (plan is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != plan.PhysiotherapistProfileId)
                return Forbidden();
        }

        var result = await _planService.DeleteAsync(id);
        return result ? NoContent() : NotFound();
    }

    // --- Completion ---

    [HttpPost("{id:guid}/complete")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Complete(Guid id)
    {
        var plan = await _planService.GetByIdAsync(id);
        if (plan is null) return NotFound();

        // Only the owning physio (or an admin) may complete — see the matching guard
        // on Update/Delete. We resolve ownership against the LIVE user's profile id,
        // not the plan's claim of who owns it, so a plan that somehow ended up
        // mis-attributed can still only be closed by its real owner.
        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != plan.PhysiotherapistProfileId)
                return Forbidden();
        }

        var updated = await _planService.CompleteAsync(id);
        return updated is null ? NotFound() : Ok(updated.ToDto());
    }

    [HttpPost("{id:guid}/reopen")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> Reopen(Guid id)
    {
        var plan = await _planService.GetByIdAsync(id);
        if (plan is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != plan.PhysiotherapistProfileId)
                return Forbidden();
        }

        var updated = await _planService.ReopenAsync(id);
        return updated is null ? NotFound() : Ok(updated.ToDto());
    }

    // --- Entries ---
    [HttpPost("{planId:guid}/entries")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> AddEntry(Guid planId, [FromBody] CreateTreatmentPlanEntryDto dto)
    {
        var plan = await _planService.GetByIdAsync(planId);
        if (plan is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != plan.PhysiotherapistProfileId)
                return Forbidden();
        }

        var entry = await _planService.AddEntryAsync(planId, dto.ToEntity());
        return Created($"api/treatmentplans/{planId}/entries/{entry.Id}", entry.ToDto());
    }

    [HttpPut("entries/{entryId:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> UpdateEntry(Guid entryId, [FromBody] UpdateTreatmentPlanEntryDto dto)
    {
        var existing = await _planService.GetEntryByIdAsync(entryId);
        if (existing is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != existing.TreatmentPlan.PhysiotherapistProfileId)
                return Forbidden();
        }

        var entry = new TreatmentPlanEntry
        {
            Id = entryId,
            Title = dto.Title,
            Description = dto.Description,
            OrderIndex = dto.OrderIndex,
            IsCompleted = dto.IsCompleted
        };

        var updated = await _planService.UpdateEntryAsync(entry);
        return updated is null ? NotFound() : Ok(updated.ToDto());
    }

    [HttpDelete("entries/{entryId:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> DeleteEntry(Guid entryId)
    {
        var existing = await _planService.GetEntryByIdAsync(entryId);
        if (existing is null) return NotFound();

        if (!IsAdmin())
        {
            var user = await GetCurrentUserWithProfiles();
            if (user?.PhysiotherapistProfile?.Id != existing.TreatmentPlan.PhysiotherapistProfileId)
                return Forbidden();
        }

        var result = await _planService.DeleteEntryAsync(entryId);
        return result ? NoContent() : NotFound();
    }

    // --- Entry comments ---

    [HttpGet("entries/{entryId:guid}/comments")]
    public async Task<IActionResult> GetEntryComments(Guid entryId)
    {
        var entry = await _planService.GetEntryByIdAsync(entryId);
        if (entry is null) return NotFound();

        if (!await CanReadEntryAsync(entry)) return Forbidden();

        var comments = await _planService.GetCommentsForEntryAsync(entryId);
        return Ok(comments.Select(c => c.ToDto()));
    }

    [HttpPost("entries/{entryId:guid}/comments")]
    public async Task<IActionResult> AddEntryComment(
        Guid entryId,
        [FromBody] CreateTreatmentPlanEntryCommentDto dto)
    {
        var entry = await _planService.GetEntryByIdAsync(entryId);
        if (entry is null) return NotFound();

        if (!await CanReadEntryAsync(entry)) return Forbidden();

        var userId = GetCurrentUserId();
        if (!userId.HasValue) return Unauthorized();

        var comment = await _planService.AddCommentAsync(entryId, userId.Value, dto.Text);
        return Created($"api/treatmentplans/entries/{entryId}/comments/{comment.Id}", comment.ToDto());
    }

    /// <summary>
    /// Shared "can this user touch this entry's comment thread" check used by both
    /// the GET and POST. Clients may interact with their own plans' entries; physios
    /// with plans they own; admins with anything. We deliberately do NOT require the
    /// physio to also be the original plan author — any physio assigned to the
    /// patient (i.e. owning the plan) qualifies, which is the same rule used for
    /// reading the plan itself.
    /// </summary>
    private async Task<bool> CanReadEntryAsync(TreatmentPlanEntry entry)
    {
        if (IsAdmin()) return true;
        var user = await GetCurrentUserWithProfiles();
        if (user is null) return false;
        return user.ClientProfile?.Id == entry.TreatmentPlan.ClientProfileId
            || user.PhysiotherapistProfile?.Id == entry.TreatmentPlan.PhysiotherapistProfileId;
    }

    private async Task<User?> GetCurrentUserWithProfiles()
    {
        var userId = GetCurrentUserId();
        return userId.HasValue ? await _userService.GetByIdAsync(userId.Value) : null;
    }
}
