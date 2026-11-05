using AlRahma.Application.Abstractions;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Infrastructure.Persistence;

/// <summary>
/// الربط مع قاعدة البيانات الموجودة (database/schema_v5.sql هو المرجع).
/// EF لا يُنشئ الجداول ولا يعدلها؛ أسماء الأعمدة snake_case عبر UseSnakeCaseNamingConvention.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<ClinicClosure> ClinicClosures => Set<ClinicClosure>();
    public DbSet<BranchClosure> BranchClosures => Set<BranchClosure>();
    public DbSet<LookupValue> LookupValues => Set<LookupValue>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserBranch> UserBranches => Set<UserBranch>();
    public DbSet<UserClinic> UserClinics => Set<UserClinic>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<BranchWorkingHour> BranchWorkingHours => Set<BranchWorkingHour>();
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<DoctorCommissionRate> DoctorCommissionRates => Set<DoctorCommissionRate>();
    public DbSet<DoctorScheduleTemplate> DoctorScheduleTemplates => Set<DoctorScheduleTemplate>();
    public DbSet<DoctorSession> DoctorSessions => Set<DoctorSession>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<PatientNoShow> PatientNoShows => Set<PatientNoShow>();
    public DbSet<PatientBanEvent> PatientBanEvents => Set<PatientBanEvent>();
    public DbSet<PatientCreditEntry> PatientCredits => Set<PatientCreditEntry>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<QueueTicket> QueueTickets => Set<QueueTicket>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentTender> PaymentTenders => Set<PaymentTender>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<Deferral> Deferrals => Set<Deferral>();
    public DbSet<DeferralItem> DeferralItems => Set<DeferralItem>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<PatientAccount> PatientAccounts => Set<PatientAccount>();
    public DbSet<PatientAccountLink> PatientAccountLinks => Set<PatientAccountLink>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<Consultation> Consultations => Set<Consultation>();
    public DbSet<ConsultationAddendum> ConsultationAddenda => Set<ConsultationAddendum>();
    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<PrescriptionLine> PrescriptionLines => Set<PrescriptionLine>();
    public DbSet<MedicalOrder> MedicalOrders => Set<MedicalOrder>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<StaffPayPlan> StaffPayPlans => Set<StaffPayPlan>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<LabTestParameter> LabTestParameters => Set<LabTestParameter>();
    public DbSet<LabResult> LabResults => Set<LabResult>();
    public DbSet<LabResultValue> LabResultValues => Set<LabResultValue>();
    public DbSet<RadiologyReport> RadiologyReports => Set<RadiologyReport>();
    public DbSet<OnlinePayment> OnlinePayments => Set<OnlinePayment>();
    public DbSet<ReceiptBlock> ReceiptBlocks => Set<ReceiptBlock>();
    public DbSet<SyncOperation> SyncOperations => Set<SyncOperation>();
    public DbSet<AgentEvent> AgentEvents => Set<AgentEvent>();
    public DbSet<FastForwardOffer> FastForwardOffers => Set<FastForwardOffer>();
    public DbSet<FastForwardRecipient> FastForwardRecipients => Set<FastForwardRecipient>();

    public async Task<bool> ExistsAsync(FormattableString sql, CancellationToken ct = default) =>
        await Database.SqlQuery<bool>(sql).SingleAsync(ct);

    public async Task<int> CountAsync(FormattableString sql, CancellationToken ct = default) =>
        await Database.SqlQuery<int>(sql).SingleAsync(ct);

    public Task<int> ExecuteAsync(FormattableString sql, CancellationToken ct = default) =>
        Database.ExecuteSqlInterpolatedAsync(sql, ct);

    // ToListAsync لا يركّب استعلاماً فوق SQL (ضروري لجمل INSERT ... RETURNING)
    public async Task<T> ScalarAsync<T>(FormattableString sql, CancellationToken ct = default) =>
        (await Database.SqlQuery<T>(sql).ToListAsync(ct)).Single();

    public void DiscardChanges() => ChangeTracker.Clear();

    public Task<List<T>> QueryAsync<T>(FormattableString sql, CancellationToken ct = default) =>
        Database.SqlQuery<T>(sql).ToListAsync(ct);

    public async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct = default) =>
        Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(ct) : null;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Branch>(e =>
        {
            e.ToTable("branches");
            e.HasKey(x => x.BranchId);
            e.HasMany(x => x.Clinics).WithOne(x => x.Branch).HasForeignKey(x => x.BranchId);
            e.HasMany(x => x.Hours).WithOne().HasForeignKey(x => x.BranchId);
        });

        b.Entity<BranchWorkingHour>(e =>
        {
            e.ToTable("branch_working_hours");
            e.HasKey(x => new { x.BranchId, x.Weekday, x.ShiftNo });
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Code);
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleCode);
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleCode, x.Permission });
        });

        b.Entity<Clinic>(e =>
        {
            e.ToTable("clinics");
            e.HasKey(x => x.ClinicId);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("system_settings");
            e.HasKey(x => x.SettingId);
            e.Property(x => x.Value).HasColumnType("jsonb");
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.UserId);
            e.HasMany(x => x.Branches).WithOne().HasForeignKey(x => x.UserId);
            e.HasMany(x => x.Clinics).WithOne().HasForeignKey(x => x.UserId);
            e.HasMany(x => x.ExtraPermissions).WithOne().HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Doctor).WithOne().HasForeignKey<Doctor>(x => x.UserId);
        });

        b.Entity<UserBranch>(e =>
        {
            e.ToTable("user_branches");
            e.HasKey(x => new { x.UserId, x.BranchId });
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId);
        });

        b.Entity<UserClinic>(e =>
        {
            e.ToTable("user_clinics");
            e.HasKey(x => new { x.UserId, x.ClinicId });
            e.HasOne(x => x.Clinic).WithMany().HasForeignKey(x => x.ClinicId);
        });

        b.Entity<UserPermission>(e =>
        {
            e.ToTable("user_permissions");
            e.HasKey(x => new { x.UserId, x.Permission });
        });

        b.Entity<ClinicClosure>(e =>
        {
            e.ToTable("clinic_closures");
            e.HasKey(x => x.ClosureId);
        });
        b.Entity<BranchClosure>(e =>
        {
            e.ToTable("branch_closures");
            e.HasKey(x => x.ClosureId);
        });
        b.Entity<LookupValue>(e =>
        {
            e.ToTable("lookup_values");
            e.HasKey(x => x.LookupId);
        });
        b.Entity<Patient>(e =>
        {
            e.ToTable("patients");
            e.HasKey(x => x.PatientId);
        });
        b.Entity<PatientNoShow>(e =>
        {
            e.ToTable("patient_no_shows");
            e.HasKey(x => x.NoShowId);
        });
        b.Entity<PatientBanEvent>(e =>
        {
            e.ToTable("patient_ban_events");
            e.HasKey(x => x.EventId);
            e.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId);
        });
        b.Entity<PatientCreditEntry>(e =>
        {
            e.ToTable("patient_credit_ledger");
            e.HasKey(x => x.EntryId);
            // ترتيب الإدراج: الدفعة والاسترجاع قبل قيد الرصيد
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
            e.HasOne<Refund>().WithMany().HasForeignKey(x => x.RefundId);
            e.HasOne<OnlinePayment>().WithMany().HasForeignKey(x => x.OnlinePaymentId);
        });
        b.Entity<Booking>(e =>
        {
            e.ToTable("bookings");
            e.HasKey(x => x.BookingId);
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId);
        });
        b.Entity<QueueTicket>(e =>
        {
            e.ToTable("queue_tickets");
            e.HasKey(x => x.TicketId);
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId);
        });
        b.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.InvoiceId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.InvoiceId);
        });
        b.Entity<InvoiceItem>(e =>
        {
            e.ToTable("invoice_items");
            e.HasKey(x => x.InvoiceItemId);
            e.Property(x => x.CommissionRate).HasPrecision(5, 2);
        });
        b.Entity<Shift>(e =>
        {
            e.ToTable("shifts");
            e.HasKey(x => x.ShiftId);
            e.Property(x => x.CashDenominations).HasColumnType("jsonb");
        });
        b.Entity<Receipt>(e =>
        {
            e.ToTable("receipts");
            e.HasKey(x => x.ReceiptId);
        });
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.PaymentId);
            e.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId);
            e.HasMany(x => x.Tenders).WithOne().HasForeignKey(x => x.PaymentId);
            e.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.PaymentId);
            e.HasOne<OnlinePayment>().WithMany().HasForeignKey(x => x.OnlinePaymentId);
        });
        b.Entity<PaymentTender>(e =>
        {
            e.ToTable("payment_tenders");
            e.HasKey(x => x.TenderId);
        });
        b.Entity<PaymentAllocation>(e =>
        {
            e.ToTable("payment_allocations");
            e.HasKey(x => new { x.PaymentId, x.InvoiceItemId });
            e.HasOne<InvoiceItem>().WithMany().HasForeignKey(x => x.InvoiceItemId);
        });
        b.Entity<Refund>(e =>
        {
            e.ToTable("refunds");
            e.HasKey(x => x.RefundId);
            e.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId);
        });

        b.Entity<Deferral>(e =>
        {
            e.ToTable("deferrals");
            e.HasKey(x => x.DeferralId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.DeferralId);
        });
        b.Entity<DeferralItem>(e =>
        {
            e.ToTable("deferral_items");
            e.HasKey(x => new { x.DeferralId, x.InvoiceItemId });
        });
        b.Entity<Expense>(e =>
        {
            e.ToTable("expenses");
            e.HasKey(x => x.ExpenseId);
        });

        b.Entity<StoredFile>(e =>
        {
            e.ToTable("files");
            e.HasKey(x => x.FileId);
            e.Property(x => x.S3SyncedAt).HasColumnName("s3_synced_at");
        });

        b.Entity<Doctor>(e =>
        {
            e.ToTable("doctors");
            e.HasKey(x => x.DoctorId);
            e.HasOne(x => x.Clinic).WithMany().HasForeignKey(x => x.ClinicId);
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.DeviceId);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.TokenId);
            // علاقة صريحة حتى يُدرج التوكن الجديد قبل تحديث القديم (replaced_by)
            e.HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedBy);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(x => new { x.IdempotencyKey, x.Scope });
            e.Property(x => x.ResponseBody).HasColumnType("jsonb");
        });

        b.Entity<Service>(e =>
        {
            e.ToTable("services");
            e.HasKey(x => x.ServiceId);
        });

        b.Entity<DoctorCommissionRate>(e =>
        {
            e.ToTable("doctor_commission_rates");
            e.HasKey(x => x.RateId);
        });

        b.Entity<DoctorScheduleTemplate>(e =>
        {
            e.ToTable("doctor_schedule_templates");
            e.HasKey(x => x.SessionTemplateId);
        });

        b.Entity<DoctorSession>(e =>
        {
            e.ToTable("doctor_sessions");
            e.HasKey(x => x.SessionId);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.LogId);
            e.Property(x => x.LogId).ValueGeneratedOnAdd();
            e.Property(x => x.Before).HasColumnType("jsonb");
            e.Property(x => x.After).HasColumnType("jsonb");
            e.Property(x => x.IpAddress).HasColumnType("inet");
        });

        // ------------------------------------------------------------ الجزء الثاني
        b.Entity<PatientAccount>(e => { e.ToTable("patient_accounts"); e.HasKey(x => x.PatientAccountId); });
        b.Entity<PatientAccountLink>(e =>
        {
            e.ToTable("patient_account_links");
            e.HasKey(x => new { x.PatientAccountId, x.PatientId });
            e.HasOne<PatientAccount>().WithMany().HasForeignKey(x => x.PatientAccountId);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId);
        });
        b.Entity<OtpCode>(e => { e.ToTable("otp_codes"); e.HasKey(x => x.OtpId); });
        b.Entity<Notification>(e => { e.ToTable("notifications"); e.HasKey(x => x.NotificationId); });
        b.Entity<PushDevice>(e => { e.ToTable("push_devices"); e.HasKey(x => x.PushDeviceId); });
        b.Entity<OutboundMessage>(e => { e.ToTable("outbound_messages"); e.HasKey(x => x.MessageId); e.Property(x => x.Data).HasColumnType("jsonb"); });
        b.Entity<BackupRun>(e => { e.ToTable("backup_runs"); e.HasKey(x => x.RunId); });
        b.Entity<Consultation>(e =>
        {
            e.ToTable("consultations");
            e.HasKey(x => x.ConsultationId);
            e.Property(x => x.Icd10Code).HasColumnName("icd10_code");
        });
        b.Entity<ConsultationAddendum>(e =>
        {
            e.ToTable("consultation_addenda");
            e.HasKey(x => x.AddendumId);
            e.HasOne<Consultation>().WithMany().HasForeignKey(x => x.ConsultationId);
        });
        b.Entity<Prescription>(e =>
        {
            e.ToTable("prescriptions");
            e.HasKey(x => x.PrescriptionId);
            e.HasOne<Consultation>().WithMany().HasForeignKey(x => x.ConsultationId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PrescriptionId);
        });
        b.Entity<PrescriptionLine>(e => { e.ToTable("prescription_lines"); e.HasKey(x => new { x.PrescriptionId, x.LineNo }); });
        b.Entity<StaffPayPlan>(e => { e.ToTable("staff_pay_plans"); e.HasKey(x => x.PlanId); });
        b.Entity<Payout>(e => { e.ToTable("payouts"); e.HasKey(x => x.PayoutId); });
        b.Entity<Referral>(e =>
        {
            e.ToTable("referrals");
            e.HasKey(x => x.ReferralId);
            e.HasOne<MedicalOrder>().WithMany().HasForeignKey(x => x.OrderItemId);
            e.HasOne<Prescription>().WithMany().HasForeignKey(x => x.PrescriptionId);
        });
        b.Entity<MedicalOrder>(e =>
        {
            e.ToTable("medical_orders");
            e.HasKey(x => x.OrderItemId);
            e.HasOne<Consultation>().WithMany().HasForeignKey(x => x.ConsultationId);
            e.HasOne<InvoiceItem>().WithMany().HasForeignKey(x => x.InvoiceItemId);
        });
        b.Entity<LabTestParameter>(e =>
        {
            e.ToTable("lab_test_parameters");
            e.HasKey(x => x.ParameterId);
            e.Property(x => x.RefLow).HasPrecision(12, 4);
            e.Property(x => x.RefHigh).HasPrecision(12, 4);
            e.Property(x => x.CriticalLow).HasPrecision(12, 4);
            e.Property(x => x.CriticalHigh).HasPrecision(12, 4);
        });
        b.Entity<LabResult>(e =>
        {
            e.ToTable("lab_results");
            e.HasKey(x => x.OrderItemId);
            e.HasMany(x => x.Values).WithOne().HasForeignKey(x => x.OrderItemId);
        });
        b.Entity<LabResultValue>(e =>
        {
            e.ToTable("lab_result_values");
            e.HasKey(x => new { x.OrderItemId, x.ParameterId });
            e.Property(x => x.Value).HasPrecision(12, 4);
        });
        b.Entity<RadiologyReport>(e => { e.ToTable("radiology_reports"); e.HasKey(x => x.OrderItemId); });
        b.Entity<OnlinePayment>(e =>
        {
            e.ToTable("online_payments");
            e.HasKey(x => x.OnlinePaymentId);
            e.Property(x => x.RawCallback).HasColumnType("jsonb");
            e.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId);
        });
        b.Entity<ReceiptBlock>(e => { e.ToTable("receipt_blocks"); e.HasKey(x => x.BlockId); });
        b.Entity<SyncOperation>(e =>
        {
            e.ToTable("sync_operations");
            e.HasKey(x => x.OpId);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnType("jsonb");
        });
        b.Entity<AgentEvent>(e => { e.ToTable("agent_events"); e.HasKey(x => x.EventId); });
        b.Entity<FastForwardOffer>(e => { e.ToTable("fast_forward_offers"); e.HasKey(x => x.OfferId); });
        b.Entity<FastForwardRecipient>(e =>
        {
            e.ToTable("fast_forward_offer_recipients");
            e.HasKey(x => new { x.OfferId, x.BookingId });
            e.HasOne<FastForwardOffer>().WithMany().HasForeignKey(x => x.OfferId);
        });
    }
}
