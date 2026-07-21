using URFYSIO.Core.Interfaces;

namespace URFYSIO.Tests.Auth0;

/// <summary>
/// Auth0 is inconsistent about which name fields it returns per connection: Google usually
/// supplies given_name/family_name, some connections only supply the combined name, and a
/// few supply nothing but a nickname. SplitName has to cope with all of them.
/// </summary>
public class Auth0UserProfileTests
{
    private static Auth0UserProfile Profile(
        string? given = null, string? family = null, string? name = null, string? nickname = null) =>
        new("someone@example.com", given, family, name, nickname);

    [Fact]
    public void SplitName_PrefersExplicitGivenAndFamilyNames()
    {
        var (first, last) = Profile(given: "Karin", family: "Jansen", name: "Ignore Me").SplitName();
        Assert.Equal("Karin", first);
        Assert.Equal("Jansen", last);
    }

    [Fact]
    public void SplitName_SplitsCombinedNameOnTheLastSpace()
    {
        var (first, last) = Profile(name: "Anna Maria de Vries").SplitName();
        Assert.Equal("Anna Maria de", first);
        Assert.Equal("Vries", last);
    }

    [Fact]
    public void SplitName_SingleWordNameBecomesFirstNameOnly()
    {
        var (first, last) = Profile(name: "Prince").SplitName();
        Assert.Equal("Prince", first);
        Assert.Null(last);
    }

    [Fact]
    public void SplitName_UsesNicknameWhenNoNameFieldsExist()
    {
        var (first, last) = Profile(nickname: "karin").SplitName();
        Assert.Equal("karin", first);
        Assert.Null(last);
    }

    [Fact]
    public void SplitName_ReturnsNullsWhenAuth0SuppliedNothing()
    {
        var (first, last) = Profile().SplitName();
        Assert.Null(first);
        Assert.Null(last);
    }

    [Fact]
    public void SplitName_TreatsWhitespaceOnlyFieldsAsMissing()
    {
        // A blank given_name must not win over a usable combined name.
        var (first, last) = Profile(given: "   ", family: "", name: "Karin Jansen").SplitName();
        Assert.Equal("Karin", first);
        Assert.Equal("Jansen", last);
    }

    [Fact]
    public void SplitName_GivenNameWithoutFamilyNameIsKept()
    {
        var (first, last) = Profile(given: "Karin").SplitName();
        Assert.Equal("Karin", first);
        Assert.Null(last);
    }

    [Fact]
    public void SplitName_TrimsSurroundingWhitespace()
    {
        var (first, last) = Profile(given: "  Karin  ", family: "  Jansen  ").SplitName();
        Assert.Equal("Karin", first);
        Assert.Equal("Jansen", last);
    }
}
