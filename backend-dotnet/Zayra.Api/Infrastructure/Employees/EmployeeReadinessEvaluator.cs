using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>One itemized readiness gap/entry, shaped for the structured 422 and the checklist UI.</summary>
public sealed record ReadinessItem(
    string Key, string Label, string Category, string Reason,
    string? Jurisdiction, string Gate, string FixKind, string? FixTarget, string? DocumentType);

/// <summary>Full readiness result for one employee against a resolved policy.</summary>
public sealed record EmployeeReadiness(
    string State,                          // Ready | NeedsAttention | Blocked
    decimal Score,                         // 0–100, policy-weighted
    IReadOnlyList<ReadinessItem> Blocking,      // failClosed activate-gate items still missing
    IReadOnlyList<ReadinessItem> PayBlocking,   // gate:"pay" items still missing/expired
    IReadOnlyList<ReadinessItem> Recommended,   // failClosed:false items still missing
    IReadOnlyList<ReadinessItem> Present,       // satisfied items (for "12 of 15 complete")
    IReadOnlyList<ReadinessItem> ExpiringSoon)  // required IDs/docs within the alert window (amber)
{
    public int RequiredTotal => Blocking.Count + PayBlocking.Count + Recommended.Count + Present.Count;
    public bool IsBlocked => Blocking.Count > 0;
}

public interface IEmployeeReadinessEvaluator
{
    /// <summary>Pure, never throws — the primitive. Used by import, list recompute, detail, gate-precheck.</summary>
    EmployeeReadiness Evaluate(EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy);

    /// <summary>Throws EmployeeActivationBlockedException when Blocking.Any(); else returns the readiness.</summary>
    Task<EmployeeReadiness> EnsureActivatableAsync(
        EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy, RequestContext ctx, CancellationToken ct);

    /// <summary>Batch snapshot loader (3 bulk queries, diff in memory) for policy fan-out + nightly sweep + list.</summary>
    Task<IReadOnlyDictionary<int, EmployeeReadinessSnapshot>> LoadSnapshotsAsync(
        Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct);
}

public sealed class EmployeeReadinessEvaluator : IEmployeeReadinessEvaluator
{
    private const int DefaultAlertDays = 60; // mirrors GCCComplianceSetting.VisaAlertDays/IqamaAlertDays default
    private readonly ZayraDbContext _db;
    public EmployeeReadinessEvaluator(ZayraDbContext db) => _db = db;

    public EmployeeReadiness Evaluate(EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy)
    {
        var asOf = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var blocking = new List<ReadinessItem>();
        var payBlocking = new List<ReadinessItem>();
        var recommended = new List<ReadinessItem>();
        var present = new List<ReadinessItem>();
        var expiring = new List<ReadinessItem>();

        foreach (var req in policy.Items)
        {
            var presence = EmployeeFieldRegistry.Presence(emp, req.Key, asOf, DefaultAlertDays, req.RequireVerified ?? false);
            var item = ToItem(req, presence);

            switch (presence)
            {
                case FieldPresence.Present:
                    present.Add(item);
                    break;
                case FieldPresence.ExpiringSoon:
                    present.Add(item);           // still satisfied for activation
                    expiring.Add(item);          // …but surfaced as amber
                    break;
                case FieldPresence.Expired:
                    // Present-but-expired: never blocks activation; a hard pay blocker (legally undocumented for pay).
                    if (req.FailClosed) payBlocking.Add(item);
                    else expiring.Add(item);
                    break;
                default: // Missing | Invalid | NotEvaluable  → FAIL CLOSED for unknown keys
                    if (req.FailClosed)
                    {
                        if (req.Gate == "pay") payBlocking.Add(item);
                        else blocking.Add(item);
                    }
                    else recommended.Add(item);
                    break;
            }
        }

        // State: any activate-blocker ⇒ Blocked; else recommended/pay gaps ⇒ NeedsAttention; else Ready.
        var state = blocking.Count > 0
            ? "Blocked"
            : (recommended.Count > 0 || payBlocking.Count > 0 ? "NeedsAttention" : "Ready");

        // Score: policy-weighted; any missing HARD blocker caps below green.
        var total = present.Count + blocking.Count + payBlocking.Count + recommended.Count;
        decimal score = total == 0 ? 100m : System.Math.Round(100m * present.Count / total, 1);
        if (blocking.Count > 0 || payBlocking.Count > 0) score = System.Math.Min(score, 55m);

        return new EmployeeReadiness(state, score, blocking, payBlocking, recommended, present, expiring);
    }

