using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Infrastructure.Data;

namespace URFYSIO.Infrastructure.Seeding;

public static class DbSeeder
{
    /// <summary>
    /// Idempotent fix-up that runs on every API startup: for any User whose Role no
    /// longer matches their existing profile entities (typically a Client who was
    /// promoted to Physiotherapist via the admin panel before the fix shipped), create
    /// the missing profile so downstream flows (availability slots, appointments) have
    /// a valid FK target. Existing profiles are left alone — we never delete history.
    /// Returns the number of profiles that were added.
    /// </summary>
    public static async Task<int> BackfillMissingProfilesAsync(AppDbContext db)
    {
        // Load all users with their current profile state. Projecting to a lightweight
        // record avoids materializing every related collection.
        var users = await db.Users
            .Select(u => new
            {
                u.Id,
                u.Role,
                HasClientProfile = db.ClientProfiles.Any(c => c.UserId == u.Id),
                HasPhysioProfile = db.PhysiotherapistProfiles.Any(p => p.UserId == u.Id)
            })
            .ToListAsync();

        var added = 0;
        foreach (var u in users)
        {
            if (u.Role == UserRole.Physiotherapist && !u.HasPhysioProfile)
            {
                db.PhysiotherapistProfiles.Add(new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = u.Id });
                added++;
            }
            else if (u.Role == UserRole.Client && !u.HasClientProfile)
            {
                db.ClientProfiles.Add(new ClientProfile { Id = Guid.NewGuid(), UserId = u.Id });
                added++;
            }
            // Admin: no profile entity required.
        }

