using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;

namespace URFYSIO.Infrastructure.Services;

/// <summary>
/// GDPR hard-delete. Lives in its own service (rather than UserService) because it
/// needs the Auth0 management client AND must orchestrate deletion across many tables
/// in FK-safe order — concerns that don't belong in the CRUD-focused UserService.
///
/// Related-data strategy (see method body for the FK-safe ordering):
///   • ClientProfile / PhysiotherapistProfile — deleted (belong to the user).
///   • Comments authored by the user — deleted (the User→Comment FK is Restrict, so
///     they MUST go before the user row; GDPR also wants the author's content removed).
///   • Client's appointments — deleted.
///   • Client's treatment plans — deleted (entries + their comments removed explicitly).
///   • Physio's future appointments / active plans — deletion is REFUSED (those involve
///     other clients; the admin must cancel/reassign first). Past appointments and
///     completed plans are deleted so the profile row can be removed.
///   • Physio's availability slots — deleted.
///   • RegistrationRequests this user processed — ProcessedByUserId nulled (audit FK).
///
/// Auth0 ordering: we delete the LOCAL data first, then the Auth0 account. If Auth0
/// deletion fails we log and leave the local erasure in place — the GDPR-relevant
/// personal data is already gone locally, and an admin can remove the Auth0 record
/// manually. (Doing Auth0 first would risk a dead Auth0 reference if the local delete
/// then failed, and would leave personal data behind — the worse outcome.)
/// </summary>
public class UserDeletionService : IUserDeletionService
{
    private readonly AppDbContext _db;
    private readonly IAuth0ManagementService _auth0;
    private readonly ILogger<UserDeletionService> _logger;

    public UserDeletionService(
        AppDbContext db,
        IAuth0ManagementService auth0,
        ILogger<UserDeletionService> logger)
    {
        _db = db;
        _auth0 = auth0;
        _logger = logger;
    }

    public async Task DeleteUserPermanentlyAsync(Guid userId)
    {
        var user = await _db.Users
            .Include(u => u.ClientProfile)
            .Include(u => u.PhysiotherapistProfile)
            .FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw DomainException.NotFound("User not found.");

        // Guard: never delete the last administrator — that would lock everyone out.
        if (user.Role == UserRole.Admin)
        {
            var adminCount = await _db.Users.CountAsync(u => u.Role == UserRole.Admin);
            if (adminCount <= 1)
                throw DomainException.Conflict(
                    "Cannot delete the last administrator account. Promote another admin first.");
        }

        var now = DateTime.UtcNow;

        // ---- Safety checks (run BEFORE any deletion so a refusal changes nothing) ----
        if (user.PhysiotherapistProfile is not null)
        {
            var physioProfileId = user.PhysiotherapistProfile.Id;

            var hasFutureAppointments = await _db.Appointments.AnyAsync(a =>
                a.PhysiotherapistProfileId == physioProfileId
                && a.Status == AppointmentStatus.Scheduled
                && a.StartTime > now);
            if (hasFutureAppointments)
                throw DomainException.Conflict(
                    "This physiotherapist has upcoming appointments. Cancel or reassign them before deleting the account.");

            var hasActivePlans = await _db.TreatmentPlans.AnyAsync(t =>
                t.PhysiotherapistProfileId == physioProfileId && !t.IsCompleted);
            if (hasActivePlans)
                throw DomainException.Conflict(
                    "This physiotherapist has active treatment plans. Complete or reassign them before deleting the account.");
        }

        // ---- Delete related data in FK-safe order, then the user ----

        // 1. Comments authored by this user (User→Comment FK is Restrict).
        var authoredComments = await _db.TreatmentPlanEntryComments
            .Where(c => c.UserId == userId)
            .ToListAsync();
        _db.TreatmentPlanEntryComments.RemoveRange(authoredComments);

        // 2. Audit FK: null out this user from any registration requests they processed.
        var processedRequests = await _db.RegistrationRequests
            .Where(r => r.ProcessedByUserId == userId)
            .ToListAsync();
        foreach (var r in processedRequests)
            r.ProcessedByUserId = null;

        // 3. Client-side data.
        if (user.ClientProfile is not null)
        {
            var clientProfileId = user.ClientProfile.Id;
            await DeletePlansAsync(t => t.ClientProfileId == clientProfileId);

            var clientAppointments = await _db.Appointments
                .Where(a => a.ClientProfileId == clientProfileId)
                .ToListAsync();
            _db.Appointments.RemoveRange(clientAppointments);
        }

        // 4. Physiotherapist-side data (safety checks above already passed).
        if (user.PhysiotherapistProfile is not null)
        {
            var physioProfileId = user.PhysiotherapistProfile.Id;
            await DeletePlansAsync(t => t.PhysiotherapistProfileId == physioProfileId);

            var physioAppointments = await _db.Appointments
                .Where(a => a.PhysiotherapistProfileId == physioProfileId)
                .ToListAsync();
            _db.Appointments.RemoveRange(physioAppointments);

            var slots = await _db.AvailabilitySlots
                .Where(s => s.PhysiotherapistProfileId == physioProfileId)
                .ToListAsync();
            _db.AvailabilitySlots.RemoveRange(slots);
        }

        // 5. Profiles.
        if (user.ClientProfile is not null)
            _db.ClientProfiles.Remove(user.ClientProfile);
        if (user.PhysiotherapistProfile is not null)
            _db.PhysiotherapistProfiles.Remove(user.PhysiotherapistProfile);

        // 6. The user row itself.
        _db.Users.Remove(user);

        // Capture the Auth0 id before we lose the reference.
        var auth0Id = user.Auth0Id;

        await _db.SaveChangesAsync();

        // 7. Auth0 erasure — local data is already gone (see class remarks for the
        //    ordering rationale). A failure here is logged, not thrown.
        if (!string.IsNullOrEmpty(auth0Id))
        {
            var ok = await _auth0.DeleteUserAsync(auth0Id);
            if (!ok)
                _logger.LogWarning(
                    "Local data for user {UserId} was deleted, but the Auth0 account {Auth0Id} " +
                    "could not be removed. Manual cleanup in the Auth0 dashboard is required.",
                    userId, auth0Id);
        }
    }

    // Deletes treatment plans matching the predicate together with their entries and
    // the entries' comments. We remove them explicitly (rather than relying on DB
    // cascade) so the behaviour is identical under the EF in-memory provider used by
    // unit tests and under SQL Server in production.
    private async Task DeletePlansAsync(Expression<Func<TreatmentPlan, bool>> predicate)
    {
        var plans = await _db.TreatmentPlans
            .Where(predicate)
            .Include(t => t.Entries)
            .ThenInclude(e => e.Comments)
            .ToListAsync();

        foreach (var plan in plans)
        {
            foreach (var entry in plan.Entries)
                _db.TreatmentPlanEntryComments.RemoveRange(entry.Comments);
            _db.TreatmentPlanEntries.RemoveRange(plan.Entries);
        }
        _db.TreatmentPlans.RemoveRange(plans);
    }
}
