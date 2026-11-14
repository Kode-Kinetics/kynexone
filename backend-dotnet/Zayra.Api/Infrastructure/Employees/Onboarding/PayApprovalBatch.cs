using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// The pay-approval rules of <see cref="CompensationApprovalGate"/> (does this package still need a second person, and may this caller approve it?)
/// evaluated for MANY employees from a fixed number of set-based reads, instead of about fifteen queries per employee. The scoped queue ran those
/// per-employee checks in a loop and took 41 s on production, where every round trip pays ~1.8 s across regions.
///
/// <para>Every rule is the gate's own, applied to the rows read here: the same tables, the same tenant pin, the same newest-row and fingerprint logic.
/// <see cref="CompensationApprovalGate.PayFingerprint"/>, <see cref="CompensationApprovalGate.RefusalFor"/> and
/// <see cref="CredentialHandlerBar.IssuedCodeTo"/> are called, not copied. Company scope and "in post" stay with the caller, as before.</para>
/// </summary>
internal sealed class PayApprovalBatch
{
    internal sealed record AuditRow(string EntityId, string Action, Guid UserId, DateTime CreatedAtUtc, string? Metadata);
    private sealed record GapRow(int EmployeeId, DateTime? ResolvedAtUtc, string? RawValue);

    private readonly Guid _tenantId;
    private readonly Dictionary<int, List<GapRow>> _gaps;
    private readonly Dictionary<int, EmployeePayrollProfile> _profiles;
    private readonly Dictionary<int, SalaryChangeRequest> _pending;
    private readonly Dictionary<int, EmployeeSalaryStructure> _structures;
    private readonly Dictionary<string, List<AuditRow>> _audit;
    private readonly Dictionary<string, List<string?>> _redemptions;
    private readonly DateTime _now;

    private PayApprovalBatch(Guid tenantId, Dictionary<int, List<GapRow>> gaps, Dictionary<int, EmployeePayrollProfile> profiles, Dictionary<int, SalaryChangeRequest> pending,
        Dictionary<int, EmployeeSalaryStructure> structures, Dictionary<string, List<AuditRow>> audit, Dictionary<string, List<string?>> redemptions, DateTime now)
    {
        _tenantId = tenantId; _gaps = gaps; _profiles = profiles; _pending = pending; _structures = structures; _audit = audit; _redemptions = redemptions; _now = now;
    }

