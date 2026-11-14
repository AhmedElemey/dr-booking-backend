using Moonlight.Api.Domain;

namespace Moonlight.Api.Infrastructure;

// Reference data that ships with every environment.
public static class ReferenceData
{
    public static readonly Guid GeneralDentistryId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    public static readonly Guid OrthodonticsId = Guid.Parse("a1000000-0000-0000-0000-000000000002");
    public static readonly Guid PediatricId = Guid.Parse("a1000000-0000-0000-0000-000000000003");
    public static readonly Guid EndodonticsId = Guid.Parse("a1000000-0000-0000-0000-000000000004");
    public static readonly Guid CosmeticId = Guid.Parse("a1000000-0000-0000-0000-000000000005");
    public static readonly Guid OralSurgeryId = Guid.Parse("a1000000-0000-0000-0000-000000000006");

    public static readonly Specialty[] Specialties =
    [
        new() { Id = GeneralDentistryId, NameEn = "General Dentistry", NameAr = "طب الأسنان العام" },
        new() { Id = OrthodonticsId, NameEn = "Orthodontics", NameAr = "تقويم الأسنان" },
        new() { Id = PediatricId, NameEn = "Pediatric Dentistry", NameAr = "طب أسنان الأطفال" },
        new() { Id = EndodonticsId, NameEn = "Endodontics", NameAr = "علاج الجذور" },
        new() { Id = CosmeticId, NameEn = "Cosmetic Dentistry", NameAr = "تجميل الأسنان" },
        new() { Id = OralSurgeryId, NameEn = "Oral Surgery", NameAr = "جراحة الفم" },
    ];

    // The seeded city list a clinic's city must come from.
    public static readonly string[] Cities =
    [
        "Cairo",
        "Giza",
        "Alexandria",
        "Mansoura",
        "Tanta",
        "Port Said",
        "Suez",
        "Ismailia",
        "Luxor",
        "Aswan",
        "Hurghada",
        "Sharm El Sheikh",
    ];
}
