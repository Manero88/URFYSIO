using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using URFYSIO.API.Middleware;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Seeding;
using URFYSIO.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Database ----
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions => sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorNumbersToAdd: null)));

// ---- Services (business logic) ----
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IUserDeletionService, UserDeletionService>();
builder.Services.AddScoped<IAppointmentService, AppointmentService>();
builder.Services.AddScoped<IAvailabilityService, AvailabilityService>();
builder.Services.AddScoped<ITreatmentPlanService, TreatmentPlanService>();
builder.Services.AddScoped<IRegistrationService, RegistrationService>();
builder.Services.AddScoped<IAuth0ManagementService, Auth0ManagementService>();

// ---- HttpClient factory (for Auth0 Management API) ----
builder.Services.AddHttpClient();

// ---- Auth0 JWT Authentication ----
var auth0Domain = builder.Configuration["Auth0:Domain"]
    ?? throw new InvalidOperationException("Auth0:Domain is not configured.");
var auth0Audience = builder.Configuration["Auth0:Audience"]
    ?? throw new InvalidOperationException("Auth0:Audience is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = auth0Domain.TrimEnd('/') + "/";
        options.Audience = auth0Audience;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "sub",
            // Bind role resolution to the standard ClaimTypes.Role. The Auth0 Action
            // injects roles under "https://urfysio.nl/roles", but we don't trust that
            // claim for authorization — Auth0UserSyncMiddleware rewrites the principal
            // with a fresh ClaimTypes.Role claim sourced from the local DB, so the DB
            // is authoritative and admin-driven role changes take effect on the next
            // request (not the next re-login).
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization();

// ---- Controllers ----
builder.Services.AddControllers();

// ---- Swagger ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "URFYSIO API",
        Version = "v1",
        Description = "Physiotherapy practice management API"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Enter 'Bearer {token}'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ---- Rate Limiting ----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("public", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
});

// ---- CORS ----
builder.Services.AddCors(options =>
{
    if (builder.Environment.IsDevelopment())
    {
        options.AddDefaultPolicy(policy =>
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    }
    else
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();
        options.AddDefaultPolicy(policy =>
            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader());
    }
});

var app = builder.Build();

// ---- Middleware pipeline ----
    app.UseSwagger();
    app.UseSwaggerUI();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.UseAuthentication();
// Ordering is load-bearing: Auth0UserSyncMiddleware MUST run after UseAuthentication
// (so the JWT has been validated and HttpContext.User is populated) and BEFORE
// UseAuthorization (so the rewritten role claim, sourced from the local DB, is what
// [Authorize(Roles="...")] sees when the endpoint is evaluated).
app.UseMiddleware<Auth0UserSyncMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

// ---- Database migration + seeding ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db);

    // Idempotent repair step for users whose role was flipped by an admin before the
    // profile auto-create fix shipped. This is a no-op once every user has the correct
    // profile, so it's safe to leave running on every startup.
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var backfilled = await DbSeeder.BackfillMissingProfilesAsync(db);
    if (backfilled > 0)
        logger.LogWarning("DbSeeder backfilled {Count} missing profile(s) at startup.", backfilled);

    // Startup self-check: prove the Auth0 Management service resolves from DI. If this
    // logs "False" the email backfill cannot work — the middleware would get a null
    // service. (It won't actually be null given the AddScoped registration above; this
    // line exists so the startup console makes the wiring unambiguous.)
    var auth0Mgmt = scope.ServiceProvider.GetService<IAuth0ManagementService>();
    logger.LogInformation(
        "Auth0UserSyncMiddleware wiring check — IAuth0ManagementService resolved: {NotNull}",
        auth0Mgmt is not null);
}

app.MapGet("/", () => Results.Ok("URFYSIO API is running"));
app.MapGet("/health", () => Results.Ok("Healthy"));

// Post-login landing page. The MAUI app opens this (Windows) after an Auth0 SSO/signup
// flow so the user gets a clean confirmation tab that closes itself.
//
// IMPORTANT — do NOT add a deep-link back to the app here. An earlier version
// redirected to "myapp://callback" as a bring-the-app-forward fallback, but that
// custom scheme IS the Auth0 OIDC redirect URI: navigating to it fires a Windows
// protocol activation that the Auth0 client plumbing treats as an auth callback,
// which re-opened the authorize screen after every successful login.
app.MapGet("/auth-callback", () => Results.Content("""
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>URFYSIO</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 100vh;
            margin: 0;
            background: #1a1a2e;
            color: white;
        }
        .card {
            text-align: center;
            padding: 36px 48px;
            background: #16213e;
            border: 1px solid #7c3aed;
            border-radius: 12px;
        }
        h1 { color: #7c3aed; font-size: 1.4em; margin: 0 0 8px 0; }
        p { font-size: 0.95em; color: #b0b0b0; margin: 6px 0; }
        .icon { font-size: 2.4em; }
        .btn {
            margin-top: 18px;
            padding: 10px 22px;
            background: #7c3aed;
            border: none;
            border-radius: 8px;
            color: white;
            font-size: 0.95em;
            cursor: pointer;
        }
        .btn:hover { background: #6d28d9; }
    </style>
</head>
<body>
    <div class="card">
        <div class="icon">&#x2705;</div>
        <h1>Logged in</h1>
        <p>You can return to the URFYSIO app.</p>
        <button class="btn" onclick="window.close()">Close window</button>
    </div>
    <script>
        // Close immediately; retry once in case the first attempt raced page load.
        // No deep-link fallback — see the route comment above.
        window.close();
        setTimeout(function () { window.close(); }, 300);
    </script>
</body>
</html>
""", "text/html"));

// Post-logout landing page. Same instant self-close strategy; never deep-links
// back into the app (the user just logged out, and myapp:// would re-fire the
// OIDC protocol activation — see /auth-callback comment).
app.MapGet("/auth-logout", () => Results.Content("""
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>URFYSIO</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 100vh;
            margin: 0;
            background: #1a1a2e;
            color: white;
        }
        .card {
            text-align: center;
            padding: 36px 48px;
            background: #16213e;
            border: 1px solid #7c3aed;
            border-radius: 12px;
        }
        h1 { color: #7c3aed; font-size: 1.4em; margin: 0 0 8px 0; }
        p { font-size: 0.95em; color: #b0b0b0; margin: 6px 0; }
        .icon { font-size: 2.4em; }
        .btn {
            margin-top: 18px;
            padding: 10px 22px;
            background: #7c3aed;
            border: none;
            border-radius: 8px;
            color: white;
            font-size: 0.95em;
            cursor: pointer;
        }
        .btn:hover { background: #6d28d9; }
    </style>
</head>
<body>
    <div class="card">
        <div class="icon">&#x1F44B;</div>
        <h1>Logged out</h1>
        <p>You can close this window.</p>
        <button class="btn" onclick="window.close()">Close window</button>
    </div>
    <script>
        window.close();
        setTimeout(function () { window.close(); }, 300);
    </script>
</body>
</html>
""", "text/html"));

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }
