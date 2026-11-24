# Phase 2: the 13 "coming soon" features — shared contract

Status: contract for parallel build (2026-11-14). Backend is the source of truth; if a builder must deviate, change
this file in the backend repo (`docs/FEATURES-PHASE2.md`) and say so in the PR.

All repos: branch `claude/project-thread-btoarl`. Arabic first (RTL), English second. Every new UI string in both
languages. Every new screen/route/nav item/button is hidden when its feature switch is off (clients read
`GET /api/v1/features`, already implemented). Backend answers `403 FEATURE_DISABLED` for any endpoint of a switched-off
feature (`[RequireFeature(...)]`), and silently ignores feature-specific booking fields when the feature is off
(rejecting them with 403 is also acceptable for clearly feature-only fields — be consistent: **ignore** is the rule).

## 0. Feature keys (all repos)

| key | default in template clinic.config.json |
|---|---|
| videoConsultation | false |
| waitingList | false |
| familyAccounts | false |
| whatsappReminders | false |
| whatsappOtp | false |
| onlinePayments | false |
| insurance | false |
| holidayCalendar | false |
| workingHoursTemplates | false |
| patientRecords | false |
| reviews | false |
| promotions | false |
| recallCampaigns | false |
| reports | false |

Add them to `clinic.config.json`, `clinic.config.schema.json` (every app repo), backend `FeatureKeys.All`, and each
client's feature-key list/types. Console: flip `available` to true for all 13 in `FeatureCatalog.cs` and the web mock
catalog; console must keep accepting/serving them in runtime-config.

JSON conventions (unchanged from MVP): camelCase properties, enums as PascalCase strings (`"Confirmed"`), money as
decimal EGP (`priceEgp`), times UTC ISO plus `*Local` where useful, `Accept-Language` picks ar/en for `name`-style
fields, errors are problem+json with `code`. New error codes: `CLINIC_CLOSED` (409), `COUPON_INVALID` (422, with
`reason`), `ALREADY_REVIEWED` (409), `NOT_REVIEWABLE` (409), `PAYMENT_NOT_ALLOWED` (409), `VIDEO_NOT_OPEN` (409),
`ALREADY_WAITING` (409), `PROVIDER_ERROR` (502).

Dashboard endpoints follow the existing `api/v1/dashboard/...` scope rules (ClinicStaff sees their clinic, Doctor sees
self, PlatformAdmin sees all). Patient endpoints require a signed-in patient.

## 1. Holiday calendar — `holidayCalendar`
Entity `ClinicHoliday { id, clinicId, date (DateOnly), nameAr, nameEn, source: Manual|EgyptPublic }` unique (clinicId,date).
- `GET  /api/v1/clinics/{clinicId}/holidays?from=&to=` (anonymous, same guest rule as doctors) → `[{date, name}]`
- `GET  /api/v1/dashboard/holidays?clinicId=&year=` → `[{id, clinicId, date, nameAr, nameEn, source}]`
- `POST /api/v1/dashboard/holidays` `{clinicId, date, nameAr, nameEn}` → 201
- `DELETE /api/v1/dashboard/holidays/{id}` → 204
- `POST /api/v1/dashboard/holidays/import-egypt` `{clinicId, year}` → `{created, skipped}` — built-in table of Egyptian
  public holidays (fixed-date ones computed; Islamic/Coptic moveable ones from a bundled data table for 2026–2028).
Effects when ON: public `doctors/{id}/slots` omits slots on holiday dates (clinic local date) and returns
`closedDates: [{date, name}]` alongside `days`; booking/rescheduling into such a slot → 409 `CLINIC_CLOSED`; bulk slot
creation and template apply skip holiday dates (reason `"holiday"`). Existing appointments are untouched (dashboard
shows a warning list: `GET /api/v1/dashboard/holidays/{id}/conflicts` → appointments on that date).

## 2. Working hours templates — `workingHoursTemplates`
Entity `WorkingHoursTemplate { id, clinicId, name, slotMinutes, rules: [{dayOfWeek, start "HH:mm", end "HH:mm"}] }`.
- `GET/POST /api/v1/dashboard/working-hours-templates`, `PUT/DELETE /api/v1/dashboard/working-hours-templates/{id}`
- `POST /api/v1/dashboard/doctors/{id}/slots/apply-template` `{templateId, fromDate, toDate}` → `BulkSlotsResultDto`
  (same shape as `slots/bulk`; reuse the generator; overlaps/holidays reported as skipped).

