using Moonlight.Api.Domain;

namespace Moonlight.Api.Features.Holidays;

public sealed record HolidayTemplate(DateOnly Date, string NameAr, string NameEn);

// Egyptian public holidays. Fixed-date holidays are computed for any year. The moveable ones (Coptic Easter based and
// Islamic calendar based) come from the bundled table below for 2026-2028 only.
//
// THE MOVEABLE DATES ARE ESTIMATES: Islamic dates depend on moon sighting (the Egyptian Dar al-Ifta announces them a
// day or two ahead) and the cabinet may move a holiday to the nearest Thursday. Staff can delete or add single days
// by hand after importing.
public static class EgyptHolidays
{
    public const int FirstTableYear = 2026;
    public const int LastTableYear = 2028;

    private static readonly (int Month, int Day, string Ar, string En)[] Fixed =
    [
        (1, 7, "عيد الميلاد المجيد (الأقباط الأرثوذكس)", "Coptic Christmas"),
        (1, 25, "عيد الثورة وعيد الشرطة", "January 25 Revolution and Police Day"),
        (4, 25, "عيد تحرير سيناء", "Sinai Liberation Day"),
        (5, 1, "عيد العمال", "Labour Day"),
        (6, 30, "ذكرى ثورة 30 يونيو", "June 30 Revolution Anniversary"),
        (7, 23, "ذكرى ثورة 23 يوليو", "July 23 Revolution Anniversary"),
        (10, 6, "عيد القوات المسلحة", "Armed Forces Day"),
    ];

    // Estimated first day of each moveable holiday per year (see the note above).
    private static readonly Dictionary<int, YearTable> Moveable = new()
    {
        [2026] = new(new(2026, 4, 13), new(2026, 3, 20), new(2026, 5, 26), new(2026, 6, 16), new(2026, 8, 25)),
        [2027] = new(new(2027, 5, 3), new(2027, 3, 9), new(2027, 5, 15), new(2027, 6, 6), new(2027, 8, 14)),
        [2028] = new(new(2028, 4, 17), new(2028, 2, 26), new(2028, 5, 4), new(2028, 5, 26), new(2028, 8, 3)),
    };

    public static bool HasMoveableData(int year) => Moveable.ContainsKey(year);

    public static IReadOnlyList<HolidayTemplate> ForYear(int year)
    {
        var list = new List<HolidayTemplate>();
        foreach (var (month, day, ar, en) in Fixed)
        {
            list.Add(new HolidayTemplate(new DateOnly(year, month, day), ar, en));
        }

        if (Moveable.TryGetValue(year, out var table))
        {
            list.Add(new HolidayTemplate(table.ShamElNessim, "شم النسيم", "Sham El-Nessim"));
            AddRun(list, table.EidAlFitr, 3, "عيد الفطر", "Eid al-Fitr");
            list.Add(new HolidayTemplate(table.Arafat, "وقفة عرفات", "Arafat Day"));
            AddRun(list, table.Arafat.AddDays(1), 3, "عيد الأضحى", "Eid al-Adha");
            list.Add(new HolidayTemplate(table.IslamicNewYear, "رأس السنة الهجرية", "Islamic New Year"));
            list.Add(new HolidayTemplate(table.ProphetBirthday, "المولد النبوي الشريف", "Prophet's Birthday"));
        }

        return list.OrderBy(h => h.Date).ToList();
    }

    private static void AddRun(List<HolidayTemplate> list, DateOnly first, int days, string ar, string en)
    {
        for (var i = 0; i < days; i++)
        {
            list.Add(new HolidayTemplate(first.AddDays(i), $"{ar} (اليوم {i + 1})", $"{en} (day {i + 1})"));
        }
    }

    private sealed record YearTable(DateOnly ShamElNessim, DateOnly EidAlFitr, DateOnly Arafat, DateOnly IslamicNewYear, DateOnly ProphetBirthday);
}
