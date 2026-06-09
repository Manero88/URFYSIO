using URFYSIO.Core.Entities;

namespace URFYSIO.Core.Interfaces;

public interface IAvailabilityService
{
    Task<AvailabilitySlot?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<AvailabilitySlot>> GetByPhysiotherapistProfileIdAsync(Guid physioProfileId, DateTime? from = null, DateTime? to = null);
    Task<IReadOnlyList<AvailabilitySlot>> GetAvailableSlotsAsync(Guid? physioProfileId = null, DateTime? from = null, DateTime? to = null);
    Task<AvailabilitySlot> CreateAsync(AvailabilitySlot slot);
    Task<AvailabilitySlot> UpdateAsync(AvailabilitySlot slot);
    Task<bool> DeleteAsync(Guid id);
}
