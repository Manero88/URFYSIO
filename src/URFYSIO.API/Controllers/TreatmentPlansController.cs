using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using URFYSIO.API.Mapping;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Shared.DTOs.TreatmentPlans;
using URFYSIO.Shared.Photos;

namespace URFYSIO.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TreatmentPlansController : BaseApiController
{
    private readonly ITreatmentPlanService _planService;
    private readonly IUserService _userService;
    private readonly IBlobStorageService _blobStorage;
    private readonly ILogger<TreatmentPlansController> _logger;

    public TreatmentPlansController(
        ITreatmentPlanService planService,
        IUserService userService,
        IBlobStorageService blobStorage,
        ILogger<TreatmentPlansController> logger)
    {
        _planService = planService;
        _userService = userService;
        _blobStorage = blobStorage;
        _logger = logger;
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

        // Comments cascade-delete with the entry, so collect their photos first —
        // afterwards there is no row left pointing at the blobs.
        var photoBlobNames = await _planService.GetCommentPhotoBlobNamesForEntriesAsync([entryId]);

        var result = await _planService.DeleteEntryAsync(entryId);
        if (!result) return NotFound();

        foreach (var blobName in photoBlobNames)
        {
            // Best-effort: the entry is already gone, so a storage failure must not turn
            // a successful delete into an error for the physio.
            try { await _blobStorage.DeletePhotoAsync(blobName); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Entry {EntryId} was deleted but its comment photo {BlobName} could not be " +
                    "removed from storage. Manual cleanup is required.", entryId, blobName);
            }
        }

        return NoContent();
    }

    // --- Entry comments ---

    [HttpGet("entries/{entryId:guid}/comments")]
    public async Task<IActionResult> GetEntryComments(Guid entryId)
    {
        var entry = await _planService.GetEntryByIdAsync(entryId);
        if (entry is null) return NotFound();

        if (!await CanReadEntryAsync(entry)) return Forbidden();

        var comments = await _planService.GetCommentsForEntryAsync(entryId);

        var dtos = new List<TreatmentPlanEntryCommentDto>(comments.Count);
        foreach (var c in comments)
            dtos.Add(await ToDtoWithPhotoAsync(c));

        return Ok(dtos);
    }

    /// <summary>
    /// Adds a comment, optionally with a photo (User Story 5 — patients giving feedback
    /// with images). Accepts multipart/form-data so text and file arrive together; the
    /// photo is optional, and a text-only comment is still the common case.
    /// </summary>
    [HttpPost("entries/{entryId:guid}/comments")]
    [RequestSizeLimit(PhotoRules.MaxBytes + 1024 * 1024)] // photo allowance + room for the form itself
    public async Task<IActionResult> AddEntryComment(
        Guid entryId,
        [FromForm] string? text,
        IFormFile? photo)
    {
        var entry = await _planService.GetEntryByIdAsync(entryId);
        if (entry is null) return NotFound();

        // Same rule as reading the thread: client on their own plans, physio on plans
        // they own, admin anywhere. Both sides may attach photos.
        if (!await CanReadEntryAsync(entry)) return Forbidden();

        var userId = GetCurrentUserId();
        if (!userId.HasValue) return Unauthorized();

        string? blobName = null;
        if (photo is not null && photo.Length > 0)
        {
            // Validate BEFORE touching storage so a bad upload costs nothing.
            var validationError = PhotoRules.Validate(photo.ContentType, photo.Length);
            if (validationError is not null)
                throw DomainException.Validation(validationError);

            await using var stream = photo.OpenReadStream();
            blobName = await _blobStorage.UploadPhotoAsync(stream, photo.ContentType, HttpContext.RequestAborted);
        }

        try
        {
            var comment = await _planService.AddCommentAsync(entryId, userId.Value, text ?? string.Empty, blobName);
            return Created(
                $"api/treatmentplans/entries/{entryId}/comments/{comment.Id}",
                await ToDtoWithPhotoAsync(comment));
        }
        catch
        {
            // The photo made it to storage but the comment row didn't. Without this the
            // blob would linger forever with nothing referencing it.
            if (blobName is not null)
            {
                try { await _blobStorage.DeletePhotoAsync(blobName); }
                catch (Exception cleanupEx)
                {
                    _logger.LogError(cleanupEx,
                        "Failed to clean up orphaned photo {BlobName} after the comment insert failed.", blobName);
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Maps a comment and mints a fresh SAS URL for its photo. A storage failure degrades
    /// to a photo-less comment rather than failing the whole thread — the text is the more
    /// important half, and one unreadable blob shouldn't blank the conversation.
    /// </summary>
    private async Task<TreatmentPlanEntryCommentDto> ToDtoWithPhotoAsync(TreatmentPlanEntryComment comment)
    {
        var dto = comment.ToDto();
        if (string.IsNullOrEmpty(comment.PhotoBlobName)) return dto;

        try
        {
            dto.PhotoUrl = await _blobStorage.GetPhotoSasUrlAsync(comment.PhotoBlobName, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not generate a photo URL for comment {CommentId} (blob {BlobName}); " +
                "returning the comment without its photo.", comment.Id, comment.PhotoBlobName);
        }

        return dto;
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
