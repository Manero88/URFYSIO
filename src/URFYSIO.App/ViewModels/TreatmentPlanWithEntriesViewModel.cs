using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.TreatmentPlans;

namespace URFYSIO.App.ViewModels;

/// <summary>
/// Thin plan wrapper: proxies the read-only fields the page binds to and replaces
/// the raw <see cref="TreatmentPlanEntryDto"/> list with a list of
/// <see cref="TreatmentPlanEntryViewModel"/>s so each entry gets its own comment
/// state. We also pre-compute a friendly "Completed on" label here rather than
/// in XAML so the date format is consistent and stays in one place.
/// </summary>
public class TreatmentPlanWithEntriesViewModel
{
    public TreatmentPlanDto Dto { get; }

    public Guid Id => Dto.Id;
    public string Title => Dto.Title;
    public string? Description => Dto.Description;
    public Guid ClientProfileId => Dto.ClientProfileId;
    public string ClientName => Dto.ClientName;
    public Guid PhysiotherapistProfileId => Dto.PhysiotherapistProfileId;
    public string PhysiotherapistName => Dto.PhysiotherapistName;
    public DateTime CreatedAt => Dto.CreatedAt;
    public DateTime? UpdatedAt => Dto.UpdatedAt;
    public bool IsCompleted => Dto.IsCompleted;
    public DateTime? CompletedAt => Dto.CompletedAt;

    public string CompletedOnLabel => Dto.CompletedAt.HasValue
        ? $"Completed on {Dto.CompletedAt.Value:dd/MM/yyyy}"
        : string.Empty;

    public List<TreatmentPlanEntryViewModel> Entries { get; }

    public TreatmentPlanWithEntriesViewModel(
        TreatmentPlanDto dto, IApiService api, IPhotoPickerService photoPicker)
    {
        Dto = dto;
        Entries = dto.Entries
            .OrderBy(e => e.OrderIndex)
            .Select(e => new TreatmentPlanEntryViewModel(e, api, photoPicker))
            .ToList();
    }
}