## 3. Waiting list — `waitingList`
Entity `WaitingListEntry { id, patientUserId, familyMemberId?, doctorId, date (DateOnly, clinic local), serviceId?,
status: Waiting|Notified|Booked|Cancelled|Expired, notifiedAt?, createdAt }`.
- `POST /api/v1/waiting-list` `{doctorId, date, serviceId?, familyMemberId?}` → 201 entry; duplicate active → 409 `ALREADY_WAITING`
- `GET /api/v1/waiting-list` → my active + recent entries `{id, doctor: DoctorRefDto, date, status, notifiedAt}`
- `DELETE /api/v1/waiting-list/{id}` → 204 (sets Cancelled)
- `GET /api/v1/dashboard/waiting-list?doctorId=&date=&status=` → entries with patient ref
- `POST /api/v1/dashboard/waiting-list/{id}/notify` → 200 (manual notify)
Auto: when a slot of that doctor on that date becomes Open (cancel / unblock / new slot), notify Waiting entries for
that doctor+date in FIFO order (all of them, first-come-first-booked), mark Notified. When the patient books that
doctor on that date, entry → Booked. Nightly: past dates → Expired. Notification text: "A slot opened with Dr X on
<date>. Book now." via the normal notification channels (push, SMS, WhatsApp when on).

## 4. Family accounts — `familyAccounts`
Entity `FamilyMember { id, ownerUserId, fullName, relation: Child|Spouse|Parent|Sibling|Other, dateOfBirth?, gender?:
Male|Female, createdAt }`, max 10 per owner.
- `GET/POST /api/v1/me/family`, `PUT/DELETE /api/v1/me/family/{id}`
- Booking `POST /api/v1/appointments` accepts `familyMemberId` (must belong to caller). Appointment stores
  `familyMemberId`. `AppointmentDto` and `DashboardAppointmentDto` gain
  `familyMember: {id, fullName, relation} | null`. Dashboard shows "for <name> (<relation>) — booked by <owner>".
- Booking limit (`BOOKING_LIMIT`) counts per person (owner or member), not per account.

## 5. WhatsApp reminders — `whatsappReminders`
`IWhatsAppSender.SendAsync(phone, templateName, language, params[])`. Providers via `WhatsApp:Provider`:
`Mock` (default; logs + writes outbox) and `Meta` (WhatsApp Cloud API: `WhatsApp:PhoneNumberId`, `WhatsApp:AccessToken`,
template names in config). All outbound messages (sms/whatsapp/push) are logged in `OutboundMessage { id, channel:
Push|Sms|WhatsApp, to, userId?, kind, body, status: Sent|Failed, providerRef?, error?, createdAt }`.
- `User.whatsAppOptIn` (default true) — `GET/PUT /api/v1/me` expose `whatsAppOptIn` and `marketingOptIn` (default true).
- When ON, confirmations, reminders, clinic cancellations, waiting-list and recall messages also go to WhatsApp for
  opted-in users.
- `GET /api/v1/dashboard/messages?channel=&from=&to=&page=` → outbox log (staff/admin).

## 6. Online payments — `onlinePayments`
`IPaymentProvider { CreateCheckoutAsync(payment, returnUrl) → {checkoutUrl, providerRef}; ParseWebhookAsync(request)
→ {providerRef, succeeded, raw}; RefundAsync(payment) }`. `Payments:Provider`: `Mock` (default) | `Paymob`
(`Payments:Paymob:ApiKey`, `IntegrationId`, `IframeId`, `HmacSecret`). Mock checkout URL is a backend-served HTML page
`GET /api/v1/payments/mock-checkout/{paymentId}` with "Pay" and "Fail" buttons that post to the mock webhook and
redirect to `returnUrl?paymentId=..&status=..`.
Entity `Payment { id, appointmentId, amountEgp, status: Pending|Paid|Failed|Refunded|Cancelled, method: Online|Cash,
provider, providerRef?, createdAt, paidAt?, refundedAt? }`.
Appointment price: `priceEgp` = service price ?? doctor fee; `discountEgp` (promotions); `totalEgp` = max(0, price−discount).
`AppointmentDto` and `DashboardAppointmentDto` gain `priceEgp, discountEgp, totalEgp, paymentStatus: Unpaid|Pending|Paid|Refunded`
(always present; payment fields only meaningful when the feature is on).
- `POST /api/v1/appointments/{id}/pay` `{returnUrl}` → `{paymentId, checkoutUrl}` (Confirmed, future, unpaid, total>0
  else 409 `PAYMENT_NOT_ALLOWED`). `returnUrl` must match an allow-list (`Payments:ReturnUrlHosts`, plus the app scheme
  `<androidApplicationId>://`).
