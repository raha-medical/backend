using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.PatientApp;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Clinical;

public record DispenseLineInput(short? LineNo, bool? Dispensed, string? SubstituteDrugName, string? NotDispensedReason);
public record DispenseRequest(List<DispenseLineInput>? Lines);
public record PrescriptionItemDto(short LineNo, string DrugName, string? Dose, string? Frequency, short? DurationDays, string? Instructions,
    bool? Dispensed, string? SubstituteDrugName, string? NotDispensedReason);
public record PrescriptionDto(Guid PrescriptionId, string PrescriptionCode, string PatientName, string? FileNumber, string? Gender, int? Age,
    string DoctorName, string ClinicName, string BranchName, string? Diagnosis, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string Status,
    DateTimeOffset? DispensedAt, string? DispensedBy, IReadOnlyList<PrescriptionItemDto> Items)
{
    /// <summary>دورة التحويل في تطبيق المريض (العقد 23).</summary>
    public Guid? ReferralId { get; init; }
    public string? ReferralStatus { get; init; }
    public string? ReferralStatusAr { get; init; }
}

/// <summary>
/// الصيدلية (المرحلة الأولى): صرف الوصفة إلكترونياً مرة واحدة في أي فرع حتى انتهاء صلاحيتها، مع توثيق ما صُرف وما لم يُصرف وسببه.
/// </summary>
public class PharmacyService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, PatientPortalService portal, ReferralTracker referrals)
{
    public async Task<PrescriptionDto> GetByCodeAsync(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        if (!normalized.StartsWith("RX-")) normalized = "RX-" + normalized;
        var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionCode == normalized, ct)
                 ?? throw new AppException(404, "PRESCRIPTION_NOT_FOUND", "الوصفة غير موجودة");
        return (await ToDtosAsync([rx], includeFileNumber: true, ct))[0];
    }

    public async Task<PrescriptionDto> DispenseAsync(Guid prescriptionId, DispenseRequest req, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync($"SELECT 1 FROM prescriptions WHERE prescription_id = {prescriptionId} FOR UPDATE", ct);
        var rx = await db.Prescriptions.Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == prescriptionId, ct)
                 ?? throw new AppException(404, "PRESCRIPTION_NOT_FOUND", "الوصفة غير موجودة");
        if (rx.Status == PrescriptionStatuses.Dispensed)
            throw new AppException(409, "PRESCRIPTION_ALREADY_DISPENSED", "صُرفت الوصفة مسبقاً")
                { Extensions = new Dictionary<string, object?> { ["dispensed_at"] = rx.DispensedAt } };
        var now = clock.UtcNow;
        if (ConsultationService.EffectiveStatus(rx, now) == PrescriptionStatuses.Expired)
            throw new AppException(409, "PRESCRIPTION_EXPIRED", "انتهت صلاحية الوصفة", "يحتاج المريض وصفة جديدة من الطبيب");

        var input = req.Lines ?? [];
        var v = new Validator();
        var expected = rx.Lines.Select(l => l.LineNo).OrderBy(x => x).ToList();
        var given = input.Where(l => l.LineNo is not null).Select(l => l.LineNo!.Value).ToList();
        v.Check(given.Count == input.Count && given.Distinct().Count() == given.Count && given.OrderBy(x => x).SequenceEqual(expected),
            "lines", $"أرسل كل الأسطر مرة واحدة ({string.Join("، ", expected)})");
        for (var i = 0; i < input.Count; i++)
        {
            var l = input[i];
            v.Check(l.Dispensed is not null, $"lines[{i}].dispensed", "حدد صُرف أو لم يُصرف");
            v.Check(l.Dispensed != false || !string.IsNullOrWhiteSpace(l.NotDispensedReason), $"lines[{i}].not_dispensed_reason", "سبب عدم الصرف مطلوب");
            v.Check(l.SubstituteDrugName is null || l.SubstituteDrugName.Length <= 200, $"lines[{i}].substitute_drug_name", "200 حرف كحد أقصى");
        }
        v.ThrowIfInvalid();
        foreach (var l in input)
        {
            var line = rx.Lines.First(x => x.LineNo == l.LineNo);
            line.Dispensed = l.Dispensed;
            line.SubstituteDrugName = l.Dispensed == true && !string.IsNullOrWhiteSpace(l.SubstituteDrugName) ? l.SubstituteDrugName.Trim() : null;
            line.NotDispensedReason = l.Dispensed == false ? l.NotDispensedReason!.Trim() : null;
        }
        rx.Status = PrescriptionStatuses.Dispensed;
        rx.DispensedAt = now;
        rx.DispensedBy = current.UserId;
        rx.DispensedBranchId = current.BranchId;
        audit.Add("PRESCRIPTION_DISPENSED", "prescription", rx.PrescriptionId, after: new
        {
            rx.PrescriptionCode, lines = input.Select(l => new { l.LineNo, l.Dispensed, l.SubstituteDrugName, l.NotDispensedReason }),
        });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        await referrals.CompletePrescriptionAsync(rx.PrescriptionId, current.BranchId, ct);
        return (await ToDtosAsync([rx], includeFileNumber: true, ct))[0];
    }

    public async Task<PagedResult<PrescriptionDto>> DispensedAsync(DateOnly? date, int page, int pageSize, CancellationToken ct)
    {
        var p = new PageQuery(page, pageSize);
        var day = date ?? clock.BaghdadToday();
        DateTimeOffset start = ClockExtensions.ToUtc(day, TimeOnly.MinValue), end = ClockExtensions.ToUtc(day.AddDays(1), TimeOnly.MinValue);
        var q = db.Prescriptions.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.DispensedBranchId == current.BranchId && x.DispensedAt >= start && x.DispensedAt < end);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.DispensedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        return new PagedResult<PrescriptionDto>(await ToDtosAsync(rows, true, ct), p.SafePage, p.SafePageSize, total);
    }

    public async Task<List<PrescriptionDto>> MyPrescriptionsAsync(Guid patientId, CancellationToken ct)
    {
        await portal.EnsureLinkedAsync(patientId, ct);
        var rows = await db.Prescriptions.AsNoTracking().Include(x => x.Lines).Where(x => x.PatientId == patientId)
            .OrderByDescending(x => x.IssuedAt).ToListAsync(ct); // كل الوصفات دائماً، حتى المنتهية (4.3)
        var ids = rows.Select(r => r.PrescriptionId).ToList();
        var byRx = await db.Referrals.AsNoTracking().Where(r => r.PrescriptionId != null && ids.Contains(r.PrescriptionId.Value))
            .ToDictionaryAsync(r => r.PrescriptionId!.Value, ct);
        return (await ToDtosAsync(rows, false, ct)).Select(d => byRx.TryGetValue(d.PrescriptionId, out var r)
            ? d with { ReferralId = r.ReferralId, ReferralStatus = r.Status, ReferralStatusAr = ReferralService.StatusAr(r.Status) } : d).ToList();
    }

    private async Task<List<PrescriptionDto>> ToDtosAsync(List<Prescription> rows, bool includeFileNumber, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var consultationIds = rows.Select(r => r.ConsultationId).ToList();
        var info = await (
            from c in db.Consultations.AsNoTracking()
            join t in db.QueueTickets.AsNoTracking() on c.TicketId equals t.TicketId
            join b in db.Branches.AsNoTracking() on t.BranchId equals b.BranchId
            join d in db.Doctors.AsNoTracking() on c.DoctorId equals d.DoctorId
            join cl in db.Clinics.AsNoTracking() on d.ClinicId equals cl.ClinicId
            join p in db.Patients.AsNoTracking() on c.PatientId equals p.PatientId
            where consultationIds.Contains(c.ConsultationId)
            select new { c.ConsultationId, c.Diagnosis, Branch = b.Name, Doctor = d.FullName, Clinic = cl.Name, Patient = p })
            .ToDictionaryAsync(x => x.ConsultationId, ct);
        var userIds = rows.Where(r => r.DispensedBy != null).Select(r => r.DispensedBy!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        var now = clock.UtcNow;
        var today = clock.BaghdadToday();
        return rows.Select(r =>
        {
            var x = info[r.ConsultationId];
            return new PrescriptionDto(r.PrescriptionId, r.PrescriptionCode, x.Patient.FullName, includeFileNumber ? x.Patient.FileNumber : null,
                x.Patient.Gender, PatientPortalService.AgeOf(x.Patient, today), x.Doctor, x.Clinic, x.Branch, x.Diagnosis, r.IssuedAt, r.ExpiresAt,
                ConsultationService.EffectiveStatus(r, now), r.DispensedAt, r.DispensedBy is { } u ? users.GetValueOrDefault(u) : null,
                r.Lines.OrderBy(l => l.LineNo).Select(l => new PrescriptionItemDto(l.LineNo, l.DrugName, l.Dose, l.Frequency, l.DurationDays,
                    l.Instructions, l.Dispensed, l.SubstituteDrugName, l.NotDispensedReason)).ToList());
        }).ToList();
    }
}
