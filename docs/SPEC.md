# Dr booking — MVP Technical Spec

Dentist reservation platform. Patients find a dentist and book a slot (mobile app or website); doctors and clinic staff manage appointments in a dashboard. Payment is at the clinic.

> **Phase 2.** The 13 optional features (video, waiting list, family accounts, WhatsApp, online payments, insurance, holidays, working-hours templates, patient records, reviews, promotions, recall campaigns, reports) are specified in [FEATURES-PHASE2.md](FEATURES-PHASE2.md). They are all off by default and gated by feature switches; with them off the MVP behaviour below is unchanged (online payment, when enabled, is optional: booking stays instantly Confirmed).

> **ASSUMPTION TO CONFIRM — Market: Egypt.** Currency EGP (display only), Arabic + English with RTL, phone-number-first sign-up (Egyptian mobile numbers, E.164 `+20…`), clinic timezone `Africa/Cairo`. Everything below assumes this; see Open Questions #1.

## Decided by the product owner (fixed)

| Repo | Stack | Audience |
|---|---|---|
| `dr-booking-backend` | .NET 8 Web API, EF Core, **PostgreSQL** | all clients |
| `dr-booking-mobile` | Flutter (Riverpod, go_router, Dio, fpdart; clean architecture) | patients |
| `dr-booking-dashboard` | Angular | doctors, clinic staff, (minimal) platform admin |
| `dr-booking-website` | Next.js | public landing, doctor profiles, web booking |

**Database: PostgreSQL** — range types plus an exclusion constraint make double-booking impossible at the database level, and it is free and cheap to host anywhere.

Architecture is one deployable API + one database. No microservices, no event bus, no queue.

---

## 1. Goals & MVP scope

**Goal:** a patient can find a dentist, see real free slots, and book in under a minute; the clinic sees the booking immediately and runs the day from the dashboard.

**In scope (v1)**
- Phone-number sign-up/login with SMS OTP (patients, doctors, staff — same flow).
- Browse/search published doctors by specialty, city, and name; public doctor profile (guest browsing, login only at booking).
- Doctors list their treatment types (e.g. Check-up, Cleaning, Filling) with a duration, for display.
- Availability is **explicit slots**: doctors/staff create concrete bookable slots (one at a time, or with a bulk "repeat" helper that just creates slot rows). A patient picks a doctor, picks one open slot, and confirms. Nothing is computed from schedules.
- Book (pick doctor, pick open slot, confirm), view, reschedule, and cancel appointments (patient: mobile + website).
- Clinic side: view/filter appointments, create appointments on behalf of phone-in patients, cancel, reschedule, mark completed / no-show.
- Clinic side: manage doctor profile, services, and the slot calendar (add, bulk add, block/unblock, remove slots); manage clinic info.
- Notifications: booking confirmation, reminder (24h before), cancellation (FCM push; SMS optional, open question).
- Arabic/English with RTL on all three client apps.
- Minimal platform admin: create clinics, create doctor/staff accounts, deactivate.
- Pay at clinic. Prices shown are informational only.

**Out of scope for v1**
- Online payments, deposits, invoices, insurance.
- Reviews/ratings, favorites.
- Medical records, prescriptions, X-rays, treatment plans, chat, video consultation.
- Booking for family members / dependents.
- Doctors working in multiple clinics; rooms, chairs, or equipment scheduling.
- Recurring appointments, waiting lists, auto-extending slot templates (the bulk helper creates rows once), working-hours schedules, time-off entities, clinic-wide holiday calendar.
- Email notifications, web push, push notifications to dashboard users (dashboard refetches on a timer).
- Analytics/reports, patient CRM, coupons/promotions.
- Fine-grained staff permissions, self-service clinic sign-up, admin CRUD for specialties (seeded).
- Map/distance search, social login, offline mode, multi-country/multi-currency.
- Microservices, event bus, caching layer, search engine.

---

## 2. Roles & permissions

One role per user in v1 (a doctor cannot also be a patient with the same phone). A user's role is in the JWT (`role` claim); doctors and staff also carry `clinicId`, doctors carry `doctorId`.

| Role | Who | Created by |
|---|---|---|
| `Patient` | Anyone with an Egyptian mobile number | Self (first OTP login), or implicitly when staff books on their behalf |
| `Doctor` | Dentist with a profile | `PlatformAdmin` |
| `ClinicStaff` | Receptionist/manager of one clinic | `PlatformAdmin` |
| `PlatformAdmin` | Dr booking team | Seeded / DB |

**Scope rules**

| Capability | Anon | Patient | Doctor | ClinicStaff | PlatformAdmin |
|---|---|---|---|---|---|
| Browse doctors, profiles, slots (published only) | yes | yes | yes | yes | yes |
| Book / view / cancel / reschedule **own** appointments | – | yes | – | – | – |
| View appointments | – | own | own (their `doctorId`) | all doctors in their clinic | – |
| Create appointment on behalf of a patient | – | – | own | clinic's doctors | – |
| Cancel / reschedule / complete / no-show | – | own (cancel/reschedule only, within window) | own | clinic's doctors | – |
| Edit doctor profile and services; create, bulk-create, block, unblock, remove slots | – | – | own | clinic's doctors | – |
| Edit clinic info | – | – | – | own clinic | – |
| Create clinics, doctor/staff accounts; deactivate | – | – | – | – | yes |
| Dashboard appointment data | – | – | – | – | no (admin does not see patient data) |

Enforcement is server-side only (policy-based authorization + resource scope checks in the service layer); the clients hide UI as a convenience. A resource outside the caller's scope returns `404`, not `403`, to avoid leaking existence. A `Patient` token calling a dashboard endpoint (or vice versa) returns `403`.

---

## 3. Data model

PostgreSQL. All primary keys are `uuid`. All instants are `timestamptz` stored in UTC. Names that users read have `_ar`/`_en` columns.

### Entities

**User** — `id`, `phone` (unique, E.164), `full_name` (nullable until profile completed), `role` (enum), `preferred_language` (`ar`|`en`, default `ar`), `is_active`, `created_at`.
*Patient is not a separate table in v1:* it is a `User` with role `Patient`. Add a `Patient` table only when patient-specific fields (DOB, history) are actually needed.