- `GET /api/v1/payments/{id}` → `{id, appointmentId, amountEgp, status}` (owner only)
- `POST /api/v1/payments/webhook/{provider}` (anonymous, signature-verified, idempotent)
- `GET /api/v1/dashboard/payments?from=&to=&status=&doctorId=` ; `POST /api/v1/dashboard/appointments/{id}/mark-paid`
  (cash) ; `POST /api/v1/dashboard/payments/{id}/refund`.
- Patient cancels a paid appointment → refund is requested automatically (status Refunded on provider success).
Booking stays instantly Confirmed (pay-online is optional in this phase).

## 7. Insurance — `insurance`
Entity `InsuranceProvider { id, nameAr, nameEn, isActive }`; doctors accept providers (many-to-many).
Appointment gains `insuranceProviderId?, insuranceMemberNumber?, insuranceStatus?: Pending|Approved|Rejected, insuranceNote?`.
- `GET /api/v1/insurance-providers` → `[{id, name}]` (active only, anonymous like doctors)
- Doctor summary/profile DTOs gain `insuranceProviders: [{id, name}]` (empty when feature off); `GET /api/v1/doctors`
  accepts `insuranceProviderId=` filter.
- Booking accepts `insurance: {providerId, memberNumber}` (provider must be accepted by the doctor → 422 otherwise).
- Dashboard: CRUD `/api/v1/dashboard/insurance-providers` (staff/admin), `PUT /api/v1/dashboard/doctors/{id}/insurance-providers`
  `{providerIds: []}`, `POST /api/v1/dashboard/appointments/{id}/insurance` `{status: Approved|Rejected, note}`.
  Appointment DTOs gain `insurance: {provider: {id,name}, memberNumber, status, note} | null`.

## 8. Patient records — `patientRecords`
Entities `PatientMedicalInfo { patientUserId, familyMemberId?, bloodType?, allergies?, chronicConditions?, medications?, updatedAt }`
and `PatientRecordEntry { id, patientUserId, familyMemberId?, appointmentId?, doctorId?, authorUserId, kind: Note|Diagnosis|Prescription|Attachment,
title, body?, attachmentUrl?, visibleToPatient (default false), createdAt, updatedAt }`.
Access: staff of a clinic and doctors see patients who have (had) an appointment at their clinic / with them; only the
author or staff can edit/delete an entry.
- `GET /api/v1/dashboard/patients?search=&page=` → `[{id, fullName, phone, lastVisitAt, appointmentsCount}]`
- `GET /api/v1/dashboard/patients/{userId}` → `{patient, familyMembers, medicalInfo, appointments[], records[]}`
  (`?familyMemberId=` scopes to a member)
- `PUT /api/v1/dashboard/patients/{userId}/medical-info` (`familyMemberId` in body optional)
- `POST /api/v1/dashboard/patients/{userId}/records`, `PUT/DELETE /api/v1/dashboard/records/{id}`,
  `PUT /api/v1/dashboard/records/{id}/attachment` (multipart, pdf/jpg/png ≤ 10 MB, uses the existing storage abstraction)
- Patient: `GET /api/v1/me/records?familyMemberId=` → entries with `visibleToPatient=true` + own medical info.

## 9. Reviews — `reviews`
Entity `Review { id, appointmentId (unique), doctorId, patientUserId, rating 1..5, comment? (≤1000), status: Published|Hidden,
reply?, repliedAt?, createdAt }`.
- `POST /api/v1/appointments/{id}/review` `{rating, comment}` — only Completed appointments of the caller, once
  (409 `ALREADY_REVIEWED` / `NOT_REVIEWABLE`). `AppointmentDto` gains `canReview: bool` and `review: {rating, comment} | null`.
- `GET /api/v1/doctors/{idOrSlug}/reviews?page=` → published, `{items: [{rating, comment, patientFirstName, createdAt, reply}], ...paging}`
- Doctor summary/profile DTOs gain `rating: {average, count} | null` (null when feature off or no reviews).
- Dashboard: `GET /api/v1/dashboard/reviews?doctorId=&status=&page=`, `POST /api/v1/dashboard/reviews/{id}/hide`,
  `/publish`, `PUT /api/v1/dashboard/reviews/{id}/reply` `{reply}`.
