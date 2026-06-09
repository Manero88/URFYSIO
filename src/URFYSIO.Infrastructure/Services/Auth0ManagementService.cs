using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using URFYSIO.Core.Interfaces;

namespace URFYSIO.Infrastructure.Services;

public class Auth0ManagementService : IAuth0ManagementService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Auth0ManagementService> _logger;
    private readonly string _domain;
    private readonly string _managementClientId;
    private readonly string _managementClientSecret;
    private readonly string _appClientId;
    private readonly string _databaseConnection;

    private static readonly SemaphoreSlim _tokenLock = new(1, 1);
    private static string? _cachedToken;
    private static DateTime _cachedTokenExpiresAt = DateTime.MinValue;

    // Role IDs in Auth0 never change once created, so cache them by name after the
    // first lookup. The tenant has exactly three roles (Admin / Physiotherapist /
    // Client) so this dictionary has at most three entries.
    private static readonly Dictionary<string, string> _roleIdCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _roleIdLock = new(1, 1);

    public Auth0ManagementService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<Auth0ManagementService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _domain = (configuration["Auth0:Domain"]
            ?? throw new InvalidOperationException("Auth0:Domain is not configured.")).TrimEnd('/');
        _managementClientId = configuration["Auth0:ManagementClientId"]
            ?? throw new InvalidOperationException("Auth0:ManagementClientId is not configured.");
        _managementClientSecret = configuration["Auth0:ManagementClientSecret"]
            ?? throw new InvalidOperationException("Auth0:ManagementClientSecret is not configured.");
        _appClientId = configuration["Auth0:AppClientId"]
            ?? throw new InvalidOperationException("Auth0:AppClientId is not configured.");
        _databaseConnection = configuration["Auth0:DatabaseConnection"] ?? "Username-Password-Authentication";
    }

    private async Task<string?> GetManagementTokenAsync()
    {
        if (_cachedToken is not null && DateTime.UtcNow < _cachedTokenExpiresAt)
            return _cachedToken;

        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken is not null && DateTime.UtcNow < _cachedTokenExpiresAt)
                return _cachedToken;

            var client = _httpClientFactory.CreateClient();
            var body = new
            {
                client_id = _managementClientId,
                client_secret = _managementClientSecret,
                audience = $"{_domain}/api/v2/",
                grant_type = "client_credentials"
            };

            var response = await client.PostAsJsonAsync($"{_domain}/oauth/token", body);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("Auth0 token request failed: {Status} {Error}", response.StatusCode, err);
                return null;
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
            if (tokenResponse is null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                _logger.LogError("Auth0 token response was empty.");
                return null;
            }

            _cachedToken = tokenResponse.AccessToken;
            // Refresh 60 seconds before actual expiry
            _cachedTokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 60);
            return _cachedToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to obtain Auth0 management token.");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<Auth0UserResult> CreateUserAsync(string email, string firstName, string lastName)
    {
        var token = await GetManagementTokenAsync();
        if (token is null)
            return Auth0UserResult.Failed("Could not obtain an Auth0 management token. Check the M2M client configuration.");

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var fullName = $"{firstName} {lastName}".Trim();
            var body = new
            {
                connection = _databaseConnection,
                email,
                password = GenerateStrongPassword(),
                // The registrant proves ownership by following the password-setup email,
                // so we don't pre-verify here. Auth0 still sends its own verification flow
                // if the tenant has it enabled.
                email_verified = false,
                given_name = firstName,
                family_name = lastName,
                name = string.IsNullOrWhiteSpace(fullName) ? email : fullName
            };

            var response = await client.PostAsJsonAsync($"{_domain}/api/v2/users", body);
            var raw = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogWarning("Auth0 create user for {Email} returned 409 Conflict (already exists): {Body}",
                    email, raw);
                return Auth0UserResult.Conflict("An Auth0 account already exists for this email address.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogError("Auth0 create user for {Email} returned 403 Forbidden: {Body}. " +
                    "Does the Management API (M2M) client have the 'create:users' scope?", email, raw);
                return Auth0UserResult.Failed("The server is not authorized to create Auth0 users (missing 'create:users' scope).");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Auth0 create user for {Email} failed: {Status} {Body}",
                    email, response.StatusCode, raw);
                return Auth0UserResult.Failed($"Auth0 user creation failed ({(int)response.StatusCode}).");
            }

            var created = JsonSerializer.Deserialize<CreateUserResponse>(raw);
            if (created is null || string.IsNullOrWhiteSpace(created.UserId))
            {
                _logger.LogError("Auth0 create user for {Email} succeeded but no user_id in response: {Body}",
                    email, raw);
                return Auth0UserResult.Failed("Auth0 user was created but no user_id was returned.");
            }

            _logger.LogInformation("Auth0 user created for {Email}: {UserId}", email, created.UserId);
            return Auth0UserResult.Created(created.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateUserAsync failed for {Email}", email);
            return Auth0UserResult.Failed($"Auth0 user creation error: {ex.Message}");
        }
    }

    // Builds a random password that always satisfies Auth0's default complexity policy
    // (lower + upper + digit + special). We guarantee one of each required class up
    // front, fill the rest from the full alphabet, then shuffle so the guaranteed
    // characters aren't always in the same positions. The value is never shown to the
    // user — they set their own password via the reset email — so length is generous.
    private static string GenerateStrongPassword(int length = 20)
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz";   // no l
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";     // no I, O
        const string digits = "23456789";                    // no 0, 1
        const string special = "!@#$%^&*-_=+";
        const string all = lower + upper + digits + special;

        Span<char> chars = stackalloc char[Math.Max(length, 4)];
        chars[0] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = special[RandomNumberGenerator.GetInt32(special.Length)];
        for (var i = 4; i < chars.Length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        // Fisher–Yates shuffle using a cryptographic RNG.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    public async Task<bool> DeleteUserAsync(string auth0UserId)
    {
        if (string.IsNullOrWhiteSpace(auth0UserId)) return false;

        var token = await GetManagementTokenAsync();
        if (token is null)
        {
            _logger.LogError("DeleteUserAsync: management token unavailable for {User}.", auth0UserId);
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // "|" in the id must be URL-encoded for the path segment.
            var url = $"{_domain}/api/v2/users/{Uri.EscapeDataString(auth0UserId)}";
            var response = await client.DeleteAsync(url);

            // Auth0 returns 204 No Content on success. A 404 means the user is already
            // gone — treat that as success too (idempotent erasure).
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return true;

            var raw = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Forbidden)
                _logger.LogError("Auth0 delete user {User} returned 403: {Body}. " +
                    "Does the Management API (M2M) client have the 'delete:users' scope?", auth0UserId, raw);
            else
                _logger.LogError("Auth0 delete user {User} failed: {Status} {Body}",
                    auth0UserId, response.StatusCode, raw);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteUserAsync failed for {User}", auth0UserId);
            return false;
        }
    }

    public async Task<bool> ChangePasswordAsync(string auth0UserId, string newPassword)
    {
        var token = await GetManagementTokenAsync();
        if (token is null) return false;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var encodedUserId = Uri.EscapeDataString(auth0UserId);
            var url = $"{_domain}/api/v2/users/{encodedUserId}";
            var body = new { password = newPassword, connection = _databaseConnection };

            var request = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = JsonContent.Create(body)
            };

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Auth0 change password failed for {User}: {Status} {Error}",
                    auth0UserId, response.StatusCode, err);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangePasswordAsync failed for {User}", auth0UserId);
            return false;
        }
    }

    public async Task<bool> SendPasswordResetEmailAsync(string email)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var body = new
            {
                client_id = _appClientId,
                email,
                connection = _databaseConnection
            };

            var response = await client.PostAsJsonAsync($"{_domain}/dbconnections/change_password", body);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Auth0 password reset email failed for {Email}: {Status} {Error}",
                    email, response.StatusCode, err);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendPasswordResetEmailAsync failed for {Email}", email);
            return false;
        }
    }

    public async Task<string?> GetUserEmailAsync(string auth0UserId)
    {
        var token = await GetManagementTokenAsync();
        if (token is null)
        {
            _logger.LogWarning(
                "GetUserEmailAsync: management token unavailable for {User}. " +
                "Check Auth0:ManagementClientId / ManagementClientSecret and that the M2M " +
                "app is authorized for the Management API.", auth0UserId);
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Auth0's user IDs contain a "|" (e.g. "auth0|abc123", "google-oauth2|123")
            // which MUST be URL-encoded for the path segment, otherwise the server returns
            // 404 because it sees the bar as a literal pipe character in the route.
            var url = $"{_domain}/api/v2/users/{Uri.EscapeDataString(auth0UserId)}?fields=email&include_fields=true";
            _logger.LogInformation("GetUserEmailAsync: GET {Url}", url);

            var response = await client.GetAsync(url);
            var rawBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Auth0 get user '{User}' failed: {Status} {Error}. " +
                    "Does the Management API client have the 'read:users' scope?",
                    auth0UserId, response.StatusCode, rawBody);
                return null;
            }

            // Log the raw body so a successful-but-empty response (e.g. Auth0 omitted
            // the email field) is distinguishable from an outright failure.
            _logger.LogInformation(
                "Auth0 get user '{User}' succeeded: {Status}. Body: {Body}",
                auth0UserId, response.StatusCode, rawBody);

            var profile = JsonSerializer.Deserialize<UserEmailResponse>(rawBody);
            var email = string.IsNullOrWhiteSpace(profile?.Email) ? null : profile.Email;
            if (email is null)
                _logger.LogWarning("Auth0 get user '{User}': response carried no email field.", auth0UserId);
            return email;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetUserEmailAsync failed for {User}", auth0UserId);
            return null;
        }
    }

    public async Task<bool> AssignRoleAsync(string auth0UserId, string roleName)
    {
        var roleId = await GetRoleIdByNameAsync(roleName);
        if (roleId is null) return false;

        var token = await GetManagementTokenAsync();
        if (token is null) return false;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"{_domain}/api/v2/users/{Uri.EscapeDataString(auth0UserId)}/roles";
            var body = new { roles = new[] { roleId } };

            var response = await client.PostAsJsonAsync(url, body);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Auth0 assign role '{Role}' to {User} failed: {Status} {Error}",
                    roleName, auth0UserId, response.StatusCode, err);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AssignRoleAsync failed for {User} / {Role}", auth0UserId, roleName);
            return false;
        }
    }

    public async Task<bool> RemoveRoleAsync(string auth0UserId, string roleName)
    {
        var roleId = await GetRoleIdByNameAsync(roleName);
        if (roleId is null) return false;

        var token = await GetManagementTokenAsync();
        if (token is null) return false;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"{_domain}/api/v2/users/{Uri.EscapeDataString(auth0UserId)}/roles";
            // DELETE with a JSON body requires building an HttpRequestMessage ourselves —
            // HttpClient.DeleteAsync doesn't accept a payload.
            var request = new HttpRequestMessage(HttpMethod.Delete, url)
            {
                Content = JsonContent.Create(new { roles = new[] { roleId } })
            };

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Auth0 remove role '{Role}' from {User} failed: {Status} {Error}",
                    roleName, auth0UserId, response.StatusCode, err);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveRoleAsync failed for {User} / {Role}", auth0UserId, roleName);
            return false;
        }
    }

    // Looks up an Auth0 role by name and returns its ID. Results are cached because
    // role IDs are stable — changing a role ID would require deleting and re-creating
    // the role in the Auth0 dashboard.
    private async Task<string?> GetRoleIdByNameAsync(string roleName)
    {
        if (_roleIdCache.TryGetValue(roleName, out var cachedId)) return cachedId;

        await _roleIdLock.WaitAsync();
        try
        {
            if (_roleIdCache.TryGetValue(roleName, out cachedId)) return cachedId;

            var token = await GetManagementTokenAsync();
            if (token is null) return null;

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // name_filter narrows to roles whose name starts with the given value, case-insensitive.
            var url = $"{_domain}/api/v2/roles?name_filter={Uri.EscapeDataString(roleName)}";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("Auth0 role lookup for '{Role}' failed: {Status} {Error}. " +
                    "Does the Management API client have the 'read:roles' scope?",
                    roleName, response.StatusCode, err);
                return null;
            }

            var roles = await response.Content.ReadFromJsonAsync<List<RoleResponse>>();
            var match = roles?.FirstOrDefault(r =>
                string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
            if (match is null || string.IsNullOrEmpty(match.Id))
            {
                _logger.LogError("Auth0 has no role named '{Role}'. Create it in the Auth0 dashboard " +
                    "(User Management → Roles).", roleName);
                return null;
            }

            _roleIdCache[roleName] = match.Id;
            return match.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRoleIdByNameAsync failed for '{Role}'", roleName);
            return null;
        }
        finally
        {
            _roleIdLock.Release();
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;
    }

    private sealed class RoleResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    private sealed class UserEmailResponse
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }

    private sealed class CreateUserResponse
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }
}
