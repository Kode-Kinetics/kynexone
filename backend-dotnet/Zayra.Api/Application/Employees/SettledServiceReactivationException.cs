namespace Zayra.Api.Application.Employees;

/// <summary>
/// A status change would return an employee to work over service a final settlement has already closed. The status route keeps the
/// joining date, so the settled service would be counted again: the provision would skip the person for the whole new tenure and the
/// next exit's end-of-service award would start from the original date. Only Rehire moves the joining date, so the refusal points
/// there. Derives from <see cref="InvalidOperationException"/> so every existing caller still refuses it; the status endpoints map it
/// to 409 with both languages.
/// </summary>
public sealed class SettledServiceReactivationException : InvalidOperationException
{
    public const string Code = "rehire_required";

    public SettledServiceReactivationException(DateOnly settledThrough)
        : base($"This employee's final settlement for service up to {settledThrough:yyyy-MM-dd} is approved or paid. "
               + "Changing the status back would count that settled service again. Use Rehire to start a new service period "
               + "from the return date (Rehire is available once the settlement is paid).")
    {
        SettledThrough = settledThrough;
        MessageAr = $"تمت الموافقة على التسوية النهائية لهذا الموظف أو صرفها عن الخدمة حتى {settledThrough:yyyy-MM-dd}. "
                    + "إعادة حالته ستحتسب تلك الخدمة المسددة مرة أخرى. استخدم إعادة التعيين لبدء فترة خدمة جديدة من تاريخ العودة "
                    + "(تتاح إعادة التعيين بعد صرف التسوية).";
    }

    public DateOnly SettledThrough { get; }
    public string MessageAr { get; }
}
