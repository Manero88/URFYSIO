namespace URFYSIO.Shared.DTOs.TreatmentPlans;

public class TreatmentPlanDto
{
    public Guid Id { get; set; }
    public Guid ClientProfileId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public Guid PhysiotherapistProfileId { get; set; }
    public string PhysiotherapistName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Lifecycle — set by the /complete and /reopen endpoints. CompletedAt is null
    // until the plan is first marked complete and is preserved across reopen/
    // re-complete cycles only by virtue of overwriting; the History tab orders by
    // this field, so it tracks the *latest* completion.
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }

    public List<TreatmentPlanEntryDto> Entries { get; set; } = [];
}

public class TreatmentPlanEntryDto
{
    public Guid Id { get; set; }
    public Guid TreatmentPlanId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int OrderIndex { get; set; }
    public bool IsCompleted { get; set; }
}

public class CreateTreatmentPlanDto
{
    public Guid ClientProfileId { get; set; }
    public Guid PhysiotherapistProfileId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class UpdateTreatmentPlanDto
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class CreateTreatmentPlanEntryDto
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int OrderIndex { get; set; }
}

public class UpdateTreatmentPlanEntryDto
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int OrderIndex { get; set; }
    public bool IsCompleted { get; set; }
}

// --- Entry comments ---

public class TreatmentPlanEntryCommentDto
{
    public Guid Id { get; set; }
    public Guid TreatmentPlanEntryId { get; set; }
    public string Text { get; set; } = string.Empty;
    // Pre-computed on the server so the UI doesn't have to look up the author —
    // and so a deleted-then-re-added user can't accidentally surface someone else's
    // current name on an old comment.
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorRole { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Short-lived SAS URL for the attached photo, or null when there is no photo (or when
    /// the URL could not be minted). Generated per response — never stored — because the
    /// blob container is private. Treat it as valid for about an hour: re-fetch the thread
    /// rather than caching this value long-term.
    /// </summary>
    public string? PhotoUrl { get; set; }

    /// <summary>Lets the UI reserve space / show an image frame without null-checking a string in XAML.</summary>
    public bool HasPhoto => !string.IsNullOrEmpty(PhotoUrl);
}

public class CreateTreatmentPlanEntryCommentDto
{
    public string Text { get; set; } = string.Empty;
}
