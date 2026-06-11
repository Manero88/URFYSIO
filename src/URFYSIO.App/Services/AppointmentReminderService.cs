using System.Diagnostics;
using Plugin.LocalNotification;
using Plugin.LocalNotification.Core.Models;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.Enums;

namespace URFYSIO.App.Services;

/// <summary>
/// <see cref="IAppointmentReminderService"/> backed by Plugin.LocalNotification.
///
/// Time handling: the app treats appointment <see cref="AppointmentDto.StartTime"/> as a
/// naive local wall-clock value (a physio picks "09:00" and the whole app displays "09:00").
/// We therefore interpret StartTime as local and fire the reminder <see cref="LeadTime"/>
/// before it, and show the same HH:mm the rest of the UI shows — so the reminder text always
/// matches the screen.
/// </summary>
public class AppointmentReminderService : IAppointmentReminderService
{
    // Lead time before the appointment that the reminder fires. 24h per User Story 8.
    private static readonly TimeSpan LeadTime = TimeSpan.FromHours(24);

    public async Task<bool> EnsurePermissionAsync()
    {
        try
        {
            if (await LocalNotificationCenter.Current.AreNotificationsEnabled())
                return true;

            // Android 13+ shows the system POST_NOTIFICATIONS dialog here; older platforms
            // return true without prompting.
            return await LocalNotificationCenter.Current.RequestNotificationPermission();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Reminders] permission check failed: {ex.Message}");
            return false;
        }
    }

    public async Task SyncRemindersAsync(IEnumerable<AppointmentDto> appointments)
    {
        try
        {
            // Full reconcile: clear every pending appointment reminder, then re-add the ones
            // that still apply. Reminders are the ONLY notifications this app schedules, so a
            // blanket clear is safe and makes cancelled/rescheduled/removed appointments drop
            // out without per-id bookkeeping.
            LocalNotificationCenter.Current.CancelAll();

            var now = DateTime.Now;
            foreach (var appt in appointments)
            {
                if (appt.Status != AppointmentStatus.Scheduled)
                    continue;

                // Interpret the naive StartTime as local wall-clock (see class remarks).
                var startLocal = DateTime.SpecifyKind(appt.StartTime, DateTimeKind.Local);
                var fireTime = startLocal - LeadTime;

                // Skip appointments whose reminder moment has already passed (includes those
                // less than the lead time away) and anything in the past.
                if (fireTime <= now)
                    continue;

                var request = new NotificationRequest
                {
                    NotificationId = ToNotificationId(appt.Id),
                    Title = "Appointment reminder",
                    Description =
                        $"You have a physiotherapy appointment tomorrow at {startLocal:HH:mm} " +
                        $"with {appt.PhysiotherapistName}",
                    Schedule = new NotificationRequestSchedule { NotifyTime = fireTime }
                };

                await LocalNotificationCenter.Current.Show(request);
            }
        }
        catch (Exception ex)
        {
            // Reminders are best-effort: a scheduling failure must never break the appointments
            // screen. Log and carry on.
            Debug.WriteLine($"[Reminders] sync failed: {ex.Message}");
        }
    }

    public Task CancelReminderAsync(Guid appointmentId)
    {
        try
        {
            LocalNotificationCenter.Current.Cancel(ToNotificationId(appointmentId));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Reminders] cancel failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    // Plugin.LocalNotification keys notifications by int. Derive a stable, non-negative int
    // from the appointment Guid so the same appointment always maps to the same notification
    // id (needed to match/cancel). Guid.GetHashCode is deterministic for a given Guid.
    private static int ToNotificationId(Guid id) => id.GetHashCode() & 0x7FFFFFFF;
}