**Clinic** — `id`, `name_ar`, `name_en`, `address_ar`, `address_en`, `city` (text, from a seeded list), `latitude?`, `longitude?`, `phone`, `timezone` (IANA, default `Africa/Cairo`), `is_active`, `created_at`.

**ClinicStaff** — `id`, `user_id` (unique FK), `clinic_id` (FK).

**Specialty** (seeded, read-only) — `id`, `name_ar`, `name_en`, e.g. General Dentistry, Orthodontics, Pediatric Dentistry, Endodontics, Cosmetic Dentistry, Oral Surgery.

**Doctor** — `id`, `user_id` (unique FK), `clinic_id` (FK, exactly one clinic in v1), `specialty_id` (FK), `slug` (unique, from English name), `name_ar`, `name_en`, `bio_ar?`, `bio_en?`, `photo_url?`, `years_experience?`, `consultation_fee_egp?` (display only), `is_published`, `created_at`.
A published doctor with no open slots stays listed; clients show "no availability".

**Service** — `id`, `doctor_id` (FK), `name_ar`, `name_en`, `duration_minutes` (15–240; informational, prefills the slot length in the bulk helper), `price_egp?` (display only), `is_active`. Never hard-deleted (appointments may reference it); "delete" sets `is_active = false`.

**Slot** — `id`, `doctor_id` (FK), `clinic_id` (FK, denormalized from the doctor for clinic-scoped queries), `starts_at`, `ends_at` (UTC), `status` (`Open` | `Reserved` | `Blocked`), `created_at`. A slot is a concrete bookable time created explicitly by the doctor/staff. There is no working-hours schedule and no time-off entity: a day off is simply the absence (or removal) of slots. Length 15–240 minutes, 15-minute aligned. A doctor's slots never overlap (exclusion constraint, below).

**Appointment** — `id`, `slot_id` (FK Slot, exactly one), `patient_user_id` (FK User), `service_id?` (what the visit is for; optional), `status` (enum), `cancelled_by?` (`Patient`|`Clinic`), `cancel_reason?`, `patient_note?`, `created_by_user_id` (patient, or staff/doctor for on-behalf bookings), `reminder_sent_at?`, `created_at`, `updated_at`, `row_version` (Postgres `xmin` concurrency token). Doctor, clinic, and times come from the slot (no copies, so they can never disagree).

**Auth support tables** — `OtpCode` (`phone`, `code_hash`, `expires_at`, `attempts`, `created_at`); `RefreshToken` (`user_id`, `token_hash`, `family_id`, `expires_at`, `revoked_at?`); `DeviceToken` (`id`, `user_id`, `fcm_token` unique, `platform` ios|android, `created_at`).

### Relations

```
Clinic 1─* Doctor 1─* Service
Clinic 1─* ClinicStaff            Doctor 1─* Slot *─1 Clinic
User 1─0..1 Doctor | ClinicStaff  Slot 1─0..1 active Appointment
Specialty 1─* Doctor              Appointment *─1 User(Patient), *─0..1 Service
User 1─* DeviceToken / RefreshToken
```

Indexes: `slots(doctor_id, starts_at)`, partial `slots(doctor_id, starts_at) WHERE status='Open'` (public listing), `slots(clinic_id, starts_at)`, unique partial `appointments(slot_id) WHERE status <> 'Cancelled'`, `appointments(patient_user_id)`, `doctors(clinic_id)`, `doctors(specialty_id) WHERE is_published`, and a partial index for the reminder query (`appointments(created_at) WHERE status='Confirmed' AND reminder_sent_at IS NULL`).

### Appointment status and transitions

