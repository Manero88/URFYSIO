using URFYSIO.Core.Entities;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Availability;
using URFYSIO.Shared.DTOs.Registration;
using URFYSIO.Shared.DTOs.TreatmentPlans;
using URFYSIO.Shared.DTOs.Users;
using SharedEnums = URFYSIO.Shared.Enums;

namespace URFYSIO.API.Mapping;

public static class MappingExtensions
{
    // --- User ---
    public static UserDto ToDto(this User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FirstName = user.FirstName,
        LastName = user.LastName,
        PhoneNumber = user.PhoneNumber,
        Role = (SharedEnums.UserRole)(int)user.Role,
        IsActive = user.IsActive,
        CreatedAt = user.CreatedAt,
        LastLoginAt = user.LastLoginAt,
        // ProfileId must match the user's CURRENT role — otherwise a user who was
        // promoted from Client to Physiotherapist would still report their stale
        // ClientProfile.Id, which the app then sends as PhysiotherapistProfileId and
        // the FK constraint rejects. Pick the profile that matches the role.
        ProfileId = user.Role switch
        {
            Core.Enums.UserRole.Client => user.ClientProfile?.Id,
            Core.Enums.UserRole.Physiotherapist => user.PhysiotherapistProfile?.Id,
            _ => null
        },
        AuthProvider = DeriveAuthProvider(user.Auth0Id)
    };

    public static UserProfileDto ToProfileDto(this User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FirstName = user.FirstName,
        LastName = user.LastName,
        PhoneNumber = user.PhoneNumber,
        DateOfBirth = user.DateOfBirth,
        Gender = user.Gender,
        Street = user.Street,
        HouseNumber = user.HouseNumber,
        PostalCode = user.PostalCode,
        City = user.City,
        Role = (SharedEnums.UserRole)(int)user.Role,
        AuthProvider = DeriveAuthProvider(user.Auth0Id),
        CreatedAt = user.CreatedAt
    };

    private static string DeriveAuthProvider(string? auth0Id) => auth0Id switch
    {
        null => "local",
        _ when auth0Id.StartsWith("auth0|") => "email",
        _ when auth0Id.StartsWith("google-oauth2|") => "google",
        _ when auth0Id.StartsWith("windowslive|") => "microsoft",
        _ => "external"
    };

    public static User ToEntity(this CreateUserDto dto) => new()
    {
        Email = dto.Email,
        FirstName = dto.FirstName,
        LastName = dto.LastName,
        PhoneNumber = dto.PhoneNumber,
        Role = (Core.Enums.UserRole)(int)dto.Role
    };

    // --- Appointment ---
    public static AppointmentDto ToDto(this Appointment a) => new()
    {
        Id = a.Id,
        ClientProfileId = a.ClientProfileId,
        ClientName = a.ClientProfile?.User?.FullName ?? "",
        PhysiotherapistProfileId = a.PhysiotherapistProfileId,
        PhysiotherapistName = a.PhysiotherapistProfile?.User?.FullName ?? "",
        AvailabilitySlotId = a.AvailabilitySlotId,
        StartTime = a.StartTime,
        EndTime = a.EndTime,
        Status = (SharedEnums.AppointmentStatus)(int)a.Status,
        Notes = a.Notes,
        CreatedAt = a.CreatedAt
    };

    public static Appointment ToEntity(this CreateAppointmentDto dto) => new()
    {
        ClientProfileId = dto.ClientProfileId,
        PhysiotherapistProfileId = dto.PhysiotherapistProfileId,
        AvailabilitySlotId = dto.AvailabilitySlotId,
        Notes = dto.Notes
    };

    // --- AvailabilitySlot ---
    public static AvailabilitySlotDto ToDto(this AvailabilitySlot s) => new()
    {
        Id = s.Id,
        PhysiotherapistProfileId = s.PhysiotherapistProfileId,
        PhysiotherapistName = s.PhysiotherapistProfile?.User?.FullName ?? "",
        StartTime = s.StartTime,
        EndTime = s.EndTime,
        IsBooked = s.IsBooked
    };

    public static AvailabilitySlot ToEntity(this CreateAvailabilitySlotDto dto) => new()
    {
        PhysiotherapistProfileId = dto.PhysiotherapistProfileId,
        StartTime = dto.StartTime,
        EndTime = dto.EndTime
    };

    // --- TreatmentPlan ---
    public static TreatmentPlanDto ToDto(this TreatmentPlan t) => new()
    {
        Id = t.Id,
        ClientProfileId = t.ClientProfileId,
        ClientName = t.ClientProfile?.User?.FullName ?? "",
        PhysiotherapistProfileId = t.PhysiotherapistProfileId,
        PhysiotherapistName = t.PhysiotherapistProfile?.User?.FullName ?? "",
        Title = t.Title,
        Description = t.Description,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        IsCompleted = t.IsCompleted,
        CompletedAt = t.CompletedAt,
        Entries = t.Entries.Select(e => e.ToDto()).ToList()
    };

    public static TreatmentPlanEntryCommentDto ToDto(this TreatmentPlanEntryComment c) => new()
    {
        Id = c.Id,
        TreatmentPlanEntryId = c.TreatmentPlanEntryId,
        Text = c.Text,
        AuthorName = c.User?.FullName ?? "Unknown",
        AuthorRole = c.User?.Role.ToString() ?? "",
        CreatedAt = c.CreatedAt
    };

    public static TreatmentPlanEntryDto ToDto(this TreatmentPlanEntry e) => new()
    {
        Id = e.Id,
        TreatmentPlanId = e.TreatmentPlanId,
        Title = e.Title,
        Description = e.Description,
        OrderIndex = e.OrderIndex,
        IsCompleted = e.IsCompleted
    };

    public static TreatmentPlan ToEntity(this CreateTreatmentPlanDto dto) => new()
    {
        ClientProfileId = dto.ClientProfileId,
        PhysiotherapistProfileId = dto.PhysiotherapistProfileId,
        Title = dto.Title,
        Description = dto.Description
    };

    public static TreatmentPlanEntry ToEntity(this CreateTreatmentPlanEntryDto dto) => new()
    {
        Title = dto.Title,
        Description = dto.Description,
        OrderIndex = dto.OrderIndex
    };

    // --- RegistrationRequest ---
    public static RegistrationRequestDto ToDto(this RegistrationRequest r) => new()
    {
        Id = r.Id,
        FirstName = r.FirstName,
        LastName = r.LastName,
        Email = r.Email,
        PhoneNumber = r.PhoneNumber,
        Message = r.Message,
        Status = (SharedEnums.RegistrationStatus)(int)r.Status,
        CreatedAt = r.CreatedAt
    };

    public static RegistrationRequest ToEntity(this CreateRegistrationRequestDto dto) => new()
    {
        FirstName = dto.FirstName,
        LastName = dto.LastName,
        Email = dto.Email,
        PhoneNumber = dto.PhoneNumber,
        Message = dto.Message
    };
}