        if (added > 0) await db.SaveChangesAsync();
        return added;
    }

    public static async Task SeedAsync(AppDbContext db)
    {
        if (db.Users.Any()) return; // Already seeded

        // --- Users ---
        var adminUser = CreateUser("admin@urfysio.nl", "Admin123!", "Beheerder", "URFYSIO", UserRole.Admin);
        var physio1User = CreateUser("jan@urfysio.nl", "Physio123!", "Jan", "de Vries", UserRole.Physiotherapist);
        var physio2User = CreateUser("marieke@urfysio.nl", "Physio123!", "Marieke", "Bakker", UserRole.Physiotherapist);
        var client1User = CreateUser("piet@example.nl", "Client123!", "Piet", "Jansen", UserRole.Client);
        var client2User = CreateUser("anna@example.nl", "Client123!", "Anna", "Smit", UserRole.Client);
        var client3User = CreateUser("kees@example.nl", "Client123!", "Kees", "van Dijk", UserRole.Client);

        db.Users.AddRange(adminUser, physio1User, physio2User, client1User, client2User, client3User);

        // --- Profiles ---
        var physio1Profile = new PhysiotherapistProfile
        {
            Id = Guid.NewGuid(), UserId = physio1User.Id,
            Specialization = "Sports Physiotherapy", LicenseNumber = "BIG-12345"
        };
        var physio2Profile = new PhysiotherapistProfile
        {
            Id = Guid.NewGuid(), UserId = physio2User.Id,
            Specialization = "Manual Therapy", LicenseNumber = "BIG-67890"
        };

        var client1Profile = new ClientProfile
        {
            Id = Guid.NewGuid(), UserId = client1User.Id,
            DateOfBirth = new DateTime(1985, 5, 15), Address = "Kerkstraat 12, Amsterdam"
        };
        var client2Profile = new ClientProfile
        {
            Id = Guid.NewGuid(), UserId = client2User.Id,
            DateOfBirth = new DateTime(1992, 8, 22), Address = "Dorpsstraat 5, Utrecht"
        };
        var client3Profile = new ClientProfile
        {
            Id = Guid.NewGuid(), UserId = client3User.Id,
            DateOfBirth = new DateTime(1978, 11, 3), Address = "Hoofdweg 88, Rotterdam"
        };

        db.PhysiotherapistProfiles.AddRange(physio1Profile, physio2Profile);
        db.ClientProfiles.AddRange(client1Profile, client2Profile, client3Profile);

        // --- Availability Slots (next 5 business days) ---
        var baseDate = DateTime.UtcNow.Date.AddDays(1);
        while (baseDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            baseDate = baseDate.AddDays(1);

        var slots = new List<AvailabilitySlot>();
        for (int day = 0; day < 5; day++)
        {
            var date = baseDate.AddDays(day);
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                date = date.AddDays(1);

            // Physio 1: 09:00 - 12:00 in 30-min blocks
            for (int hour = 9; hour < 12; hour++)
            {
                slots.Add(new AvailabilitySlot
                {
                    Id = Guid.NewGuid(), PhysiotherapistProfileId = physio1Profile.Id,
                    StartTime = date.AddHours(hour), EndTime = date.AddHours(hour).AddMinutes(30)
                });
                slots.Add(new AvailabilitySlot
                {
                    Id = Guid.NewGuid(), PhysiotherapistProfileId = physio1Profile.Id,
                    StartTime = date.AddHours(hour).AddMinutes(30), EndTime = date.AddHours(hour + 1)
                });
            }

            // Physio 2: 13:00 - 17:00 in 45-min blocks
            var slotStart = date.AddHours(13);
            while (slotStart.AddMinutes(45) <= date.AddHours(17))
            {
                slots.Add(new AvailabilitySlot
                {
                    Id = Guid.NewGuid(), PhysiotherapistProfileId = physio2Profile.Id,
                    StartTime = slotStart, EndTime = slotStart.AddMinutes(45)
                });
                slotStart = slotStart.AddMinutes(45);
            }
        }
        db.AvailabilitySlots.AddRange(slots);

        // --- Book a couple of sample appointments ---
        var bookedSlot1 = slots[0];
        bookedSlot1.IsBooked = true;
        db.Appointments.Add(new Appointment
        {
            Id = Guid.NewGuid(),
            ClientProfileId = client1Profile.Id,
            PhysiotherapistProfileId = physio1Profile.Id,
            AvailabilitySlotId = bookedSlot1.Id,
            StartTime = bookedSlot1.StartTime,
            EndTime = bookedSlot1.EndTime,
            Status = AppointmentStatus.Scheduled,
            Notes = "Initial consultation — lower back pain"
        });

        var bookedSlot2 = slots.First(s => s.PhysiotherapistProfileId == physio2Profile.Id);
        bookedSlot2.IsBooked = true;
        db.Appointments.Add(new Appointment
        {
            Id = Guid.NewGuid(),
            ClientProfileId = client2Profile.Id,
            PhysiotherapistProfileId = physio2Profile.Id,
            AvailabilitySlotId = bookedSlot2.Id,
            StartTime = bookedSlot2.StartTime,
            EndTime = bookedSlot2.EndTime,
            Status = AppointmentStatus.Scheduled,
            Notes = "Follow-up shoulder rehabilitation"
        });

        // --- Treatment Plans ---
        var plan1 = new TreatmentPlan
        {
            Id = Guid.NewGuid(),
            ClientProfileId = client1Profile.Id,
            PhysiotherapistProfileId = physio1Profile.Id,
            Title = "Lower Back Rehabilitation",
            Description = "6-week program focusing on core stability and flexibility."
        };
        db.TreatmentPlans.Add(plan1);
        db.TreatmentPlanEntries.AddRange(
            new TreatmentPlanEntry { Id = Guid.NewGuid(), TreatmentPlanId = plan1.Id, Title = "Core stability exercises", Description = "Planks, dead bugs, bird dogs — 3x per week", OrderIndex = 1 },
            new TreatmentPlanEntry { Id = Guid.NewGuid(), TreatmentPlanId = plan1.Id, Title = "Hip flexor stretches", Description = "Daily stretching routine, 2x15 sec per side", OrderIndex = 2 },
            new TreatmentPlanEntry { Id = Guid.NewGuid(), TreatmentPlanId = plan1.Id, Title = "Progress to weighted exercises", Description = "After week 3, add light resistance training", OrderIndex = 3 }
        );

        // --- Registration Requests ---
        db.RegistrationRequests.Add(new RegistrationRequest
        {
            Id = Guid.NewGuid(),
            FirstName = "Lisa",
            LastName = "Mulder",
            Email = "lisa@example.nl",
            PhoneNumber = "0612345678",
            Message = "I have chronic knee pain and would like to start physiotherapy.",
            Status = RegistrationStatus.Pending
        });

        await db.SaveChangesAsync();
    }

    private static User CreateUser(string email, string password, string firstName, string lastName, UserRole role)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = HashPassword(password),
            FirstName = firstName,
            LastName = lastName,
            Role = role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
}
