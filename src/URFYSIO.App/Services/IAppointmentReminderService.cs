using URFYSIO.Shared.DTOs.Appointments;

namespace URFYSIO.App.Services;

/// <summary>
/// Schedules on-device local notifications that remind a client about an upcoming
/// physiotherapy appointment (User Story 8). Reminders fire a fixed lead time before
/// the appointment start. No server/push infrastructure is involved.
/// </summary>
public interface IAppointmentReminderService
{
    /// <summary>
    /// Ensures notification permission is granted, requesting it from the OS if needed
    /// (Android 13+ requires a runtime POST_NOTIFICATIONS prompt). Returns false if the
    /// user denied it or the platform reported notifications are unavailable. Never throws.
    /// </summary>
    Task<bool> EnsurePermissionAsync();

    /// <summary>
    /// Reconciles all scheduled reminders against the supplied set of appointments:
    /// cancels every previously-scheduled appointment reminder, then schedules a fresh
    /// reminder for each appointment that is still in the future and Scheduled. This makes
    /// cancelled / rescheduled / removed appointments fall out automatically. Never throws.
    /// </summary>
    Task SyncRemindersAsync(IEnumerable<AppointmentDto> appointments);

    /// <summary>
    /// Cancels the reminder for a single appointment by its id (e.g. right after the
    /// client cancels it, before the list reloads). Never throws.
    /// </summary>
    Task CancelReminderAsync(Guid appointmentId);
}
