using Zayra.Api.Application.Auth;

namespace Zayra.Api.Application.Employees.Onboarding;

/// <summary>One warning the caller chose to override. <see cref="Reason"/> must be 10-500 characters.</summary>
public sealed record AcknowledgedWarning(string Code, string? Reason);

/// <summary>A statutory-floor warning (never a data-integrity failure). Shape of one entry of the 409 body.</summary>
public sealed record EmployeeWarning(string Code, string Field, string Message, bool RequiresPermission = false);

/// <summary>
/// A refused request. <see cref="Status"/> is 400 (data integrity, <c>validation_failed</c>) or 422 (business
/// refusal). Never surfaces as a 500: the problem filter maps it to RFC 7807 <c>application/problem+json</c>.
/// <see cref="Errors"/> is <c>{ "camelCaseField": ["code", ...] }</c>; <see cref="Detail"/> is an English fallback.
/// </summary>
public sealed class EmployeeValidationException : Exception
{
    public EmployeeValidationException(
        int status, string code, IReadOnlyDictionary<string, string[]> errors, string detail, string? field = null)
        : base(detail)
    {
        Status = status;
        Code = code;
        Errors = errors;
        Detail = detail;
        Field = field;
    }

    public int Status { get; }
    public string Code { get; }
    public IReadOnlyDictionary<string, string[]> Errors { get; }
    public string Detail { get; }
    /// <summary>Set for <c>invalid_reference</c>: the request field whose value was refused.</summary>
    public string? Field { get; }
}

/// <summary>Statutory floors tripped and not acknowledged: 409 <c>warnings_require_acknowledgement</c>.</summary>
public sealed class EmployeeWarningsRequireAckException : Exception
{
    public EmployeeWarningsRequireAckException(IReadOnlyList<EmployeeWarning> warnings)
        : base("This request needs an acknowledgement for: " + string.Join(", ", warnings.Select(w => w.Code)))
    {
        Warnings = warnings;
    }

    public IReadOnlyList<EmployeeWarning> Warnings { get; }
}

/// <summary>How strict the guard is. Draft floor = English name + employing company (CD1).</summary>
public enum EmployeeGuardMode { Create, Update, Draft }

/// <summary>
/// The single entry point every employee writer calls before persisting: validates, checks every foreign
/// reference against the caller's tenant and scope, applies warning acknowledgements, and returns a
/// NORMALISED request (ISO-2 nationality, E.164 phone, ASCII digits, country derived from the company).
/// Throws <see cref="EmployeeValidationException"/> or <see cref="EmployeeWarningsRequireAckException"/>.
/// </summary>
public interface IEmployeeInputGuard
{
    Task<EmployeeCreateRequest> EnforceCreateAsync(
        Guid tenantId, EmployeeCreateRequest request, RequestContext context, CancellationToken ct);

    Task<EmployeeCreateRequest> EnforceUpdateAsync(
        Guid tenantId, int employeeId, EmployeeCreateRequest request, RequestContext context, CancellationToken ct);

    Task<EmployeeCreateRequest> EnforceDraftAsync(
        Guid tenantId, int? employeeId, EmployeeCreateRequest request, RequestContext context, CancellationToken ct);
}
