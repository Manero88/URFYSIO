using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Auth0.OidcClient;

namespace URFYSIO.App.Services;

public class AuthService : IAuthService
{
    private readonly Auth0Client _auth0;
    private const string TokenKey = "auth_token";
    private const string RoleKey = "auth_role";
    private const string UserIdKey = "auth_userid";
    private const string UserNameKey = "auth_username";
    private const string ApiAudience = "https://urfysio-api";
    private const string Auth0Domain = "https://dev-n4gkajag3xbup52c.us.auth0.com";
    private const string Auth0ClientId = "KM0R5l4jcCMaDIX9LcAxMOASD5VU4Cfm";
    private const string RolesClaim = "https://urfysio.nl/roles";

#if DEBUG
    private static readonly string ApiBaseUrl = DeviceInfo.Platform == DevicePlatform.Android
        ? "http://10.0.2.2:5068"
        : "http://localhost:5068";
#else
    private const string ApiBaseUrl = "https://urfysio-api-crfnashkcphjeaah.westeurope-01.azurewebsites.net";
#endif

    public string? CurrentRole { get; private set; }
    public Guid? CurrentUserId { get; private set; }
    public string? CurrentUserName { get; private set; }

    public AuthService(Auth0Client auth0)
    {
        _auth0 = auth0;
    }

    public async Task<bool> LoginAsync()
    {
        var extraParameters = new Dictionary<string, string>
        {
            { "audience", ApiAudience }
        };
        var result = await _auth0.LoginAsync(extraParameters);
        if (result.IsError) return false;

        return await ProcessLoginResult(result.AccessToken, result.User);
    }

    public async Task<bool> LoginWithGoogleAsync()
    {
        var extraParameters = new Dictionary<string, string>
        {
            { "audience", ApiAudience },
            { "connection", "google-oauth2" }
        };
        var result = await _auth0.LoginAsync(extraParameters);
        if (result.IsError) return false;

        return await ProcessLoginResult(result.AccessToken, result.User);
    }

    public async Task<bool> LoginWithMicrosoftAsync()
    {
        var extraParameters = new Dictionary<string, string>
        {
            { "audience", ApiAudience },
            { "connection", "windowslive" }
        };
        var result = await _auth0.LoginAsync(extraParameters);
        if (result.IsError) return false;

        return await ProcessLoginResult(result.AccessToken, result.User);
    }

    public async Task<(bool Success, string? ErrorMessage)> LoginWithPasswordAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return (false, "Email and password are required.");

