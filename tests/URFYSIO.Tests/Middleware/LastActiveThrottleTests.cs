using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using URFYSIO.API.Middleware;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Interfaces;

namespace URFYSIO.Tests.Middleware;

/// <summary>
/// "Last active" is maintained on authenticated requests, so it must NOT write on every
/// call — that would add a DB round-trip to each API request for a value nobody needs to
/// the second. These cover the throttle decision and that the middleware honours it.
/// </summary>
public class LastActiveThrottleTests
{
    private const string Auth0Id = "auth0|abc123";
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    // ---- The throttle decision ----

    [Fact]
    public void ShouldUpdateLastActive_NeverRecorded_Updates()
    {
        Assert.True(Auth0UserSyncMiddleware.ShouldUpdateLastActive(null, Now));
    }

    [Fact]
    public void ShouldUpdateLastActive_JustRecorded_Skips()
    {
        Assert.False(Auth0UserSyncMiddleware.ShouldUpdateLastActive(Now, Now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(14)]
    public void ShouldUpdateLastActive_WithinThrottleWindow_Skips(int minutesAgo)
    {
        var last = Now.AddMinutes(-minutesAgo);
        Assert.False(Auth0UserSyncMiddleware.ShouldUpdateLastActive(last, Now));
    }

    [Theory]
    [InlineData(15)]   // exactly the window — stale enough to refresh
    [InlineData(16)]
    [InlineData(60)]
    [InlineData(60 * 24 * 30)]
    public void ShouldUpdateLastActive_OlderThanThrottleWindow_Updates(int minutesAgo)
    {
        var last = Now.AddMinutes(-minutesAgo);
        Assert.True(Auth0UserSyncMiddleware.ShouldUpdateLastActive(last, Now));
    }

    [Fact]
    public void ShouldUpdateLastActive_FutureTimestamp_Updates()
    {
        // Clock skew or a hand-edited row must not freeze the field permanently.
        Assert.True(Auth0UserSyncMiddleware.ShouldUpdateLastActive(Now.AddHours(1), Now));
    }

    [Fact]
    public void LastActiveThrottle_IsFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Auth0UserSyncMiddleware.LastActiveThrottle);
    }

    // ---- The middleware honours it ----

    private static Auth0UserSyncMiddleware CreateMiddleware() =>
        new(_ => Task.CompletedTask, Mock.Of<ILogger<Auth0UserSyncMiddleware>>());

    private static DefaultHttpContext AuthenticatedContext() =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Auth0Id),
                 new Claim(ClaimTypes.Email, "sam@example.com"),
                 new Claim(ClaimTypes.GivenName, "Sam"),
                 new Claim(ClaimTypes.Surname, "Smit")],
                authenticationType: "Bearer")),
            Request = { Path = "/api/appointments" }
        };

    private static User ExistingUser(DateTime? lastLoginAt) => new()
    {
        Id = Guid.NewGuid(),
        Auth0Id = Auth0Id,
        Email = "sam@example.com",
        FirstName = "Sam",
        LastName = "Smit",
        Role = UserRole.Client,
        IsActive = true,
        LastLoginAt = lastLoginAt,
        ClientProfile = new ClientProfile { Id = Guid.NewGuid() }
    };

    [Fact]
    public async Task InvokeAsync_StaleLastActive_WritesAndPersists()
    {
        var user = ExistingUser(DateTime.UtcNow.AddHours(-2));
        var before = user.LastLoginAt!.Value;

        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(Auth0Id)).ReturnsAsync(user);
        users.Setup(u => u.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        await CreateMiddleware().InvokeAsync(
            AuthenticatedContext(), users.Object, Mock.Of<IAuth0ManagementService>());

        Assert.True(user.LastLoginAt > before);
        users.Verify(u => u.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_RecentLastActive_DoesNotWrite()
    {
        var recent = DateTime.UtcNow.AddMinutes(-2);
        var user = ExistingUser(recent);

        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(Auth0Id)).ReturnsAsync(user);

        await CreateMiddleware().InvokeAsync(
            AuthenticatedContext(), users.Object, Mock.Of<IAuth0ManagementService>());

        Assert.Equal(recent, user.LastLoginAt);
        users.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_NeverRecorded_BackfillsOnNextRequest()
    {
        var user = ExistingUser(null);

        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(Auth0Id)).ReturnsAsync(user);
        users.Setup(u => u.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        await CreateMiddleware().InvokeAsync(
            AuthenticatedContext(), users.Object, Mock.Of<IAuth0ManagementService>());

        // Accounts that predate the column pick up a value on their next request.
        Assert.NotNull(user.LastLoginAt);
        users.Verify(u => u.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_RepeatedRequests_WriteOnlyOnce()
    {
        var user = ExistingUser(DateTime.UtcNow.AddHours(-1));

        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(Auth0Id)).ReturnsAsync(user);
        users.Setup(u => u.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        var middleware = CreateMiddleware();
        var auth0 = Mock.Of<IAuth0ManagementService>();
        for (var i = 0; i < 5; i++)
            await middleware.InvokeAsync(AuthenticatedContext(), users.Object, auth0);

        // The first call refreshes the stamp; the remaining four fall inside the window.
        users.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_NewUser_IsStampedAtCreationWithoutASecondWrite()
    {
        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);

        User? created = null;
        users.Setup(u => u.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
             .Callback<User, string>((u, _) => created = u)
             .ReturnsAsync((User u, string _) => u);

        await CreateMiddleware().InvokeAsync(
            AuthenticatedContext(), users.Object, Mock.Of<IAuth0ManagementService>());

        Assert.NotNull(created?.LastLoginAt);
        // Stamped inline at creation — the throttle must not add a redundant update.
        users.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
    }
}
