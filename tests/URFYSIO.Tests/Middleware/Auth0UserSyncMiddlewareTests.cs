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
/// Covers the SSO sign-up path: Google/Microsoft access tokens carry neither email nor
/// name, so the middleware has to source them from the Auth0 Management API. Without that
/// every social sign-up landed in the database as "Unknown User".
/// </summary>
public class Auth0UserSyncMiddlewareTests
{
    private const string GoogleId = "google-oauth2|101413127194131230063";

    private static Auth0UserSyncMiddleware CreateMiddleware(RequestDelegate? next = null) =>
        new(next ?? (_ => Task.CompletedTask),
            Mock.Of<ILogger<Auth0UserSyncMiddleware>>());

    /// <summary>An authenticated context carrying only the claims a Google access token really has.</summary>
    private static DefaultHttpContext ContextFor(string auth0Id, params Claim[] extraClaims)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, auth0Id) };
        claims.AddRange(extraClaims);
        var identity = new ClaimsIdentity(claims, authenticationType: "Bearer");
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            Request = { Path = "/api/users/me" }
        };
    }

    // ---- HasPlaceholderName ----

    [Theory]
    [InlineData("Unknown", "User", true)]
    [InlineData("unknown", "user", true)]   // casing shouldn't matter
    [InlineData("", "", true)]
    [InlineData("Unknown", "", true)]
    [InlineData("", "User", true)]
    [InlineData("Karin", "Jansen", false)]
    [InlineData("Karin", "User", false)]    // real first name — not a placeholder
    [InlineData("Unknown", "Jansen", false)] // real surname — not a placeholder
    public void HasPlaceholderName_IdentifiesAutoCreatedPlaceholders(string first, string last, bool expected)
    {
        var user = new User { FirstName = first, LastName = last };
        Assert.Equal(expected, Auth0UserSyncMiddleware.HasPlaceholderName(user));
    }

    // ---- New SSO user: name captured from the Auth0 profile ----

    [Fact]
    public async Task InvokeAsync_NewSsoUser_CapturesNameAndEmailFromAuth0Profile()
    {
        var auth0 = new Mock<IAuth0ManagementService>();
        auth0.Setup(a => a.GetUserProfileAsync(GoogleId))
             .ReturnsAsync(new Auth0UserProfile("karin@gmail.com", "Karin", "Jansen", "Karin Jansen", "karin"));

        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(GoogleId)).ReturnsAsync((User?)null);

        User? created = null;
        users.Setup(u => u.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
             .Callback<User, string>((u, _) => created = u)
             .ReturnsAsync((User u, string _) => u);

        await CreateMiddleware().InvokeAsync(ContextFor(GoogleId), users.Object, auth0.Object);

        Assert.NotNull(created);
        Assert.Equal("Karin", created!.FirstName);
        Assert.Equal("Jansen", created.LastName);
        Assert.Equal("karin@gmail.com", created.Email);
        // Self-service sign-ups must NOT be usable until an admin approves them.
        Assert.False(created.IsActive);
    }

    [Fact]
    public async Task InvokeAsync_NewSsoUser_FallsBackToUnknownWhenAuth0HasNothing()
    {
        var auth0 = new Mock<IAuth0ManagementService>();
        auth0.Setup(a => a.GetUserProfileAsync(GoogleId)).ReturnsAsync((Auth0UserProfile?)null);

        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(GoogleId)).ReturnsAsync((User?)null);

        User? created = null;
        users.Setup(u => u.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
             .Callback<User, string>((u, _) => created = u)
             .ReturnsAsync((User u, string _) => u);

        await CreateMiddleware().InvokeAsync(ContextFor(GoogleId), users.Object, auth0.Object);

        Assert.NotNull(created);
        Assert.Equal("Unknown", created!.FirstName);
        Assert.Equal("User", created.LastName);
        Assert.Equal($"{GoogleId}@auth0.local", created.Email);
    }

    [Fact]
    public async Task InvokeAsync_NewUserWithJwtClaims_SkipsTheManagementApiCall()
    {
        var auth0 = new Mock<IAuth0ManagementService>();
        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        users.Setup(u => u.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
             .ReturnsAsync((User u, string _) => u);

        var ctx = ContextFor("auth0|abc",
            new Claim(ClaimTypes.Email, "someone@example.com"),
            new Claim(ClaimTypes.GivenName, "Sam"),
            new Claim(ClaimTypes.Surname, "Smit"));

        await CreateMiddleware().InvokeAsync(ctx, users.Object, auth0.Object);

        // The token already had everything — no reason to pay for a remote round-trip.
        auth0.Verify(a => a.GetUserProfileAsync(It.IsAny<string>()), Times.Never);
    }

    // ---- Existing "Unknown User": backfilled on next login ----

    [Fact]
    public async Task InvokeAsync_ExistingUnknownUser_BackfillsNameOnNextLogin()
    {
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Auth0Id = GoogleId,
            FirstName = "Unknown",
            LastName = "User",
            Email = $"{GoogleId}@auth0.local",
            Role = UserRole.Client,
            IsActive = true,
            // Recent, so the last-active throttle stays inert and the only write this
            // test can observe is the name/email heal it is actually about.
            LastLoginAt = DateTime.UtcNow
        };

        var auth0 = new Mock<IAuth0ManagementService>();
        auth0.Setup(a => a.GetUserProfileAsync(GoogleId))
             .ReturnsAsync(new Auth0UserProfile("karin@gmail.com", "Karin", "Jansen", "Karin Jansen", "karin"));

        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(GoogleId)).ReturnsAsync(existing);
        users.Setup(u => u.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        await CreateMiddleware().InvokeAsync(ContextFor(GoogleId), users.Object, auth0.Object);

        Assert.Equal("Karin", existing.FirstName);
        Assert.Equal("Jansen", existing.LastName);
        Assert.Equal("karin@gmail.com", existing.Email);
        users.Verify(u => u.UpdateAsync(existing), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_ExistingUserWithRealName_IsNotTouched()
    {
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Auth0Id = GoogleId,
            FirstName = "Karin",
            LastName = "Jansen",
            Email = "karin@gmail.com",
            Role = UserRole.Client,
            IsActive = true,
            // Recent, so the last-active throttle doesn't write and "is not touched"
            // means what it says here.
            LastLoginAt = DateTime.UtcNow,
            ClientProfile = new ClientProfile { Id = Guid.NewGuid() }
        };

        var auth0 = new Mock<IAuth0ManagementService>();
        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(GoogleId)).ReturnsAsync(existing);

        await CreateMiddleware().InvokeAsync(ContextFor(GoogleId), users.Object, auth0.Object);

        // A healed row must not keep paying for Management API calls on every request.
        auth0.Verify(a => a.GetUserProfileAsync(It.IsAny<string>()), Times.Never);
        users.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_PlaceholderNameButAuth0ReturnsNothing_LeavesNameAlone()
    {
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Auth0Id = GoogleId,
            FirstName = "Unknown",
            LastName = "User",
            Email = "real@example.com", // email already healed; only the name is a placeholder
            Role = UserRole.Client,
            IsActive = true,
            // Recent, so the last-active throttle contributes no write of its own.
            LastLoginAt = DateTime.UtcNow,
            ClientProfile = new ClientProfile { Id = Guid.NewGuid() }
        };

        var auth0 = new Mock<IAuth0ManagementService>();
        auth0.Setup(a => a.GetUserProfileAsync(GoogleId)).ReturnsAsync((Auth0UserProfile?)null);

        var users = new Mock<IUserService>();
        users.Setup(u => u.GetByAuth0IdAsync(GoogleId)).ReturnsAsync(existing);

        await CreateMiddleware().InvokeAsync(ContextFor(GoogleId), users.Object, auth0.Object);

        // Nothing usable came back, so we must not blank out what's already stored.
        Assert.Equal("Unknown", existing.FirstName);
        Assert.Equal("User", existing.LastName);
        users.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
    }
}