- After an appointment is marked Completed, send a "rate your visit" notification (push/WhatsApp) once.

## 10. Promotions / coupons — `promotions`
Entity `Coupon { id, clinicId?, code (unique, upper-case A-Z0-9-, 3..20), titleAr, titleEn, descriptionAr?, descriptionEn?,
discountType: Percent|Fixed, value, maxDiscountEgp?, validFrom, validTo, maxRedemptions?, perPatientLimit (default 1),
doctorId?, serviceId?, isPublic, isActive, redemptionsCount, createdAt }` and `CouponRedemption { couponId, appointmentId, patientUserId, discountEgp, createdAt }`.
- `GET /api/v1/promotions` → public active offers `[{code, title, description, discountType, value, validTo, doctorId?, serviceId?}]`
- `POST /api/v1/coupons/validate` `{code, doctorId, serviceId?}` → `{valid, discountEgp, totalEgp, reason?}` where
  reason ∈ `NotFound|Expired|NotStarted|Exhausted|PerPatientLimit|NotApplicable|NoPrice`
- Booking accepts `couponCode`; invalid → 422 `COUPON_INVALID` with `reason`. Cancelling releases the redemption.
- Dashboard CRUD `/api/v1/dashboard/coupons`, `GET /api/v1/dashboard/coupons/{id}/redemptions`.

## 11. Recall campaigns — `recallCampaigns`
Entity `RecallCampaign { id, clinicId, name, messageAr, messageEn, channel: Auto|Sms|WhatsApp|Push, monthsSinceLastVisit,
doctorId?, serviceId?, mode: OneOff|Recurring, isActive, lastRunAt?, createdAt }` and
`RecallSend { id, campaignId, patientUserId, channel, status: Sent|Failed|Skipped, sentAt }`.
Audience: patients (with `marketingOptIn`) whose last Completed appointment at the clinic (optionally with doctor/service)
is ≥ N months ago and who have no future Confirmed appointment; never the same patient twice for the same campaign
within 30 days. Recurring campaigns run daily (background service, 10:00 clinic time); OneOff runs on "send now".
Message placeholders `{name}`, `{doctor}`, `{clinic}`, `{link}`.
- Dashboard CRUD `/api/v1/dashboard/recall-campaigns`, `POST .../{id}/preview` → `{audienceCount, sample: [{name, phone, lastVisitAt}]}`,
  `POST .../{id}/send-now` → `{sent, failed, skipped}`, `GET .../{id}/sends?page=`.

## 12. Reports — `reports`
- `GET /api/v1/dashboard/reports/bookings?from=&to=&doctorId=&groupBy=Day|Week|Month` →
  `{totals: {booked, completed, cancelled, noShow, cancellationRate}, series: [{period, booked, completed, cancelled, noShow}],
    byDoctor: [{doctorId, name, booked, completed}], byService: [...], bySource: {patient, staff}}`
- `GET /api/v1/dashboard/reports/revenue?from=&to=&doctorId=&groupBy=` →
  `{totals: {expectedEgp, collectedEgp, onlineEgp, cashEgp, discountsEgp, refundsEgp}, series: [{period, expectedEgp, collectedEgp}], byDoctor: [...]}`
  expected = totals of Completed appointments; collected = Paid payments (online + cash).
- Both accept `format=csv` (UTF-8 BOM so Excel shows Arabic). Range max 366 days. Clinic local dates.

## 13. Video consultation — `videoConsultation`
Doctor gains `offersVideo` (dashboard doctor edit; public DTOs expose `offersVideo`). Appointment gains `mode: InPerson|Video`
(default InPerson; booking accepts `mode`; Video only if doctor offers it and feature on, else 422).
`IVideoProvider.CreateRoomAsync(appointment) → {roomName, patientUrl, hostUrl}`. `Video:Provider`: `Jitsi` (default;
`Video:Jitsi:BaseUrl` default `https://meet.jit.si`, unguessable room name) | `Mock`. Room created lazily on first join.
- `GET /api/v1/appointments/{id}/video` → `{joinUrl, opensAt, closesAt}`; window = start−15 min … end+30 min, else 409 `VIDEO_NOT_OPEN`.
- `GET /api/v1/dashboard/appointments/{id}/video` → `{joinUrl (host), opensAt, closesAt}`.
- Appointment DTOs gain `mode`. Confirmation/reminder messages for video appointments say "video consultation".

