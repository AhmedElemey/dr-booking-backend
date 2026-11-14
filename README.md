# moonlight-backend

.NET 8 Web API for Moonlight, a dentist reservation platform. The MVP spec for all four Moonlight repos is in [docs/SPEC.md](docs/SPEC.md). This repo implements milestones M0 to M2: auth, catalog, schedule management, slots, appointments and notifications.

## Run locally

```bash
docker compose up -d          # Postgres on localhost:5432 (user/password postgres)
dotnet run --project src/Moonlight.Api
curl http://localhost:5267/health
```

In **Development** the API migrates the `moonlight` database on startup and seeds demo data (see below). Swagger UI is at `/swagger`, the raw document at `/swagger/v1/swagger.json`. Nothing is migrated or seeded in any other environment; production runs `dotnet ef database update` as an explicit CI/CD step.

### Demo data and accounts (Development only)

Everything logs in with phone OTP. In Development and Staging the fixed code **`123456`** is accepted (`Auth:FixedOtp`); the API refuses to start in Production if that setting is present, and in Production the code is simply rejected. The SMS sender is a stub that logs the real code.

| Role | Name | Phone | Notes |
|---|---|---|---|
| `PlatformAdmin` | `<clinic name> Admin` (Moonlight Admin by default) | `+201000000001` (`01000000001`) | admin endpoints |
| `ClinicStaff` | Mona Reception | `+201000000002` | Smile Care Clinic |
| `Doctor` | Dr. Amr Hassan | `+201000000011` | General Dentistry, slug `dr-amr-hassan` |
| `Doctor` | Dr. Sara Mahmoud | `+201000000012` | Orthodontics, slug `dr-sara-mahmoud` |
| `Doctor` | Dr. Youssef Nabil | `+201000000013` | Pediatric Dentistry, slug `dr-youssef-nabil` |
| `Patient` | any other Egyptian mobile | e.g. `01011112222` | created on first OTP login |

The seed creates the six specialties (also shipped by the migration), the demo clinic *Smile Care Clinic* in Cairo (`Africa/Cairo`), three published doctors with services, and 30-minute open slots (10:00-14:00 and 17:00-20:00, Saturday to Thursday) for the next 14 clinic-local days. Restarting the API tops the slots up again.

```bash
# log in as a patient
curl -X POST localhost:5267/api/v1/auth/otp/request -H 'content-type: application/json' -d '{"phone":"01011112222"}'
curl -X POST localhost:5267/api/v1/auth/otp/verify  -H 'content-type: application/json' -d '{"phone":"01011112222","code":"123456"}'
```

## Configuration

Environment variables use `__` for nesting (`Jwt__Key`).

| Setting | Default | Purpose |
|---|---|---|
| `ConnectionStrings__Default` | dev: local `moonlight` db | Npgsql connection string |
| `Jwt__Key` | dev key in `appsettings.Development.json` | HS256 signing key, at least 32 characters, **required** outside Development |
| `Booking__MaxUpcomingPerPatient` | 3 | upcoming Confirmed appointments per patient (also max 1 per doctor per clinic-local day) |
| `Booking__PatientCancelWindowHours` | 2 | patients may cancel / reschedule until this long before the visit |
| `Booking__MinLeadMinutes` | 60 | patients cannot book slots starting sooner than this |
| `RateLimits__OtpPerPhonePerMinute` / `PerHour`, `OtpPerIpPerHour` | 1 / 5 / 20 | OTP request limits |
| `Sms__NotificationsEnabled` | false | SMS confirmations/reminders for patients without a device token (OTP SMS is always sent) |
| `Sms__Provider` | `Log` | only the logging stub exists until M6 |
| `Fcm__ServiceAccountJson` | empty | Firebase service account; empty means pushes are only logged |
| `Reminders__Enabled` / `IntervalMinutes` | true / 5 | reminder background service |
| `Storage__Path` | `uploads` | local disk for doctor photos (object storage lands in M6) |
| `Auth__FixedOtp` | dev: `123456` | development bypass, startup fails if set in Production |
| `ClinicConfig__Path` | auto | path of `clinic.config.json`; by default found next to the binaries (copied on build/publish), then the repo root |
| `Console__Url` | `console.url` in `clinic.config.json` | base URL of the super admin console; the runtime pull is off unless Url, ClinicId and ApiKey are all set |
| `Console__ClinicId` | `console.clinicId` in `clinic.config.json` | this clinic's id in the console |
| `Console__ApiKey` | empty | sent as `X-Clinic-Key`; keep it a secret, never put it in the config file |
| `Console__RefreshSeconds` | 300 | how often the console is pulled (at least 1) |

