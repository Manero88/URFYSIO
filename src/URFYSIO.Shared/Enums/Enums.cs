namespace URFYSIO.Shared.Enums;

public enum UserRole
{
    Client = 0,
    Physiotherapist = 1,
    Admin = 2
}

public enum AppointmentStatus
{
    Scheduled = 0,
    Completed = 1,
    Cancelled = 2,
    NoShow = 3
}

public enum RegistrationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}