## Client scope
- **Dashboard (Angular, staff/doctors):** Holidays page; Working-hours templates page + "Apply template" in slot
  calendar; Waiting list page; family member + insurance + payment + mode + review shown in appointment drawer (with
  mark-paid, refund, approve/reject insurance, join video); Payments page; Insurance providers page + per-doctor
  accepted providers on doctor edit; `offersVideo` on doctor edit; Patients page + patient record page (medical info,
  timeline, entries, attachments); Reviews moderation page; Coupons page; Recall campaigns page; Reports page (charts +
  CSV); Messages log page. Mock backend must cover all new endpoints for demo/screenshots.
- **Mobile (Flutter) and Website (Next.js), patients:** Family members screen + "booking for" picker; waiting list
  join (when a day has no slots or is full) + "My waiting list"; pay online after booking and from appointment details
  (mobile opens checkout in browser / in-app browser and handles return deep link; website redirects back to
  `/[locale]/appointments/{id}?payment=...`); insurance picker at confirm + doctor filter; ratings on doctor list/profile,
  reviews list, "rate your visit" on completed appointments; offers on home + coupon field at confirm with live
  validation; in-person/video choice when doctor offers video + "Join video" button in the window; "My records" screen;
  closed holiday dates shown in the slot picker; WhatsApp + marketing opt-in toggles in settings/profile. Demo/mock
  modes must cover them.
- **Console:** 13 features available; descriptions mention provider setup where needed (payments, WhatsApp, video);
  tests updated.

---

## Deviations and notes (backend implementation, 2026-11-15)

The sections above are unchanged. The backend implements them as written; the points below clarify what the contract left open or where the implementation differs. Clients should read this list.

**Added**
- `GET /api/v1/payments/return` (anonymous): the Paymob *response callback*. Redirects to the `returnUrl` given to `pay` and appends `paymentId` and `status` (`paid`, `pending`, `failed`). Mock checkout redirects the same way.
- `POST /api/v1/payments/webhook/{provider}` answers `{ "handled": bool }`; 401 `UNAUTHORIZED` for a bad or missing Paymob `hmac` query parameter; 404 when `{provider}` is not the configured provider. Mock webhook body: `{ "providerRef": "<paymentId>", "success": true|false }`.
- `returnUrl` allow-list: `Payments:ReturnUrlHosts`, the clinic website domain from `clinic.config.json`, and the app schemes (`mobile.androidApplicationId`, `mobile.iosBundleId`). A rejected URL is 400 `VALIDATION_FAILED`.
- `GET /api/v1/dashboard/holidays/{id}/conflicts` returns dashboard appointment DTOs on that clinic-local date (Confirmed only).
- `DashboardBookAppointmentDto` (staff booking) also accepts `mode` and `insurance`; coupons and `familyMemberId` are patient-booking only.
- User DTO (`/auth` responses and `/me`) exposes `whatsAppOptIn` and `marketingOptIn`; `PUT /me` accepts both.