    public static async Task<PayApprovalBatch> LoadAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Employee> employees, CancellationToken ct)
    {
        var ids = employees.Select(e => e.Id).Distinct().ToList();
        var idStrings = ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        var userIds = employees.Where(e => e.UserAccountId != null).Select(e => e.UserAccountId!.Value.ToString()).Distinct().ToList();
        var now = DateTime.UtcNow;

        var gaps = (await db.EmployeeImportGaps.AsNoTracking()
                .Where(g => g.TenantId == tenantId && ids.Contains(g.EmployeeId) && g.GapType == CompensationApprovalGate.GapType)
                .Select(g => new GapRow(g.EmployeeId, g.ResolvedAtUtc, g.RawValue)).ToListAsync(ct))
            .GroupBy(g => g.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        var profiles = (await db.EmployeePayrollProfiles.AsNoTracking()
                .Where(p => p.TenantId == tenantId && ids.Contains(p.EmployeeId) && !p.IsDeleted).ToListAsync(ct))
            .GroupBy(p => p.EmployeeId).ToDictionary(g => g.Key, g => g.First());

        var pending = (await ScopedBypass.TenantWide(db.SalaryChangeRequests, tenantId,
                    "Pay approval queue: the pending first salary packages of the employees being listed; tenant pinned, employees pinned, company scope is applied by the caller.")
                .AsNoTracking()
                .Where(x => ids.Contains(x.EmployeeId) && x.Status == SalaryChangeRequest.Pending
                    && (x.Source == SalaryChangeSources.NewHire || x.Source == SalaryChangeSources.EmployeeImport))
                .ToListAsync(ct))
            .GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.RequestedAtUtc).First());

        var structures = (await db.EmployeeSalaryStructures.AsNoTracking()
                .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId) && x.IsActive).ToListAsync(ct))
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).First());

        // Who entered and who last changed each employee's pay, and any rejection: one read for the whole set, split by employee below.
        var audit = (await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, "Pay approval queue: who entered and last changed the listed employees' pay and any rejection; tenant and entities pinned.")
                .AsNoTracking()
                .Where(a => a.EntityName == "Employee" && a.EntityId != null && idStrings.Contains(a.EntityId) && a.UserId != null
                    && (a.Action == CompensationApprovalGate.CreatedAction || a.Action == CompensationApprovalGate.WrittenAction || a.Action == Zayra.Api.Controllers.Employees.PayApprovalsController.RejectedAction))
                .Select(a => new AuditRow(a.EntityId!, a.Action, a.UserId!.Value, a.CreatedAtUtc, a.Metadata)).ToListAsync(ct))
            .GroupBy(a => a.EntityId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.CreatedAtUtc).ToList());

        var redemptions = new Dictionary<string, List<string?>>();
        if (userIds.Count > 0)
        {
            var since = now - CredentialHandlerBar.Window;
            redemptions = (await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId,
                        "Credential-handler bar for the pay approval queue: who was shown the welcome code each listed employee redeemed; the tenant is re-applied.")
                    .AsNoTracking()
                    .Where(a => a.Action == WelcomeCodeRedeemer.RedeemedAction && a.EntityName == "User" && a.EntityId != null && userIds.Contains(a.EntityId) && a.CreatedAtUtc >= since)
                    .Select(a => new { a.EntityId, a.Metadata }).ToListAsync(ct))
                .GroupBy(a => a.EntityId!).ToDictionary(g => g.Key, g => g.Select(a => a.Metadata).ToList());
        }
        return new PayApprovalBatch(tenantId, gaps, profiles, pending, structures, audit, redemptions, now);
    }

    public EmployeePayrollProfile? Profile(Employee e) => _profiles.GetValueOrDefault(e.Id);

    /// <summary>The employee's stored pay package right now (the gate's <c>LivePackageAsync</c>, from the rows read here).</summary>
    public CompensationApprovalGate.PayPackage Package(Employee e)
    {
        var salary = e.Salary;
        var profile = Profile(e);
        if (_pending.TryGetValue(e.Id, out var p))
        {
            var gross = p.BasicSalary + p.HousingAllowance + p.TransportAllowance + p.FoodAllowance + p.MobileAllowance + p.OtherAllowance;
            return new CompensationApprovalGate.PayPackage(salary is > 0m ? salary : gross, p.BasicSalary, p.HousingAllowance, p.TransportAllowance, p.FoodAllowance,
                p.MobileAllowance, p.OtherAllowance, p.FixedDeduction, string.IsNullOrWhiteSpace(p.Currency) ? profile?.SalaryCurrency : p.Currency, profile?.Iban, profile?.AccountNumber);
        }
        _structures.TryGetValue(e.Id, out var s);
        return new CompensationApprovalGate.PayPackage(salary, s?.BasicSalary ?? 0m, s?.HousingAllowance ?? 0m, s?.TransportAllowance ?? 0m, s?.FoodAllowance ?? 0m,
            s?.MobileAllowance ?? 0m, s?.OtherAllowance ?? 0m, s?.FixedDeduction ?? 0m, s?.Currency ?? profile?.SalaryCurrency, profile?.Iban, profile?.AccountNumber);
    }

    private string ApprovedFingerprint(int employeeId)
    {
        var resolved = _gaps.GetValueOrDefault(employeeId)?.Where(g => g.ResolvedAtUtc != null).ToList();
        if (resolved is null || resolved.Count == 0) return CompensationApprovalGate.NoApproval;
        var raw = resolved.OrderByDescending(g => g.ResolvedAtUtc).First().RawValue;
        return raw is { } v && v.StartsWith(CompensationApprovalGate.ApprovedMarker, StringComparison.Ordinal) ? v[CompensationApprovalGate.ApprovedMarker.Length..] : CompensationApprovalGate.LegacyApproval;
    }

    /// <summary><see cref="CompensationApprovalGate.NeedsApprovalAsync"/>: a hire not yet in post whose stored figures are not the approved ones.</summary>
    public bool NeedsApproval(Employee e)
    {
        if (Zayra.Api.Application.Organization.EstablishmentOccupancy.IsOccupyingStatus(e.Status)) return false;
        if (_gaps.GetValueOrDefault(e.Id)?.Any(g => g.ResolvedAtUtc == null) == true) return true;                       // an open gap
        var approved = ApprovedFingerprint(e.Id);
        var package = Package(e);
        if (approved is not (CompensationApprovalGate.NoApproval or CompensationApprovalGate.LegacyApproval)
            && !string.Equals(approved, CompensationApprovalGate.PayFingerprint(package), StringComparison.Ordinal)) return true;   // drifted from the approval
        if (_pending.ContainsKey(e.Id)) return true;                                                                      // figures still waiting as a request
        if (approved != CompensationApprovalGate.NoApproval) return false;
        return CompensationApprovalGate.CarriesCompensation(package.Salary, package.Iban, package.AccountNumber)
            || package.Basic > 0m || package.Housing > 0m || package.Transport > 0m || package.Food > 0m || package.Mobile > 0m || package.Other > 0m;
    }

    /// <summary>The audit rows of this employee's pay writes and rejections since the last approval, oldest first.</summary>
    public IReadOnlyList<AuditRow> RowsSinceApproval(Employee e)
    {
        var since = _gaps.GetValueOrDefault(e.Id)?.Where(g => g.ResolvedAtUtc != null).Max(g => g.ResolvedAtUtc) ?? DateTime.MinValue;
        return (_audit.GetValueOrDefault(e.Id.ToString(CultureInfo.InvariantCulture)) ?? []).Where(a => a.CreatedAtUtc > since).ToList();
    }

    /// <summary><see cref="CompensationApprovalGate.IsBarredAsync"/>: the creator, anyone who wrote the pay since the last approval, the profile's editors, the subject, or the person who issued the sign-in code.</summary>
    public bool IsBarred(Employee e, Guid caller)
    {
        var profile = Profile(e);
        var makers = new HashSet<Guid>(RowsSinceApproval(e).Where(a => a.Action is CompensationApprovalGate.CreatedAction or CompensationApprovalGate.WrittenAction).Select(a => a.UserId));
        if (profile?.CreatedBy is Guid pc) makers.Add(pc);
        if (profile?.UpdatedBy is Guid pu) makers.Add(pu);
        if (CompensationApprovalGate.RefusalFor(caller, e.CreatedBy, e.UserAccountId, makers) is not null) return true;
        if (e.UserAccountId is not Guid user) return false;
        return _redemptions.GetValueOrDefault(user.ToString())?.Any(meta => CredentialHandlerBar.IssuedCodeTo(meta, caller)) == true;
    }
}
