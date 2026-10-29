using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Employees;

/// <summary>
/// OD4: the scoped new-hire pay approval view. A holder of <c>payroll.approve</c> (the seeded Finance Approver does not hold
/// <c>employees.sensitive</c>) lists the pay packages they may approve and opens ONE package showing only what the decision
/// needs: name, code, company, designation, the salary components, the bank name, the account's last four characters, the
/// person who entered it, and an opaque fingerprint. Nothing else about the employee is reachable from here (no national ID,
/// Iqama, passport, date of birth or full account number), and the approval binds to the fingerprint that was shown.
/// A record outside the caller's tenant or company answers exactly like a missing one (404).
/// </summary>
[ApiController]
[Route("api/pay-approvals")]
[Authorize]
public sealed class PayApprovalsController : ControllerBase
{
    public const string RejectedAction = "employee.compensation_rejected";
    private const int MaxListed = 100;

    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;

    public PayApprovalsController(ZayraDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public sealed record DecisionRequest(string? Fingerprint, string? Decision, string? Reason, bool OverrideGradeRange = false);

    // ── Queue ────────────────────────────────────────────────────────────────────────────────────
    [HttpGet]
    [HasPermission(CompensationApprovalGate.ApprovePermission)]
    [TenantUserRateLimit("pay-approval-read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var caller = GetUserId();
        if (caller is null) return NotFound();
        var scope = this.GetEntityScope();
        var ids = await _db.EmployeeImportGaps.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null)
            .Select(g => g.EmployeeId).Distinct().ToListAsync(ct);
        var employees = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId,
                "Pay approval queue: candidates are resolved tenant-wide and then filtered by the caller's company scope below.")
            .AsNoTracking().Where(e => ids.Contains(e.Id) && !e.IsDeleted).OrderBy(e => e.Id).ToListAsync(ct);
        var items = new List<object>();
        var hasMore = false;
        foreach (var e in employees)
        {
            if (!scope.CanAccessCompany(e.CompanyId) || IsInPost(e)) continue;
            if (await CompensationApprovalGate.IsBarredAsync(_db, tenantId, e, caller.Value, ct)) continue;
            if (items.Count >= MaxListed) { hasMore = true; break; }
            var view = await BuildAsync(tenantId, e, ct);
            items.Add(new
            {
                employeeId = e.Id,
                employeeCode = e.EmployeeCode,
                name = DisplayName(e),
                companyId = e.CompanyId,
                companyName = view.CompanyName,
                designation = view.Designation,
                currency = view.Package.Currency,
                gross = view.Gross,
                ibanLast4 = view.IbanLast4,
                enteredBy = view.EnteredBy,
                lastChangedAtUtc = view.LastChanged?.AtUtc,
                status = view.Rejection is null ? "pending" : "rejected",
            });
        }
        return Ok(new { items, count = items.Count, hasMore });
    }

    // ── One package ──────────────────────────────────────────────────────────────────────────────
    [HttpGet("{employeeId:int}")]
    [HasPermission(CompensationApprovalGate.ApprovePermission)]
    [TenantUserRateLimit("pay-approval-read")]
    public async Task<IActionResult> Detail(int employeeId, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var caller = GetUserId();
        var employee = await FindInScopeAsync(tenantId, employeeId, ct);
        if (employee is null || caller is null) return NotFound();
        if (!await NeedsApprovalAsync(tenantId, employee, ct)) return NotPending();
        if (await CompensationApprovalGate.IsBarredAsync(_db, tenantId, employee, caller.Value, ct)) return SecondPersonRequired();
        var v = await BuildAsync(tenantId, employee, ct);
        var p = v.Package;
        return Ok(new
        {
            employee = new { id = employee.Id, code = employee.EmployeeCode, name = DisplayName(employee), companyId = employee.CompanyId, companyName = v.CompanyName, designation = v.Designation },
            components = new
            {
                basic = p.Basic, housing = p.Housing, transport = p.Transport, food = p.Food, mobile = p.Mobile, other = p.Other,
                fixedDeduction = p.FixedDeduction, gross = v.Gross, currency = p.Currency,
            },
            bank = new { bankName = v.BankName, ibanLast4 = v.IbanLast4, accountLast4 = v.AccountLast4, accountHolderName = DisplayName(employee) },
            enteredBy = v.EnteredBy,
            lastChangedBy = v.LastChanged,
            fingerprint = Token(tenantId, employee.Id, CompensationApprovalGate.PayFingerprint(p)),
            sharedIbanCount = v.SharedIbanCount,
            lastRejection = v.Rejection,
        });
    }

