using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Availability;
using URFYSIO.Shared.DTOs.Registration;
using URFYSIO.Shared.DTOs.TreatmentPlans;
using URFYSIO.Shared.DTOs.Users;

namespace URFYSIO.App.Services;

public interface IApiService
{
    // Users
    Task<UserDto?> GetCurrentUserAsync();
    Task<List<UserDto>> GetUsersAsync();
    Task<List<UserDto>> GetPhysiotherapistsAsync();
    Task<List<UserDto>> GetClientsAsync();
    Task<UserDto?> CreateUserAsync(CreateUserDto dto);
    Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserDto dto);
    Task<bool> DeactivateUserAsync(Guid id);

    /// <summary>
    /// Approves an account (IsActive = true). Used for SSO sign-ups, which are
    /// auto-created inactive and can't log in until an admin approves them.
    /// </summary>
    Task<(bool Success, string Message)> ActivateUserAsync(Guid id);
    Task<(bool Success, string Message)> DeleteUserPermanentlyAsync(Guid id);
    Task<UserProfileDto?> GetMyProfileAsync();
    Task<UserProfileDto?> UpdateMyProfileAsync(UpdateUserProfileDto dto);
    Task<(bool Success, string Message)> ChangeMyPasswordAsync(ChangePasswordDto dto);
    Task<(bool Success, string Message)> AdminResetPasswordAsync(Guid userId);

    // Appointments
    Task<AppointmentDto?> GetAppointmentByIdAsync(Guid id);
    Task<List<AppointmentDto>> GetAppointmentsByClientAsync(Guid clientProfileId);
    Task<List<AppointmentDto>> GetAppointmentsByPhysioAsync(Guid physioProfileId);
    Task<List<AppointmentDto>> GetAllAppointmentsAsync();
    Task<AppointmentDto?> CreateAppointmentAsync(CreateAppointmentDto dto);
    Task<bool> CancelAppointmentAsync(Guid id);
    Task<AppointmentDto?> RescheduleAppointmentAsync(Guid id, Guid newSlotId);

    // Availability
    Task<List<AvailabilitySlotDto>> GetAvailableSlotsAsync(Guid? physioProfileId = null);
    Task<List<AvailabilitySlotDto>> GetPhysioSlotsAsync(Guid physioProfileId);
    Task<(AvailabilitySlotDto? Slot, string? Error)> CreateAvailabilitySlotAsync(CreateAvailabilitySlotDto dto);
    Task<AvailabilitySlotDto?> UpdateAvailabilitySlotAsync(Guid id, UpdateAvailabilitySlotDto dto);
    Task<bool> DeleteAvailabilitySlotAsync(Guid id);

    // Treatment Plans
    Task<List<TreatmentPlanDto>> GetTreatmentPlansByClientAsync(Guid clientProfileId);
    Task<List<TreatmentPlanDto>> GetTreatmentPlansByPhysioAsync(Guid physioProfileId);
    Task<(TreatmentPlanDto? Plan, string? Error)> CreateTreatmentPlanAsync(CreateTreatmentPlanDto dto);
    Task<TreatmentPlanDto?> UpdateTreatmentPlanAsync(Guid id, UpdateTreatmentPlanDto dto);
    Task<bool> DeleteTreatmentPlanAsync(Guid id);
    Task<TreatmentPlanDto?> AddTreatmentPlanEntryAsync(Guid planId, CreateTreatmentPlanEntryDto dto);
    Task<TreatmentPlanEntryDto?> UpdateTreatmentPlanEntryAsync(Guid entryId, UpdateTreatmentPlanEntryDto dto);
    Task<bool> DeleteTreatmentPlanEntryAsync(Guid entryId);
    Task<TreatmentPlanDto?> CompleteTreatmentPlanAsync(Guid id);
    Task<TreatmentPlanDto?> ReopenTreatmentPlanAsync(Guid id);
    Task<List<TreatmentPlanEntryCommentDto>> GetEntryCommentsAsync(Guid entryId);
    Task<(TreatmentPlanEntryCommentDto? Comment, string? Error)> AddEntryCommentAsync(Guid entryId, string text);

    // Registration
    Task<(bool Success, string? Error)> SubmitRegistrationAsync(CreateRegistrationRequestDto dto);
    /// <summary>
    /// Loads registration requests, optionally filtered server-side by status.
    /// Unlike most list getters this THROWS on HTTP/network failure instead of
    /// returning an empty list — the admin must be able to tell "no pending
    /// registrations" apart from "the request failed" (e.g. Azure SQL serverless
    /// resuming from auto-pause made the list silently appear empty).
    /// </summary>
    Task<List<RegistrationRequestDto>> GetRegistrationRequestsAsync(Shared.Enums.RegistrationStatus? status = null);
    Task<(bool Success, string Message)> ApproveRegistrationAsync(Guid id);
    Task<bool> RejectRegistrationAsync(Guid id);
}
