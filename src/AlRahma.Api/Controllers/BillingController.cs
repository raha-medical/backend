using AlRahma.Api.Infrastructure;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Billing;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>الفواتير والتحصيل والإيصالات والاسترجاع (الأقسام 8 و 9 من العقد).</summary>
[ApiController]
[Produces("application/json")]
[Authorize]
public class BillingController(PaymentService payments, InvoiceService invoices, RefundService refunds, ReceiptService receipts) : ControllerBase
{
    /// <summary>السلة الموحدة: كل بنود المريض غير المدفوعة في الفرع، وبنود القسم المحدد أولاً.</summary>
    [HttpGet("api/v1/patients/{patientId:guid}/billable-items")]
    [Authorize(Policy = Policies.BillingRead)]
    public Task<BillableItemsDto> BillableItems(Guid patientId, [FromQuery(Name = "department")] string? department, CancellationToken ct) =>
        payments.BillableItemsAsync(patientId, department, ct);

    [HttpPost("api/v1/payments")]
    [Authorize(Policy = Permissions.PaymentsCollect)]
    [Idempotent]
    [ProducesResponseType<PaymentResponse>(201)]
    public async Task<IActionResult> Collect(CollectPaymentRequest req, CancellationToken ct) =>
        StatusCode(201, await payments.CollectAsync(req, ct));

    [HttpPost("api/v1/invoices")]
    [Authorize(Policy = Permissions.PaymentsCollect)]
    [Idempotent]
    [ProducesResponseType<InvoiceDto>(201)]
    public async Task<IActionResult> CreateInvoice(CreateInvoiceRequest req, CancellationToken ct) =>
        StatusCode(201, await invoices.CreateAsync(req, ct));

    [HttpGet("api/v1/invoices")]
    [Authorize(Policy = Policies.BillingRead)]
    public Task<PagedResult<InvoiceListItem>> Invoices(
        [FromQuery(Name = "patient_id")] Guid? patientId,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        invoices.ListAsync(new InvoiceListQuery(patientId, status, from, to, q, page, pageSize), ct);

    [HttpGet("api/v1/invoices/{invoiceId:guid}")]
    [Authorize(Policy = Policies.BillingRead)]
    public Task<InvoiceDto> Invoice(Guid invoiceId, CancellationToken ct) => invoices.GetAsync(invoiceId, ct);

    [HttpPost("api/v1/invoices/{invoiceId:guid}/void")]
    [Authorize(Policy = Permissions.RefundsApprove)]
    public Task<InvoiceDto> Void(Guid invoiceId, VoidInvoiceRequest req, CancellationToken ct) => invoices.VoidAsync(invoiceId, req, ct);

    [HttpPost("api/v1/invoice-items/{invoiceItemId:guid}/refunds")]
    [Authorize(Policy = Policies.RefundsRequest)]
    [Idempotent]
    [ProducesResponseType<RefundDto>(201)]
    public async Task<IActionResult> Refund(Guid invoiceItemId, CreateRefundRequest req, CancellationToken ct) =>
        StatusCode(201, await refunds.RequestAsync(invoiceItemId, req, ct));

    /// <summary>طلبات الاسترجاع. status=pending_approval لقائمة الموافقات.</summary>
    [HttpGet("api/v1/refunds")]
    [Authorize(Policy = Policies.RefundsRequest)]
    public Task<PagedResult<RefundDto>> Refunds(
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        refunds.ListAsync(new RefundListQuery(status, page, pageSize), ct);

    [HttpGet("api/v1/refunds/{refundId:guid}")]
    [Authorize(Policy = Policies.RefundsRequest)]
    public Task<RefundDto> GetRefund(Guid refundId, CancellationToken ct) => refunds.GetAsync(refundId, ct);

    [HttpPost("api/v1/refunds/{refundId:guid}/approve")]
    [Authorize(Policy = Permissions.RefundsApprove)]
    public Task<RefundDto> Approve(Guid refundId, CancellationToken ct) => refunds.ApproveAsync(refundId, ct);

    [HttpPost("api/v1/refunds/{refundId:guid}/reject")]
    [Authorize(Policy = Permissions.RefundsApprove)]
    public Task<RefundDto> Reject(Guid refundId, RejectRefundRequest req, CancellationToken ct) => refunds.RejectAsync(refundId, req, ct);

    [HttpGet("api/v1/receipts/{receiptId:guid}/print-payload")]
    [Authorize(Policy = Policies.BillingRead)]
    public Task<PrintPayloadDto> PrintPayload(Guid receiptId, [FromQuery(Name = "copy")] int? copy, CancellationToken ct) =>
        receipts.PrintPayloadAsync(receiptId, copy, ct);

    [HttpPost("api/v1/receipts/{receiptId:guid}/reprint")]
    [Authorize(Policy = Policies.BillingRead)]
    public Task<ReprintResponse> Reprint(Guid receiptId, CancellationToken ct) => receipts.ReprintAsync(receiptId, ct);

    /// <summary>إيصالات الوردية (الافتراضي ورديتي المفتوحة) لإعادة الطباعة.</summary>
    [HttpGet("api/v1/receipts")]
    [Authorize(Policy = Policies.BillingRead)]
    public async Task<object> ShiftReceipts([FromQuery(Name = "shift_id")] Guid? shiftId, CancellationToken ct) =>
        new { items = await receipts.ShiftReceiptsAsync(shiftId, ct) };

    /// <summary>التحقق العام من الإيصال عبر مسح الـ QR. لا يعرض بيانات المريض.</summary>
    [HttpGet("api/v1/public/receipts/verify/{code}")]
    [AllowAnonymous]
    public Task<VerifyReceiptDto> Verify(string code, CancellationToken ct) => receipts.VerifyAsync(code, ct);

    /// <summary>المفتاح العام (PEM) للتحقق من توكن فتح الدرج في الوكيل المحلي.</summary>
    [HttpGet("api/v1/agent/drawer-public-key")]
    [AllowAnonymous]
    public IActionResult DrawerPublicKey([FromServices] IDrawerTokenIssuer drawer) =>
        Ok(new { algorithm = "ES256", key_id = drawer.PublicKeyId, public_key_pem = drawer.PublicKeyPem });
}
