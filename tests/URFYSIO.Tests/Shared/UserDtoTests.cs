using URFYSIO.Shared.DTOs.Users;
using URFYSIO.Shared.Enums;

namespace URFYSIO.Tests.Shared;

public class UserDtoTests
{
    private static UserDto User(string first, string last, string provider = "google") => new()
    {
        Id = Guid.NewGuid(),
        Email = "someone@example.com",
        FirstName = first,
        LastName = last,
        Role = UserRole.Client,
        AuthProvider = provider
    };

    [Fact]
    public void FullName_JoinsFirstAndLast()
    {
        Assert.Equal("Karin Jansen", User("Karin", "Jansen").FullName);
    }

    /// <summary>
    /// Regression: FullName used to be an untrimmed interpolation, so a user with no last
    /// name produced "Karin " with a trailing space. The admin's type-to-confirm delete
    /// compares the (trimmed) typed text against FullName, so that stray space made such
    /// accounts impossible to delete no matter what was typed.
    /// </summary>
    [Fact]
    public void FullName_HasNoTrailingSpaceWhenLastNameIsMissing()
    {
        var dto = User("Karin", string.Empty);
        Assert.Equal("Karin", dto.FullName);
        Assert.Equal(dto.FullName.Trim(), dto.FullName);
    }

    [Fact]
    public void FullName_HasNoLeadingSpaceWhenFirstNameIsMissing()
    {
        var dto = User(string.Empty, "Jansen");
        Assert.Equal("Jansen", dto.FullName);
        Assert.Equal(dto.FullName.Trim(), dto.FullName);
    }

    [Fact]
    public void FullName_IsEmptyWhenBothPartsAreMissing()
    {
        // The UI falls back to the email in this case rather than rendering a blank row.
        Assert.Equal(string.Empty, User(string.Empty, string.Empty).FullName);
    }

    [Theory]
    [InlineData("email", "Email / password")]
    [InlineData("google", "Google")]
    [InlineData("microsoft", "Microsoft")]
    [InlineData("local", "Created by admin")]
    [InlineData("external", "External provider")]
    [InlineData("GOOGLE", "Google")]
    public void AuthProviderLabel_DescribesHowTheUserSignedUp(string provider, string expected)
    {
        Assert.Equal(expected, User("A", "B", provider).AuthProviderLabel);
    }

    [Fact]
    public void IsEmailPasswordUser_OnlyTrueForTheDatabaseConnection()
    {
        Assert.True(User("A", "B", "email").IsEmailPasswordUser);
        Assert.False(User("A", "B", "google").IsEmailPasswordUser);
    }

    [Fact]
    public void LastLoginDisplay_NeverSeen_ShowsNever()
    {
        var dto = User("A", "B");
        dto.LastLoginAt = null;
        Assert.Equal("Never", dto.LastLoginDisplay);
    }

    [Fact]
    public void LastLoginDisplay_UsesAppDateFormatInLocalTime()
    {
        var utc = new DateTime(2026, 7, 21, 10, 30, 0, DateTimeKind.Utc);
        var dto = User("A", "B");
        dto.LastLoginAt = utc;

        // Stored UTC is rendered in the viewer's zone — a raw UTC stamp would read
        // an hour or two off for a practice in the Netherlands.
        var expected = utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        Assert.Equal(expected, dto.LastLoginDisplay);
    }

    [Fact]
    public void LastLoginDisplay_TreatsUnspecifiedKindAsUtc()
    {
        // EF materialises DateTime columns as Unspecified; without SpecifyKind the
        // conversion would silently treat the value as already-local and skip the offset.
        var unspecified = new DateTime(2026, 7, 21, 10, 30, 0, DateTimeKind.Unspecified);
        var dto = User("A", "B");
        dto.LastLoginAt = unspecified;

        var expected = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc)
            .ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        Assert.Equal(expected, dto.LastLoginDisplay);
    }
}