    /// <summary>
    /// Fold advisory org-skeleton / payroll import gaps into a computed readiness WITHOUT touching the
    /// activation gate. Blocking (and therefore ActivationBlockersCount / IsBlocked) is left untouched, so
    /// an employee whose ONLY gaps are advisory still passes EnsureActivatableAsync. A Ready employee with
    /// advisory gaps becomes NeedsAttention and its score is lowered proportionally; a Blocked one stays
    /// Blocked. Used at import (to stamp NeedsAttention) and by the stamp paths (to keep the signal durable
    /// until the operator completes the org skeleton).
    /// </summary>
    public static EmployeeReadiness MergeAdvisoryGaps(EmployeeReadiness r, IReadOnlyList<ReadinessItem> advisory)
    {
        if (advisory is null || advisory.Count == 0) return r;
        var recommended = r.Recommended.Concat(advisory).ToList();
        var state = r.Blocking.Count > 0 ? "Blocked" : "NeedsAttention";
        var total = r.Present.Count + r.Blocking.Count + r.PayBlocking.Count + recommended.Count;
        decimal score = total == 0 ? 100m : System.Math.Round(100m * r.Present.Count / total, 1);
        if (r.Blocking.Count > 0 || r.PayBlocking.Count > 0) score = System.Math.Min(score, 55m);
        return r with { State = state, Score = score, Recommended = recommended };
    }

