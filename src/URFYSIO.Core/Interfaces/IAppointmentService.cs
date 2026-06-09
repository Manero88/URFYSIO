using URFYSIO.Core.Entities;

namespace URFYSIO.Core.Interfaces;

public interface IAppointmentService
{
    Task<Appointment?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<Appointment>> GetByClientProfileIdAsync(Guid clientProfileId);
    Task<IReadOnlyList<Appointment>> GetByPhysiotherapistProfileIdAsync(Guid physioProfileId);
    Task<IReadOnlyList<Appointment>> GetAllAsync(DateTime? from = null, DateTime? to = null);
    Task<Appointment> CreateAsync(Appointment appointment);
    Task<Appointment> UpdateAsync(Appointment appointment);
    Task<bool> CancelAsync(Guid id);
    Task<Appointment> RescheduleAsync(Guid id, Guid newSlotId);
}