        string rawResponse = string.Empty;
        try
        {
            using var http = new HttpClient();

            // 1. Exchange credentials for tokens via Resource Owner Password Grant
            HttpResponseMessage response;
            try
            {
                var body = new
                {
                    grant_type = "password",
                    client_id = Auth0ClientId,
                    username = email,
                    password,
                    audience = ApiAudience,
                    scope = "openid profile email"
                };
                response = await http.PostAsJsonAsync($"{Auth0Domain}/oauth/token", body);
            }
            catch (Exception httpEx)
            {
                return (false, $"Network error contacting Auth0: {httpEx.Message}");
            }

            try
            {
                rawResponse = await response.Content.ReadAsStringAsync();
            }
            catch (Exception readEx)
            {
                return (false, $"Failed to read Auth0 response: {readEx.Message}");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ParseAuth0Error(rawResponse, response.StatusCode);
            }

            // 2. Parse token response
            string? accessToken;
            try
            {
                using var tokenDoc = JsonDocument.Parse(rawResponse);
                accessToken = GetStringOrNull(tokenDoc.RootElement, "access_token");
            }
            catch (Exception parseEx)
            {
                return (false, $"Failed to parse token response: {parseEx.Message}");
            }

            if (string.IsNullOrEmpty(accessToken))
                return (false, "No access token received from Auth0.");

            // 3. Derive a fallback role from the access_token JWT in case the API sync fails.
            string jwtRole = "Client";
            try
            {
                var tokenClaims = DecodeJwtPayload(accessToken);
                jwtRole = GetClaimValue(tokenClaims, RolesClaim) ?? "Client";
            }
            catch
            {
                // Non-fatal
            }

            // 4. Fetch user profile from /userinfo endpoint as a fallback name source.
            string fallbackName = email;
            try
            {
                using var infoReq = new HttpRequestMessage(HttpMethod.Get, $"{Auth0Domain}/userinfo");
                infoReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                using var infoResp = await http.SendAsync(infoReq);
                if (infoResp.IsSuccessStatusCode)
                {
                    var infoJson = await infoResp.Content.ReadAsStringAsync();
                    using var infoDoc = JsonDocument.Parse(infoJson);
                    var infoRoot = infoDoc.RootElement;
                    fallbackName = GetStringOrNull(infoRoot, "name")
                        ?? GetStringOrNull(infoRoot, "email")
                        ?? email;
                }
            }
            catch
            {
                // Non-fatal — keep fallback name (the email)
            }

            // 5. Persist token first so downstream API calls can authenticate.
            await SecureStorage.Default.SetAsync(TokenKey, accessToken);

            // 6. Sync role/name/id from /api/users/me — this is the source of truth for the
            //    LOCAL database role, which matters when an admin has changed a user's role
            //    since the last login (the JWT would still carry the old Auth0 role).
            var apiUser = await FetchUserFromApiAsync(accessToken);
            var role = apiUser?.Role ?? jwtRole;
            var name = apiUser?.Name ?? fallbackName;

            await SecureStorage.Default.SetAsync(RoleKey, role);
            await SecureStorage.Default.SetAsync(UserNameKey, name);
            if (apiUser?.UserId is Guid uid)
                await SecureStorage.Default.SetAsync(UserIdKey, uid.ToString());

            CurrentRole = role;
            CurrentUserName = name;
            CurrentUserId = apiUser?.UserId;

            return (true, null);
        }
        catch (Exception ex)
        {
            var snippet = string.IsNullOrEmpty(rawResponse) ? "" : $" (response: {Truncate(rawResponse, 200)})";
            return (false, $"Login failed: {ex.Message}{snippet}");
        }
    }

    private static (bool Success, string? ErrorMessage) ParseAuth0Error(string rawResponse, System.Net.HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return (false, $"Login failed (HTTP {(int)statusCode}).");

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var error = GetStringOrNull(doc.RootElement, "error");
            var description = GetStringOrNull(doc.RootElement, "error_description");

            return error switch
            {
                "invalid_grant" => (false, "Invalid email or password."),
                "unauthorized" => (false, description ?? "Unauthorized. Your email may not be verified."),
                "unsupported_grant_type" => (false, "Password login is not enabled for this application. Enable the Password grant in Auth0."),
                "invalid_request" => (false, description ?? "Invalid login request."),
                _ => (false, description ?? $"Login failed (HTTP {(int)statusCode})."),
            };
        }
        catch
        {
            return (false, $"Login failed (HTTP {(int)statusCode}): {Truncate(rawResponse, 200)}");
        }
    }

    private static string? GetStringOrNull(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    /// <summary>
    /// After an Auth0 login, fetch the current user's role from the local database via
    /// <c>/api/users/me</c> so that admin role changes take effect on the next login instead
    /// of being shadowed by a stale JWT roles claim.
    /// Returns null if the API call or parsing fails — callers should keep their JWT-derived
    /// fallback role in that case.
    /// </summary>
    private static async Task<(string Role, string? Name, Guid? UserId)?> FetchUserFromApiAsync(string accessToken)
    {
        try
        {
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/users/me");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Role is serialized as an int by System.Text.Json (no StringEnumConverter on the API).
            string? role = null;
            if (root.TryGetProperty("role", out var roleProp))
            {
                role = roleProp.ValueKind switch
                {
                    JsonValueKind.Number when roleProp.TryGetInt32(out var n) => n switch
                    {
                        0 => "Client",
                        1 => "Physiotherapist",
                        2 => "Admin",
                        _ => null
                    },
                    JsonValueKind.String => roleProp.GetString(),
                    _ => null
                };
            }
            if (role is null) return null;

            var first = GetStringOrNull(root, "firstName");
            var last = GetStringOrNull(root, "lastName");
            string? name = null;
            if (!string.IsNullOrWhiteSpace(first) || !string.IsNullOrWhiteSpace(last))
                name = $"{first} {last}".Trim();

            Guid? userId = null;
            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                && Guid.TryParse(idProp.GetString(), out var parsedId))
            {
                userId = parsedId;
            }

            return (role, name, userId);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement>? DecodeJwtPayload(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1];
            // Base64URL → Base64
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            var json = Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return dict;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetClaimValue(Dictionary<string, JsonElement>? claims, string key)
    {
        if (claims is null || !claims.TryGetValue(key, out var value)) return null;

        try
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Array:
                    // Walk the array and return the first string element.
                    // FirstOrDefault() can return a default (Undefined) JsonElement,
                    // and calling GetString() on that throws InvalidOperationException.
                    foreach (var element in value.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                            return element.GetString();
                    }
                    return null;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                default:
                    return value.ToString();
            }
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SignUpAsync()
    {
        var extraParameters = new Dictionary<string, string>
        {
            { "screen_hint", "signup" },
            { "audience", ApiAudience }
        };
        var result = await _auth0.LoginAsync(extraParameters);
        if (result.IsError) return false;

        return await ProcessLoginResult(result.AccessToken, result.User);
    }

    private async Task<bool> ProcessLoginResult(string accessToken, ClaimsPrincipal user)
    {
        var jwtRole = user.FindFirst("https://urfysio.nl/roles")?.Value ?? "Client";
        var jwtName = user.FindFirst("name")?.Value
                ?? user.FindFirst(ClaimTypes.Name)?.Value
                ?? "Unknown";

        // Persist token first so /api/users/me can authenticate.
        await SecureStorage.Default.SetAsync(TokenKey, accessToken);

        // Source of truth for role is the LOCAL database (an admin may have changed it since
        // the JWT was issued). Fall back to the JWT claims on any failure.
        var apiUser = await FetchUserFromApiAsync(accessToken);
        var role = apiUser?.Role ?? jwtRole;
        var name = apiUser?.Name ?? jwtName;

        await SecureStorage.Default.SetAsync(RoleKey, role);
        await SecureStorage.Default.SetAsync(UserNameKey, name);
        if (apiUser?.UserId is Guid uid)
            await SecureStorage.Default.SetAsync(UserIdKey, uid.ToString());

        CurrentRole = role;
        CurrentUserName = name;
        CurrentUserId = apiUser?.UserId;

#if WINDOWS
        try { await Launcher.OpenAsync($"{ApiBaseUrl}/auth-callback"); } catch { }
#endif

        return true;
    }

    public async Task LogoutAsync()
    {
        SecureStorage.Default.Remove(TokenKey);
        SecureStorage.Default.Remove(RoleKey);
        SecureStorage.Default.Remove(UserIdKey);
        SecureStorage.Default.Remove(UserNameKey);

        CurrentRole = null;
        CurrentUserId = null;
        CurrentUserName = null;

#if WINDOWS
        // Silent logout on Windows — don't call Auth0's logout (it opens another browser tab).
        // Instead open our friendly logout page to replace any stale Auth0 tab.
        try { await Launcher.OpenAsync($"{ApiBaseUrl}/auth-logout"); } catch { }
#else
        await _auth0.LogoutAsync();
#endif
    }

    public async Task<string?> GetTokenAsync()
    {
        return await SecureStorage.Default.GetAsync(TokenKey);
    }

    public async Task<bool> IsLoggedInAsync()
    {
        var token = await GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        // Hydrate cached properties from SecureStorage first so the UI has something to
        // show immediately (no blocking network call on startup).
        if (CurrentRole is null)
        {
            CurrentRole = await SecureStorage.Default.GetAsync(RoleKey);
            var userIdStr = await SecureStorage.Default.GetAsync(UserIdKey);
            CurrentUserId = Guid.TryParse(userIdStr, out var uid) ? uid : null;
            CurrentUserName = await SecureStorage.Default.GetAsync(UserNameKey);
        }

        // Then refresh from the API in the background. If an admin changed the user's
        // role while they were logged in, the cached role would otherwise stay stale
        // until the user explicitly logs out and back in.
        _ = Task.Run(() => RefreshFromApiAsync(token));

        return true;
    }

    /// <summary>
    /// Public-interface entry point: fetches the access token and delegates to the
    /// token-parameterised overload. Callers can <c>await</c> this before reading
    /// <see cref="CurrentRole"/> to ensure the cached value reflects the current DB
    /// state (not the SecureStorage snapshot from login time).
    /// </summary>
    public async Task RefreshFromApiAsync()
    {
        var token = await GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return;
        await RefreshFromApiAsync(token);
    }

    /// <summary>
    /// Re-hits <c>/api/users/me</c> and updates the cached role, name, and user id.
    /// Safe to call any time the user is already authenticated; silently no-ops on
    /// failure so a flaky network doesn't log the user out.
    /// </summary>
    private async Task RefreshFromApiAsync(string accessToken)
    {
        try
        {
            var apiUser = await FetchUserFromApiAsync(accessToken);
            if (apiUser is null) return;

            var (role, name, userId) = apiUser.Value;

            await SecureStorage.Default.SetAsync(RoleKey, role);
            if (!string.IsNullOrEmpty(name))
                await SecureStorage.Default.SetAsync(UserNameKey, name);
            if (userId is Guid uid)
                await SecureStorage.Default.SetAsync(UserIdKey, uid.ToString());

            CurrentRole = role;
            if (!string.IsNullOrEmpty(name))
                CurrentUserName = name;
            if (userId.HasValue)
                CurrentUserId = userId;
        }
        catch
        {
            // Non-fatal — keep cached values.
        }
    }
}