`AppointmentStatus`: `Confirmed`, `Cancelled`, `Completed`, `NoShow`.
Bookings are confirmed immediately (no clinic approval step in v1 — Open Question #2). `Cancelled`, `Completed`, `NoShow` are terminal.

| From → To | Endpoint | Who | Guard | Slot effect |
|---|---|---|---|---|
| (none) → `Confirmed` | `POST /appointments` (patient) or `POST /dashboard/appointments` (doctor/staff) | Patient / Doctor / Staff | slot is `Open`; for patients also `starts_at ≥ now + MinLeadMinutes` (default 60) | slot `Open → Reserved` |
| `Confirmed` → `Confirmed` (moved to another slot) | `POST /appointments/{id}/reschedule`, `POST /dashboard/appointments/{id}/reschedule` | Patient / Doctor / Staff | target slot is `Open`, same doctor; patient also bound by the cancel window and lead time; resets `reminder_sent_at` | old `Reserved → Open`, new `Open → Reserved` |
| `Confirmed` → `Cancelled` | `POST /appointments/{id}/cancel` | Patient (`cancelled_by=Patient`) | `now ≤ starts_at − PatientCancelWindowHours` (default 2h) | slot `Reserved → Open` |
| `Confirmed` → `Cancelled` | `POST /dashboard/appointments/{id}/cancel` | Doctor / Staff (`cancelled_by=Clinic`) | `now < starts_at` (after start, use complete / no-show) | slot `Reserved → Open` |
| `Confirmed` → `Completed` | `POST /dashboard/appointments/{id}/complete` | Doctor / Staff | `now ≥ starts_at` | none (stays `Reserved`) |
| `Confirmed` → `NoShow` | `POST /dashboard/appointments/{id}/no-show` | Doctor / Staff | `now ≥ starts_at` | none (stays `Reserved`) |

Any other transition returns `409 INVALID_TRANSITION`. Transitions use an optimistic concurrency check (`row_version`) so two staff clicking at once yield one success and one `409`. Correcting a mistaken `Completed`/`NoShow` is not supported in v1 (Open Question #5).

### Slots: lifecycle and authoring

`SlotStatus`: `Open` (bookable), `Reserved` (held by one active appointment), `Blocked` (hidden from patients, e.g. doctor unavailable). Slots are created in the future only; past slots are read-only and never shown to patients. No cleanup job is needed in v1.

| Transition | Endpoint | Guard |
|---|---|---|
| (none) → `Open` | `POST /dashboard/doctors/{id}/slots` (single), `POST …/slots/bulk` (repeat helper) | `starts_at ≥ now`; no overlap with the doctor's existing slots (`SLOT_OVERLAP` for single; skipped and reported for bulk) |
| `Open` → `Reserved` | booking / reschedule-in (see table above) | conditional update, below |
| `Reserved` → `Open` | cancel / reschedule-out | in the same transaction as the appointment change |
| `Open` → `Blocked` | `POST …/slots/{slotId}/block` | only `Open`; a `Reserved` slot returns `SLOT_RESERVED` (cancel the appointment first) |
| `Blocked` → `Open` | `POST …/slots/{slotId}/unblock` | slot still in the future |
| `Open`/`Blocked` → deleted | `DELETE …/slots/{slotId}`, `DELETE …/slots?from&to` (range) | `Reserved` slots are never deleted (single: `SLOT_RESERVED`; range: counted in `skippedReserved`) |

**Bulk "repeat" helper** — input `{fromDate, toDate, daysOfWeek[], startTime, endTime, slotMinutes}` (clinic-local dates and wall-clock times; max 92 days and 2000 slots per call). For each date in range whose weekday is selected, step from `startTime` by `slotMinutes` while `slot end ≤ endTime`; convert each local start/end to UTC with the clinic's IANA timezone (`TimeZoneInfo`); skip slots in the past or overlapping an existing slot; insert all rows in one transaction; return `{created, skipped}`. It stores no schedule: it is a one-shot expansion into ordinary Slot rows, edited afterwards slot by slot. A break in the day is two runs of the helper.

### Timezone rules

- Store UTC (`timestamptz`) everywhere. Wall-clock local time exists only as bulk-helper input, converted once at creation. A local time that does not exist (DST gap) is skipped; an ambiguous one resolves to the first occurrence. Never hardcode `+02:00`: Egypt's DST rules come from tzdata (the runtime Docker image must include `tzdata`).
- Clinic timezone is `Clinic.timezone` (`Africa/Cairo` by default). Clients display times in the **clinic** timezone (from API fields `startsAtLocal` / `local` / `timezone`), not the device timezone.
- API instants are ISO 8601; `Z` or offsets are accepted on input and normalized to UTC. Date-only query params (`from`, `to`, `fromDate`, `toDate`) are clinic-local dates.

### Preventing double-booking

Authoritative guards are in the database, in three layers.

1. **Reservation by conditional update (primary).** Booking runs in one transaction:
   ```sql
   UPDATE slots SET status = 'Reserved'
   WHERE id = @slotId AND status = 'Open' AND starts_at >= now() + @lead;   -- lead = 0 for staff
   ```
   One row affected → insert the appointment in the same transaction. Zero rows → the slot is missing/not visible (`404`), Reserved or Blocked (`409 SLOT_TAKEN`), or past / inside the lead time (`409 SLOT_UNAVAILABLE`). The `UPDATE` takes a row lock, so concurrent bookers of the same slot serialize: the second sees `Reserved` and gets `SLOT_TAKEN`.
2. **Unique partial index (backstop).** `CREATE UNIQUE INDEX ux_appointments_active_slot ON appointments (slot_id) WHERE status <> 'Cancelled';` — even a bug in layer 1 cannot create two active appointments for one slot; a violation (`23505`) maps to `SLOT_TAKEN`.
3. **No overlapping slots.** Two different slots for the same doctor must not overlap in time, or one doctor could be double-booked through two slot rows:
   ```sql
   CREATE EXTENSION IF NOT EXISTS btree_gist;
   ALTER TABLE slots ADD CONSTRAINT no_overlapping_slots
     EXCLUDE USING gist (doctor_id WITH =, tstzrange(starts_at, ends_at) WITH &&);
   ```
   (`tstzrange` is half-open `[)`, so back-to-back slots are fine. Added by a raw SQL EF Core migration; violations map to `SLOT_OVERLAP`.)

Cancel and reschedule-out set the slot back to `Open` in the same transaction as the appointment change, so a freed slot is bookable immediately. Reschedule reserves the new slot with the same conditional update, frees the old one, and re-points `appointment.slot_id` atomically.

---

## 4. REST API

Base path `/api/v1`. JSON, `camelCase`. `Authorization: Bearer <accessToken>`. `Accept-Language: ar|en` selects the language of public read responses (they return a single localized `name`, `bio`, etc.); management endpoints return both `nameAr`/`nameEn`. IDs are UUIDs; doctor reads also accept the `slug`. OpenAPI/Swagger is exposed in Development.

Role legend: **Anon** = no token, **Any** = any authenticated user, **P** Patient, **D** Doctor, **S** ClinicStaff, **A** PlatformAdmin. For D/S "scope" means the D/S only reach their own doctor / clinic's doctors (Section 2).

### Auth & profile

| Method | Path | Role | Purpose |
|---|---|---|---|
| POST | `/auth/otp/request` | Anon | Send 6-digit OTP to `{phone}` |
| POST | `/auth/otp/verify` | Anon | `{phone, code}` → `{accessToken, refreshToken, expiresIn, isNewUser, user}`; creates a `Patient` user for an unknown phone |
| POST | `/auth/refresh` | Anon | `{refreshToken}` → new token pair (rotation) |
| POST | `/auth/logout` | Any | Revoke the refresh-token family |
| GET | `/me` | Any | Current user (+ `doctorId`, `clinicId` when applicable) |
| PUT | `/me` | Any | Update `fullName`, `preferredLanguage` |
| PUT | `/me/device-tokens` | P | Register FCM token `{token, platform}` → `{id}` (idempotent by token) |
| DELETE | `/me/device-tokens/{id}` | P | Unregister on logout |

### Public catalog

| Method | Path | Role | Purpose |
|---|---|---|---|
| GET | `/specialties` | Anon | List specialties |
| GET | `/cities` | Anon | Distinct cities of active clinics with published doctors |
| GET | `/doctors` | Anon | Search published doctors: `specialtyId`, `city`, `q` (name), `page`, `pageSize` |
| GET | `/doctors/{idOrSlug}` | Anon | Profile with clinic info and active services |
| GET | `/doctors/{idOrSlug}/slots` | Anon | `Open` slots: `from`, `to` (clinic-local dates, max 14 days) |

`GET /doctors/{id}/slots` response (only `Open` slots with `starts_at ≥ now + MinLeadMinutes` of a published doctor at an active clinic; otherwise `404`):

```json
{
  "doctorId": "…", "timezone": "Africa/Cairo",
  "days": [ { "date": "2026-11-02",
              "slots": [ { "id": "…", "startsAt": "2026-11-02T07:00:00Z", "endsAt": "2026-11-02T07:30:00Z", "local": "09:00" } ] } ]
}
```

### Patient appointments

| Method | Path | Role | Purpose |
|---|---|---|---|
| POST | `/appointments` | P | Book `{slotId, serviceId?, patientNote?}` → `201` Appointment, status `Confirmed`, slot becomes `Reserved` |
| GET | `/appointments` | P | Own appointments: `scope=upcoming\|past`, `page`, `pageSize` |
| GET | `/appointments/{id}` | P | Own appointment detail |
| POST | `/appointments/{id}/cancel` | P | `{reason?}` → `Cancelled` (window rule) |
| POST | `/appointments/{id}/reschedule` | P | `{slotId}` another `Open` slot of the same doctor (window rule) |

Appointment DTO: `id`, `status`, `slotId`, `startsAt`, `endsAt`, `startsAtLocal`, `timezone` (all from the slot), `doctor {id, slug, name, photoUrl}`, `clinic {id, name, address, phone, latitude?, longitude?}`, `service? {id, name, durationMinutes, priceEgp?}`, `patientNote?`, `cancelledBy?`, `cancelReason?`. Dashboard variants replace the `clinic` block with `patient {id, fullName, phone}` and keep `doctor {id, name}`.

### Dashboard — appointments (D scoped to self, S scoped to clinic)

| Method | Path | Role | Purpose |
|---|---|---|---|
| GET | `/dashboard/appointments` | D, S | List: `doctorId?`, `status?`, `from`, `to` (clinic-local dates of the slot, inclusive), `page`, `pageSize`; ordered by slot start |
| GET | `/dashboard/appointments/{id}` | D, S | Detail (includes patient name and phone) |
| POST | `/dashboard/appointments` | D, S | Book on behalf: `{slotId, serviceId?, patientPhone, patientName, patientNote?}`; finds or creates the `Patient` user by phone |
| POST | `/dashboard/appointments/{id}/reschedule` | D, S | `{slotId}` another `Open` slot of the same doctor |
| POST | `/dashboard/appointments/{id}/cancel` | D, S | `{reason?}` → `Cancelled` (`cancelled_by=Clinic`) |
| POST | `/dashboard/appointments/{id}/complete` | D, S | `Confirmed` → `Completed` |
| POST | `/dashboard/appointments/{id}/no-show` | D, S | `Confirmed` → `NoShow` |

### Dashboard — doctors, schedule, clinic

| Method | Path | Role | Purpose |
|---|---|---|---|
| GET | `/dashboard/doctors` | D, S | D: self only; S: all clinic doctors (includes unpublished) |
| GET | `/dashboard/doctors/{id}` | D, S | Full profile (both languages) |
| PUT | `/dashboard/doctors/{id}` | D, S | Update profile fields, `specialtyId`, `isPublished` |
| PUT | `/dashboard/doctors/{id}/photo` | D, S | Multipart image upload (≤ 2 MB, jpeg/png/webp) → `{photoUrl}` |
| GET | `/dashboard/doctors/{id}/services` | D, S | List services (incl. inactive) |
| POST | `/dashboard/doctors/{id}/services` | D, S | Create service |
| PUT | `/dashboard/doctors/{id}/services/{serviceId}` | D, S | Update service (incl. `isActive`) |
| DELETE | `/dashboard/doctors/{id}/services/{serviceId}` | D, S | Soft delete (`isActive=false`) |
| GET | `/dashboard/doctors/{id}/slots` | D, S | Slot calendar: `from`, `to` (clinic-local dates, max 31 days), `status?` → array of slots; `Reserved` ones embed `appointmentId`, patient name and phone |
| POST | `/dashboard/doctors/{id}/slots` | D, S | Create one slot `{startsAt, endsAt}` → `201`; `SLOT_OVERLAP`, or `VALIDATION_FAILED` if in the past / misaligned |
| POST | `/dashboard/doctors/{id}/slots/bulk` | D, S | Repeat helper `{fromDate, toDate, daysOfWeek[], startTime, endTime, slotMinutes}` → `{created, skipped}` (§3) |
| POST | `/dashboard/doctors/{id}/slots/{slotId}/block` | D, S | `Open` → `Blocked` (`SLOT_RESERVED` if reserved) |
| POST | `/dashboard/doctors/{id}/slots/{slotId}/unblock` | D, S | `Blocked` → `Open` |
| DELETE | `/dashboard/doctors/{id}/slots/{slotId}` | D, S | Remove an `Open`/`Blocked` slot (`SLOT_RESERVED` if reserved) |
| DELETE | `/dashboard/doctors/{id}/slots` | D, S | Remove all `Open`/`Blocked` slots in `from`..`to` (clinic-local dates) → `{deleted, skippedReserved}` |
| GET | `/dashboard/clinic` | D, S | Own clinic info (incl. `timezone`) |
| PUT | `/dashboard/clinic` | S | Update name, address, city, phone, coordinates |

Editing services never alters existing appointments. Slots that have started are read-only. A day off is made by removing or blocking that day's slots (reserved ones must have their appointments cancelled or rescheduled first).

### Platform admin (minimal)

| Method | Path | Role | Purpose |
|---|---|---|---|
| GET | `/admin/clinics` | A | List clinics, paged |
| POST | `/admin/clinics` | A | Create clinic |
| PATCH | `/admin/clinics/{id}/active` | A | `{isActive}` hide/show clinic everywhere |
| POST | `/admin/clinics/{id}/users` | A | Create `{phone, fullName, role: Doctor\|ClinicStaff}`; for `Doctor` also `{nameAr, nameEn, specialtyId}` (creates the Doctor row, unpublished) |
| PATCH | `/admin/users/{id}/active` | A | `{isActive}` deactivate/reactivate; deactivation revokes refresh tokens |

### Ops

| Method | Path | Role | Purpose |
|---|---|---|---|
| GET | `/health` | Anon | Liveness + DB check |

### Authentication

- **Phone OTP**: phone normalized to E.164 (`01012345678` → `+201012345678`); v1 accepts Egyptian mobile numbers only. OTP is 6 digits, hashed at rest, TTL 5 min, max 5 verify attempts, then the code is burned. Rate limits (built-in ASP.NET rate limiter): 1 request/min and 5/hour per phone, 20/hour per IP. An unknown phone creates a `Patient`. Doctor/staff phones must be pre-created by an admin; their first OTP login simply attaches to that user.
- **Dev/staging**: SMS is stubbed (code logged) and the fixed code `123456` is accepted. This is gated on the hosting environment, not only a config flag: the bypass is registered only when `IHostEnvironment` is not `Production`, and startup fails fast if the bypass setting is present in Production.
- **Phone already in use**: an unknown phone becomes a `Patient` on first OTP. If the admin later creates a Doctor/ClinicStaff account for a phone that already belongs to another user, or staff book on behalf of a phone that belongs to a Doctor/ClinicStaff user, the API returns `409 PHONE_IN_USE` (v1 does not convert roles; the admin uses a different number or deactivates the old user).
- **Booking limit**: a patient may hold at most `Booking__MaxUpcomingPerPatient` (default 3) upcoming `Confirmed` appointments, and at most one per doctor per clinic-local day; otherwise `409 BOOKING_LIMIT`. This stops one cheap OTP account from reserving a doctor's whole week. On-behalf bookings by staff are exempt.
- **JWT**: access token 15 min (HS256 for v1, signing key from secret store; claims `sub`, `role`, `clinicId?`, `doctorId?`). Refresh token: opaque random, 30 days, stored hashed, **rotated on every use**; reusing an already-rotated token revokes the whole family.
- **Client storage**: mobile — `flutter_secure_storage`; website — tokens live only in httpOnly cookies, so browser JavaScript can never read them; every authenticated website call (`/me`, `/appointments*`) therefore goes through a Next.js catch-all route handler (`app/api/[...path]`) that reads the cookie, attaches the `Bearer` header, refreshes on `401`, and forwards to the API (public catalog reads may call the API directly or render server-side); dashboard — access token in memory, refresh token in `localStorage` (accepted MVP trade-off; revisit with a cookie flow).
- CORS allows only the dashboard and website origins per environment.

### Error response shape

RFC 7807 `application/problem+json` with a stable machine `code`:

```json
{
  "type": "https://drbooking.example/errors/slot-taken",
  "title": "Slot no longer available",
  "status": 409,
  "code": "SLOT_TAKEN",
  "detail": "Someone just booked this time. Please pick another slot.",
  "traceId": "00-…",
  "errors": { "startsAt": ["…"] }
}
```

`errors` appears only for validation failures (field → messages). `detail` is localized by `Accept-Language`; clients key off `code`.

| Status | `code` values |
|---|---|
| 400 | `VALIDATION_FAILED`, `OTP_INVALID`, `OTP_EXPIRED` |
| 401 | `UNAUTHORIZED` (missing/expired token) |
| 403 | `FORBIDDEN` (wrong role) |
| 404 | `NOT_FOUND` |
| 409 | `SLOT_TAKEN` (slot is Reserved or Blocked), `SLOT_UNAVAILABLE` (slot in the past or inside the lead time), `SLOT_RESERVED` (block/remove a reserved slot), `SLOT_OVERLAP`, `INVALID_TRANSITION`, `CANCEL_WINDOW_PASSED`, `PHONE_IN_USE`, `BOOKING_LIMIT` |
| 429 | `RATE_LIMITED` |
| 500 | `INTERNAL_ERROR` (no internals leaked) |

### Pagination

Page-based: `?page=1&pageSize=20` (default 20, max 100, `page` starts at 1). Response envelope:

```json
{ "items": [ … ], "page": 1, "pageSize": 20, "totalCount": 134 }
```

Un-paged lists (`/specialties`, `/cities`, services, slot lists) return a plain JSON array (slot lists are bounded by their date range). Ordering is always explicit and stable (appointments by slot start, slots by start, doctors by name then `id`).

---

## 5. Screens per app

Every screen below lists the endpoints it uses; all exist in Section 4.

### Mobile (`dr-booking-mobile`, patient)

| Screen | Purpose | Endpoints |
|---|---|---|
| Language select (first launch) | Choose Arabic/English; local setting | – |
| Phone entry | Enter Egyptian mobile number | `POST /auth/otp/request` |
| OTP verify | Enter code, resend timer | `POST /auth/otp/verify` |
| Complete profile (new users) | Enter full name | `PUT /me` |
| Home / search | Specialty chips, search box, city filter | `GET /specialties`, `GET /cities`, `GET /doctors` |
| Doctor list | Paged results | `GET /doctors` |
| Doctor profile | Bio, clinic, services, fee info | `GET /doctors/{idOrSlug}` |
| Pick slot | Date strip + grid of open slots, shown in clinic timezone | `GET /doctors/{idOrSlug}/slots` |
| Confirm booking | Summary, optional visit type (from the doctor's services) and note, "pay at clinic"; prompts login if guest | `POST /appointments` |
| Booking success | Details, add-to-calendar; local state | – |
| My appointments | Upcoming / Past tabs | `GET /appointments?scope=` |
| Appointment details | Info, call/directions to clinic, cancel, reschedule | `GET /appointments/{id}`, `POST …/cancel` |
| Reschedule | Reuses slot picker | `GET /doctors/{id}/slots`, `POST …/reschedule` |
| Profile & settings | Name, language, logout, register/unregister FCM token | `GET/PUT /me`, `PUT/DELETE /me/device-tokens` |

Guests can use everything up to "Confirm booking". Doctor/staff accounts that log into the patient app get `403` on patient endpoints and see a "use the dashboard" message.

### Dashboard (`dr-booking-dashboard`, doctors / staff / admin)

| Screen | Purpose | Endpoints |
|---|---|---|
| Login | Phone + OTP; rejects Patient role | `POST /auth/otp/*`, `GET /me` |
| Today / agenda | Appointments for a day or week, filter by doctor and status, auto-refresh every 60s | `GET /dashboard/appointments`, `GET /dashboard/doctors` |
| Appointment drawer | Detail and actions: reschedule, cancel (reason), complete, no-show | `GET /dashboard/appointments/{id}`, `POST …/reschedule`, `…/cancel`, `…/complete`, `…/no-show` |
| New appointment | Phone-in patient: doctor, open slot, optional service, patient phone and name | `GET /dashboard/doctors`, `GET /dashboard/doctors/{id}/slots?status=Open`, `GET /dashboard/doctors/{id}/services`, `POST /dashboard/appointments` |
| Doctors list (staff) | Clinic doctors with published flag | `GET /dashboard/doctors` |
| Doctor profile editor | Bio, specialty, fee, photo, publish toggle | `GET/PUT /dashboard/doctors/{id}`, `PUT …/photo` |
| Services | CRUD with durations | `GET/POST/PUT/DELETE /dashboard/doctors/{id}/services` |
| Slot calendar | Day/week calendar per doctor showing Open / Reserved / Blocked slots; add a single slot, block/unblock, remove; clicking a Reserved slot opens its appointment drawer | `GET /dashboard/doctors/{id}/slots`, `POST …/slots`, `POST …/slots/{slotId}/block`, `POST …/slots/{slotId}/unblock`, `DELETE …/slots/{slotId}` |
| Bulk add / remove slots (dialog) | Repeat helper: date range, days of week, start/end, slot length, shows count before saving; also "remove open slots in range" (day off) | `POST …/slots/bulk`, `DELETE …/slots?from&to` |
| Clinic settings (staff) | Name, address, phone, coordinates; doctor sees read-only | `GET/PUT /dashboard/clinic` |
| Admin: clinics | List, create, activate/deactivate (A only) | `GET/POST /admin/clinics`, `PATCH …/active` |
| Admin: add user | Create doctor/staff account for a clinic; deactivate user | `POST /admin/clinics/{id}/users`, `PATCH /admin/users/{id}/active` |
| Account | Language, logout | `PUT /me`, `POST /auth/logout` |

### Website (`dr-booking-website`, public + web booking)

Routes are prefixed `/ar` and `/en` (hreflang alternates, `dir="rtl"` for Arabic). Public pages are server-rendered for SEO.

| Screen | Purpose | Endpoints |
|---|---|---|
| Landing | Value proposition, specialty tiles, search entry, app-store links | `GET /specialties` |
| Search results `/doctors` | Filters + paged list (SSR) | `GET /doctors`, `GET /specialties`, `GET /cities` |
| Doctor profile `/doctors/[slug]` | Bio, clinic, services, JSON-LD (`Physician`) | `GET /doctors/{slug}` |
| Booking `/doctors/[slug]/book` | Pick open slot, then OTP login if needed, then confirm | `GET /doctors/{slug}/slots`, `POST /auth/otp/*`, `PUT /me`, `POST /appointments` |
| Booking confirmation | Details | – (from `POST /appointments` response) |
| My appointments `/account/appointments` | Upcoming/past, cancel, reschedule | `GET /appointments`, `GET /appointments/{id}`, `POST …/cancel`, `POST …/reschedule`, `GET …/slots` |
| Static: About, Contact, Terms, Privacy | Content only | – |

No clinic-management features on the website; clinic users are pointed to the dashboard.

---

## 6. Notifications

| Event | Trigger | Recipient | Content |
|---|---|---|---|
| Confirmation | Booking created (patient or on-behalf) or rescheduled | Patient | Doctor, clinic, local date/time |
| Reminder | 24 hours before `starts_at` | Patient | Same + address; tap opens appointment |
| Cancellation | `Cancelled` with `cancelled_by = Clinic` | Patient | Reason if given, prompt to rebook |

Patient-initiated cancellation sends nothing to the patient (the UI already confirmed it); the clinic sees it on the next dashboard refresh.

**Channels.** Push via **FCM** to every `DeviceToken` of the patient (mobile only), localized to `preferred_language`; payload `data: {type, appointmentId}` and go_router deep-links to the appointment. Invalid/`UNREGISTERED` tokens are deleted on send. **SMS is optional (Open Question #3)**: website bookers and phone-in patients have no device token, so when SMS is enabled (`Sms:NotificationsEnabled`, default `false`) it is used when the patient has no device token. SMS delivery for OTP is mandatory and independent of this switch.

**Implementation (no queue, no event bus).**
- Confirmation/cancellation are sent inline after the DB commit through an `INotificationService`; any failure is logged and **never fails the request**.
- Reminders: a `BackgroundService` ticks every 5 minutes and claims work atomically: `UPDATE appointments a SET reminder_sent_at = now() FROM slots s WHERE s.id = a.slot_id AND a.status='Confirmed' AND a.reminder_sent_at IS NULL AND s.starts_at BETWEEN now() + interval '2 hours' AND now() + interval '24 hours' AND a.created_at < s.starts_at - interval '24 hours' RETURNING …`, then sends. The `created_at` clause skips reminders for bookings made inside the 24h window (the confirmation already covers them); claiming with `RETURNING` makes it safe even if two API instances run.
- A reschedule resets `reminder_sent_at` to `NULL`. (Known v1 quirk: the `created_at` check uses the original booking time, so a booking moved to a slot less than 24h away still gets a reminder on top of the reschedule confirmation. Harmless; not worth an extra column.)

---

## 7. Repo setup

Common to all repos: `main` is protected, work happens on short-lived branches via PR, squash-merge, CI must pass. `main` auto-deploys to **dev**; a version tag (`vX.Y.Z`) deploys to **prod**. No secrets in git: GitHub Actions environment secrets / the host's secret store.

### `dr-booking-backend`

```
moonlight-backend/
  src/Moonlight.Api/
    Program.cs
    Features/            # one folder per area: Auth, Me, Catalog, Appointments,
      Auth/              #   DashboardAppointments, Slots, Clinics, Admin, Notifications
        AuthController.cs  AuthService.cs  Dtos.cs
      Appointments/ …
    Domain/              # entities, enums, AppointmentStatus + SlotStatus transitions, SlotGenerator (pure: bulk-helper expansion)
    Infrastructure/      # MoonlightDbContext, Migrations/, Auth (JWT, OTP), Sms/, Fcm/, Storage/
    Common/              # ProblemDetails mapping, pagination, rate-limit setup
  tests/Moonlight.Tests/ # unit (SlotGenerator, transitions) + integration (Testcontainers Postgres)
  docker-compose.yml     # postgres for local dev
  Dockerfile             # multi-stage, runtime image includes tzdata
  .github/workflows/ci.yml
```

- Controllers → feature services → `DbContext`. No repository layer or MediatR; `SlotGenerator` is a pure function so it is unit-testable without a database.
- **Environments**: `appsettings.json` (defaults), `appsettings.Development.json`, `Production` via environment variables (`ConnectionStrings__Default`, `Jwt__Key`, `Fcm__ServiceAccountJson`, `Sms__*`, `Storage__*`, `Booking__PatientCancelWindowHours`, `Booking__MinLeadMinutes`). Dev: stubbed SMS + fixed OTP, local disk for photos. Prod: real SMS, object storage (S3-compatible) for photos. Migrations run on startup in dev; in prod via an explicit CI/CD step.
- **CI** (GitHub Actions, on PR and `main`): `dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build -warnaserror` → `dotnet test` (integration tests use a Postgres service container) → on `main`/tags `docker build` (and push image).

### `dr-booking-mobile`

Follows the house style: Flutter + Riverpod + go_router + Dio + fpdart, `lib/features/<feature>/{data,domain,presentation}`.

```
moonlight-mobile/
  lib/
    main.dart  main_dev.dart  main_prod.dart   # shared bootstrap + two entry points
    core/        # dio client + auth/refresh interceptor, router, env config, theme, l10n, secure storage, push setup
    shared/      # reusable widgets (slot grid, error view, doctor card, appointment card)
    features/
      auth/          # phone, otp, profile completion
      search/        # specialties, cities, doctor list
      doctor/        # profile
      booking/       # slot picker, confirm, success
      appointments/  # list, details, cancel, reschedule
      profile/       # settings, language, logout
  l10n/ (app_ar.arb, app_en.arb)   test/   .github/workflows/ci.yml
```

- **Environments**: `main_dev.dart` / `main_prod.dart` set base URL and Firebase options; separate Firebase projects and app ids (`com.moonlight.app.dev` / `com.moonlight.app`) so dev and prod installs coexist.
- RTL comes from the locale (`flutter_localizations` + ARB); use `EdgeInsetsDirectional`/`AlignmentDirectional`. Times are formatted from the API's `startsAtLocal`/`timezone`, not the device zone.
- Repo conventions from the house CLAUDE.md apply: dispose controllers, `autoDispose` for screen-scoped providers, `mounted` checks after awaits, `.builder` lists, cached network images.
- **CI**: `flutter pub get` → `dart format --set-exit-if-changed .` → `flutter analyze` → `flutter test` → `flutter build apk --debug -t lib/main_dev.dart` (PR); signed release builds (AAB, and iOS on a macOS runner) on version tags.

### `dr-booking-dashboard`

```
moonlight-dashboard/
  src/app/
    core/        # auth service, http interceptors (token, refresh, error→toast), guards (role), api clients, i18n/direction
    shared/      # table, drawer, date/time pickers (clinic timezone), status chip
    features/
      auth/  appointments/  new-appointment/  doctors/  slots/   # slot calendar + bulk-add dialog; services live under doctors/
      clinic/  admin/
  src/environments/ (environment.ts, environment.prod.ts)
  .github/workflows/ci.yml
```

- Angular current LTS at kickoff, standalone components + signals, Angular Material, `@ngx-translate/core` for ar/en with `Directionality` for RTL. Date handling with Luxon in the clinic timezone.
- **Environments**: `environment.ts` (dev API URL) and `environment.prod.ts` via Angular build configurations. Route guards check role from `/me`; admin routes only for `PlatformAdmin`.
- **CI**: `npm ci` → `ng lint` → `ng test --watch=false --browsers=ChromeHeadless` → `ng build --configuration production`.

### `dr-booking-website`

```
moonlight-website/
  src/
    app/[locale]/   # page.tsx (landing), doctors/, doctors/[slug]/, doctors/[slug]/book/, account/appointments/, about/ contact/ terms/ privacy/
    app/api/auth/   # route handlers: proxy /auth/*, set/clear httpOnly cookies
    app/api/[...path]/  # authenticated proxy: cookie → Bearer header, refresh on 401
    components/  lib/api/  i18n/
  messages/ (ar.json, en.json)
  .env.development  .env.production   # NEXT_PUBLIC_API_BASE_URL, NEXT_PUBLIC_SITE_URL
  .github/workflows/ci.yml
```

- Next.js App Router, TypeScript, Tailwind with logical properties (`ms-*`, `ps-*`) for RTL, `next-intl` for `/ar` and `/en`. Public pages are server components with `generateMetadata`, `sitemap.xml`, hreflang, and JSON-LD for doctors; booking and account pages are client-interactive.
- **CI**: `npm ci` → `eslint` → `tsc --noEmit` → `vitest` → `next build`.

**API types**: the backend publishes `openapi.json` as a CI artifact; dashboard and website may generate TypeScript types from it (`openapi-typescript`). Mobile DTOs are hand-written in `features/*/data`.

---

## 8. Build order / milestones

Backend first: every client depends on it. Dashboard comes before mobile/website because without clinics' schedules there is nothing to book.

**M0 — Foundations (≈ 1 week)**
Repos, branch protection, CI skeletons, dev environment (Postgres, backend deploy), mobile/dashboard/website hello-world builds.
*Acceptance*: CI green on all four repos; `GET /health` reachable on dev; each client app builds and shows an ar/en toggle with RTL flip.

**M1 — Backend: auth, catalog, schedule management**
OTP + JWT/refresh, `/me`, device tokens, admin endpoints, dashboard doctor/service/slot (single, bulk, block/unblock, remove)/clinic endpoints, public catalog (`/specialties`, `/cities`, `/doctors`, profile), seed data, error shape, pagination.
*Acceptance*: integration tests cover OTP happy path, rate limit, wrong/expired code, refresh rotation and reuse detection; role/scope tests prove a Doctor cannot touch another doctor and a Staff cannot touch another clinic (404); overlapping slot creation is rejected (`SLOT_OVERLAP`, including at the DB constraint); the bulk helper creates the expected rows, skips past/overlapping ones and reports them; a `Reserved` slot cannot be blocked or removed; Swagger documents every endpoint.

**M2 — Backend: slots, appointments, notifications**
Public open-slot listing, patient + dashboard appointment endpoints (slot reservation transaction), slot exclusion constraint + unique partial index migrations, status transitions, reminder `BackgroundService`, FCM sender (SMS behind the flag).
*Acceptance*: `SlotGenerator` unit tests: day-of-week filter, slot stepping and end-of-window cut-off, a range crossing an Egypt DST change, skipping past/overlapping slots, 2000-slot cap. Public listing returns only `Open` slots outside the lead time of published doctors. A concurrency test fires 20 parallel `POST /appointments` for one slot: exactly one `201`, nineteen `409 SLOT_TAKEN`, and exactly one active appointment row exists. Every row of both transition tables in §3 has a passing test and every illegal transition returns `INVALID_TRANSITION`. Cancel window enforced; cancelling or rescheduling sets the old slot back to `Open` and it is bookable immediately. Reminder job sends once per appointment, skips cancelled and in-window bookings, and resets after reschedule. Staff on-behalf booking creates or reuses the patient by phone, and returns `PHONE_IN_USE` for a doctor/staff phone. Booking limit enforced for patients (4th upcoming booking and a 2nd same-day booking with one doctor both return `BOOKING_LIMIT`). The fixed dev OTP is rejected when the app runs as Production.

**M3 — Dashboard**
Login, agenda, appointment drawer with all actions, new appointment, doctors/profile/services, slot calendar with bulk add/remove, clinic settings, admin screens.
*Acceptance*: with a fresh clinic created by the admin screens, a staff user can configure a doctor, add slots singly and through the bulk helper, publish the doctor, create an on-behalf booking, then reschedule, cancel, complete, and no-show other bookings; the slot calendar reflects Open/Reserved/Blocked changes immediately and a freed slot reappears as Open; slot times match the clinic timezone; Arabic RTL layouts verified on every screen; errors (`SLOT_TAKEN`, `SLOT_RESERVED`, `SLOT_OVERLAP`) shown with localized messages.

**M4 — Mobile**
Auth, search, doctor profile, booking, my appointments, cancel/reschedule, settings, FCM registration and deep links.
*Acceptance*: a new user goes from install to confirmed booking in ≤ 2 minutes on dev; the booking appears in the dashboard; cancelling from the dashboard sends a push that opens the appointment; guest can browse to the confirm step; language switch flips layout; token refresh is transparent (expire the access token and the next call succeeds); `flutter analyze` and `flutter test` clean; Android release build installs.

**M5 — Website**
Landing, search, doctor profile (SSR + SEO), booking with OTP, my appointments, static pages, ar/en routes.
*Acceptance*: doctor profile pages are server-rendered with metadata, hreflang, and valid JSON-LD; a visitor completes a booking with OTP login; booking made on the website shows in the dashboard and in the mobile app after login with the same phone; Lighthouse SEO ≥ 90 and mobile performance ≥ 80 on landing and profile.

**M6 — Pilot hardening and launch**
Prod environments and secrets, real SMS provider, object storage, backups, structured logging and error alerts, store submissions, onboarding 1–2 pilot clinics, privacy/terms text.
*Acceptance*: prod deploy from a tag; automated daily DB backup with a tested restore; OTP and reminder delivered on real Egyptian numbers; 2 pilot clinics fully configured; one week of pilot bookings with zero double-bookings and no critical defects.

---

## 9. Product decisions

Answered by the product owner on Nov 24, 2026. Questions 10 and 12 are still open in part.

1. **Market** — Egypt only in v1: EGP, `Africa/Cairo`, Arabic default with English, `+20` mobile numbers.
2. **Approval step** — Clinics may opt in to approving bookings first. Bookings are confirmed immediately by default. With the `manualReview` feature on (off by default), they start as `Pending` and become `Confirmed` when approved.
3. **Messaging** — WhatsApp for OTP, confirmations and reminders. No SMS in v1. Each clinic may later send from its own WhatsApp Business number (needs Meta business verification and approved templates).
4. **Rules and defaults** — Cancel or reschedule up to 2 h before, 60 min minimum booking lead, at most 3 upcoming bookings (one per doctor per day), reminder 24 h before. No booking ban for no-shows; the lost deposit is the penalty and the no-show count is shown to the clinic.
5. **Corrections** — Staff can fix a wrong `Completed` or `NoShow` within 24 hours. Only a clinic admin or the visit's doctor may do it. It is logged. A correction refunds nothing.
6. **Slot authoring** — Build both auto-fill templates and one-click day-off or vacation. Vacation comes first. Existing bookings on a vacation day are flagged, not deleted.
7. **Doctor model** — One doctor per clinic account, one patient at a time. A doctor in several clinics gets one account per clinic.
8. **Staff login** — WhatsApp OTP for doctors and staff, no passwords. The team onboards clinics manually for the pilot.
9. **Pricing display** — Public by default. Prices show only when the super-admin feature `publicPrices` is on for the clinic and the clinic's `ShowPrices` is on.
10. **Content and brand** — Decided so far: the app is named Dr booking. Firebase and other accounts stay under the owner's personal account for now and move to the company later. The owner opens the Play and App Store accounts. Copy, logo, colours and domains are still open.
11. **Privacy** — A consent text and retention policy are drafted for review by a legal specialist before any real clinic (Egypt Personal Data Protection Law No. 151 of 2020). The recommended hosting region is Frankfurt; the final choice is open (see 12).
12. **Hosting and budget** — Open. The owner deferred it until the end.
13. **Visit type** — Optional and informational in v1. Slot length is whatever the clinic created. Per-type durations come after the first clinics.

The super admin controls every feature per clinic, including booking approval, price visibility and the deposit amount. New features ship behind a flag.
