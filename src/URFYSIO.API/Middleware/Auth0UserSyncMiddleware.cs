using System.Security.Claims;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Interfaces;

namespace URFYSIO.API.Middleware;

/// <summary>
/// On each authenticated request, ensures a local User record exists for the Auth0 subject
/// and replaces the authenticated principal's role claims with the *current database role*.
///
/// Why the role rewrite matters: ASP.NET Core's <c>[Authorize(Roles="...")]</c> resolves
/// via <c>TokenValidationParameters.RoleClaimType</c>. If we let it read the JWT claim
/// directly, an admin-driven role change in the local DB wouldn't take effect until the
/// user's JWT expires (~24h) or they log out and back in — because the already-issued
/// JWT still carries the old role. By stripping any inbound role claims and re-adding a
/// fresh <c>ClaimTypes.Role</c> claim from the DB, the DB becomes authoritative for API
/// authorization decisions — not just UI routing.
///
/// Pipeline ordering is load-bearing: this middleware MUST run after <c>UseAuthentication</c>
/// (so the JWT has been validated and <c>HttpContext.User</c> is populated) but BEFORE
/// <c>UseAuthorization</c> (so the rewritten claims are visible to the authorization filter).
/// See <c>Program.cs</c>.
///
/// EMAIL BACKFILL (subtle — read this before changing the email logic):
/// Auth0 access tokens (the bearer token sent to this API) do NOT include an
/// <c>email</c> claim by default. The custom <c>https://urfysio.nl/roles</c> claim arrives
/// because there's an Auth0 Action specifically adding it; there is no equivalent action
/// for email. So when we sync a user, we cannot trust the JWT to tell us their email —
/// we have to look it up via the Auth0 Management API. To keep that cheap, we only call
/// the Management API when the local DB row is missing a real email (either empty or
/// the legacy "{auth0Id}@auth0.local" placeholder); once the row is healed, subsequent
/// requests skip the lookup entirely.
/// </summary>
public class Auth0UserSyncMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<Auth0UserSyncMiddleware> _logger;

    public Auth0UserSyncMiddleware(RequestDelegate next, ILogger<Auth0UserSyncMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        // Logged once at app startup, when the pipeline is built. If this line is absent
        // from the console, the middleware was never registered — check Program.cs.
        _logger.LogInformation("Auth0UserSyncMiddleware constructed and registered in the pipeline.");
    }

    public async Task InvokeAsync(
        HttpContext context,
        IUserService userService,
        IAuth0ManagementService auth0Management)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var auth0Id = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? context.User.FindFirstValue("sub");

            _logger.LogInformation(
                "Auth0Sync: invoked for {Path}. Authenticated=true, auth0Id='{Auth0Id}', " +
                "IAuth0ManagementService injected: {MgmtNotNull}.",
                context.Request.Path, auth0Id ?? "(none)", auth0Management is not null);

            if (!string.IsNullOrEmpty(auth0Id))
            {
                // Step 1: try the JWT first (cheap). Auth0 access tokens don't carry
                // email by default, but if the tenant ever adds an Auth0 Action to inject
                // it (or if a user logs in via a flow that does include it), prefer the
                // local claim over a remote round-trip.
                var jwtEmail = context.User.FindFirstValue(ClaimTypes.Email)
                            ?? context.User.FindFirstValue("email");
                var jwtFirst = context.User.FindFirstValue(ClaimTypes.GivenName)
                            ?? context.User.FindFirstValue("given_name");
                var jwtLast = context.User.FindFirstValue(ClaimTypes.Surname)
                           ?? context.User.FindFirstValue("family_name");

                var user = await userService.GetByAuth0IdAsync(auth0Id);

                if (user is null)
                {
                    // Auto-create flow. Social access tokens (Google/Microsoft) carry
                    // neither email nor name, so fetch the Auth0 profile once and use it
                    // for both. We skip the round-trip entirely when the JWT already
                    // supplied everything (tenant with claim-injection Actions).
                    Auth0UserProfile? profile = null;
                    if (jwtEmail is null || jwtFirst is null || jwtLast is null)
                        profile = await auth0Management.GetUserProfileAsync(auth0Id);

                    var email = jwtEmail
                             ?? profile?.Email
                             ?? $"{auth0Id}@auth0.local";

                    // Only fall back to "Unknown"/"User" when neither the token nor Auth0
                    // knows the name — otherwise every SSO sign-up looks like a stranger
                    // in the admin's user list.
                    var (profileFirst, profileLast) = profile?.SplitName() ?? (null, null);
                    var firstName = jwtFirst ?? profileFirst ?? "Unknown";
                    var lastName = jwtLast ?? profileLast ?? "User";

                    _logger.LogInformation(
                        "Auth0Sync: creating local user for {Auth0Id}. JWT email: '{JwtEmail}'. " +
                        "Resolved email: '{Email}'. Resolved name: '{First} {Last}'.",
                        auth0Id, jwtEmail ?? "(none)", email, firstName, lastName);

                    var roleClaim = context.User.FindFirstValue("https://urfysio.nl/roles");
                    var role = Enum.TryParse<UserRole>(roleClaim, ignoreCase: true, out var parsed)
                        ? parsed
                        : UserRole.Client;

                    var newUser = new User
                    {
                        Auth0Id = auth0Id,
                        Email = email,
                        FirstName = firstName,
                        LastName = lastName,
                        Role = role,
                        PasswordHash = string.Empty,
                        // Stamped at creation so the very first request doesn't
                        // immediately trigger a second write from the throttle below.
                        LastLoginAt = DateTime.UtcNow,
                        // Auto-created accounts (someone logging in via Auth0 — Google,
                        // Microsoft, or a fresh email/password signup — with no prior
                        // local record) start INACTIVE. They must be approved by an
                        // admin before they can use the app. The login screen detects
                        // the inactive state, signs them straight back out, and shows
                        // a "pending approval" message. Admin-created users and
                        // registration-approved users are created active elsewhere.
                        IsActive = false
                    };

                    user = await userService.CreateAsync(newUser, string.Empty);
                }
                else
                {
                    // Self-heal: backfill the real email if this row was created when we
                    // were defaulting to the "{auth0Id}@auth0.local" placeholder. Detect
                    // either the literal "@auth0.local" suffix OR a row whose email still
                    // matches the auth0Id verbatim (older code path that didn't add the
                    // suffix).
                    var hasFakeEmail = string.IsNullOrEmpty(user.Email)
                                    || user.Email.EndsWith("@auth0.local", StringComparison.OrdinalIgnoreCase)
                                    || user.Email.Equals(auth0Id, StringComparison.OrdinalIgnoreCase);
                    var hasPlaceholderName = HasPlaceholderName(user);

                    if (hasFakeEmail || hasPlaceholderName)
                    {
                        // Diagnostic: dump every claim the JWT actually carried. This is
                        // the definitive answer to "is the email in the token?" — if it's
                        // here under some claim type we didn't check, we'll see it.
                        var allClaims = string.Join(" | ",
                            context.User.Claims.Select(c => $"{c.Type}={c.Value}"));
                        _logger.LogInformation(
                            "Auth0Sync: JWT claims for {Auth0Id} => {Claims}",
                            auth0Id, allClaims);

                        // Try JWT first, then Management API. The lookup is gated on the
                        // row actually being incomplete, so we only pay it until the row
                        // is healed — afterwards this branch never re-fires for that user.
                        var realEmail = jwtEmail;
                        var source = "JWT";
                        Auth0UserProfile? profile = null;

                        var needsRemoteEmail = hasFakeEmail
                            && (string.IsNullOrEmpty(realEmail)
                                || realEmail.EndsWith("@auth0.local", StringComparison.OrdinalIgnoreCase));
                        var needsRemoteName = hasPlaceholderName && (jwtFirst is null || jwtLast is null);

                        if (needsRemoteEmail || needsRemoteName)
                        {
                            profile = await auth0Management.GetUserProfileAsync(auth0Id);
                            if (needsRemoteEmail)
                            {
                                realEmail = profile?.Email;
                                source = "Auth0 Management API";
                            }
                        }

                        var changed = false;

                        if (hasFakeEmail)
                        {
                            _logger.LogInformation(
                                "Auth0Sync: user {UserId} ({Auth0Id}) has fake DB email '{DbEmail}'. " +
                                "JWT email: '{JwtEmail}'. Resolved via {Source}: '{Resolved}'.",
                                user.Id, auth0Id, user.Email, jwtEmail ?? "(none)", source, realEmail ?? "(none)");

                            if (!string.IsNullOrEmpty(realEmail)
                                && !realEmail.EndsWith("@auth0.local", StringComparison.OrdinalIgnoreCase))
                            {
                                user.Email = realEmail;
                                changed = true;
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Auth0Sync: could not resolve a real email for user {UserId} ({Auth0Id}); " +
                                    "leaving placeholder in place. Check that the Management API client has the " +
                                    "'read:users' scope.",
                                    user.Id, auth0Id);
                            }
                        }

                        if (hasPlaceholderName)
                        {
                            // Same self-heal as the email: existing rows created before the
                            // profile lookup existed show as "Unknown User", and this repairs
                            // them on the owner's next login.
                            var (profileFirst, profileLast) = profile?.SplitName() ?? (null, null);
                            var resolvedFirst = jwtFirst ?? profileFirst;
                            var resolvedLast = jwtLast ?? profileLast;

                            _logger.LogInformation(
                                "Auth0Sync: user {UserId} ({Auth0Id}) has placeholder name '{DbName}'. " +
                                "Resolved: '{First} {Last}'.",
                                user.Id, auth0Id, $"{user.FirstName} {user.LastName}".Trim(),
                                resolvedFirst ?? "(none)", resolvedLast ?? "(none)");

                            // Only overwrite with something real — never replace a known name
                            // with a blank because Auth0 happened to omit the field.
                            if (!string.IsNullOrWhiteSpace(resolvedFirst))
                            {
                                user.FirstName = resolvedFirst;
                                changed = true;
                            }
                            if (!string.IsNullOrWhiteSpace(resolvedLast))
                            {
                                user.LastName = resolvedLast;
                                changed = true;
                            }

                            if (!changed)
                                _logger.LogWarning(
                                    "Auth0Sync: could not resolve a real name for user {UserId} ({Auth0Id}); " +
                                    "leaving placeholder in place.", user.Id, auth0Id);
                        }

                        if (changed)
                        {
                            await userService.UpdateAsync(user);
                            _logger.LogInformation(
                                "Auth0Sync: healed user {UserId} — email '{Email}', name '{Name}'.",
                                user.Id, user.Email, $"{user.FirstName} {user.LastName}".Trim());
                        }
                    }

                    // Self-heal: a user whose role was changed by an admin may be missing
                    // the profile entity that matches their current role (e.g. promoted
                    // from Client to Physiotherapist but no PhysiotherapistProfile was ever
                    // created). Add the missing profile on the fly so downstream flows
                    // (creating availability slots, booking appointments, etc.) succeed.
                    if ((user.Role == UserRole.Physiotherapist && user.PhysiotherapistProfile is null)
                        || (user.Role == UserRole.Client && user.ClientProfile is null))
                    {
                        await userService.EnsureProfileForRoleAsync(user.Id);
                    }
                }

                // Record activity, throttled. Writing on every authenticated request would
                // add a DB round-trip to each call for a value nobody needs to the second;
                // at one write per LastActiveThrottle window the admin still gets a useful
                // "last active" reading. Newly created users were stamped above, so this
                // is a no-op for them.
                if (ShouldUpdateLastActive(user.LastLoginAt, DateTime.UtcNow))
                {
                    user.LastLoginAt = DateTime.UtcNow;
                    await userService.UpdateAsync(user);
                }

                // Replace the authenticated principal with one whose role claims reflect
                // the *current DB* role. We strip two kinds of stale claims:
                //   - "https://urfysio.nl/roles" (Auth0 Action injects this)
                //   - ClaimTypes.Role (the standard, which is what TokenValidationParameters
                //     .RoleClaimType maps inbound claims onto)
                // and we add a single fresh ClaimTypes.Role from user.Role. This also
                // normalizes "Administrator" / "Admin" / any Auth0 duplicate role naming
                // to the single canonical value that lives in our enum.
                var claims = context.User.Claims
                    .Where(c => c.Type != ClaimTypes.Role
                             && c.Type != "https://urfysio.nl/roles")
                    .ToList();
                claims.Add(new Claim(ClaimTypes.Role, user.Role.ToString()));

                var newIdentity = new ClaimsIdentity(
                    claims,
                    context.User.Identity?.AuthenticationType,
                    nameType: ClaimTypes.NameIdentifier,
                    roleType: ClaimTypes.Role);
                context.User = new ClaimsPrincipal(newIdentity);

                // Store local user ID in HttpContext for controllers
                context.Items["LocalUserId"] = user.Id;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// How stale <c>User.LastLoginAt</c> must be before it's worth another write. Chosen to
    /// keep the value meaningful as a "last active" reading while costing at most one write
    /// per user per window, instead of one per authenticated request.
    /// </summary>
    public static readonly TimeSpan LastActiveThrottle = TimeSpan.FromMinutes(15);

    /// <summary>
    /// True when activity should be persisted: the user has never been recorded, or the
    /// stored stamp is older than <see cref="LastActiveThrottle"/>.
    /// A stamp in the future (clock skew, or a row edited by hand) also refreshes, so a bad
    /// value can't freeze the field forever.
    /// </summary>
    public static bool ShouldUpdateLastActive(DateTime? lastLoginAt, DateTime utcNow)
    {
        if (lastLoginAt is null) return true;
        var elapsed = utcNow - lastLoginAt.Value;
        return elapsed >= LastActiveThrottle || elapsed < TimeSpan.Zero;
    }

    /// <summary>
    /// True when the stored name is the auto-created placeholder rather than something the
    /// user would recognise as their own. Covers blank fields and the literal
    /// "Unknown"/"User" pair written by the auto-create path before the Auth0 profile
    /// lookup existed. A user genuinely surnamed "User" with a real first name is not
    /// matched, because both halves must look like placeholders.
    /// </summary>
    public static bool HasPlaceholderName(User user)
    {
        var first = user.FirstName?.Trim() ?? string.Empty;
        var last = user.LastName?.Trim() ?? string.Empty;

        if (first.Length == 0 && last.Length == 0) return true;

        var firstIsPlaceholder = first.Length == 0
            || first.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        var lastIsPlaceholder = last.Length == 0
            || last.Equals("User", StringComparison.OrdinalIgnoreCase);

        return firstIsPlaceholder && lastIsPlaceholder;
    }
}
