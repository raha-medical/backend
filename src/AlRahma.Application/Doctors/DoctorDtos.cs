namespace AlRahma.Application.Doctors;

public record DoctorDto(Guid DoctorId, string FullName, string Specialty, string? JobTitle, Guid ClinicId, string ClinicName, short DefaultSlotMinutes, string? PhotoUrl);
public record SetPhotoRequest(Guid? FileId);
/// <summary>
/// إعدادات الطبيب (المرحلة 3) باتفاقه مع مدير الفرع. القيم الفارغة = إعداد الفرع.
/// follow_up_price: 0 = مراجعة مجانية. online_deposit_amount: 0 = لا عربون. allow_extra: false = «ممنوع الإضافي».
/// </summary>
public record DoctorSettingsDto(bool AllowExtra, short? LateGraceMinutes, short? FollowUpDays, long? FollowUpPrice, long? OnlineDepositAmount,
    long BranchLateGraceMinutes, long BranchFollowUpDays, long BranchOnlineDepositAmount)
{
    /// <summary>مدة العودة المجانية بالنتائج للطبيب (null = العيادة ثم الفرع)، والقيمة الفعلية المطبقة.</summary>
    public short? FreeReturnDays { get; init; }
    public int EffectiveFreeReturnDays { get; init; }
}
/// <summary>free_return_days: يُستبدل مثل باقي الحقول (null = العيادة ثم إعداد الفرع).</summary>
public record PutDoctorSettingsRequest(bool? AllowExtra, short? LateGraceMinutes, short? FollowUpDays, long? FollowUpPrice, long? OnlineDepositAmount,
    short? FreeReturnDays = null);

public record CommissionRateDto(decimal NewVisitRate, decimal FollowUpRate, DateOnly EffectiveFrom, DateTimeOffset CreatedAt);

public record CommissionHistoryDto(Guid DoctorId, CommissionRateDto? Current, IReadOnlyList<CommissionRateDto> Items);

public record AddCommissionRequest(decimal? NewVisitRate, decimal? FollowUpRate, DateOnly? EffectiveFrom);

/// <summary>weekday: sun | mon | tue | wed | thu | fri | sat. الأوقات بتوقيت بغداد بصيغة HH:mm.</summary>
public record ScheduleSlotDto(
    Guid? SessionTemplateId, string? Weekday, string? Start, string? End, short? SlotMinutes, short? OnlineQuotaPercent,
    short? Capacity = null, short? OnlineQuota = null);

public record WeeklyScheduleDto(Guid DoctorId, IReadOnlyList<ScheduleSlotDto> Weekly);

public record PutScheduleRequest(List<ScheduleSlotDto>? Weekly);

public record SessionDto(
    Guid SessionId, DateOnly Date, string Weekday, string Start, string End, DateTimeOffset StartsAt,
    short SlotMinutes, short CapacityTotal, short OnlineQuota, string Status, string? CancelReason)
{
    /// <summary>وجبة موجودة في يوم أغلقه جدول دوام الفرع: غير قابلة للحجز، وحجوزاتها القائمة باقية.</summary>
    public bool BranchClosedDay { get; init; }
}

public record CancelSessionRequest(string? Reason);

public record CancelSessionResponse(int CancelledSessions, int AffectedBookings, int Notified);
