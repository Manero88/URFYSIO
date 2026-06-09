using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;

namespace URFYSIO.Tests.Services;

public class RegistrationServiceTests
{
    private static AppDbContext GetDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    // The Auth0 mock defaults to a successful user creation + role/email no-ops so the
    // happy-path approval test and the unrelated Create/Reject tests work without extra
    // setup. Tests that need a failure/conflict override CreateUserAsync on the returned mock.
    private static (RegistrationService Service, AppDbContext Db, Mock<IAuth0ManagementService> Auth0) CreateService()
    {
        var db = GetDbContext();
        var userService = new UserService(db);
        var auth0 = new Mock<IAuth0ManagementService>();
        auth0.Setup(a => a.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string email, string _, string _) => Auth0UserResult.Created($"auth0|{Guid.NewGuid():N}"));
        auth0.Setup(a => a.AssignRoleAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        auth0.Setup(a => a.SendPasswordResetEmailAsync(It.IsAny<string>())).ReturnsAsync(true);
        var service = new RegistrationService(db, userService, auth0.Object, Mock.Of<ILogger<RegistrationService>>());
        return (service, db, auth0);
    }

    [Fact]
    public async Task CreateAsync_WithValidData_CreatesRegistration()
    {
        var (service, db, _) = CreateService();
        using (db)
        {
            var request = new RegistrationRequest
            {
                FirstName = "John", LastName = "Doe",
                Email = "john@example.com", PhoneNumber = "0612345678"
            };

            var result = await service.CreateAsync(request);

            Assert.NotNull(result);
            Assert.NotEqual(Guid.Empty, result.Id);
            Assert.Equal(RegistrationStatus.Pending, result.Status);
            Assert.NotEqual(default, result.CreatedAt);
        }
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateEmail_ThrowsDomainException()
    {
        var (service, db, _) = CreateService();
        using (db)
        {
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(), Email = "existing@example.com",
                FirstName = "A", LastName = "B", Role = UserRole.Client,
                PasswordHash = "", CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var request = new RegistrationRequest
            {
                FirstName = "C", LastName = "D",
                Email = "existing@example.com", PhoneNumber = "06"
            };

            var ex = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("already exists", ex.Message);
        }
    }

    [Fact]
    public async Task CreateAsync_WithDuplicatePendingRequest_ThrowsDomainException()
    {
        var (service, db, _) = CreateService();
        using (db)
        {
            db.RegistrationRequests.Add(new RegistrationRequest
            {
                Id = Guid.NewGuid(), FirstName = "A", LastName = "B",
                Email = "pending@example.com", Status = RegistrationStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var request = new RegistrationRequest
            {
                FirstName = "C", LastName = "D",
                Email = "pending@example.com", PhoneNumber = "06"
            };

            var ex = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("pending", ex.Message);
        }
    }

    [Fact]
    public async Task ApproveAsync_WithValidId_CreatesAuth0AndLocalUser()
    {
        var (service, db, auth0) = CreateService();
        using (db)
        {
            const string auth0Id = "auth0|created123";
            auth0.Setup(a => a.CreateUserAsync("approve@example.com", "John", "Doe"))
                .ReturnsAsync(Auth0UserResult.Created(auth0Id));

            var request = new RegistrationRequest
            {
                Id = Guid.NewGuid(), FirstName = "John", LastName = "Doe",
                Email = "approve@example.com", PhoneNumber = "06",
                Status = RegistrationStatus.Pending, CreatedAt = DateTime.UtcNow
            };
            db.RegistrationRequests.Add(request);
            await db.SaveChangesAsync();

            var adminId = Guid.NewGuid();
            var result = await service.ApproveAsync(request.Id, adminId);

            Assert.Equal(RegistrationStatus.Approved, result.Status);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == "approve@example.com");
            Assert.NotNull(user);
            Assert.Equal(UserRole.Client, user.Role);
            Assert.True(user.IsActive);
            // CRITICAL: the Auth0Id must be persisted so the sync middleware matches this
            // row on first login and doesn't create a duplicate.
            Assert.Equal(auth0Id, user.Auth0Id);
            // No local password stored — Auth0 owns the credential.
            Assert.Equal(string.Empty, user.PasswordHash);

            // The role assignment + password-setup email were triggered.
            auth0.Verify(a => a.AssignRoleAsync(auth0Id, "Client"), Times.Once);
            auth0.Verify(a => a.SendPasswordResetEmailAsync("approve@example.com"), Times.Once);
        }
    }

    [Fact]
    public async Task ApproveAsync_WhenAuth0CreationFails_DoesNotApproveOrCreateUser()
    {
        var (service, db, auth0) = CreateService();
        using (db)
        {
            auth0.Setup(a => a.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Auth0UserResult.Failed("Auth0 down"));

            var request = new RegistrationRequest
            {
                Id = Guid.NewGuid(), FirstName = "Jane", LastName = "Roe",
                Email = "fail@example.com", Status = RegistrationStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            db.RegistrationRequests.Add(request);
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<DomainException>(
                () => service.ApproveAsync(request.Id, Guid.NewGuid()));

            // Transactional integrity: request stays Pending, no local user created,
            // and we never tried to send a password email.
            var reloaded = await db.RegistrationRequests.FindAsync(request.Id);
            Assert.Equal(RegistrationStatus.Pending, reloaded!.Status);
            Assert.False(await db.Users.AnyAsync(u => u.Email == "fail@example.com"));
            auth0.Verify(a => a.SendPasswordResetEmailAsync(It.IsAny<string>()), Times.Never);
        }
    }

    [Fact]
    public async Task ApproveAsync_WhenAuth0UserAlreadyExists_ThrowsConflict()
    {
        var (service, db, auth0) = CreateService();
        using (db)
        {
            auth0.Setup(a => a.CreateUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Auth0UserResult.Conflict("An Auth0 account already exists for this email address."));

            var request = new RegistrationRequest
            {
                Id = Guid.NewGuid(), FirstName = "Al", LastName = "Ready",
                Email = "dupe@example.com", Status = RegistrationStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            db.RegistrationRequests.Add(request);
            await db.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<DomainException>(
                () => service.ApproveAsync(request.Id, Guid.NewGuid()));
            Assert.Equal(409, ex.StatusCode);

            var reloaded = await db.RegistrationRequests.FindAsync(request.Id);
            Assert.Equal(RegistrationStatus.Pending, reloaded!.Status);
        }
    }

    [Fact]
    public async Task ApproveAsync_WithNonExistentId_ThrowsDomainException()
    {
        var (service, db, _) = CreateService();
        using (db)
        {
            var ex = await Assert.ThrowsAsync<DomainException>(
                () => service.ApproveAsync(Guid.NewGuid(), Guid.NewGuid()));
            Assert.Equal(404, ex.StatusCode);
        }
    }

    [Fact]
    public async Task ApproveAsync_AlreadyApproved_ThrowsDomainException()
    {
        var (service, db, _) = CreateService();
        using (db)
        {
            var request = new RegistrationRequest
            {
                Id = Guid.NewGuid(), FirstName = "A", LastName = "B",
                Email = "approved@test.com", Status = RegistrationStatus.Approved,
                CreatedAt = DateTime.UtcNow
            };
            db.RegistrationRequests.Add(request);
            await db.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<DomainException>(
                () => service.ApproveAsync(request.Id, Guid.NewGuid()));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("already been processed", ex.Message);
        }
    }

    [Fact]
    public async Task RejectAsync_WithValidId_SetsStatusToRejected()
    {
        var (service, db, _) = CreateService();
        using (db)
        {
            var request = new RegistrationRequest
            {
                Id = Guid.NewGuid(), FirstName = "A", LastName = "B",
                Email = "reject@test.com", Status = RegistrationStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            db.RegistrationRequests.Add(request);
            await db.SaveChangesAsync();

            var result = await service.RejectAsync(request.Id, Guid.NewGuid());

            Assert.Equal(RegistrationStatus.Rejected, result.Status);
        }
    }

    [Fact]
    public async Task RejectAsync_WithNonExistentId_ThrowsDomainException()
    {
        var (service, db, _) = CreateService();
        using (db)
        {
            var ex = await Assert.ThrowsAsync<DomainException>(
                () => service.RejectAsync(Guid.NewGuid(), Guid.NewGuid()));
            Assert.Equal(404, ex.StatusCode);
        }
    }
}
