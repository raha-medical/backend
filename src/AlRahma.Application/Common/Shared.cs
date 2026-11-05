using System.Globalization;
using AlRahma.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Common;

/// <summary>call_next_by: doctor | reception | both. auto_call_next: الاستدعاء التلقائي عند إنهاء المعاينة.</summary>
public record CallPolicyDto(string CallNextBy, bool AutoCallNext);

/// <summary>إعدادات النظام (system_settings): قيمة الفرع إن وجدت، وإلا القيمة العامة، وإلا الافتراضي.</summary>
public class SettingsReader(IAppDbContext db)
{
    public const string BanNoShowLimit = "ban.no_show_limit";
    public const string BanWindowDays = "ban.window_days";
    public const string UnbanDepositAmount = "ban.unban_deposit_amount";
    public const string CancelMinHours = "booking.cancel_min_hours";
    public const string FollowUpWindowDays = "visit.follow_up_window_days";
    public const string OnlineDepositAmount = "booking.online_deposit_amount";
    public const string PrescriptionValidDays = "prescription.valid_days";
    public const string ConsultationEditHours = "consultation.edit_window_hours";
    public const string ReceiptBlockSize = "receipt.block_size";
    public const string ReminderMinutes = "booking.reminder_minutes";
    /// <summary>يُغلق التحويل غير المفعّل تلقائياً بعد هذه الأيام (المرحلة 5).</summary>
    public const string ReferralExpiryDays = "referral.expiry_days";
    /// <summary>1 = نتيجة البند المؤجل تظهر للمريض قبل السداد، 0 = بعد السداد فقط (الافتراضي، حسب المواصفة 5.2).</summary>
    public const string ShowDeferredResults = "results.show_deferred_to_patient";
    /// <summary>تذكير «موعدك قريب» قبل الموعد المتوقع بهذه الدقائق (20).</summary>
    public const string ReminderSoonMinutes = "booking.reminder_soon_minutes";
    /// <summary>ساعة إرسال تذكير «موعدك غداً» مساء اليوم السابق (18 = السادسة مساءً، 0 = إيقاف).</summary>
    public const string DayBeforeHour = "booking.day_before_hour";
    /// <summary>أقصى حجوزات قائمة للمريض الواحد (0 = بلا حد).</summary>
    public const string MaxOpenBookings = "booking.max_open_per_patient";
    public const string ArriveEarlyMinutes = "booking.arrive_early_minutes";
    public const string TurnSoonAhead = "queue.turn_soon_ahead";
    public const string AutoCallNext = "queue.auto_call_next";
    public const string CallAlertRepeats = "queue.call_alert_repeats";
    /// <summary>مهلة المحجوز المتأخر (دقيقة): ضمنها يدخل بعد المريض الذي في الغرفة، وبعدها يُخرج من الحساب ويُعامل كمتأخر.</summary>
    public const string NoShowGraceMinutes = "queue.no_show_grace_minutes";
    /// <summary>بعد نهاية الوجبة (الفعلية) بهذه الدقائق يُغلق التسجيل تلقائياً وتُعلّم الحجوزات الباقية غائبة.</summary>
    public const string AutoCloseAfterMinutes = "queue.auto_close_after_minutes";
    /// <summary>أقصى عدد «إضافي» فوق السعة كنسبة من سعة الوجبة (0 = بلا حد).</summary>
    public const string ExtraMaxPercent = "queue.extra_max_percent";
    /// <summary>من يستدعي المريض التالي في الفرع: doctor | reception | both.</summary>
    public const string CallNextBy = "queue.call_next_by";

    // ------------------------------------------------------------------ المرحلة 3: سياسة الإلغاء وتغيير الموعد والحضور الذاتي
    /// <summary>رسوم الإلغاء المتأخر (داخل booking.cancel_min_hours) بالدينار، 0 = بلا رسوم.</summary>
    public const string LateCancelFee = "booking.late_cancel_fee";
    /// <summary>1 = الإلغاء المتأخر يُحتسب غياباً (السلوك السابق)، 0 = لا.</summary>
    public const string LateCancelCountsNoShow = "booking.late_cancel_counts_no_show";
    /// <summary>أقصى عدد مرات تغيير موعد الحجز الواحد (0 = تغيير الموعد غير متاح).</summary>
    public const string MaxReschedules = "booking.max_reschedules";
    /// <summary>أقل مدة قبل الموعد الحالي يُسمح فيها بتغييره (ساعة).</summary>
    public const string RescheduleMinHours = "booking.reschedule_min_hours";
    /// <summary>أين يذهب المبلغ المدفوع إلكترونياً عند الإلغاء: credit (رصيد، السابق) | refund (إرجاع لوسيلة الدفع).</summary>
    public const string CancelPaidTo = "booking.cancel_paid_to";
    /// <summary>1 = تثبيت الحضور الذاتي بمسح QR شاشة الانتظار مفعل.</summary>
    public const string SelfCheckInEnabled = "checkin.self_enabled";
    /// <summary>نطاق رمز الشاشة: clinic (الحجز في عيادة الشاشة فقط) | branch (أي عيادة في الفرع).</summary>
    public const string CheckInQrScope = "checkin.qr_scope";