## Clinic config and features

Each clinic's identity lives in one file, [`clinic.config.json`](clinic.config.json) at the repo root (validated by [`clinic.config.schema.json`](clinic.config.schema.json), logo in `branding/`). The API loads it at startup and **refuses to start** with a message listing every problem if it is missing or invalid. It is copied next to the binaries on build and into the Docker image on publish. The same file is used by the website, mobile app and console repos.

It sets the clinic name (Arabic and English), tagline, colours, logo, support phone, app ids and the **default feature switches**. The clinic name replaces "Moonlight" everywhere users see it: the OTP SMS, the Swagger title and the dev admin account name. To rebrand a copy of this repo for a clinic, edit `clinic.config.json` and replace `branding/logo.svg`; no code changes.

Public endpoints (anonymous):

- `GET /api/v1/features` returns `{ features: { ... }, source, updatedAt }`, cacheable for 60 s. `source` is `console` (pulled live), `cache` (last copy of a console pull, used after a restart while the console is unreachable) or `defaults` (`clinic.config.json`).
- `GET /api/v1/branding` returns `{ slug, name, tagline, colors, supportPhone, logoUrl }`; `GET /api/v1/branding/logo` serves the logo file.

| Feature | When off |
|---|---|
| `mobileApp`, `website`, `englishLanguage` | reported to clients only; they hide or replace their UI |
| `guestBrowsing` | doctor search, profile and slots answer 401 to anonymous callers (specialties and cities stay public) |
| `patientSelfService` | patient cancel and reschedule answer 403 `FEATURE_DISABLED`; clinic staff can still cancel |
| `pushReminders` | confirmation and reminder pushes are skipped (a clinic cancellation is still pushed) |
| `smsNotifications` | when on, patients without a device token (or with pushes off) get SMS, in addition to `Sms__NotificationsEnabled` |
| `multiBranch` | admin cannot create a second clinic: 403 `FEATURE_DISABLED` (the first is allowed) |

A disabled feature returns `application/problem+json` with `code: "FEATURE_DISABLED"`, `feature: "<key>"` and a title localized by `Accept-Language`.

**Console pull.** When `Console__Url`, `Console__ClinicId` and `Console__ApiKey` are all set, a background service calls `GET {Url}/api/v1/clinics/{clinicId}/runtime-config` (header `X-Clinic-Key`) every `Console__RefreshSeconds` and expects `{ "features": { ... }, "updatedAt": "..." }`. Unknown keys are ignored and missing keys take the `clinic.config.json` default. Each good response is saved in the single-row `feature_state` table, so a restart while the console is down keeps the last switches. A failing console (timeout, error status, bad JSON) is logged and never affects the API: the previous switches stay in force.

## Test

```bash
dotnet test
```

Integration tests start a throwaway Postgres with Testcontainers (needs Docker). To use an existing server instead, set `TEST_DATABASE_URL` to an Npgsql connection string, for example `Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres`. Every test class then creates, migrates and drops its own database, so classes never collide. CI does this with a Postgres service container.

CI (`.github/workflows/ci.yml`) runs restore, `dotnet tool restore`, `dotnet format --verify-no-changes`, `dotnet build -warnaserror`, `dotnet test`, exports `openapi.json` with the Swashbuckle CLI and uploads it as the `openapi` artifact, and builds the Docker image on `main`.

## Migrations

```bash
dotnet tool restore
ASPNETCORE_ENVIRONMENT=Staging ConnectionStrings__Default="Host=localhost;Database=moonlight_design;Username=postgres;Password=postgres" \
Jwt__Key=design-time-key-design-time-key-design-time dotnet ef migrations add Name --project src/Moonlight.Api --output-dir Infrastructure/Migrations
```

Use a non-Development environment so the app does not migrate and seed while the tool loads it. The `SlotOverlapConstraint` migration creates `btree_gist` and the `no_overlapping_slots` exclusion constraint with raw SQL.

## Layout

- `src/Moonlight.Api` — the API: `Features/` (Auth, Me, Catalog, Appointments, Dashboard, Admin, Notifications, Flags for features and the console pull, Branding), `Domain/` (entities, `SlotGenerator`, `AppointmentRules`), `Infrastructure/` (DbContext, migrations, JWT, SMS, FCM, storage, dev seed), `Common/` (problem details, paging, rate limiting, clinic config)
- `tests/Moonlight.Tests` — unit tests (slot generator, transition rules, phone parsing) and integration tests against Postgres