**Clarified status codes and shapes**
- Duplicate holiday date: 400 `VALIDATION_FAILED` (field `date`), not 409.
- 11th family member, insurance provider not accepted by the doctor, `mode: Video` for a doctor that does not offer it: 422 `VALIDATION_FAILED` with field errors. Every 422 is a problem+json with `code`.
- `GET /api/v1/dashboard/patients` returns a plain array (as in the contract; `?page=&pageSize=` are accepted and the total is in the `X-Total-Count` header).
- `GET /api/v1/dashboard/reviews`, `.../messages`, `.../recall-campaigns/{id}/sends` and `GET /api/v1/doctors/{id}/reviews` return `{ items, page, pageSize, totalCount }` (corrected: the field is `totalCount`, as the clients read it).
- `GET /api/v1/dashboard/waiting-list` entries: `{ id, doctor, patient, familyMember, date, status, serviceId, notifiedAt, createdAt }`; `POST /api/v1/waiting-list` returns `{ id, doctor, date, serviceId, familyMemberId, status, notifiedAt, createdAt }`.
- `paymentStatus` on appointments: `Unpaid` when there is no payment or the last attempt Failed, `Pending` while a checkout is open, `Paid`, `Refunded`. `priceEgp` is a snapshot taken at booking (service price, else doctor fee, else 0); `totalEgp = max(0, priceEgp - discountEgp)`.
- `POST /appointments/{id}/pay` is allowed for Confirmed, future appointments with total > 0 and no Paid payment; otherwise 409 `PAYMENT_NOT_ALLOWED`. A call while a checkout is Pending is **not** a 409: the open payment is cancelled (status `Cancelled`) and replaced by a new one, so an abandoned checkout can always be retried. A new attempt after a Failed one creates a new payment. Concurrent calls are serialized: an appointment never has more than one Pending payment.
- Coupon validation: `valid:false` always carries `reason`; `discountEgp` is capped at the price and at `maxDiscountEgp`. The booking redeems atomically in the booking transaction (`maxRedemptions` cannot be oversold) and cancelling by anyone releases it. `perPatientLimit` counts per account owner, not per family member.
- Coupon `code` is globally unique, upper-cased on input, 3-20 of `A-Z0-9-`.
- `GET /api/v1/doctors/{id}/slots` returns `closedDates: []` when `holidayCalendar` is off (the field is always present); the public doctor DTOs always carry `offersVideo`, `insuranceProviders` and `rating` (empty/false/null while the feature is off).
- Reviews: `patientFirstName` is the first word of the patient name (null when unnamed). `canReview` is true for the caller's own Completed, not yet reviewed appointment.
- Review prompt: sent once, `Jobs:ReviewPromptDelayMinutes` (default 30) after the appointment is marked Completed; the claim is an atomic update of `reviewPromptSentAt`.
- Waiting list: all `Waiting` entries of that doctor and clinic-local date are notified in FIFO order when a slot opens (cancel, unblock, new slot, bulk or template creation, reschedule away); notified entries are not notified again. Past dates expire on the background tick, not only nightly.
- Recall: audience excludes patients with `marketingOptIn = false`, a future Confirmed appointment, a recall send for the same campaign in the last 30 days, and a last visit (Completed, at that clinic, matching doctor/service when set) newer than `monthsSinceLastVisit` months. Channel `Auto` prefers WhatsApp (when on and opted in), then push, then SMS (when SMS notifications are on); otherwise the send is recorded `Skipped`. Recurring campaigns are claimed atomically once per clinic-local day after `Jobs:RecallHour`.
- Records: `PUT /dashboard/records/{id}/attachment` is multipart (`file`), pdf/jpg/png up to 10 MB, content sniffed. See the audit-fix appendix below: attachments are no longer public static files. Only the author, clinic staff and platform admin may edit or delete an entry. A doctor sees patients who have an appointment with them; staff see patients of their clinic.
- Reports: rows are bucketed by the appointment's clinic-local slot date; `collectedEgp` is every payment with `paidAt` in the range, whatever its status now (Paid or Refunded); `refundsEgp` is every payment with `refundedAt` in the range; net = collected - refunds. `cancellationRate` is cancelled / booked rounded to 4 decimals. CSV: `period,booked,completed,cancelled,noShow` (bookings) and `period,expectedEgp,collectedEgp` (revenue), each followed by a `total` row.
- Video: the join endpoint answers 409 `VIDEO_NOT_OPEN` outside the window (15 min before to 30 min after the visit), also for an already created room. The room is created on the first successful join and stored on the appointment; the patient and host URLs share the room. `GET .../video` on an in-person appointment is 404.
- WhatsApp: template kinds `confirmed`, `cancelled`, `reminder`, `waitlist`, `review`, `recall` map to `WhatsApp:Templates:<kind>`; body parameters are the patient name, the doctor name and the date/time (kind specific). WhatsApp is sent in addition to (not instead of) push/SMS for confirmations, reminders, clinic cancellations, waiting-list and review prompts; recall uses a single channel per patient.
- Egyptian holiday data: fixed-date holidays are computed for any year; moveable ones are a bundled table for 2026-2028 and are **estimates**. For other years only the fixed ones are imported.
- Working-hours template `rules[].dayOfWeek` is the .NET name (`"Monday"` ... `"Sunday"`); `start`/`end` are `HH:mm` on 15-minute boundaries, `slotMinutes` a multiple of 15.


---

## Appendix: audit fixes (backend, 2026-11-15)

Changes after the Phase 2 audit. Response shapes are unchanged except where listed under "Shape changes".

