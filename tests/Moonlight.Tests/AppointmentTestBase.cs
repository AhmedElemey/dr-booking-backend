using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;

namespace Moonlight.Tests;

// One clinic with one published doctor (plus a service), a staff user and a doctor login, per test.
public abstract class AppointmentTestBase(PostgresFixture db, Dictionary<string, string?>? settings = null) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _shared = db;
    private PostgresFixture? _own;
    protected static readonly TimeZoneInfo Cairo = ClinicTime.FindZone("Africa/Cairo");

    protected MoonlightApiFactory Factory { get; private set; } = null!;

    protected string ConnectionString { get; private set; } = "";

    // Tests that touch global state (the reminder job claims rows of every patient) get a database of their own per test.
    protected virtual bool IsolatedDatabase => false;

    // Lets a test swap services (e.g. an HTTP handler) before the host starts.
    protected virtual Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? ConfigureServices => null;

    protected TestWorld World { get; private set; } = null!;

    protected Clinic Clinic { get; private set; } = null!;

    protected Doctor Doctor { get; private set; } = null!;

    protected Service Service { get; private set; } = null!;

    protected Session Staff { get; private set; } = null!;

    protected Session ClinicAdmin { get; private set; } = null!;

    protected Session DoctorSession { get; private set; } = null!;

    protected DateTime Now => Factory.Clock.UtcNow;

    // Tests that depend on the time of day override this to run on a fixed clock.
    protected virtual DateTimeOffset? PinnedNow => null;

    public virtual async Task InitializeAsync()
    {
        if (IsolatedDatabase)
        {
            _own = new PostgresFixture();
            await _own.InitializeAsync();
        }

        ConnectionString = (_own ?? _shared).ConnectionString;
        Factory = new MoonlightApiFactory(ConnectionString, settings: settings, configureServices: ConfigureServices);
        if (PinnedNow is { } pinned)
        {
            Factory.Clock.Set(pinned);
        }

        World = new TestWorld(Factory);
        Clinic = await World.NewClinicAsync();
        Doctor = await World.NewDoctorAsync(Clinic);
        Service = await World.NewServiceAsync(Doctor);
        Staff = await World.LoginAsync(await World.NewStaffAsync(Clinic));
        ClinicAdmin = await World.LoginAsync(await World.NewStaffAsync(Clinic, isAdmin: true));
        DoctorSession = await World.LoginAsync((await World.WithDbAsync(d => d.Users.FindAsync(Doctor.UserId).AsTask()))!);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_own is not null)
        {
            await _own.DisposeAsync();
        }
    }

    protected Task<Slot> SlotAsync(TimeSpan fromNow, Doctor? doctor = null, int minutes = 30) =>
        World.NewSlotAsync(doctor ?? Doctor, fromNow, minutes);

    // A slot at a clinic-local wall-clock time on a given day offset from today (so "same day" is unambiguous).
    protected Task<Slot> LocalSlotAsync(int dayOffset, int hour, Doctor? doctor = null)
    {
        var day = ClinicTime.LocalDate(Now, Cairo).AddDays(dayOffset);
        Assert.True(ClinicTime.TryLocalToUtc(day.ToDateTime(new TimeOnly(hour, 0)), Cairo, out var start));
        return World.NewSlotAtAsync(doctor ?? Doctor, start);
    }

    protected async Task<Appointment> AppointmentAsync(Guid id) =>
        await World.WithDbAsync(d => d.Appointments.AsNoTracking().FirstAsync(a => a.Id == id));

    protected static string Patient(Guid id) => $"/api/v1/appointments/{id}";

    protected static string Dash(Guid id) => $"/api/v1/dashboard/appointments/{id}";

    protected async Task<Guid> RegisterDeviceAsync(Session patient, string? token = null)
    {
        var response = await patient.PutAsync("/api/v1/me/device-tokens", new { token = token ?? "token-" + Guid.NewGuid(), platform = "android" });
        Assert.Equal(200, response.StatusCode);
        return response["id"].GetGuid();
    }
}
