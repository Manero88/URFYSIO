# URFYSIO - Physiotherapy Practice Management

URFYSIO is a comprehensive, cross-platform physiotherapy practice management system built with **.NET 10**.

It comprises an ASP.NET Core 10 Web API backend and a .NET MAUI 10 frontend for Windows (and Android ready). The project uses a Clean Architecture approach with a deeply integrated Role-based Access Control (RBAC) system for Admins, Physiotherapists, and Clients.

## Features

- **Multi-Role Authentication**: JWT-based authentication supporting Admin, Physiotherapist, and Client roles.
- **Cross-Platform Client**: .NET MAUI application with distinct navigation and dashboards depending on the user token.
- **Appointment Management**: Securely book, reschedule, and cancel appointments with collision detection and double-booking prevention.
- **Availability Management**: Physiotherapists can define their specific working blocks, which clients can book into.
- **Treatment Plans**: Physiotherapists can define treatment plans consisting of multiple daily exercises for their clients.
- **Registration Flow**: Public registration request form that Admins can approve or reject, automatically generating user accounts and profile data.

## Project Structure

- `src/URFYSIO.Core` — Domain entities, Enums, and Service Interfaces.
- `src/URFYSIO.Infrastructure` — EF Core DbContext, Migrations, Repositories/Services implementations, and Seeding logic.
- `src/URFYSIO.API` — ASP.NET Core 10 Web API with JWT Auth, Swagger, and endpoint controllers.
- `src/URFYSIO.App` — .NET MAUI 10 multi-platform frontend application using CommunityToolkit.MVVM and Shell navigation.
- `src/URFYSIO.Shared` — DTOs and Enums shared across API and App for strong typing.
- `tests/URFYSIO.Tests` — xUnit testing suite covering core business logic and services using an In-Memory EF database.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- .NET MAUI workload (`dotnet workload install maui` or via Visual Studio Installer)
- Visual Studio 2022 (v17.10+) or VS Code with C# Dev Kit.

## Configuration & Secrets

The API reads its configuration from `src/URFYSIO.API/appsettings.json`, which is **git-ignored** because it contains secrets. The repository ships a template instead.

**First-time setup:**

1. Copy the template to the real file:
   ```bash
   cp src/URFYSIO.API/appsettings.example.json src/URFYSIO.API/appsettings.json
   ```
2. Fill in the values below.

| Setting | Secret? | Description |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | **Yes** (Password) | SQL Server / Azure SQL connection string. |
| `Auth0:Domain` | No | Your Auth0 tenant domain, e.g. `https://your-tenant.us.auth0.com`. |
| `Auth0:Audience` | No | The API identifier configured in Auth0. |
| `Auth0:ManagementClientId` | No | Machine-to-Machine (M2M) application client id. |
| `Auth0:ManagementClientSecret` | **Yes** | M2M application client secret. |
| `Auth0:AppClientId` | No | The native/SPA application client id used by the MAUI app. |
| `Auth0:DatabaseConnection` | No | Auth0 database connection name (default `Username-Password-Authentication`). |
| `Cors:AllowedOrigins` | No | Allowed origins for production CORS. |

The Auth0 **M2M application** must be granted these Management API scopes: `read:users`, `create:users`, `update:users`, `delete:users`, `read:roles`, plus role-assignment permissions. These power user sync, registration approval, role changes, and GDPR deletion.

> In production, supply these through **Azure App Service Configuration** or environment variables rather than a file. Never commit real secrets.

## Setup & Run Instructions

### 1. Database Migrations

The database is built on SQLite for localized development. The initial migration is provided. The API will automatically apply migrations and seed data on startup.

### 2. Running the API

Open a terminal at the solution root:

```bash
cd src/URFYSIO.API
dotnet run
```

The API will start at `https://localhost:7165` (or similar, check the output log). Swagger documentation is available at `/swagger`.

### 3. Running the MAUI App

Open another terminal at the solution root to run the Windows app:

```bash
cd src/URFYSIO.App
dotnet build -t:Run -f net10.0-windows10.0.19041.0
```

*(Note: ensure the `ApiService.cs` points to the correct local API port if it changes)*

## Demo Login Credentials

The database is automatically seeded upon first run with the following test accounts:

| Role | Email | Password |
|---|---|---|
| **Admin** | `admin@urfysio.nl` | `Admin123!` |
| **Physiotherapist** | `jan@urfysio.nl` | `Physio123!` |
| **Physiotherapist** | `marieke@urfysio.nl` | `Physio123!` |
| **Client** | `piet@example.nl` | `Client123!` |
| **Client** | `anna@example.nl` | `Client123!` |

*You can login with any of these users to evaluate their specific dashboard and role-restricted features in the MAUI application.*

## Running the Tests

To run the unit tests covering the business service layer logic:

```bash
dotnet test tests/URFYSIO.Tests
```
