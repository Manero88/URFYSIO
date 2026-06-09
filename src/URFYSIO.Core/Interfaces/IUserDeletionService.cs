namespace URFYSIO.Core.Interfaces;

/// <summary>
/// Permanent ("hard") user deletion for GDPR right-to-erasure. Distinct from
/// <see cref="IUserService.DeactivateAsync"/>, which is a reversible soft-disable.
/// Removes the local user, their profile(s), all related data, and the Auth0 account.
/// Throws <see cref="Core.Exceptions.DomainException"/> when deletion must be refused
/// (e.g. a physiotherapist still has upcoming appointments or active plans).
/// </summary>
public interface IUserDeletionService
{
    Task DeleteUserPermanentlyAsync(Guid userId);
}
