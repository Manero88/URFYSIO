using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;

namespace URFYSIO.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<ClientProfile> ClientProfiles => Set<ClientProfile>();
    public DbSet<PhysiotherapistProfile> PhysiotherapistProfiles => Set<PhysiotherapistProfile>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AvailabilitySlot> AvailabilitySlots => Set<AvailabilitySlot>();
    public DbSet<TreatmentPlan> TreatmentPlans => Set<TreatmentPlan>();
    public DbSet<TreatmentPlanEntry> TreatmentPlanEntries => Set<TreatmentPlanEntry>();
    public DbSet<TreatmentPlanEntryComment> TreatmentPlanEntryComments => Set<TreatmentPlanEntryComment>();
    public DbSet<RegistrationRequest> RegistrationRequests => Set<RegistrationRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Auth0Id).IsUnique().HasFilter("Auth0Id IS NOT NULL");
            e.Property(u => u.Auth0Id).HasMaxLength(128);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(256).IsRequired();
            e.Property(u => u.FirstName).HasMaxLength(100).IsRequired();
            e.Property(u => u.LastName).HasMaxLength(100).IsRequired();
            e.Property(u => u.PhoneNumber).HasMaxLength(20);
            e.Property(u => u.Gender).HasMaxLength(20);
            e.Property(u => u.Street).HasMaxLength(200);
            e.Property(u => u.HouseNumber).HasMaxLength(20);
            e.Property(u => u.PostalCode).HasMaxLength(10);
            e.Property(u => u.City).HasMaxLength(100);
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            e.Ignore(u => u.FullName);
        });

        // ClientProfile
        modelBuilder.Entity<ClientProfile>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasOne(c => c.User)
             .WithOne(u => u.ClientProfile)
             .HasForeignKey<ClientProfile>(c => c.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(c => c.Address).HasMaxLength(500);
        });

        // PhysiotherapistProfile
        modelBuilder.Entity<PhysiotherapistProfile>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasOne(p => p.User)
             .WithOne(u => u.PhysiotherapistProfile)
             .HasForeignKey<PhysiotherapistProfile>(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.Specialization).HasMaxLength(200);
            e.Property(p => p.LicenseNumber).HasMaxLength(50);
        });

        // Appointment
        modelBuilder.Entity<Appointment>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasOne(a => a.ClientProfile)
             .WithMany(c => c.Appointments)
             .HasForeignKey(a => a.ClientProfileId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.PhysiotherapistProfile)
             .WithMany(p => p.Appointments)
             .HasForeignKey(a => a.PhysiotherapistProfileId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.AvailabilitySlot)
             .WithOne(s => s.Appointment)
             .HasForeignKey<Appointment>(a => a.AvailabilitySlotId)
             .OnDelete(DeleteBehavior.SetNull);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Notes).HasMaxLength(1000);
        });

        // AvailabilitySlot
        modelBuilder.Entity<AvailabilitySlot>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasOne(s => s.PhysiotherapistProfile)
             .WithMany(p => p.AvailabilitySlots)
             .HasForeignKey(s => s.PhysiotherapistProfileId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // TreatmentPlan
        modelBuilder.Entity<TreatmentPlan>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasOne(t => t.ClientProfile)
             .WithMany(c => c.TreatmentPlans)
             .HasForeignKey(t => t.ClientProfileId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.PhysiotherapistProfile)
             .WithMany(p => p.TreatmentPlans)
             .HasForeignKey(t => t.PhysiotherapistProfileId)
             .OnDelete(DeleteBehavior.Restrict);
            e.Property(t => t.Title).HasMaxLength(200).IsRequired();
            e.Property(t => t.Description).HasMaxLength(2000);
        });

        // TreatmentPlanEntry
        modelBuilder.Entity<TreatmentPlanEntry>(e =>
        {
            e.HasKey(te => te.Id);
            e.HasOne(te => te.TreatmentPlan)
             .WithMany(t => t.Entries)
             .HasForeignKey(te => te.TreatmentPlanId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(te => te.Title).HasMaxLength(200).IsRequired();
            e.Property(te => te.Description).HasMaxLength(2000);
        });

        // TreatmentPlanEntryComment
        modelBuilder.Entity<TreatmentPlanEntryComment>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasOne(c => c.TreatmentPlanEntry)
             .WithMany(te => te.Comments)
             .HasForeignKey(c => c.TreatmentPlanEntryId)
             .OnDelete(DeleteBehavior.Cascade);
            // Author deletion is rare and we want to keep historical comments around,
            // so use Restrict — deleting the user explicitly requires removing or
            // reassigning their comments first. This also prevents EF Core from going
            // multi-cascade-path-crazy via User -> Comment AND User -> Profile -> Plan
            // -> Entry -> Comment, which SQL Server rejects.
            e.HasOne(c => c.User)
             .WithMany()
             .HasForeignKey(c => c.UserId)
             .OnDelete(DeleteBehavior.Restrict);
            e.Property(c => c.Text).HasMaxLength(1000).IsRequired();
            // Generated blob names are 32 hex chars + extension; 256 leaves room without
            // letting an unbounded string into the table.
            e.Property(c => c.PhotoBlobName).HasMaxLength(256);
        });

        // RegistrationRequest
        modelBuilder.Entity<RegistrationRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.FirstName).HasMaxLength(100).IsRequired();
            e.Property(r => r.LastName).HasMaxLength(100).IsRequired();
            e.Property(r => r.Email).HasMaxLength(256).IsRequired();
            e.Property(r => r.PhoneNumber).HasMaxLength(20);
            e.Property(r => r.Message).HasMaxLength(2000);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(r => r.ProcessedByUser)
             .WithMany()
             .HasForeignKey(r => r.ProcessedByUserId)
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
