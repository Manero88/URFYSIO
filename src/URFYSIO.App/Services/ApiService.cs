using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Availability;
using URFYSIO.Shared.DTOs.Registration;
using URFYSIO.Shared.DTOs.TreatmentPlans;
using URFYSIO.Shared.DTOs.Users;

namespace URFYSIO.App.Services;

public class ApiService : IApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<ApiService> _logger;

    public ApiService(HttpClient http, ILogger<ApiService> logger)
    {
        _http = http;
        _logger = logger;
    }

    // --- Users ---
    public async Task<UserDto?> GetCurrentUserAsync() =>
        await SafeGetAsync<UserDto>("api/users/me");

    public async Task<List<UserDto>> GetUsersAsync() =>
        await SafeGetListAsync<UserDto>("api/users");

    public async Task<List<UserDto>> GetPhysiotherapistsAsync() =>
        await SafeGetListAsync<UserDto>("api/users/physiotherapists");

    public async Task<List<UserDto>> GetClientsAsync() =>
        await SafeGetListAsync<UserDto>("api/users/clients");

    public async Task<UserDto?> CreateUserAsync(CreateUserDto dto)
    {
        var resp = await SafePostAsync<UserDto>("api/users", dto);
        return resp;
    }

    public async Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserDto dto)
    {
        try
        {
            var response = await _http.PutAsJsonAsync($"api/users/{id}", dto);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<UserDto>();
            _logger.LogWarning("PUT api/users/{Id} failed with {Status}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update user failed");
            return null;
        }
    }

    public async Task<bool> DeactivateUserAsync(Guid id)
    {
        try
        {
            var response = await _http.DeleteAsync($"api/users/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deactivate user failed");
            return false;
        }
    }

    // Returns the API's message so the admin sees WHY a permanent delete was refused
    // (e.g. physiotherapist has upcoming appointments, last admin, etc.).
    public async Task<(bool Success, string Message)> DeleteUserPermanentlyAsync(Guid id)
    {
        try
        {
            var response = await _http.DeleteAsync($"api/users/{id}/permanent");
            // ReadProblemDetailAsync pulls the "message" field on success and the
            // ProblemDetails "detail" on failure (DomainException refusals).
            var message = await ReadProblemDetailAsync(response);
            return (response.IsSuccessStatusCode,
                message ?? (response.IsSuccessStatusCode ? "User permanently deleted." : "Failed to delete user."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Permanent delete user failed");
            return (false, ex.Message);
        }
    }

    public async Task<UserProfileDto?> GetMyProfileAsync() =>
        await SafeGetAsync<UserProfileDto>("api/users/me/profile");

    public async Task<UserProfileDto?> UpdateMyProfileAsync(UpdateUserProfileDto dto)
    {
        try
        {
            var response = await _http.PutAsJsonAsync("api/users/me/profile", dto);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<UserProfileDto>();
            _logger.LogWarning("PUT api/users/me/profile failed with {Status}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update profile failed");
            return null;
        }
    }

    public async Task<(bool Success, string Message)> ChangeMyPasswordAsync(ChangePasswordDto dto) =>
        await PostMessageAsync("api/users/me/change-password", dto);

    public async Task<(bool Success, string Message)> AdminResetPasswordAsync(Guid userId) =>
        await PostMessageAsync($"api/users/{userId}/reset-password", null);

    private async Task<(bool Success, string Message)> PostMessageAsync(string url, object? body)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(url, body);
            var payload = await response.Content.ReadFromJsonAsync<MessageResponse>();
            var message = payload?.Message ?? (response.IsSuccessStatusCode ? "OK" : "Request failed.");
            return (response.IsSuccessStatusCode, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POST {Url} failed", url);
            return (false, ex.Message);
        }
    }

    private sealed class MessageResponse
    {
        public string? Message { get; set; }
    }

    // --- Appointments ---
    public async Task<AppointmentDto?> GetAppointmentByIdAsync(Guid id) =>
        await SafeGetAsync<AppointmentDto>($"api/appointments/{id}");

    public async Task<List<AppointmentDto>> GetAppointmentsByClientAsync(Guid clientProfileId) =>
        await SafeGetListAsync<AppointmentDto>($"api/appointments/client/{clientProfileId}");

    public async Task<List<AppointmentDto>> GetAppointmentsByPhysioAsync(Guid physioProfileId) =>
        await SafeGetListAsync<AppointmentDto>($"api/appointments/physiotherapist/{physioProfileId}");

    public async Task<List<AppointmentDto>> GetAllAppointmentsAsync() =>
        await SafeGetListAsync<AppointmentDto>("api/appointments");

    public async Task<AppointmentDto?> CreateAppointmentAsync(CreateAppointmentDto dto) =>
        await SafePostAsync<AppointmentDto>("api/appointments", dto);

    public async Task<bool> CancelAppointmentAsync(Guid id) =>
        await SafePostBoolAsync($"api/appointments/{id}/cancel", null);

    public async Task<AppointmentDto?> RescheduleAppointmentAsync(Guid id, Guid newSlotId) =>
        await SafePostAsync<AppointmentDto>($"api/appointments/{id}/reschedule",
            new RescheduleAppointmentDto { NewAvailabilitySlotId = newSlotId });

    // --- Availability ---
    public async Task<List<AvailabilitySlotDto>> GetAvailableSlotsAsync(Guid? physioProfileId = null)
    {
        var url = physioProfileId.HasValue
            ? $"api/availability/available?physiotherapistProfileId={physioProfileId}"
            : "api/availability/available";
        return await SafeGetListAsync<AvailabilitySlotDto>(url);
    }

    public async Task<List<AvailabilitySlotDto>> GetPhysioSlotsAsync(Guid physioProfileId) =>
        await SafeGetListAsync<AvailabilitySlotDto>($"api/availability/physiotherapist/{physioProfileId}");

    public async Task<(AvailabilitySlotDto? Slot, string? Error)> CreateAvailabilitySlotAsync(CreateAvailabilitySlotDto dto) =>
        await SafePostWithErrorAsync<AvailabilitySlotDto>("api/availability", dto);

    public async Task<AvailabilitySlotDto?> UpdateAvailabilitySlotAsync(Guid id, UpdateAvailabilitySlotDto dto)
    {
        try
        {
            var response = await _http.PutAsJsonAsync($"api/availability/{id}", dto);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<AvailabilitySlotDto>();
            _logger.LogWarning("PUT api/availability/{Id} failed with {Status}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update availability slot failed");
            return null;
        }
    }

    public async Task<bool> DeleteAvailabilitySlotAsync(Guid id)
    {
        try
        {
            var response = await _http.DeleteAsync($"api/availability/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete availability slot failed");
            return false;
        }
    }

    // --- Treatment Plans ---
    public async Task<List<TreatmentPlanDto>> GetTreatmentPlansByClientAsync(Guid clientProfileId) =>
        await SafeGetListAsync<TreatmentPlanDto>($"api/treatmentplans/client/{clientProfileId}");

    public async Task<List<TreatmentPlanDto>> GetTreatmentPlansByPhysioAsync(Guid physioProfileId) =>
        await SafeGetListAsync<TreatmentPlanDto>($"api/treatmentplans/physiotherapist/{physioProfileId}");

    public async Task<(TreatmentPlanDto? Plan, string? Error)> CreateTreatmentPlanAsync(CreateTreatmentPlanDto dto) =>
        await SafePostWithErrorAsync<TreatmentPlanDto>("api/treatmentplans", dto);

    public async Task<TreatmentPlanDto?> UpdateTreatmentPlanAsync(Guid id, UpdateTreatmentPlanDto dto)
    {
        try
        {
            var response = await _http.PutAsJsonAsync($"api/treatmentplans/{id}", dto);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<TreatmentPlanDto>();
            _logger.LogWarning("PUT api/treatmentplans/{Id} failed with {Status}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update treatment plan failed");
            return null;
        }
    }

    public async Task<bool> DeleteTreatmentPlanAsync(Guid id)
    {
        try
        {
            var response = await _http.DeleteAsync($"api/treatmentplans/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete treatment plan failed");
            return false;
        }
    }

    public async Task<TreatmentPlanDto?> AddTreatmentPlanEntryAsync(Guid planId, CreateTreatmentPlanEntryDto dto) =>
        await SafePostAsync<TreatmentPlanDto>($"api/treatmentplans/{planId}/entries", dto);

    public async Task<TreatmentPlanEntryDto?> UpdateTreatmentPlanEntryAsync(Guid entryId, UpdateTreatmentPlanEntryDto dto)
    {
        try
        {
            var response = await _http.PutAsJsonAsync($"api/treatmentplans/entries/{entryId}", dto);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<TreatmentPlanEntryDto>();
            _logger.LogWarning("PUT api/treatmentplans/entries/{Id} failed with {Status}", entryId, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update treatment plan entry failed");
            return null;
        }
    }

    public async Task<bool> DeleteTreatmentPlanEntryAsync(Guid entryId)
    {
        try
        {
            var response = await _http.DeleteAsync($"api/treatmentplans/entries/{entryId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete treatment plan entry failed");
            return false;
        }
    }

    public async Task<TreatmentPlanDto?> CompleteTreatmentPlanAsync(Guid id) =>
        await SafePostAsync<TreatmentPlanDto>($"api/treatmentplans/{id}/complete", null);

    public async Task<TreatmentPlanDto?> ReopenTreatmentPlanAsync(Guid id) =>
        await SafePostAsync<TreatmentPlanDto>($"api/treatmentplans/{id}/reopen", null);

    public async Task<List<TreatmentPlanEntryCommentDto>> GetEntryCommentsAsync(Guid entryId) =>
        await SafeGetListAsync<TreatmentPlanEntryCommentDto>($"api/treatmentplans/entries/{entryId}/comments");

    // Uses the error-extracting variant so the UI can show the API's actual
    // ProblemDetails message (e.g. "Comment text cannot exceed 1000 characters")
    // instead of a generic "Failed to add comment".
    public async Task<(TreatmentPlanEntryCommentDto? Comment, string? Error)> AddEntryCommentAsync(Guid entryId, string text) =>
        await SafePostWithErrorAsync<TreatmentPlanEntryCommentDto>(
            $"api/treatmentplans/entries/{entryId}/comments",
            new CreateTreatmentPlanEntryCommentDto { Text = text });

    // --- Registration ---
    public async Task<bool> SubmitRegistrationAsync(CreateRegistrationRequestDto dto) =>
        await SafePostBoolAsync("api/registration", dto);

    // Deliberately NOT a SafeGetListAsync call: swallowing a failed load here made the
    // admin's Pending Registrations list look empty whenever the API/DB hiccuped
    // (verified cause: Azure SQL serverless auto-pause returning "database not
    // currently available" while resuming). Throwing lets the dashboard show a real
    // error banner instead of a misleading empty list.
    public async Task<List<RegistrationRequestDto>> GetRegistrationRequestsAsync(URFYSIO.Shared.Enums.RegistrationStatus? status = null)
    {
        var url = status.HasValue ? $"api/registration?status={status}" : "api/registration";
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GET {Url} failed with {Status}", url, response.StatusCode);
            throw new HttpRequestException(
                $"Could not load registrations (HTTP {(int)response.StatusCode}). " +
                "The server or database may be starting up — try again in a moment.");
        }
        return await response.Content.ReadFromJsonAsync<List<RegistrationRequestDto>>() ?? [];
    }

    // Returns the API's message on failure (e.g. "An Auth0 account already exists for
    // this email address." or the missing-scope message) so the admin sees WHY the
    // approval didn't go through, not just a generic failure.
    public async Task<(bool Success, string Message)> ApproveRegistrationAsync(Guid id) =>
        await PostMessageAsync($"api/registration/{id}/approve", null);

    public async Task<bool> RejectRegistrationAsync(Guid id) =>
        await SafePostBoolAsync($"api/registration/{id}/reject", null);

    // --- Helpers ---
    private async Task<T?> SafeGetAsync<T>(string url) where T : class
    {
        try
        {
            return await _http.GetFromJsonAsync<T>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GET {Url} failed", url);
            return null;
        }
    }

    private async Task<List<T>> SafeGetListAsync<T>(string url)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<T>>(url) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GET {Url} failed", url);
            return [];
        }
    }

    private async Task<T?> SafePostAsync<T>(string url, object? body) where T : class
    {
        try
        {
            var response = await _http.PostAsJsonAsync(url, body);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>();
            _logger.LogWarning("POST {Url} failed with {Status}", url, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POST {Url} failed", url);
            return null;
        }
    }

    // Like SafePostAsync<T>, but also extracts the ProblemDetails.detail field from
    // the API's error response so the UI can surface the real reason for a failure
    // (FK violation, overlapping slot, forbidden profile, etc.) instead of a generic
    // fallback message.
    private async Task<(T? Value, string? Error)> SafePostWithErrorAsync<T>(string url, object? body) where T : class
    {
        try
        {
            var response = await _http.PostAsJsonAsync(url, body);
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>();
                return (value, null);
            }

            var detail = await ReadProblemDetailAsync(response);
            _logger.LogWarning("POST {Url} failed with {Status}: {Detail}", url, response.StatusCode, detail);
            return (null, detail ?? $"Request failed with status {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POST {Url} failed", url);
            return (null, ex.Message);
        }
    }

    // Reads the ProblemDetails payload the API produces via ExceptionHandlingMiddleware
    // and returns the .detail field. Tolerates non-JSON bodies — returns null in that case.
    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return raw;

            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                return detail.GetString();
            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString();
            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> SafePostBoolAsync(string url, object? body)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(url, body);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POST {Url} failed", url);
            return false;
        }
    }
}
