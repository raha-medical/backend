using AlRahma.Application.Auth;
using AlRahma.Application.Branches;
using AlRahma.Application.Billing;
using AlRahma.Application.Bookings;
using AlRahma.Application.Common;
using AlRahma.Application.Devices;
using AlRahma.Application.Doctors;
using AlRahma.Application.Patients;
using AlRahma.Application.Queue;
using AlRahma.Application.Files;
using AlRahma.Application.Lookups;
using AlRahma.Application.Services;
using AlRahma.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace AlRahma.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<Security.AccessCache>();
        services.AddScoped<Security.RoleService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<BranchService>();
        services.AddScoped<ClosureService>();
        services.AddScoped<BranchPortalService>();
        services.AddScoped<Reports.BranchSettlementService>();
        services.AddScoped<Reports.ReportScope>();
        services.AddScoped<Reports.RevenueService>();
        services.AddScoped<Reports.DoctorReportService>();
        services.AddScoped<Reports.DashboardService>();
        services.AddScoped<Reports.StatsService>();
        services.AddScoped<Reports.EntitlementService>();
        services.AddScoped<Reports.AuditLogService>();
        services.AddScoped<DeferralService>();
        services.AddScoped<ExpenseService>();
        services.AddScoped<BranchSettingsService>();
        services.AddScoped<LookupService>();
        services.AddScoped<ServiceCatalog>();
        services.AddScoped<DoctorService>();
        services.AddScoped<FileService>();
        services.AddScoped<SettingsReader>();
        services.AddScoped<NumberSequences>();
        services.AddScoped<OperationClock>();
        services.AddScoped<BanService>();
        services.AddScoped<PatientService>();
        services.AddScoped<QueueEngine>();
        services.AddScoped<QueueReader>();
        services.AddScoped<QueueNotifier>();
        services.AddScoped<QueueService>();
        services.AddSingleton<CheckInTokenService>();
        services.AddScoped<ReceptionService>();
        services.AddScoped<BookingService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<FinanceCore>();
        services.AddScoped<ShiftService>();
        services.AddScoped<Support.SupportService>();
        services.AddScoped<DoctorCancelOptionsService>();
        services.AddScoped<CurrencyService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<RefundService>();
        services.AddScoped<Notifications.NotificationService>();
        services.AddScoped<Notifications.ScheduledJobs>();
        services.AddScoped<Backups.BackupService>();
        services.AddScoped<PatientApp.PatientAuthService>();
        services.AddScoped<PatientApp.PatientPortalService>();
        services.AddScoped<PatientApp.FastForwardService>();
        services.AddScoped<PatientApp.MyQueueService>();
        services.AddScoped<PatientApp.PatientFileService>();
        services.AddScoped<Assistant.AssistantTools>();
        services.AddScoped<Assistant.AssistantService>();
        services.AddScoped<PatientApp.OnlinePaymentService>();
        // طبقة الدفع الموحدة (3.5): التنفيذ يُختار بالإعداد Payments__Providers__{name}__Mode. البوابة الحقيقية تُسجَّل هنا بـ Mode = "live".
        services.AddScoped<PatientApp.Gateways.IPaymentGateway, PatientApp.Gateways.ZainCashMockGateway>();
        services.AddScoped<PatientApp.Gateways.IPaymentGateway, PatientApp.Gateways.QiCardMockGateway>();
        services.AddScoped<PatientApp.Gateways.PaymentGatewayRegistry>();
        services.AddScoped<PatientApp.Gateways.MockGatewayService>();
        services.AddScoped<Clinical.ReferralTracker>();
        services.AddScoped<Clinical.ReferralService>();
        services.AddScoped<Clinical.MedicalOrderSync>();
        services.AddScoped<Clinical.OrderBilling>();
        services.AddScoped<Clinical.ConsultationService>();
        services.AddScoped<Clinical.DepartmentCore>();
        services.AddScoped<Clinical.LabService>();
        services.AddScoped<Clinical.RadiologyService>();
        services.AddScoped<Clinical.PharmacyService>();
        services.AddScoped<Clinical.PharmacyBillingService>();
        services.AddScoped<Offline.SyncService>();
        return services;
    }
}
