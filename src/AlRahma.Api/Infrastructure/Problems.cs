using System.Text.Json;
using AlRahma.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Infrastructure;

/// <summary>صيغة الأخطاء الموحدة (RFC 7807) كما في القسم 0.6 من عقود الـ API.</summary>
public static class Problems
{
    public const string TypeBase = "https://docs.alrahma.local/errors/";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
    };

    public static ProblemDetails Create(HttpContext ctx, int status, string code, string title, string? detail = null,
        IDictionary<string, string[]>? errors = null, IDictionary<string, object?>? extensions = null)
    {
        var p = new ProblemDetails
        {
            Type = TypeBase + code,
            Title = title,
            Status = status,
            Detail = detail,
        };
        p.Extensions["code"] = code;
        p.Extensions["trace_id"] = ctx.TraceIdentifier;
        if (errors is not null) p.Extensions["errors"] = errors;
        if (extensions is not null)
            foreach (var (k, v) in extensions) p.Extensions[k] = v;
        return p;
    }

    public static Task WriteAsync(HttpContext ctx, ProblemDetails p)
    {
        ctx.Response.StatusCode = p.Status ?? 500;
        ctx.Response.ContentType = "application/problem+json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(p, Json));
    }

    public static Task WriteAsync(HttpContext ctx, int status, string code, string title, string? detail = null) =>
        WriteAsync(ctx, Create(ctx, status, code, title, detail));
}

public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (AppException ex) when (!ctx.Response.HasStarted)
        {
            await Problems.WriteAsync(ctx, Problems.Create(ctx, ex.Status, ex.Code, ex.Title, ex.Detail, ex.Errors, ex.Extensions));
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
        {
            log.LogError(ex, "Unhandled error {TraceId}", ctx.TraceIdentifier);
            await Problems.WriteAsync(ctx, 500, "INTERNAL_ERROR", "حدث خطأ غير متوقع",
                "تم تسجيل الخطأ. أعد المحاولة أو تواصل مع الدعم مع رقم التتبع.");
        }
    }
}