    public static class CancelPaidToValues
    {
        public const string Credit = "credit";
        public const string Refund = "refund";
        public static readonly IReadOnlyList<string> All = [Credit, Refund];
    }

    public static class CheckInQrScopeValues
    {
        public const string Clinic = "clinic";
        public const string Branch = "branch";
        public static readonly IReadOnlyList<string> All = [Clinic, Branch];
    }

    public static class CallNextByValues
    {
        public const string Doctor = "doctor";
        public const string Reception = "reception";
        public const string Both = "both";
        public static readonly IReadOnlyList<string> All = [Doctor, Reception, Both];
    }

    public async Task<string?> GetStringAsync(string key, Guid? branchId, CancellationToken ct)
    {
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key && (s.BranchId == null || s.BranchId == branchId))
            .Select(s => new { s.BranchId, s.Value })
            .ToListAsync(ct);
        var row = rows.FirstOrDefault(r => r.BranchId != null) ?? rows.FirstOrDefault();
        return row?.Value.Trim('"', ' ');
    }

    /// <summary>سياسة الاستدعاء للفرع، تُعرض في /queue/live ليعرف الفرونت الأزرار المتاحة لكل دور.</summary>
    public async Task<CallPolicyDto> CallPolicyAsync(Guid branchId, CancellationToken ct)
    {
        var by = await GetStringAsync(CallNextBy, branchId, ct);
        if (by is null || !CallNextByValues.All.Contains(by)) by = CallNextByValues.Both;
        var auto = await GetLongAsync(AutoCallNext, branchId, 0, ct) == 1;
        return new CallPolicyDto(by, auto);
    }

    public async Task<long> GetLongAsync(string key, Guid? branchId, long fallback, CancellationToken ct)
    {
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key && (s.BranchId == null || s.BranchId == branchId))
            .Select(s => new { s.BranchId, s.Value })
            .ToListAsync(ct);
        var row = rows.FirstOrDefault(r => r.BranchId != null) ?? rows.FirstOrDefault();
        return row is not null && long.TryParse(row.Value.Trim('"', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }
}

/// <summary>الأرقام المقروءة لكل فرع (ملف، فاتورة، ...) من جدول number_sequences بشكل آمن مع التزامن.</summary>
public class NumberSequences(IAppDbContext db)
{
    public async Task<long> NextAsync(Guid branchId, string kind, string period, CancellationToken ct) =>
        await db.ScalarAsync<long>($"""
            INSERT INTO number_sequences (branch_id, kind, period, next_value) VALUES ({branchId}, {kind}, {period}, 2)
            ON CONFLICT (branch_id, kind, period) DO UPDATE SET next_value = number_sequences.next_value + 1
            RETURNING next_value - 1 AS "Value"
            """, ct);

    public async Task<string> BranchCodeAsync(Guid branchId, CancellationToken ct) =>
        await db.Branches.Where(b => b.BranchId == branchId).Select(b => b.Code).FirstAsync(ct);
}

/// <summary>إرسال الأحداث المباشرة (SignalR). المجموعات موثقة في القسم 0.9 من العقد.</summary>
public interface IRealtime
{
    Task SendAsync(string group, string eventName, object payload);
}

public static class RealtimeGroups
{
    public static string BranchQueue(Guid branchId) => $"branch:{branchId}:queue";
    public static string Doctor(Guid doctorId) => $"doctor:{doctorId}";
    public static string User(Guid userId) => $"user:{userId}";
    public static string Display(Guid deviceId) => $"display:{deviceId}";
    /// <summary>المحاسبون ومدير الفرع: تنبيهات الورديات وطلبات الاسترجاع.</summary>
    public static string BranchFinance(Guid branchId) => $"branch:{branchId}:finance";
    /// <summary>فنيو المختبر أو أخصائيو الأشعة في الفرع: lab | radiology.</summary>
    public static string BranchDepartment(Guid branchId, string department) => $"branch:{branchId}:{department}";
    public static string PatientAccount(Guid accountId) => $"patient_account:{accountId}";
}

public static class PhoneNumbers
{
    /// <summary>توحيد رقم الهاتف العراقي: 07XXXXXXXXX (يقبل +964 و 964 وأرقاماً عربية).</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsDigit).Select(c => (char)('0' + (int)char.GetNumericValue(c))).ToArray());
        if (digits.StartsWith("00964")) digits = "0" + digits[5..];
        else if (digits.StartsWith("964")) digits = "0" + digits[3..];
        else if (digits.Length == 10 && digits.StartsWith('7')) digits = "0" + digits;
        return digits.Length == 11 && digits.StartsWith("07") ? digits : null;
    }
}
