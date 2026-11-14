namespace Moonlight.Api.Common;

public sealed class ApiException(string code, string? detail = null, IDictionary<string, string[]>? errors = null)
    : Exception(detail ?? code)
{
    public string Code { get; } = code;

    public string? Detail { get; } = detail;

    public IDictionary<string, string[]>? Errors { get; } = errors;

    public static ApiException NotFound() => new(ErrorCodes.NotFound);

    public static ApiException Validation(string field, string message) =>
        new(ErrorCodes.ValidationFailed, errors: new Dictionary<string, string[]> { [field] = [message] });
}

public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string OtpInvalid = "OTP_INVALID";
    public const string OtpExpired = "OTP_EXPIRED";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string SlotTaken = "SLOT_TAKEN";
    public const string SlotUnavailable = "SLOT_UNAVAILABLE";
    public const string SlotReserved = "SLOT_RESERVED";
    public const string SlotOverlap = "SLOT_OVERLAP";
    public const string InvalidTransition = "INVALID_TRANSITION";
    public const string CancelWindowPassed = "CANCEL_WINDOW_PASSED";
    public const string PhoneInUse = "PHONE_IN_USE";
    public const string BookingLimit = "BOOKING_LIMIT";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";

    private sealed record Entry(int Status, string TitleEn, string TitleAr, string DetailEn, string DetailAr);

    private static readonly Dictionary<string, Entry> Catalog = new()
    {
        [ValidationFailed] = new(400, "Validation failed", "فشل التحقق من البيانات", "One or more fields are invalid.", "بعض الحقول غير صحيحة."),
        [OtpInvalid] = new(400, "Invalid code", "رمز غير صحيح", "The verification code is incorrect.", "رمز التحقق غير صحيح."),
        [OtpExpired] = new(400, "Code expired", "انتهت صلاحية الرمز", "The verification code has expired. Request a new one.", "انتهت صلاحية رمز التحقق. اطلب رمزا جديدا."),
        [Unauthorized] = new(401, "Unauthorized", "غير مصرح", "Authentication is required or the token has expired.", "المصادقة مطلوبة أو انتهت صلاحية الجلسة."),
        [Forbidden] = new(403, "Forbidden", "ممنوع", "Your account is not allowed to perform this action.", "حسابك غير مسموح له بتنفيذ هذا الإجراء."),
        [NotFound] = new(404, "Not found", "غير موجود", "The requested resource was not found.", "المورد المطلوب غير موجود."),
        [SlotTaken] = new(409, "Slot no longer available", "الموعد لم يعد متاحا", "Someone just booked this time. Please pick another slot.", "تم حجز هذا الموعد للتو. من فضلك اختر موعدا آخر."),
        [SlotUnavailable] = new(409, "Slot unavailable", "الموعد غير متاح", "This time is in the past or too close to now to be booked.", "هذا الموعد قد مضى أو قريب جدا من الوقت الحالي."),
        [SlotReserved] = new(409, "Slot is reserved", "الموعد محجوز", "This slot has an appointment. Cancel the appointment first.", "هذا الموعد محجوز. ألغِ الحجز أولا."),
        [SlotOverlap] = new(409, "Slot overlaps", "تداخل في المواعيد", "The slot overlaps another slot of this doctor.", "الموعد يتداخل مع موعد آخر لهذا الطبيب."),
        [InvalidTransition] = new(409, "Invalid status change", "تغيير حالة غير مسموح", "The appointment cannot move to that status.", "لا يمكن نقل الحجز إلى هذه الحالة."),
        [CancelWindowPassed] = new(409, "Cancellation window passed", "انتهت مهلة الإلغاء", "It is too late to change this appointment. Please call the clinic.", "فات وقت تعديل الحجز. من فضلك اتصل بالعيادة."),
        [PhoneInUse] = new(409, "Phone number in use", "رقم الهاتف مستخدم", "This phone number belongs to another account type.", "رقم الهاتف مرتبط بنوع حساب آخر."),
        [BookingLimit] = new(409, "Booking limit reached", "تم الوصول للحد الأقصى للحجوزات", "You have reached the booking limit. Cancel an upcoming booking or pick another day.", "وصلت للحد الأقصى للحجوزات. ألغِ حجزا قادما أو اختر يوما آخر."),
        [RateLimited] = new(429, "Too many requests", "طلبات كثيرة", "Too many requests. Please try again later.", "طلبات كثيرة جدا. حاول مرة أخرى لاحقا."),
        [InternalError] = new(500, "Internal error", "خطأ داخلي", "Something went wrong.", "حدث خطأ ما."),
    };

    public static int StatusOf(string code) => Catalog[code].Status;

    public static string TitleOf(string code, string lang) => lang == "en" ? Catalog[code].TitleEn : Catalog[code].TitleAr;

    public static string DetailOf(string code, string lang) => lang == "en" ? Catalog[code].DetailEn : Catalog[code].DetailAr;

    public static string FromStatus(int status) => status switch
    {
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        429 => RateLimited,
        >= 500 => InternalError,
        _ => ValidationFailed,
    };
}