**Shape and behaviour changes clients must know about**
- **Record attachments are private.** `PUT /api/v1/dashboard/records/{id}/attachment` still answers `{ attachmentUrl }`, but the value is now the authorized download path, not a `/uploads/...` file: `/api/v1/dashboard/records/{id}/attachment`. It is relative unless `Storage:PublicBaseUrl` is configured (the Host header is never used). Patients get `attachmentUrl` = `/api/v1/me/records/{id}/attachment` in `GET /me/records` entries. Both URLs are `GET` endpoints that need the bearer token (fetch with the Authorization header and render the blob; the browser cannot open them with a plain link). Dashboard: clinic staff, or a doctor who has the patient in scope (404 otherwise). Patient: only when the entry is `visibleToPatient` (404 otherwise). Responses are `application/pdf`, `image/jpeg` or `image/png` with `Cache-Control: private, no-store`. Replacing an attachment or deleting the entry deletes the file. Files live under `Storage:RecordsPath` (default `private-records`, outside the public uploads root).
- **`DELETE /me/family/{id}` is a soft delete.** Still 204/404. The member disappears from `GET /me/family` and from booking and the waiting list (booking for a deleted member is a validation error), their active waiting-list entries are cancelled, and a deleted member no longer counts towards the limit of 10. The clinic's records, medical info and the appointments keep pointing at the member (appointment DTOs still show `familyMember`).
- **`GET /api/v1/waiting-list` entries gain `familyMember: { id, fullName, relation } | null`.**
- **Mock providers outside Development.** Payments with `Payments:Provider=Mock` and WhatsApp with `WhatsApp:Provider=Mock` only work in the Development environment or when `Payments:AllowMock=true` / `WhatsApp:AllowMock=true` (staging demos). Otherwise the API still starts: `POST /appointments/{id}/pay` answers 409 `PAYMENT_NOT_ALLOWED` with detail "Online payments are not configured.", `GET /payments/mock-checkout/{id}` and `POST /payments/webhook/mock` answer 404, and WhatsApp sends are recorded in the outbox as `Failed` with error "not configured". Outside Development `Payments__PublicBaseUrl` is required for checkout links (the Mock provider only falls back to the request host in Development).
- **Payments.** New payment status `Refunding` (a refund claimed and in flight; appointments report it as `Paid`). A refund claims the payment first (`Paid` to `Refunding`), so concurrent refunds reach the provider once; a second request answers 409 `INVALID_TRANSITION`, and a refused refund puts the payment back to `Paid`. A successful webhook for a payment that was cancelled or replaced is recorded: it becomes `Paid` when the appointment is still valid, otherwise (appointment cancelled or already paid another way) it is marked paid and refunded automatically. A signed success whose amount or currency differs from the payment is logged and not applied. Paymob `pending: true` callbacks change nothing; non-TRANSACTION (TOKEN) callbacks are acknowledged without a signature check.
- **Return URLs.** The website domain from `clinic.config.json` and its `www.` variant are both allowed.
- **Templates and reviews.** Doctors may `GET /dashboard/working-hours-templates` and apply a template; creating, updating and deleting templates is clinic staff only. Doctors may reply to reviews; hide and publish are clinic staff only (403 for doctors).
- **Recall send-now** is claimed atomically: a second call within 5 minutes of a run answers 409 `INVALID_TRANSITION`.
- **CORS** exposes `X-Total-Count`.
- **`clinic.config.json`:** `mobile.androidApplicationId` must match `^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$` (no underscores, because it is also the payment-return URI scheme); schema and startup validation both enforce it.
- **Video:** the public `meet.jit.si` server is for demos; production needs a self-hosted Jitsi or JaaS (`Video__Jitsi__BaseUrl`). Room names are unguessable and the join endpoint stops returning a link after the window closes. Per-join JWTs with moderator rights are not built yet.

**Internal fixes without shape changes:** one live checkout per appointment (appointment lock plus partial unique index `ux_payments_live_checkout`); per-patient coupon limit re-checked under the patient lock; cancellation releases the coupon in the same transaction and its refund and waiting-list side effects are independent; outbox writes use their own database context; maintenance jobs (waiting-list expiry, review prompts, recall) run in their own hosted service (`Jobs:Enabled`, default true) independent of `Reminders:Enabled`; "today" for public offers, holidays and the waiting list is the clinic-local date; the waiting-list duplicate guard already treats NULL family members as equal (`NULLS NOT DISTINCT`), now covered by a concurrency test. Migration `Phase2AuditFixes`.
