using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;

namespace URFYSIO.Core.Interfaces;

public interface IRegistrationService
{
    Task<RegistrationRequest?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<RegistrationRequest>> GetAllAsync(RegistrationStatus? statusFilter = null);
    Task<RegistrationRequest> CreateAsync(RegistrationRequest request);
    Task<RegistrationRequest> ApproveAsync(Guid id, Guid processedByUserId);
    Task<RegistrationRequest> RejectAsync(Guid id, Guid processedByUserId);
}
