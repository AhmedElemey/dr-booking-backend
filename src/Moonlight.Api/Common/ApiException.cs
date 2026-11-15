namespace Moonlight.Api.Common;

public sealed class ApiException(string code, string? detail = null, IDictionary<string, string[]>? errors = null, IDictionary<string, object?>? extensions = null)
    : Exception(detail ?? code)
{
    public string Code { get; } = code;

    public string? Detail { get; } = detail;

    public IDictionary<string, string[]>? Errors { get; } = errors;

    /// <summary>Extra top-level members of the problem body (e.g. <c>feature</c>).</summary>
    public IDictionary<string, object?>? Extensions { get; } = extensions;

    /// <summary>Overrides the HTTP status of the code (e.g. 422 for a VALIDATION_FAILED about a business rule).</summary>
    public int? StatusOverride { get; init; }

    public static ApiException FeatureDisabled(string feature) =>
        new(ErrorCodes.FeatureDisabled, extensions: new Dictionary<string, object?> { ["feature"] = feature });

    public static ApiException NotFound() => new(ErrorCodes.NotFound);

    /// <summary>A well-formed request that breaks a business rule: VALIDATION_FAILED with status 422.</summary>
    public static ApiException Unprocessable(string field, string message) =>
        new(ErrorCodes.ValidationFailed, errors: new Dictionary<string, string[]> { [field] = [message] }) { StatusOverride = 422 };

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
    public const string FeatureDisabled = "FEATURE_DISABLED";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";
    public const string ClinicClosed = "CLINIC_CLOSED";
    public const string CouponInvalid = "COUPON_INVALID";
    public const string AlreadyReviewed = "ALREADY_REVIEWED";
    public const string NotReviewable = "NOT_REVIEWABLE";
    public const string PaymentNotAllowed = "PAYMENT_NOT_ALLOWED";
    public const string VideoNotOpen = "VIDEO_NOT_OPEN";
    public const string AlreadyWaiting = "ALREADY_WAITING";
    public const string ProviderError = "PROVIDER_ERROR";

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
        [FeatureDisabled] = new(403, "Feature unavailable", "الخدمة غير متاحة", "This feature is turned off for this clinic. Please call the clinic.", "هذه الخدمة غير مفعلة لدى العيادة. من فضلك اتصل بالعيادة."),
        [RateLimited] = new(429, "Too many requests", "طلبات كثيرة", "Too many requests. Please try again later.", "طلبات كثيرة جدا. حاول مرة أخرى لاحقا."),
        [ClinicClosed] = new(409, "Clinic closed", "العيادة مغلقة", "The clinic is closed on this date. Please pick another day.", "العيادة مغلقة في هذا اليوم. من فضلك اختر يوما آخر."),
        [CouponInvalid] = new(422, "Coupon not valid", "الكوبون غير صالح", "This coupon cannot be applied to the booking.", "لا يمكن تطبيق هذا الكوبون على الحجز."),
        [AlreadyReviewed] = new(409, "Already reviewed", "تم التقييم مسبقا", "You have already reviewed this visit.", "لقد قمت بتقييم هذه الزيارة من قبل."),
        [NotReviewable] = new(409, "Visit cannot be reviewed", "لا يمكن تقييم الزيارة", "Only completed visits can be reviewed.", "يمكن تقييم الزيارات المكتملة فقط."),
        [PaymentNotAllowed] = new(409, "Payment not allowed", "الدفع غير متاح", "This appointment cannot be paid online (it must be confirmed, upcoming, unpaid and have an amount due).", "لا يمكن دفع هذا الحجز إلكترونيا (يجب أن يكون مؤكدا وقادما وغير مدفوع وله مبلغ مستحق)."),
        [VideoNotOpen] = new(409, "Video room not open", "غرفة الفيديو غير متاحة", "The video room opens 15 minutes before the visit and closes 30 minutes after it ends.", "تفتح غرفة الفيديو قبل الموعد بـ 15 دقيقة وتغلق بعد انتهائه بـ 30 دقيقة."),
        [AlreadyWaiting] = new(409, "Already on the waiting list", "أنت في قائمة الانتظار بالفعل", "You are already waiting for this doctor on that day.", "أنت في قائمة الانتظار لهذا الطبيب في هذا اليوم بالفعل."),
        [ProviderError] = new(502, "Provider error", "خطأ في مزود الخدمة", "The external provider failed. Please try again later.", "تعذر الاتصال بمزود الخدمة الخارجي. حاول مرة أخرى لاحقا."),
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
