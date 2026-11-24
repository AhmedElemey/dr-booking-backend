using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Domain;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Tests;

public sealed class Resp(HttpStatusCode status, JsonElement body)
{
    public HttpStatusCode Status { get; } = status;

    public JsonElement Body { get; } = body;

    public int StatusCode => (int)Status;

    public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;

    public JsonElement this[string name] => Body.GetProperty(name);

    public Guid Id => Body.GetProperty("id").GetGuid();
}

// A client (anonymous or logged in) with JSON helpers.
public sealed class Session(HttpClient client, User? user = null, string? accessToken = null, string? refreshToken = null)
{
    public HttpClient Client { get; } = client;

    public User? User { get; } = user;

    public string? AccessToken { get; } = accessToken;

    public string? RefreshToken { get; } = refreshToken;

    public Task<Resp> GetAsync(string url, string? language = null) => SendAsync(HttpMethod.Get, url, null, language);

    public Task<Resp> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body ?? new { });

    public Task<Resp> PutAsync(string url, object? body) => SendAsync(HttpMethod.Put, url, body);

    public Task<Resp> PatchAsync(string url, object? body) => SendAsync(HttpMethod.Patch, url, body);

    public Task<Resp> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url, null);

    public async Task<Resp> SendAsync(HttpMethod method, string url, object? body, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }

        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new Resp(response.StatusCode, json);
    }
}

// Test data helpers working directly against the database of one factory.
public sealed class TestWorld(MoonlightApiFactory factory)
{
    private static int _counter = 10_000_000;

    public MoonlightApiFactory Factory { get; } = factory;

    public static string NewPhone() => "+2010" + Interlocked.Increment(ref _counter);

    public Session Anonymous() => new(Factory.CreateClient());

    public async Task<T> WithDbAsync<T>(Func<MoonlightDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<MoonlightDbContext>());
    }

    public async Task<Session> LoginAsync(string phone)
    {
        var client = Factory.CreateClient();
        var anonymous = new Session(client);
        var response = await anonymous.PostAsync("/api/v1/auth/otp/verify", new { phone, code = MoonlightApiFactory.FixedOtp });
        Assert.Equal(200, response.StatusCode);
        var token = response["accessToken"].GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var user = await WithDbAsync(db => db.Users.FindAsync(response["user"].GetProperty("id").GetGuid()).AsTask());
        return new Session(client, user, token, response["refreshToken"].GetString());
    }

    public async Task<Session> LoginAsync(User user) => await LoginAsync(user.Phone);

    public async Task<Session> NewPatientAsync() => await LoginAsync(NewPhone());

    public Task<Clinic> NewClinicAsync(string name = "Clinic", bool active = true, string city = "Cairo") =>
        WithDbAsync(async db =>
        {
            var clinic = new Clinic
            {
                NameAr = name + " ar",
                NameEn = name,
                AddressAr = "عنوان",
                AddressEn = "Address",
                City = city,
                Phone = "+20221234567",
                IsActive = active,
                CreatedAt = Factory.Clock.UtcNow,
            };
            db.Clinics.Add(clinic);
            await db.SaveChangesAsync();
            return clinic;
        });

    public Task<Doctor> NewDoctorAsync(Clinic clinic, string name = "Dr Test", bool published = true, Guid? specialtyId = null) =>
        WithDbAsync(async db =>
        {
            var user = new User { Phone = NewPhone(), FullName = name, Role = UserRole.Doctor, CreatedAt = Factory.Clock.UtcNow };
            var doctor = new Doctor
            {
                UserId = user.Id,
                ClinicId = clinic.Id,
                SpecialtyId = specialtyId ?? ReferenceData.GeneralDentistryId,
                Slug = Slug(name),
                NameEn = name,
                NameAr = name + " ar",
                IsPublished = published,
                CreatedAt = Factory.Clock.UtcNow,
            };
            db.Users.Add(user);
            db.Doctors.Add(doctor);
            await db.SaveChangesAsync();
            return doctor;
        });

    public Task<User> NewStaffAsync(Clinic clinic, bool isAdmin = false) =>
        WithDbAsync(async db =>
        {
            var user = new User { Phone = NewPhone(), FullName = "Staff", Role = UserRole.ClinicStaff, CreatedAt = Factory.Clock.UtcNow };
            db.Users.Add(user);
            db.ClinicStaff.Add(new ClinicStaff { UserId = user.Id, ClinicId = clinic.Id, IsAdmin = isAdmin });
            await db.SaveChangesAsync();
            return user;
        });

    public Task<User> NewAdminAsync() =>
        WithDbAsync(async db =>
        {
            var user = new User { Phone = NewPhone(), FullName = "Admin", Role = UserRole.PlatformAdmin, CreatedAt = Factory.Clock.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        });

    public Task<Slot> NewSlotAsync(Doctor doctor, TimeSpan fromNow, int minutes = 30, SlotStatus status = SlotStatus.Open) =>
        NewSlotAtAsync(doctor, Factory.Clock.SlotStart(fromNow), minutes, status);

    public Task<Slot> NewSlotAtAsync(Doctor doctor, DateTime startsAt, int minutes = 30, SlotStatus status = SlotStatus.Open) =>
        WithDbAsync(async db =>
        {
            var slot = new Slot
            {
                DoctorId = doctor.Id,
                ClinicId = doctor.ClinicId,
                StartsAt = startsAt,
                EndsAt = startsAt.AddMinutes(minutes),
                Status = status,
                CreatedAt = Factory.Clock.UtcNow,
            };
            db.Slots.Add(slot);
            await db.SaveChangesAsync();
            return slot;
        });

    public Task<Service> NewServiceAsync(Doctor doctor, bool active = true) =>
        WithDbAsync(async db =>
        {
            var service = new Service { DoctorId = doctor.Id, NameAr = "كشف", NameEn = "Check-up", DurationMinutes = 30, PriceEgp = 300, IsActive = active };
            db.Services.Add(service);
            await db.SaveChangesAsync();
            return service;
        });

    public Task<List<Appointment>> AppointmentsOfSlotAsync(Guid slotId) =>
        WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Appointments.Where(a => a.SlotId == slotId)));

    public Task<SlotStatus> SlotStatusAsync(Guid slotId) =>
        WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(db.Slots.Where(s => s.Id == slotId).Select(s => s.Status)));

    // Books through the patient API and returns the appointment id.
    public async Task<Guid> BookAsync(Session patient, Slot slot)
    {
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id });
        Assert.Equal(201, response.StatusCode);
        return response.Id;
    }

    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-') + "-" + Guid.NewGuid().ToString("N")[..6];
}
