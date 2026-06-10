using URFYSIO.Core.Common;
using URFYSIO.Core.Entities;

namespace URFYSIO.Core.Interfaces;

public interface IAvailabilityService
{
    Task<AvailabilitySlot?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<AvailabilitySlot>> GetByPhysiotherapistProfileIdAsync(Guid physioProfileId, DateTime? from = null, DateTime? to = null);
    Task<IReadOnlyList<AvailabilitySlot>> GetAvailableSlotsAsync(Guid? physioProfileId = null, DateTime? from = null, DateTime? to = null);

    /// <summary>
    /// Creates a slot. Validation failures (start ≥ end, slot in the past, overlap
    /// with an existing slot) are returned as a failed <see cref="ServiceResult{T}"/>,
    /// NOT thrown — these are expected user-input outcomes, and throwing made the
    /// debugger break on every overlapping-slot attempt.
    /// </summary>
    Task<ServiceResult<AvailabilitySlot>> CreateAsync(AvailabilitySlot slot);

    /// <summary>Same result-based contract as <see cref="CreateAsync"/> (booked slot / invalid times fail).</summary>
    Task<ServiceResult<AvailabilitySlot>> UpdateAsync(AvailabilitySlot slot);

    /// <summary>
    /// Deletes an unbooked slot. Returns false when the slot doesn't exist (or is
    /// booked — callers must pre-check <c>IsBooked</c>; the controller does).
    /// </summary>
    Task<bool> DeleteAsync(Guid id);
}
