namespace AlRahma.Application.Common;

/// <summary>خطأ أعمال يتحول إلى Problem Details برمز HTTP ورمز خطأ ثابت (انظر الملحق أ في عقود الـ API).</summary>
public class AppException(int status, string code, string title, string? detail = null) : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public IDictionary<string, string[]>? Errors { get; init; }
    public IDictionary<string, object?>? Extensions { get; init; }

    public static AppException Validation(IDictionary<string, string[]> errors) =>
        new(400, "VALIDATION_ERROR", "خطأ في البيانات المرسلة") { Errors = errors };

    public static AppException NotFound(string what) =>
        new(404, "NOT_FOUND", "المورد غير موجود", what);

    public static AppException Forbidden(string? detail = null) =>
        new(403, "FORBIDDEN", "لا تملك صلاحية لهذه العملية", detail);
}

/// <summary>يجمع أخطاء الحقول ثم يرميها مرة واحدة.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public Validator Check(bool ok, string field, string message)
    {
        if (!ok)
        {
            if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
            list.Add(message);
        }
        return this;
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
            throw AppException.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public record PageQuery(int Page = 1, int PageSize = 20)
{
    public int SafePage => Page < 1 ? 1 : Page;
    public int SafePageSize => PageSize is < 1 ? 20 : Math.Min(PageSize, 100);
}