    // ── Decide ───────────────────────────────────────────────────────────────────────────────────
    [HttpPost("{employeeId:int}/decision")]
    [HasPermission(CompensationApprovalGate.ApprovePermission)]
    [TenantUserRateLimit("pay-approval-decide")]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Decide(int employeeId, [FromBody] DecisionRequest body, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var caller = GetUserId();
        var employee = await FindInScopeAsync(tenantId, employeeId, ct);
        if (employee is null || caller is null) return NotFound();
        if (!await NeedsApprovalAsync(tenantId, employee, ct)) return NotPending();
        if (await CompensationApprovalGate.IsBarredAsync(_db, tenantId, employee, caller.Value, ct)) return SecondPersonRequired();

        var decision = body.Decision?.Trim().ToLowerInvariant();
        if (decision is not ("approve" or "reject")) return Problem(400, "invalid_decision", "Decision must be approve or reject.");
        var reason = body.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason)) return Problem(400, "reason_required", "Add a reason saying what was checked.");
        if (reason.Length > 500) return Problem(400, "reason_too_long", "Keep the reason under 500 characters.");
        if (string.IsNullOrWhiteSpace(body.Fingerprint)) return Problem(400, "fingerprint_required", "Say which package you reviewed (fingerprint).");

        var package = await CompensationApprovalGate.LivePackageAsync(_db, tenantId, employee.Id, ct);
        var live = CompensationApprovalGate.PayFingerprint(package);
        var token = Token(tenantId, employee.Id, live);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(body.Fingerprint.Trim().ToLowerInvariant())))
            return Problem(409, "package_changed", "The pay package changed after you opened it. Reload it and review the new figures.");
        var compactIban = Compact(package.Iban);
        if (compactIban.Length >= 8 && Compact(reason).Contains(compactIban, StringComparison.Ordinal))
            return Problem(400, "reason_contains_account_number", "Do not write the account number in the reason.");

        var context = Context(tenantId);
        // The figures are a pending SalaryChangeRequest when the package is a hire's first: ONE decision path, Payroll's, applies or
        // rejects it (and closes the pay ledger with an approval). This view only adds the scoped read, the fingerprint and the reason.
        if ((await FirstPackageDecision.PendingIdsAsync(_db, tenantId, employee.Id, ct)).Count > 0)
        {
            var packageMeta = new { via = "pay-approval-view", reason, ibanLast4 = Last4(compactIban), packageHash = token, hashes = CompensationApprovalGate.FingerprintForApproval(package.Salary, package.Iban) };
            if (decision == "approve")
            {
                var refusal = await FirstPackageDecision.ApproveAsync(this, tenantId, employee.Id, reason, body.OverrideGradeRange, ct);
                if (refusal is not null) return refusal;
                _audit.Stage(CompensationApprovalGate.ApprovedAction, "Employee", employee.Id.ToString(CultureInfo.InvariantCulture), context, JsonSerializer.Serialize(packageMeta), employee.CompanyId);
                await _db.SaveChangesAsync(ct);
                return Ok(new { decision = "approved" });
            }
            var rejectRefusal = await FirstPackageDecision.RejectAsync(this, tenantId, employee.Id, reason, ct);
            if (rejectRefusal is not null) return rejectRefusal;
        }
        var open = await _db.EmployeeImportGaps
            .Where(g => g.TenantId == tenantId && g.EmployeeId == employee.Id && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null)
            .ToListAsync(ct);
        var meta = new { via = "pay-approval-view", reason, ibanLast4 = Last4(compactIban), packageHash = token, hashes = CompensationApprovalGate.FingerprintForApproval(package.Salary, package.Iban) };

        if (decision == "reject")
        {
            // The gap stays (or is re-opened) so the hire cannot activate; the reason is the audit row HR reads back.
            if (open.Count == 0)
                _db.EmployeeImportGaps.Add(new EmployeeImportGap
                {
                    TenantId = tenantId, CompanyId = employee.CompanyId, ImportBatchId = Guid.Empty, EmployeeId = employee.Id, RowNumber = 0,
                    GapType = CompensationApprovalGate.GapType, GapCategory = "pay", Detail = "Pay package and bank details are awaiting approval by Finance.",
                });
            await _db.SaveChangesAsync(ct);
            await _audit.WriteAsync(RejectedAction, "Employee", employee.Id.ToString(CultureInfo.InvariantCulture), context, JsonSerializer.Serialize(meta), ct);
            return Ok(new { decision = "rejected" });
        }

        var now = DateTime.UtcNow;
        if (open.Count == 0)
        {
            var gap = new EmployeeImportGap
            {
                TenantId = tenantId, CompanyId = employee.CompanyId, ImportBatchId = Guid.Empty, EmployeeId = employee.Id, RowNumber = 0,
                GapType = CompensationApprovalGate.GapType, GapCategory = "pay", Detail = "Pay package and bank details approved by Finance.",
            };
            _db.EmployeeImportGaps.Add(gap);
            open.Add(gap);
        }
        foreach (var g in open)
        {
            g.ResolvedAtUtc = now;
            g.RawValue = CompensationApprovalGate.ApprovedMarker + live;   // bound to exactly the figures that were shown
        }
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync(CompensationApprovalGate.ApprovedAction, "Employee", employee.Id.ToString(CultureInfo.InvariantCulture), context, JsonSerializer.Serialize(meta), ct);
        return Ok(new { decision = "approved" });
    }

    // ── view model ───────────────────────────────────────────────────────────────────────────────
    private sealed record Actor(string Name, DateTime AtUtc);
    private sealed record Rejection(string Reason, string ByName, DateTime AtUtc);
    private sealed record View(CompensationApprovalGate.PayPackage Package, decimal Gross, string BankName, string? IbanLast4, string? AccountLast4,
        string CompanyName, string? Designation, Actor? EnteredBy, Actor? LastChanged, int SharedIbanCount, Rejection? Rejection);

    private async Task<View> BuildAsync(Guid tenantId, Employee employee, CancellationToken ct)
    {
        var package = await CompensationApprovalGate.LivePackageAsync(_db, tenantId, employee.Id, ct);
        var gross = package.Basic + package.Housing + package.Transport + package.Food + package.Mobile + package.Other;
        if (gross == 0m) gross = package.Salary ?? 0m;
        var profile = await _db.EmployeePayrollProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employee.Id && !p.IsDeleted, ct);
        var companyName = employee.CompanyId is Guid cid
            ? await _db.Companies.AsNoTracking().Where(c => c.Id == cid).Select(c => string.IsNullOrEmpty(c.TradeName) ? c.LegalNameEn : c.TradeName).FirstOrDefaultAsync(ct) ?? ""
            : "";
        string? designation = employee.DesignationId is Guid did
            ? await _db.Designations.AsNoTracking().Where(d => d.Id == did).Select(d => d.TitleEn).FirstOrDefaultAsync(ct)
            : null;
        if (string.IsNullOrWhiteSpace(designation)) designation = string.IsNullOrWhiteSpace(employee.JobTitle) ? null : employee.JobTitle;

        var iban = Compact(package.Iban);
        var account = Compact(package.AccountNumber);

        // Who entered it and who last changed it, from the pay-write audit rows since the last approval.
        var since = await _db.EmployeeImportGaps.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.EmployeeId == employee.Id && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc != null)
            .MaxAsync(g => g.ResolvedAtUtc, ct) ?? DateTime.MinValue;
        var entityId = employee.Id.ToString(CultureInfo.InvariantCulture);
        var rows = await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenantId, "Pay approval view: who entered and last changed this employee's pay and any rejection; tenant and entity pinned.")
            .AsNoTracking()
            .Where(a => a.EntityName == "Employee" && a.EntityId == entityId && a.UserId != null && a.CreatedAtUtc > since
                && (a.Action == CompensationApprovalGate.CreatedAction || a.Action == CompensationApprovalGate.WrittenAction || a.Action == RejectedAction))
            .OrderBy(a => a.CreatedAtUtc).Select(a => new { a.Action, UserId = a.UserId!.Value, a.CreatedAtUtc, a.Metadata }).ToListAsync(ct);
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        Actor ToActor(Guid id, DateTime at) => new(names.GetValueOrDefault(id) ?? "", at);
        var writes = rows.Where(r => r.Action != RejectedAction).ToList();
        var entered = writes.FirstOrDefault();
        var last = writes.LastOrDefault();
        Rejection? rejection = null;
        if (rows.LastOrDefault(r => r.Action == RejectedAction) is { } rej && (last is null || rej.CreatedAtUtc >= last.CreatedAtUtc))
            rejection = new Rejection(ReasonOf(rej.Metadata), names.GetValueOrDefault(rej.UserId) ?? "", rej.CreatedAtUtc);

        var shared = 0;
        if (iban.Length > 0)
        {
            // The warning is useful only inside the approver's company scope. A tenant-wide count would reveal that
            // an otherwise invisible sibling company has an employee using the same bank account.
            var sharing = await _db.EmployeePayrollProfiles.AsNoTracking()
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.EmployeeId != employee.Id && p.Iban != "" && p.Iban.Replace(" ", "").ToUpper() == iban)
                .Select(p => p.EmployeeId).Distinct().ToListAsync(ct);
            shared = sharing.Count == 0 ? 0 : await _db.Employees.AsNoTracking()
                .CountAsync(e => e.TenantId == tenantId && sharing.Contains(e.Id) && !e.IsDeleted, ct);
        }
        return new View(package, gross, profile?.BankName ?? "", iban.Length >= 4 ? iban[^4..] : null, account.Length >= 4 ? account[^4..] : null,
            companyName, designation, entered is null ? null : ToActor(entered.UserId, entered.CreatedAtUtc),
            last is null ? null : ToActor(last.UserId, last.CreatedAtUtc), shared, rejection);
    }

    private static string ReasonOf(string? metadata)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadata ?? "{}");
            return doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    // ── rules ────────────────────────────────────────────────────────────────────────────────────
    private static bool IsInPost(Employee e) => Zayra.Api.Application.Organization.EstablishmentOccupancy.IsOccupyingStatus(e.Status);

    /// <summary>A pending package: a hire not yet in post whose stored figures are not the approved ones.</summary>
    private async Task<bool> NeedsApprovalAsync(Guid tenantId, Employee employee, CancellationToken ct) =>
        !IsInPost(employee)
        && (await CompensationApprovalGate.IsOpenAsync(_db, tenantId, employee.Id, ct)
            || await CompensationApprovalGate.HasDriftedFromApprovalAsync(_db, tenantId, employee.Id, ct)
            || await CompensationApprovalGate.CarriesUnapprovedPayAsync(_db, tenantId, employee.Id, ct));

    /// <summary>An opaque, per-employee token for a pay fingerprint: it identifies the reviewed package but is not the stored hash.</summary>
    internal static string Token(Guid tenantId, int employeeId, string fingerprint) =>
        CompensationApprovalGate.Sha256($"payview.v1|{tenantId:N}|{employeeId}|{fingerprint}");

    private static string Compact(string? v) => (v ?? string.Empty).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    private static string? Last4(string compact) => compact.Length >= 4 ? compact[^4..] : null;
    private static string DisplayName(Employee e) => string.IsNullOrWhiteSpace(e.EnglishName) ? e.FullName : e.EnglishName;

    private IActionResult SecondPersonRequired() => StatusCode(403, new
    {
        error = "second_person_required",
        message = "A pay package must be approved by someone other than the person who entered it, the employee, or the person who issued the employee's sign-in code.",
    });

    private IActionResult NotPending() => Problem(409, "not_pending", "This employee has no new-hire pay package waiting for approval.");

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────
    private async Task<Employee?> FindInScopeAsync(Guid tenantId, int id, CancellationToken ct)
    {
        var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId,
                "Pay approval view: the employee's company is resolved before the ambient company filter can hide it; the scope check follows.")
            .AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, ct);
        return employee is not null && this.GetEntityScope().CanAccessCompany(employee.CompanyId) ? employee : null;
    }

    private ObjectResult Problem(int status, string code, string detail) => EmployeeHireController.BuildProblem(HttpContext.TraceIdentifier, status, code, detail);
    private Guid RequireTenant() => Guid.Parse(User.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("Tenant claim missing."));
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private RequestContext Context(Guid tenantId) => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), tenantId,
        Permissions: User.EffectivePermissions());
}
