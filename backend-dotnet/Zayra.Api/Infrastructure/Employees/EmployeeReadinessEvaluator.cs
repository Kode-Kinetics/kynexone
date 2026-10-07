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

    /// <summary>The readiness key emitted when a policy resolves to ZERO requirements. Deliberately NOT a
    /// registry FieldSpec key (the catalog↔spec parity guard owns that namespace) — it is a synthetic,
    /// evaluator-owned item saying that readiness could not be evaluated, and why.</summary>
    public const string NoPolicyKey = "ReadinessPolicy";

    /// <summary>
    /// A policy with NO requirements is NOT evidence of readiness — it is the absence of evidence. Before
    /// this, <c>total == 0 ⇒ score 100</c> with no items made an employee with a blank or non-GCC
    /// CountryCode (an empty floor and no configured profile) report "Ready" at 100% — a fabricated
    /// all-clear.
    ///
    /// <para>The honest answer is <b>NeedsAttention</b>, score 0, with one RECOMMENDED item naming the
    /// missing policy — deliberately NOT "Blocked". Blocked would be a new fail-closed gate on LIVE data:
    /// every existing employee whose company/country has no policy would suddenly be refused on their next
    /// activation or rehire, and every such Active employee would start raising a payroll drift warning
    /// (the pay interlock treats IsBlocked on an Active employee as drift). Their data did not change;
    /// only the absence of a policy was made visible. So the state stops claiming Ready, the badge turns
    /// amber, the checklist says what to configure — and nothing that worked yesterday is refused today.
    /// Real statutory gates remain wherever a policy exists (every GCC country has a code floor).</para>
    ///
    /// <para>Integrity blockers (<see cref="IntegrityBlockers"/>) still apply: a policy-less employee with
    /// an unknown joining date is Blocked on that date, not on the missing policy. So is one whose
    /// JURISDICTION is unknown (<see cref="UnknownJurisdictionBlocker"/>) — that is a narrower claim than
    /// this one and the reason it is allowed to block: "we do not know which country's law applies" is not
    /// the same as "this known country has no rules configured yet". When that blocker has already named
    /// the country, this method does not repeat it as a second checklist line.</para>
    /// </summary>
    private static EmployeeReadiness NoPolicy(ResolvedReadinessPolicy policy, IReadOnlyList<ReadinessItem> integrityBlockers,
        IReadOnlyList<ReadinessItem> payIntegrityBlockers)
    {
        var country = (policy.CountryCode ?? string.Empty).Trim();
        var empty = System.Array.Empty<ReadinessItem>();
        // The jurisdiction blocker already says the country is missing or unrecognised, with the field to
        // fix. Saying it twice would give the operator two lines for one problem.
        if (integrityBlockers.Any(i => string.Equals(i.Key, "CountryCode", StringComparison.OrdinalIgnoreCase)))
            return new EmployeeReadiness("Blocked", 0m, integrityBlockers, payIntegrityBlockers, empty, empty, empty);
        // Three honestly-different causes. "Country not recognised" is its own case because a value like
        // "UAE" is neither ISO-2 nor ISO-3: it normalises to NOTHING, so "no policy for UAE" would send the
        // operator to configure a policy that could never match, when the fix is the country code itself.
        var label =
            country.Length == 0 ? "No readiness policy applies: the employee's country is not set"
            : !Application.Common.CountryCodeStandard.IsValid(country) ? $"No readiness policy applies: country '{country}' is not recognised"
            : $"No readiness policy configured for {country}";
        var item = new ReadinessItem(
            Key: NoPolicyKey,
            Label: label,
            Category: "compliance",
            Reason: "not_evaluable",
            Jurisdiction: country.Length == 0 ? null : country,
            Gate: "recommended",
            FixKind: "policy",
            FixTarget: null,
            DocumentType: null);
        // Score 0, not 100: nothing was checked, so nothing is proven.
        return new EmployeeReadiness(
            integrityBlockers.Count > 0 ? "Blocked" : "NeedsAttention", 0m,
            integrityBlockers, payIntegrityBlockers, new[] { item }, empty, empty);
    }

    /// <summary>
    /// Record-integrity blockers that hold under EVERY policy, including none. There are two: an UNKNOWN
    /// JURISDICTION (<see cref="UnknownJurisdictionBlocker"/> — the country that keys every statutory
    /// requirement is blank or unrecognised, so an empty requirement list means "not evaluated", not
    /// "nothing required"), and an unknown joining date (<c>JoiningDate == default</c>).
    ///
    /// <para>On the DATE: the CSV import leaves it unset when the cell cannot be read rather than
    /// inventing one, and every date that hangs off it — the salary structure's effective date, the
    /// reporting line, probation, end-of-service accrual — would otherwise inherit year 1. Such a record
    /// must not become Active until someone states the real date. No live record is affected by that one:
    /// every create path (form, draft approval, offer acceptance, import) always sets a joining date;
    /// only an unreadable import cell leaves it unknown.</para>
    ///
    /// <para>Each is emitted only when violated, so neither changes the score of a record that satisfies
    /// it.</para>
    /// </summary>
    private static IReadOnlyList<ReadinessItem> IntegrityBlockers(EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy)
    {
        var items = new List<ReadinessItem>();
        if (UnknownJurisdictionBlocker(policy) is { } jurisdiction) items.Add(jurisdiction);
        if (emp.JoiningDate == default
            // A configured policy that already requires JoiningDate reports it through the normal loop.
            && !policy.Items.Any(i => string.Equals(i.Key, "JoiningDate", StringComparison.OrdinalIgnoreCase)))
        {
            var req = new ReadinessRequirement("JoiningDate", "contract", FailClosed: true, Gate: "activate", Source: "integrity");
            items.Add(ToItem(req, FieldPresence.Missing));
        }
        return items.Count == 0 ? System.Array.Empty<ReadinessItem>() : items;
    }

    /// <summary>
    /// FAIL CLOSED ON AN UNKNOWN JURISDICTION. Every statutory requirement is keyed on the employee's
    /// country, and <c>GccReadinessFloor.Resolve</c> answers an unrecognised one with an EMPTY list.
    /// Read as "nothing is required", that turned the absence of a jurisdiction into a clean bill of
    /// health: a non-GCC expat with no Iqama and no GOSI reference activated into a Saudi legal entity
    /// because their country was blank. Per the product invariant — a critical uncertain value is
    /// flagged or blocked, never guessed — an employee whose country cannot be identified is now
    /// refused activation and told why.
    ///
    /// <para><b>The test is "known", not "GCC".</b> The product supports non-GCC employers (the country
    /// list spans GB, IN, US, PH… and <c>GccReadinessFloor</c> documents non-GCC countries as carrying an
    /// empty floor by design, with config profiles alone applying). A UK or Indian entity's employee has
    /// a KNOWN jurisdiction with no GCC floor, and still activates exactly as before — nothing here
    /// requires a GCC country. Only a country that is blank, or free text no country list recognises
    /// (e.g. "UAE", which is neither ISO-2 nor ISO-3), is refused.</para>
    ///
    /// <para><b>Reachability.</b> The country is derived before this runs (explicit ⇒ the employing
    /// company's — <see cref="Application.Common.HomeJurisdiction.DeriveEmployeeCountry"/>), and a create
    /// under a company with no country is already refused outright (<c>CompanyCountryMissingException</c>).
    /// So this cannot fire for a record created through a fixed path; it exists to catch rows that were
    /// persisted blank before the derivation existed, and free text that normalises to nothing.</para>
    ///
    /// <para>Distinct from <see cref="NoPolicy"/>, which stays ADVISORY: "no policy configured for GB" is
    /// a known country nobody has set rules for, and blocking on it would refuse live employees whose
    /// data never changed. "We do not know which country's law applies" is a different claim.</para>
    /// </summary>
    private static ReadinessItem? UnknownJurisdictionBlocker(ResolvedReadinessPolicy policy)
    {
        var country = (policy.CountryCode ?? string.Empty).Trim();
        if (Application.Common.CountryCodeStandard.IsValid(country)) return null;
        // A policy that already requires CountryCode reports it through the normal loop — no duplicate.
        if (policy.Items.Any(i => string.Equals(i.Key, "CountryCode", StringComparison.OrdinalIgnoreCase)))
            return null;
        return new ReadinessItem(
            Key: "CountryCode",
            Label: country.Length == 0
                ? "Country not set — no statutory requirements can be applied to this employee"
                : $"Country '{country}' is not recognised — no statutory requirements can be applied to this employee",
            Category: "personal",
            Reason: "statutory",
            Jurisdiction: null,
            Gate: "activate",
            FixKind: "field",
            FixTarget: "CountryCode",
            DocumentType: null);
    }

    /// <summary>
    /// Record-integrity PAY blockers that hold under every policy. Today there is one: an approved IBAN moved the
    /// employee to another bank, the old bank's routing code was cleared (EmployeeBankProfileSync), and no routing
    /// code for the new bank has been approved yet. The WPS/SIF line carries the routing code, so until one is
    /// supplied the employee is held at the pay gate (Active staff surface as drift that must be acknowledged,
    /// like any other pay requirement). Emitted only when violated.
    /// </summary>
    private static IReadOnlyList<ReadinessItem> PayIntegrityBlockers(EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy)
    {
        if (!emp.BankRoutingCodeRequired || !string.IsNullOrWhiteSpace(emp.BankRoutingCode)) return System.Array.Empty<ReadinessItem>();
        if (policy.Items.Any(i => string.Equals(i.Key, "BankRoutingCode", StringComparison.OrdinalIgnoreCase)))
            return System.Array.Empty<ReadinessItem>();
        var req = new ReadinessRequirement("BankRoutingCode", "payroll", FailClosed: true, Gate: "pay", Source: "integrity");
        return new[] { ToItem(req, FieldPresence.Missing) };
    }

    public EmployeeReadiness Evaluate(EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy)
    {
        var integrity = IntegrityBlockers(emp, policy);
        var payIntegrity = PayIntegrityBlockers(emp, policy);
        // Nothing to evaluate against: say so honestly (see NoPolicy) — never Ready/100, never a new block.
        if (policy.Items.Count == 0) return NoPolicy(policy, integrity, payIntegrity);

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
        blocking.AddRange(integrity);
        payBlocking.AddRange(payIntegrity);

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
            ["pay:bankUnverified"] = "Imported bank details not yet verified",
            // A CSV cell that could not be read (left blank and flagged, never guessed).
            ["data:unparsedDate"] = "A date in the file could not be read",
            ["data:unparsedNumber"] = "A number in the file could not be read",
            // Work-email auto-derivation gaps (advisory, fail-open — never block activation).
            ["email:needs-info"] = "Work email needs manual entry",
            ["email:domain-mismatch"] = "Work email domain mismatch",
            ["email:duplicate"] = "Work email already in use",
            // Duplicate-person flags (advisory, human-resolved — never auto-heal). Category "dup".
            ["dup:strong"] = "Possible duplicate (ID match)",
            ["dup:possible"] = "Possible duplicate (name+DOB)",
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
        var routingClearedIds = (await _db.EmployeeHistories.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId)
                        && x.EventType == Zayra.Api.Application.Employees.EmployeeBankProfileSync.RoutingCodeClearedEventType)
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();
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
                hasSalary: salaryEmpIds.Contains(e.Id) || (e.Salary ?? 0m) > 0m,
                bankRoutingCodeRequired: routingClearedIds.Contains(e.Id));
        }
        return result;
    }

    /// <summary>Assemble a snapshot from a persisted Employee (+ optional payroll/docs/expiries).</summary>
    public static EmployeeReadinessSnapshot BuildFromEmployee(
        Employee e, EmployeePayrollProfile? pf, IReadOnlyList<DocumentPresence> docs,
        IReadOnlyDictionary<string, DateOnly?> complianceExpiries, bool hasSalary, bool bankRoutingCodeRequired = false) => new()
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
        BankRoutingCodeRequired = bankRoutingCodeRequired,
        Documents = docs,
        ComplianceExpiries = complianceExpiries,
    };
}
