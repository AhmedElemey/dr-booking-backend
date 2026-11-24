using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Dashboard;

public sealed class ClinicManagementService(MoonlightDbContext db, DashboardScope scope)
{
    public async Task<ClinicDto> GetAsync(CancellationToken ct) => ToDto(await scope.RequireClinicAsync(ct));

    public async Task<ClinicDto> UpdateAsync(UpdateClinicDto request, CancellationToken ct)
    {
        var city = ReferenceData.Cities.FirstOrDefault(c => c.Equals(request.City?.Trim(), StringComparison.OrdinalIgnoreCase));
        new ValidationErrors()
            .RequireText(request.NameAr, "nameAr", 200)
            .RequireText(request.NameEn, "nameEn", 200)
            .RequireText(request.AddressAr, "addressAr", 500)
            .RequireText(request.AddressEn, "addressEn", 500)
            .RequireText(request.Phone, "phone", 30)
            .Require(city is not null, "city", "city must be one of: " + string.Join(", ", ReferenceData.Cities))
            .Require(request.Latitude is null or (>= -90 and <= 90), "latitude", "latitude must be between -90 and 90.")
            .Require(request.Longitude is null or (>= -180 and <= 180), "longitude", "longitude must be between -180 and 180.")
            .Require((request.Latitude is null) == (request.Longitude is null), "latitude", "latitude and longitude must be set together.")
            .ThrowIfAny();

        var clinic = await scope.RequireClinicAsync(ct);
        clinic.NameAr = request.NameAr!.Trim();
        clinic.NameEn = request.NameEn!.Trim();
        clinic.AddressAr = request.AddressAr!.Trim();
        clinic.AddressEn = request.AddressEn!.Trim();
        clinic.City = city!;
        clinic.Phone = request.Phone!.Trim();
        clinic.Latitude = request.Latitude;
        clinic.Longitude = request.Longitude;
        if (request.ShowPrices is not null)
        {
            clinic.ShowPrices = request.ShowPrices.Value;
        }
        await db.SaveChangesAsync(ct);
        return ToDto(clinic);
    }

    public static ClinicDto ToDto(Domain.Clinic c) => new(
        c.Id, c.NameAr, c.NameEn, c.AddressAr, c.AddressEn, c.City, c.Latitude, c.Longitude, c.Phone, c.Timezone, c.IsActive, c.ShowPrices);
}
