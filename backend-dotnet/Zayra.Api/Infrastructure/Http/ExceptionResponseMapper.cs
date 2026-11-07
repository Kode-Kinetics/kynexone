using Microsoft.AspNetCore.Http;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// The status, stable code and message the global exception handler answers an unhandled exception with. Lives here (not inline in
/// Program.cs) so the mapping is unit-tested: a refusal that carries a stable code must not collapse into a bare 400 or a 500.
/// </summary>
public static class ExceptionResponseMapper
{
    /// <summary>Stable-code prefixes of the company write guard (ZayraDbContext): "code: explanation". Answered as 409 with that code.</summary>
    public static readonly string[] CompanyGuardCodes = ["company_scope_required", "company_reassignment_blocked"];

    public static (int StatusCode, string Code, string Message) Map(Exception? ex) => ex switch
    {
        // POD-B1 — a GL post into a closed period surfaces as a typed 422 gl_period_closed.
        Zayra.Api.Infrastructure.Payroll.PeriodClosedException closed => (StatusCodes.Status422UnprocessableEntity, "gl_period_closed", closed.Message),
        // An end-of-service wage that would silently leave out a salary line whose allowance definition is gone: refused, never understated.
        Zayra.Api.Infrastructure.Payroll.CashAllowanceDefinitionMissingException orphan =>
            (StatusCodes.Status422UnprocessableEntity, "cash_allowance_definition_invalid", orphan.Message),
        // A pay reduction with no written-agreement reference that reached the global handler: the same stable code the salary endpoints answer with.
        Zayra.Api.Infrastructure.Payroll.SalaryReductionConsentRequiredException consent =>
            (StatusCodes.Status400BadRequest, consent.Code, consent.Message),
        // Field encryption: the tenant's keys are revoked (423) or temporarily unavailable (503) - never a 500, never key detail.
        Zayra.Api.Infrastructure.FieldEncryption.FieldEncryptionUnavailableException fe => (fe.StatusCode, fe.Code, fe.SafeMessage),
        // Another writer saved the same organisation code first (the unique index refused this save): a conflict, not a fault.
        Zayra.Api.Application.Organization.OrgCodeConflictException taken => (StatusCodes.Status409Conflict, Zayra.Api.Application.Organization.OrgCodeConflictException.ErrorCode, taken.Message),
        UnauthorizedAccessException unauthorized => (StatusCodes.Status403Forbidden, "forbidden", unauthorized.Message),
        InvalidOperationException guard when CompanyGuardCodes.Any(c => guard.Message.StartsWith(c + ":", StringComparison.Ordinal))
            => (StatusCodes.Status409Conflict, guard.Message[..guard.Message.IndexOf(':')], guard.Message),
        InvalidOperationException invalid => (StatusCodes.Status400BadRequest, "bad_request", invalid.Message),
        _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred. Quote your traceId when contacting support."),
    };
}
