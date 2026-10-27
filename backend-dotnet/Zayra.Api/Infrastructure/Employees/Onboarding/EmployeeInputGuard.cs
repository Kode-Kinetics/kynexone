using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Scope;
using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <inheritdoc cref="IEmployeeInputGuard"/>
public sealed class EmployeeInputGuard : IEmployeeInputGuard
{
    public const string OverridePermission = "employees.override_validation";
    private const int ReasonMin = 10, ReasonMax = 500;

    private readonly ZayraDbContext _db;
    private readonly IRequestEntityScopeResolver? _scope;
    private readonly ITenantClock? _clock;
    private readonly IDataScopeService? _dataScope;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? _http;
    // The last request this guard returned. Handing that same instance back in (the draft autosave path enforces
    // as a draft, then the service enforces as an update) is not validated twice.
    private EmployeeCreateRequest? _lastEnforced;

    public EmployeeInputGuard(ZayraDbContext db, IRequestEntityScopeResolver? scope = null, ITenantClock? clock = null,
        IDataScopeService? dataScope = null, Microsoft.AspNetCore.Http.IHttpContextAccessor? http = null)
    {
        _db = db; _scope = scope; _clock = clock; _dataScope = dataScope; _http = http;
    }

    public Task<EmployeeCreateRequest> EnforceCreateAsync(Guid tenantId, EmployeeCreateRequest request, RequestContext context, CancellationToken ct) =>
        RunAsync(tenantId, null, request, EmployeeGuardMode.Create, context, ct);

    public Task<EmployeeCreateRequest> EnforceUpdateAsync(Guid tenantId, int employeeId, EmployeeCreateRequest request, RequestContext context, CancellationToken ct) =>
        RunAsync(tenantId, employeeId, request, EmployeeGuardMode.Update, context, ct);

    public Task<EmployeeCreateRequest> EnforceDraftAsync(Guid tenantId, int? employeeId, EmployeeCreateRequest request, RequestContext context, CancellationToken ct) =>
        RunAsync(tenantId, employeeId, request, EmployeeGuardMode.Draft, context, ct);

    public async Task EnforceActivationAsync(Guid tenantId, int employeeId, RequestContext context, CancellationToken ct,
        IReadOnlyCollection<AcknowledgedWarning>? acknowledged = null)
    {
        var errors = await EmployeeStoredDataCheck.ErrorsAsync(_db, tenantId, employeeId, _clock, ct);
        if (errors.Count > 0)
            throw new EmployeeValidationException(400, EmployeeProblemCodes.ValidationFailed,
                errors.ToDictionary(k => k.Key, k => k.Value.ToArray()),
                "Some fields need attention before activation: " + string.Join(", ", errors.Keys) + ".");
    }

    private async Task<EmployeeCreateRequest> RunAsync(
        Guid tenantId, int? employeeId, EmployeeCreateRequest request, EmployeeGuardMode mode, RequestContext context, CancellationToken ct)
    {
        if (ReferenceEquals(request, _lastEnforced)) return request;
        var scope = _scope?.Resolve().ToEntityScopeContext() ?? EntityScopeContext.GroupLevel;
        var refs = new EmployeeReferenceValidator(_db);
        var mayOverride = context.Permissions?.Contains(OverridePermission) == true;
        var acks = (request.AcknowledgedWarnings ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Code))
            .GroupBy(a => a.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // The employing company fixes the jurisdiction every other rule depends on.
        var companyForRules = request.CompanyId;
        if (companyForRules is null && mode == EmployeeGuardMode.Update && employeeId is int id)
            companyForRules = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, "Existing employee's company for validation; tenant re-applied.")
                .AsNoTracking().Where(e => e.Id == id).Select(e => e.CompanyId).FirstOrDefaultAsync(ct);
        var forRefs = request with { CompanyId = companyForRules };
        Models.Company? company = null;
        if (!(mode == EmployeeGuardMode.Draft && companyForRules is null))
            company = await refs.ResolveCompanyAsync(tenantId, companyForRules, scope, ct);

        var country = company?.CountryCode;
        var tz = OnboardingClock.ZoneFor(country);
        var today = _clock is not null ? await _clock.TodayAsync(tenantId, ct) : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        var result = EmployeeInputValidator.Validate(request, new EmployeeInputValidationContext(country, today, mode, tz));

