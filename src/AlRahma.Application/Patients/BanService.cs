using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Patients;

/// <summary>
/// قاعدة الحظر: عدد الغيابات (ban.no_show_limit، افتراضياً 3) خلال نافذة (ban.window_days، افتراضياً 90 يوماً).
/// الغيابات التي سبقت آخر فك حظر لا تُحتسب، حتى لا يُعاد حظر المريض فوراً بعد فكه.
/// </summary>
public class BanService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, SettingsReader settings)
{
    public async Task<int> CountedNoShowsAsync(Guid patientId, CancellationToken ct)
    {
        var windowDays = await settings.GetLongAsync(SettingsReader.BanWindowDays, current.BranchId, 90, ct);
        var since = clock.UtcNow.AddDays(-windowDays);
        var lastUnban = await db.PatientBanEvents
            .Where(e => e.PatientId == patientId && e.Action == "unban")
            .MaxAsync(e => (DateTimeOffset?)e.CreatedAt, ct);
        if (lastUnban > since) since = lastUnban.Value;
        return await db.PatientNoShows.CountAsync(n => n.PatientId == patientId && n.OccurredAt > since, ct);
    }

    /// <summary>يسجل غياباً ويحظر المريض إذا بلغ الحد. لا يحفظ (SaveChanges على المستدعي).</summary>
    public async Task<bool> RecordNoShowAsync(Patient patient, Guid? ticketId, Guid? bookingId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        db.PatientNoShows.Add(new PatientNoShow
        {
            NoShowId = UuidV7.New(), PatientId = patient.PatientId, TicketId = ticketId, BookingId = bookingId, OccurredAt = now,
        });
        patient.NoShowCount++;
        patient.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        if (patient.IsBanned) return false;
        var limit = await settings.GetLongAsync(SettingsReader.BanNoShowLimit, current.BranchId, 3, ct);
        var counted = await CountedNoShowsAsync(patient.PatientId, ct);
        if (counted < limit) return false;

        patient.IsBanned = true;
        patient.BannedAt = now;
        patient.BanReason = "no_show_limit";
        db.PatientBanEvents.Add(new PatientBanEvent
        {
            EventId = UuidV7.New(), PatientId = patient.PatientId, Action = "ban", Method = "auto",
            Note = $"{counted} غيابات", PerformedBy = null, CreatedAt = now,
        });
        audit.Add("PATIENT_BANNED", "patient", patient.PatientId, after: new { reason = "no_show_limit", no_shows = counted });
        return true;
    }

    public Task UnbanAsync(Patient patient, string method, long amount, string? note, CancellationToken ct, Guid? receiptId = null)
    {
        var now = clock.UtcNow;
        patient.IsBanned = false;
        patient.BannedAt = null;
        patient.BanReason = null;
        patient.UpdatedAt = now;
        db.PatientBanEvents.Add(new PatientBanEvent
        {
            EventId = UuidV7.New(), PatientId = patient.PatientId, Action = "unban", Method = method, Amount = amount,
            Note = note, ReceiptId = receiptId, PerformedBy = current.StaffUserId, CreatedAt = now,
        });
        audit.Add("PATIENT_UNBANNED", "patient", patient.PatientId, after: new { method, amount }, reason: note);
        return Task.CompletedTask;
    }
}
