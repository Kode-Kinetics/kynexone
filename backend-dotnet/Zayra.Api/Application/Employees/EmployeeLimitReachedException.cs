namespace Zayra.Api.Application.Employees;

/// <summary>
/// A status change would bring one more person into a status the plan's employee limit counts, and the limit is already reached.
/// Derives from <see cref="InvalidOperationException"/> so every caller that does not know it still refuses the change; the status
/// endpoints map it to 402 with both languages, the same body creating an employee returns.
/// </summary>
public sealed class EmployeeLimitReachedException : InvalidOperationException
{
    public const string Code = "employee_limit_reached";

    public EmployeeLimitReachedException(int count, int max)
        : base($"Your plan allows up to {max} active employees. You have {count}. Upgrade your plan to add more.")
    {
        Count = count;
        Max = max;
        MessageAr = $"تتيح باقتك حتى {max} موظفاً نشطاً، ولديك حالياً {count}. قم بترقية الباقة لإضافة المزيد.";
    }

    public int Count { get; }
    public int Max { get; }
    public string MessageAr { get; }
}