        var consumed = new Dictionary<string, AcknowledgedWarning>(StringComparer.Ordinal);
        // CD5: a checksum failure is waivable by an authorised caller with a reason; a format failure never is.
        foreach (var (field, codes) in result.Errors.ToList())
            if (codes.Remove(EmployeeWarningCodes.IdChecksumInvalid))
            {
                if (acks.TryGetValue(EmployeeWarningCodes.IdChecksumInvalid, out var ack))
                {
                    RequireAllowed(mayOverride); RequireReason(ack);
                    consumed[EmployeeWarningCodes.IdChecksumInvalid] = ack;
                }
                else codes.Add(EmployeeWarningCodes.IdChecksumInvalid);
                if (codes.Count == 0) result.Errors.Remove(field);
            }
        if (result.Errors.Count > 0)
            throw new EmployeeValidationException(400, EmployeeProblemCodes.ValidationFailed,
                result.Errors.ToDictionary(k => k.Key, k => k.Value.ToArray()),
                "Some fields need attention: " + string.Join(", ", result.Errors.Keys) + ".");

        // A manager the caller may not see answers exactly like a manager that does not exist.
        Func<int, bool>? canAccessEmployee = null;
        if (_dataScope is not null && _http?.HttpContext?.User is { Identity.IsAuthenticated: true } caller)
            canAccessEmployee = (await _dataScope.ResolveAsync(caller, tenantId, ct)).CanAccessEmployee;
        var refResult = await refs.ValidateAsync(tenantId, forRefs, scope, employeeId, ct, canAccessEmployee);
        var warnings = result.Warnings.Concat(refResult.Warnings).ToList();

        var unacknowledged = new List<EmployeeWarning>();
        foreach (var w in warnings)
        {
            if (!acks.TryGetValue(w.Code, out var ack)) { unacknowledged.Add(w); continue; }
            RequireReason(ack);
            consumed[w.Code] = ack;
            if (w.RequiresPermission) RequireAllowed(mayOverride);
        }
        if (unacknowledged.Count > 0) throw new EmployeeWarningsRequireAckException(unacknowledged);

        // Only the acknowledgements that were actually needed travel on, so the audit row lists real overrides.
        // The grade band is also checked by the employee service against the MERGED pay package (the request's figures
        // plus the grade's pay scale), which the guard cannot see; an acknowledgement for it must survive to there.
        if (acks.TryGetValue(EmployeeWarningCodes.GradeBandOutOfRange, out var bandAck))
        {
            RequireReason(bandAck);
            consumed[EmployeeWarningCodes.GradeBandOutOfRange] = bandAck;
        }
        var normalised = result.Normalised with { AcknowledgedWarnings = consumed.Values.ToList() };
        normalised = await ApplyEligibilityDefaultsAsync(tenantId, employeeId, normalised, mode, country, ct);
        if (request.CompanyId is null && mode != EmployeeGuardMode.Update) normalised = normalised with { CompanyId = refResult.Company.Id };
        _lastEnforced = normalised;
        return normalised;
    }

    // CD30: wps/eosb come from the employing company's country when the caller did not state them. A create with no
    // payroll block still gets one, so the flags never depend on an IBAN or MOL id having been typed. An update keeps
    // whatever the stored profile says; a hire that has no profile row yet gets the country default.
    private async Task<EmployeeCreateRequest> ApplyEligibilityDefaultsAsync(
        Guid tenantId, int? employeeId, EmployeeCreateRequest r, EmployeeGuardMode mode, string? country, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(country)) return r;
        var p = r.PayrollProfile;
        if (p is { WpsEligible: not null, EosbEligible: not null }) return r;
        if (mode != EmployeeGuardMode.Create && employeeId is int id
            && await _db.EmployeePayrollProfiles.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == id && !x.IsDeleted, ct))
            return r;
        if (p is null && mode != EmployeeGuardMode.Create) return r;
        var iso = CountryCodeStandard.NormalizeToIso2(country) ?? country;
        var (wps, eosb) = PayrollEligibilityPolicy.DefaultsFor(iso);
        p ??= new EmployeePayrollProfileRequest(null, null, null, null, null, null, null, null, null, null, null, null);
        return r with { PayrollProfile = p with { WpsEligible = p.WpsEligible ?? wps, EosbEligible = p.EosbEligible ?? eosb } };
    }

    private static void RequireReason(AcknowledgedWarning ack)
    {
        var len = ack.Reason?.Trim().Length ?? 0;
        if (len < ReasonMin || len > ReasonMax)
            throw new EmployeeValidationException(422, EmployeeProblemCodes.OverrideReasonRequired,
                new Dictionary<string, string[]> { ["acknowledgedWarnings"] = [EmployeeProblemCodes.OverrideReasonRequired] },
                $"A reason of {ReasonMin} to {ReasonMax} characters is required to override this.");
    }

    private static void RequireAllowed(bool mayOverride)
    {
        if (!mayOverride)
            throw new EmployeeValidationException(422, EmployeeProblemCodes.OverrideNotPermitted,
                new Dictionary<string, string[]> { ["acknowledgedWarnings"] = [EmployeeProblemCodes.OverrideNotPermitted] },
                "You do not have permission to override this check.");
    }
}