    private static readonly IReadOnlyDictionary<string, string> ImportGapLabels =
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["org:company"] = "Company not resolved",
            ["org:branch"] = "Branch not resolved",
            ["org:department"] = "Department not resolved",
            ["org:designation"] = "Designation not resolved",
            ["org:grade"] = "Grade not resolved",
            ["org:position"] = "Position not assigned",
            ["org:establishment"] = "Over establishment budget",
            ["link:manager"] = "Manager not linked",
            ["link:supervisor"] = "Supervisor not linked",
            ["pay:salaryHeld"] = "Salary held (no grade)",
            ["pay:salaryReview"] = "Salary needs review",
        };

    /// <summary>Map a persisted import gap type into an advisory (fail-open, recommended-gate) readiness item.</summary>
    public static ReadinessItem ImportGapToItem(string gapType, string category) => new(
        Key: gapType,
        Label: ImportGapLabels.GetValueOrDefault(gapType, gapType),
        Category: category,
        Reason: "recommended",
        Jurisdiction: null,
        Gate: "recommended",
        FixKind: category == "org" ? "org" : (category == "pay" ? "pay" : "field"),
        FixTarget: null,
        DocumentType: null);

    public async Task<EmployeeReadiness> EnsureActivatableAsync(
        EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy, RequestContext ctx, CancellationToken ct)
    {
        var readiness = Evaluate(emp, policy);
        if (readiness.IsBlocked)
            throw new EmployeeActivationBlockedException(emp.EmployeeId, readiness, policy);
        await Task.CompletedTask;
        return readiness;
    }

    private static ReadinessItem ToItem(ReadinessRequirement req, FieldPresence presence)
    {
        var spec = EmployeeFieldRegistry.Resolve(req.Key, req.RequireVerified ?? false);
        var label = spec?.Label ?? req.Key;
        var category = spec?.Category ?? req.Category;
        var fixKind = spec?.FixKind ?? "field";
        var fixTarget = spec?.FixTarget;
        var docType = spec?.DocumentType;
        var reason = presence == FieldPresence.Expired ? "expired"
            : presence == FieldPresence.Invalid ? "invalid"
            : req.FailClosed ? "statutory" : "recommended";
        return new ReadinessItem(req.Key, label, category, reason, req.Jurisdiction, req.Gate, fixKind, fixTarget, docType);
    }

    // ── Batch snapshot loader (MissingDocumentsAsync shape: 3 bulk queries, diff in memory) ──
    public async Task<IReadOnlyDictionary<int, EmployeeReadinessSnapshot>> LoadSnapshotsAsync(
        Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct)
    {
        var result = new Dictionary<int, EmployeeReadinessSnapshot>();
        if (employeeIds.Count == 0) return result;
        var ids = employeeIds.ToHashSet();

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && ids.Contains(e.Id))
            .ToListAsync(ct);
        var profiles = (await _db.EmployeePayrollProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && ids.Contains(p.EmployeeId))
            .ToListAsync(ct)).GroupBy(p => p.EmployeeId).ToDictionary(g => g.Key, g => g.First());
        var docs = (await _db.EmployeeDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.EmployeeId != null && ids.Contains(x.EmployeeId!.Value))
            .Select(x => new { x.EmployeeId, x.DocumentType, x.ApprovalStatus, x.ExpiryDate })
            .ToListAsync(ct)).GroupBy(x => x.EmployeeId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var comp = (await _db.EmployeeComplianceRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && ids.Contains(x.EmployeeId))
            .Select(x => new { x.EmployeeId, x.FieldKey, x.ExpiryDate })
            .ToListAsync(ct)).GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        var salaryEmpIds = (await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && ids.Contains(x.EmployeeId))
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();

        foreach (var e in employees)
        {
            profiles.TryGetValue(e.Id, out var pf);
            var docList = docs.TryGetValue(e.Id, out var dl)
                ? dl.Select(d => new DocumentPresence(d.DocumentType, string.Equals(d.ApprovalStatus, "Verified", System.StringComparison.OrdinalIgnoreCase), d.ExpiryDate)).ToList()
                : new List<DocumentPresence>();
            var expiries = new Dictionary<string, DateOnly?>(System.StringComparer.OrdinalIgnoreCase);
            if (comp.TryGetValue(e.Id, out var cl))
                foreach (var c in cl) expiries[c.FieldKey] = c.ExpiryDate;
            result[e.Id] = BuildFromEmployee(e, pf, docList, expiries,
                hasSalary: salaryEmpIds.Contains(e.Id) || (e.Salary ?? 0m) > 0m);
        }
        return result;
    }

    /// <summary>Assemble a snapshot from a persisted Employee (+ optional payroll/docs/expiries).</summary>
    public static EmployeeReadinessSnapshot BuildFromEmployee(
        Employee e, EmployeePayrollProfile? pf, IReadOnlyList<DocumentPresence> docs,
        IReadOnlyDictionary<string, DateOnly?> complianceExpiries, bool hasSalary) => new()
    {
        EmployeeId = e.Id,
        CountryCode = e.CountryCode,
        Nationality = e.Nationality,
        EnglishName = e.EnglishName,
        FullName = e.FullName,
        Gender = e.Gender,
        DateOfBirth = e.DateOfBirth,
        WorkEmail = e.WorkEmail,
        Phone = e.Phone,
        DepartmentId = e.DepartmentId,
        DesignationId = e.DesignationId,
        JoiningDate = e.JoiningDate,
        ContractType = e.ContractType,
        EmploymentType = e.EmploymentType,
        PassportNumber = e.PassportNumber,
        PassportExpiryDate = e.PassportExpiryDate,
        IqamaNumber = e.IqamaNumber,
        IqamaExpiryDate = e.IqamaExpiryDate,
        EmiratesIdExpiryDate = e.EmiratesIdExpiryDate,
        QidExpiryDate = e.QidExpiryDate,
        CivilIdExpiryDate = e.CivilIdExpiryDate,
        GosiReference = e.GosiReference,
        EmiratesId = e.EmiratesId,
        Qid = e.Qid,
        CivilId = e.CivilId,
        IdNumber = e.IdNumber,
        VisaNumber = e.VisaNumber,
        VisaExpiryDate = e.VisaExpiryDate,
        WorkPermitNumber = e.WorkPermitNumber,
        MuqeemNumber = e.MuqeemNumber,
        LaborCardNumber = e.LaborCardNumber,
        QiwaContractNumber = e.QiwaContractNumber,
        // IBAN authority is the payroll profile (Δ13/P1-1), but the dual-home era left rows whose
        // PP.Iban is BLANK while the fix landed on the Employee scalar (checklist/edit route bankIban→
        // employee.BankIban). `??` catches only null, so an empty PP.Iban would pin the blocker forever —
        // treat blank-or-whitespace as unset and fall through to the scalar so the fix actually clears.
        BankIban = string.IsNullOrWhiteSpace(pf?.Iban) ? e.BankIban : pf!.Iban,
        PaymentMethod = pf?.PaymentMethod ?? string.Empty,
        MolId = pf?.MolId ?? string.Empty,
        BankRoutingCode = pf?.BankRoutingCode ?? string.Empty,
        SocialInsuranceReference = pf?.SocialInsuranceReference ?? string.Empty,
        HasSalary = hasSalary,
        WpsEligible = pf?.WpsEligible ?? true,
        Documents = docs,
        ComplianceExpiries = complianceExpiries,
    };
}
