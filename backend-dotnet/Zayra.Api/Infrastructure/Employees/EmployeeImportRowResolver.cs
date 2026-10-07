using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>One typed org-skeleton / payroll gap produced while resolving a CSV row. Emitted by the
/// SHARED resolver so preview and commit agree byte-for-byte on which references failed. Detail carries
/// no "Row N:" prefix — the caller prepends it for the flat error/warning list.</summary>
public sealed record ImportGap(string Type, string Category, string Detail, string? RawValue);

/// <summary>Salary landing decision for a row (accept-never-block): Apply keeps the salary; Hold imports
/// the person but withholds the salary structure (no valid grade to enforce eligibility); Review keeps the
/// salary but flags it for human review (outside the grade band).</summary>
public enum ImportSalaryDecision { Apply, Hold, Review }

/// <summary>Lightweight designation projection the resolver needs (avoids leaking the entity).</summary>
public sealed record DesignationRef(Guid Id, Guid? GradeId, string TitleEn, Guid? DepartmentId = null, bool IsActive = true);

/// <summary>A department as the name lookup needs it: the branch is what tells two same-named ones apart.</summary>
public sealed record DepartmentRef(Guid Id, Guid? BranchId);

/// <summary>Master-data lookups loaded ONCE per import (preview and commit share the loader, so the two
/// endpoints resolve against identical data → dry-run counts == commit).</summary>
public sealed class ImportLookups
{
    public required IReadOnlyDictionary<string, Company> CompaniesByName { get; init; }
    public Company? DefaultCompany { get; init; }
    public required IReadOnlyDictionary<(Guid CompanyId, string Code), Branch> BranchesByCode { get; init; }
    public required IReadOnlyDictionary<Guid, List<Branch>> BranchesByCompany { get; init; }
    public required IReadOnlyDictionary<(Guid? CompanyId, string Code), CostCenter> CostCentersByCode { get; init; }
    public required IReadOnlyDictionary<string, Guid> DeptByCode { get; init; }   // CODE (upper) -> id
    public required IReadOnlyDictionary<string, Guid> DeptByName { get; init; }   // name (lower) -> id
    public required IReadOnlyDictionary<string, DesignationRef> DesigByTitle { get; init; } // title (lower)
    public required IReadOnlyDictionary<string, Grade> GradeByCode { get; init; } // CODE (upper)
    public required IReadOnlyDictionary<string, Grade> GradeByName { get; init; } // name (lower)
    public required IReadOnlyDictionary<Guid, Grade> GradeById { get; init; }
    public required IReadOnlyDictionary<string, Position> PositionsByCode { get; init; } // CODE (upper)

    /// <summary>How many active companies the importer can see — the default-company rule needs it.</summary>
    public int ActiveCompanyCount { get; init; }

    /// <summary>
    /// Lookup keys that match MORE THAN ONE record, as "kind|KEY" → the stored values that collide (e.g.
    /// two department codes 'ops' and 'OPS', or two companies with the same legal name). Such a key is
    /// left OUT of the lookups above and resolves to nothing with a review gap naming the clash — it used
    /// to throw while the lookups were built (a duplicate dictionary key), which failed every employee
    /// import and preview for that tenant with a 500 until someone found and renamed the rows, or, where
    /// the loader grouped instead, silently picked the first record. Built by <see cref="ImportLookupBuilder"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Ambiguous { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Every department sharing an ambiguous name (lower-case), so the row's branch can pick one.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DepartmentRef>> DeptNameCandidates { get; init; } =
        new Dictionary<string, IReadOnlyList<DepartmentRef>>();

    /// <summary>Every designation sharing an ambiguous title (lower-case), so the row's department can pick one.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DesignationRef>> DesigTitleCandidates { get; init; } =
        new Dictionary<string, IReadOnlyList<DesignationRef>>();

    /// <summary>The colliding stored values when <paramref name="key"/> of <paramref name="kind"/> is ambiguous.</summary>
    public IReadOnlyList<string>? AmbiguousValues(string kind, string key) =>
        Ambiguous.TryGetValue(ImportLookupBuilder.AmbiguityKey(kind, key), out var values) ? values : null;
}

/// <summary>
/// Builds a by-key lookup that never throws on a duplicate key and never picks one of several matches: a key
/// shared by two or more distinct records is recorded as ambiguous and left out. ONE place for the employee
/// importer's case/duplicate handling of every reference column (company, branch, cost centre, department,
/// designation, grade, position), the read-side twin of <c>OrgCodes.TryBuildLookup</c> used by the org importers.
/// </summary>
public static class ImportLookupBuilder
{
    public static string AmbiguityKey(string kind, string key) => $"{kind}|{key}";

    public static Dictionary<TKey, TValue> Build<TItem, TKey, TValue>(
        IEnumerable<TItem> items, Func<TItem, TKey> keyOf, Func<TItem, TValue> valueOf, Func<TItem, Guid> identityOf,
        Func<TItem, string> rawOf, string kind, Func<TKey, string> keyText,
        Dictionary<string, IReadOnlyList<string>> ambiguous)
        where TKey : notnull
    {
        var lookup = new Dictionary<TKey, TValue>();
        foreach (var group in items.GroupBy(keyOf))
        {
            var distinct = group.GroupBy(identityOf).Select(g => g.First()).ToList();
            if (distinct.Count == 1) { lookup[group.Key] = valueOf(distinct[0]); continue; }
            ambiguous[AmbiguityKey(kind, keyText(group.Key))] = distinct.Select(rawOf).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        return lookup;
    }
}

/// <summary>The resolved FKs + typed gaps for a single CSV row — the SINGLE SOURCE OF TRUTH for every
/// accept-never-block decision. It never signals a drop: a row is dropped only for no-name or duplicate
/// EmployeeCode, both decided by the caller against the DB/batch, never here.</summary>
public sealed class ResolvedImportRow
{
    public Guid? CompanyId { get; set; }

    /// <summary>The EMPLOYING company's own country, carried so the row builder can derive the employee's
    /// jurisdiction without a second lookup: an explicit CountryCode column wins, otherwise this
    /// (<see cref="Zayra.Api.Application.Common.HomeJurisdiction.DeriveEmployeeCountry"/>). A file with no
    /// CountryCode column used to import every row with a BLANK country, which resolves an EMPTY statutory
    /// floor — the whole file landed Active with no jurisdiction gate applied.</summary>
    public string CompanyCountryCode { get; set; } = string.Empty;
    public Guid? BranchId { get; set; }
    public string BranchNameEn { get; set; } = string.Empty;
    public Guid? CostCenterId { get; set; }
    public string CostCenterCode { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public Guid? DesignationId { get; set; }
    public Guid? GradeId { get; set; }
    public string FinalGradeCode { get; set; } = string.Empty;
    public Guid? PositionId { get; set; }
    public decimal GrossSalary { get; set; }
    public ImportSalaryDecision SalaryDecision { get; set; } = ImportSalaryDecision.Apply;
    // ── Work email (auto-derive / validate; accept-never-block) ──────────────────────────────────
    // The employing company's email domain (empty ⇒ edge-7 manual). The controller owns final uniqueness
    // (cumulative claim set), so the resolver produces a CANDIDATE local part (blank when derivation
    // isn't possible) and/or keeps the PROVIDED value; the resolver never queries the DB.
    public string WorkEmailDomain { get; set; } = string.Empty;
    public string WorkEmailPattern { get; set; } = Models.WorkEmailPatterns.FirstLast;
    public string WorkEmailLocalPart { get; set; } = string.Empty; // derived candidate (blank if none)
    public string WorkEmailProvided { get; set; } = string.Empty;  // value the row supplied (kept as-is)
    public List<ImportGap> Gaps { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>Shared resolver used by BOTH EmployeesController.Import (commit) and .ImportPreview (dry-run).
/// The org/grade/position/salary resolution lives here VERBATIM so the two paths can never diverge.</summary>
public static class EmployeeImportRowResolver
{
    public static async Task<ImportLookups> LoadImportLookupsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // Every lookup goes through ImportLookupBuilder: a key matching two records (case-only code clashes,
        // same-named companies/departments/designations/grades) is AMBIGUOUS — left out and flagged per row —
        // instead of throwing while the dictionary is built or quietly taking the first record.
        var ambiguous = new Dictionary<string, IReadOnlyList<string>>();

        // Company/Branch/CostCenter: IsActive && !IsDeleted (operational master data).
        var activeCompanies = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);
        var companiesByName = ImportLookupBuilder.Build(activeCompanies, c => c.LegalNameEn.Trim().ToUpperInvariant(), c => c,
            c => c.Id, c => c.LegalNameEn, "company", k => k, ambiguous);
        // The SAME default-company rule as the Add Employee form: the only active company, never the oldest.
        var defaultCompany = EmployeeAssignmentRules.DefaultCompany(activeCompanies);

        var branches = await db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.IsActive && !b.IsDeleted)
            .ToListAsync(ct);
        var branchesByCode = ImportLookupBuilder.Build(branches, b => (b.CompanyId, Code: b.Code.Trim().ToUpperInvariant()), b => b,
            b => b.Id, b => b.Code, "branch", k => $"{k.CompanyId}|{k.Code}", ambiguous);
        var branchesByCompany = branches
            .GroupBy(b => b.CompanyId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).ToList());

        var costCenters = await db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .ToListAsync(ct);
        var costCentersByCode = ImportLookupBuilder.Build(costCenters, c => (c.CompanyId, Code: c.Code.Trim().ToUpperInvariant()), c => c,
            c => c.Id, c => c.Code, "costcenter", k => $"{k.CompanyId}|{k.Code}", ambiguous);

        // Departments/Designations/Grades: !IsDeleted (matches the authoritative commit filter — parity).
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .Select(d => new { d.Id, d.Code, d.NameEn, d.BranchId })
            .ToListAsync(ct);
        var deptByCode = ImportLookupBuilder.Build(departments, d => d.Code.Trim().ToUpperInvariant(), d => d.Id,
            d => d.Id, d => d.Code, "department-code", k => k, ambiguous);
        var deptByName = ImportLookupBuilder.Build(departments, d => d.NameEn.Trim().ToLowerInvariant(), d => d.Id,
            d => d.Id, d => d.NameEn, "department-name", k => k, ambiguous);
        var deptNameCandidates = departments
            .GroupBy(d => d.NameEn.Trim().ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DepartmentRef>)g.Select(d => new DepartmentRef(d.Id, d.BranchId)).ToList());

        var designations = await db.Designations.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .Select(d => new DesignationRef(d.Id, d.GradeId, d.TitleEn, d.DepartmentId, d.IsActive))
            .ToListAsync(ct);
        var desigByTitle = ImportLookupBuilder.Build(designations, d => d.TitleEn.Trim().ToLowerInvariant(), d => d,
            d => d.Id, d => d.TitleEn, "designation", k => k, ambiguous);
        var desigTitleCandidates = designations
            .GroupBy(d => d.TitleEn.Trim().ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DesignationRef>)g.ToList());

        var grades = await db.Grades.AsNoTracking()
            .Where(g => g.TenantId == tenantId && !g.IsDeleted)
            .ToListAsync(ct);
        var gradeByCode = ImportLookupBuilder.Build(grades, g => g.Code.Trim().ToUpperInvariant(), g => g,
            g => g.Id, g => g.Code, "grade-code", k => k, ambiguous);
        var gradeByName = ImportLookupBuilder.Build(grades, g => g.Name.Trim().ToLowerInvariant(), g => g,
            g => g.Id, g => g.Name, "grade-name", k => k, ambiguous);
        var gradeById = grades.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());

        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .ToListAsync(ct);
        var positionsByCode = ImportLookupBuilder.Build(positions, p => p.Code.Trim().ToUpperInvariant(), p => p,
            p => p.Id, p => p.Code, "position", k => k, ambiguous);

        return new ImportLookups
        {
            CompaniesByName = companiesByName,
            DefaultCompany = defaultCompany,
            BranchesByCode = branchesByCode,
            BranchesByCompany = branchesByCompany,
            CostCentersByCode = costCentersByCode,
            DeptByCode = deptByCode,
            DeptByName = deptByName,
            DesigByTitle = desigByTitle,
            GradeByCode = gradeByCode,
            GradeByName = gradeByName,
            GradeById = gradeById,
            PositionsByCode = positionsByCode,
            ActiveCompanyCount = activeCompanies.Count,
            Ambiguous = ambiguous,
            DeptNameCandidates = deptNameCandidates,
            DesigTitleCandidates = desigTitleCandidates,
        };
    }

    public static decimal GrossSalaryFromRow(Dictionary<string, string> row)
    {
        // InvariantCulture, deliberately: the figures the commit path PERSISTS are parsed with
        // InvariantCulture (EmployeesController.ParseImportSalary). Parsing the same cells here with the
        // ambient culture made the grade-band decision (hold / review) and the stored amount disagree
        // whenever the container's locale used a decimal comma.
        static decimal Amount(Dictionary<string, string> source, string key) =>
            decimal.TryParse(source.GetValueOrDefault(key, string.Empty),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0m;
        return Amount(row, "BasicSalary") + Amount(row, "HousingAllowance") + Amount(row, "TransportAllowance")
             + Amount(row, "FoodAllowance") + Amount(row, "MobileAllowance") + Amount(row, "OtherAllowance");
    }

    /// <summary>Resolve one row. NEVER drops. Every unresolved reference becomes a typed gap + a warning,
    /// and the person is imported with the weakest-safe FK (null / default company). <paramref name="claimedPositionCodes"/>
    /// is mutated cumulatively (file order wins) exactly as the legacy commit loop did. <paramref name="joiningDate"/>
    /// is the row's readable joining date (null when it is unknown): a position's effective window is checked
    /// against it, exactly as the Add Employee form checks it (<see cref="EmployeeAssignmentRules"/>).</summary>
    public static ResolvedImportRow ResolveRow(Dictionary<string, string> row, ImportLookups lk, HashSet<string> claimedPositionCodes,
        DateOnly? joiningDate = null)
    {
        var r = new ResolvedImportRow();
        string V(string k) => row.GetValueOrDefault(k, string.Empty).Trim();
        static string Clash(IReadOnlyList<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

        // ── Company (Leak #1): NEVER drops. A name that is not found, or that matches more than one company,
        // falls back to the tenant's ONLY active company (the Add Employee form's default rule); with several
        // companies nothing is assumed — the person lands without a company and the row says why. A blank cell
        // used to file the person into the OLDEST company silently, which in a group tenant is the wrong legal
        // employer (wrong GOSI / WPS establishment) with nothing on screen to show it.
        var companyNameRaw = V("CompanyLegalName");
        Company? company = null;
        if (string.IsNullOrWhiteSpace(companyNameRaw))
        {
            company = lk.DefaultCompany;
            if (company is null && lk.ActiveCompanyCount > 1)
            {
                r.Gaps.Add(new ImportGap("org:company", "org",
                    $"No CompanyLegalName supplied and the tenant has {lk.ActiveCompanyCount} companies — none is assumed; assign the employing company.",
                    null));
                r.Warnings.Add($"No CompanyLegalName supplied — the tenant has {lk.ActiveCompanyCount} companies, so none is assumed; imported without a company.");
            }
        }
        else if (lk.AmbiguousValues("company", companyNameRaw.ToUpperInvariant()) is { } companyClash)
        {
            company = null; // two or more companies share the name, so there is no "only company" either
            r.Gaps.Add(new ImportGap("org:company", "org",
                $"Company '{companyNameRaw}' matches more than one company ({Clash(companyClash)}) — imported without a company; rename one of them in Organization setup.",
                companyNameRaw));
            r.Warnings.Add($"CompanyLegalName '{companyNameRaw}' matches more than one company ({Clash(companyClash)}) — imported without a company.");
        }
        else
        {
            company = lk.CompaniesByName.GetValueOrDefault(companyNameRaw.ToUpperInvariant());
            if (company is null)
            {
                company = lk.DefaultCompany; // the only company, or null — never a guess between several
                r.Gaps.Add(new ImportGap("org:company", "org",
                    company is not null
                        ? $"Company '{companyNameRaw}' not found — assigned the default company."
                        : lk.ActiveCompanyCount > 1
                            ? $"Company '{companyNameRaw}' not found — imported without a company (the tenant has {lk.ActiveCompanyCount} companies; none is assumed)."
                            : $"Company '{companyNameRaw}' not found and no company exists — imported without a company.",
                    companyNameRaw));
                r.Warnings.Add(company is not null
                    ? $"CompanyLegalName '{companyNameRaw}' not found — assigned default company '{company.LegalNameEn}'."
                    : lk.ActiveCompanyCount > 1
                        ? $"CompanyLegalName '{companyNameRaw}' not found — imported without a company (none of the {lk.ActiveCompanyCount} companies is assumed)."
                        : $"CompanyLegalName '{companyNameRaw}' not found and no company exists to default to — imported without a company.");
            }
        }
        r.CompanyId = company?.Id;
        r.CompanyCountryCode = company?.CountryCode ?? string.Empty;

        // ── Work email (validate against company domain; accept-never-block) ──
        var workEmailRaw = V("WorkEmail");
        var emailDomain = (company?.EmailDomain ?? string.Empty).Trim().ToLowerInvariant();
        var emailPattern = Models.WorkEmailPatterns.Normalize(company?.WorkEmailPattern);
        r.WorkEmailDomain = emailDomain;
        r.WorkEmailPattern = emailPattern;
        if (string.IsNullOrWhiteSpace(emailDomain))
        {
            // EDGE 7: company has no email domain → derivation isn't configured for this company; keep any
            // provided value and NEVER block. A blank work email is a normal profile-completeness item (the
            // readiness/completeness signal already surfaces it), so we deliberately do NOT flood every row
            // of a non-derivation tenant with a per-row import gap.
            r.WorkEmailProvided = workEmailRaw;
        }
        else if (string.IsNullOrWhiteSpace(workEmailRaw))
        {
            // Blank stays blank: a derived address is a SUGGESTION only, never saved (employee-access contract §3).
            // The employee waits for their real work email (Employees → "Add work emails").
        }
        else
        {
            // Provided + domain present → validate against the company domain (req 6). Mismatch/malformed → FLAG, keep value.
            r.WorkEmailProvided = workEmailRaw;
            var (matches, providedDomain) = WorkEmailDeriver.ValidateAgainstDomain(workEmailRaw, emailDomain);
            if (!matches)
            {
                r.Gaps.Add(new ImportGap("email:domain-mismatch", "readiness",
                    providedDomain is null
                        ? $"Work email '{workEmailRaw}' is missing a domain — expected '@{emailDomain}'."
                        : $"Work email domain '@{providedDomain}' does not match the company domain '@{emailDomain}'.",
                    workEmailRaw));
                r.Warnings.Add($"Work email '{workEmailRaw}' does not match the company domain '@{emailDomain}' — imported as-is and flagged.");
            }
        }

        // ── Branch: optional, and NEVER guessed ─────────────────────────────────────────────────────
        // A blank BranchCode used to silently inherit the company's FIRST branch — an office the file never
        // named — and, when the company had no branches at all, that same expression returned null and left
        // the person branch-less with NO gap and NO warning (the gap was guarded on a non-blank code). A
        // tenant that imports before running org setup (TenantProvisioningBundle seeds zero branches) hit
        // that second path on every row: a blank Branch column across the whole People list and nothing in
        // the import summary explaining it. Both paths guessed, which the accept-never-block doctrine
        // forbids — an uncertain value is flagged, never assumed. Branch is now assigned ONLY from a code
        // the file actually supplied, and the three not-assigned outcomes are told apart:
        //   1. code supplied, not found      → org:branch gap + warning (unchanged).
        //   2. blank code, company HAS branches → org:branch gap + warning: a real per-row uncertainty (we
        //      could have picked any of N), so the row lands reviewable/deep-linkable and the gap self-heals
        //      the moment a branch is assigned. Assigning an arbitrary branch instead writes a
        //      plausible-but-wrong location into payroll/attendance/position-eligibility scope, where
        //      nothing downstream can tell it from a deliberate choice.
        //   3. blank code, company has NO branches → WARNING ONLY, with its own tenant-level text. Nothing
        //      could have been chosen here: the fix is "create the org structure", not "review this person",
        //      so this is deliberately NOT a per-employee gap — it would mark every imported human
        //      NeedsAttention for one setup task they cannot resolve on their own record. Same reasoning as
        //      the cost-center warning below. The operator still sees it in the import summary.
        // Gap types are a shared contract with the review UI / People-list deep link / self-heal map, so
        // case 2 reuses `org:branch` and varies only its detail text — no new gap type is introduced.
        var branchCodeRaw = V("BranchCode").ToUpperInvariant();
        var companyBranchCount = company is not null
            ? (lk.BranchesByCompany.GetValueOrDefault(company.Id)?.Count ?? 0)
            : 0;
        Branch? branch = company is not null && !string.IsNullOrWhiteSpace(branchCodeRaw)
            ? lk.BranchesByCode.GetValueOrDefault((company.Id, branchCodeRaw))
            : null;
        var branchClash = company is not null && !string.IsNullOrWhiteSpace(branchCodeRaw)
            ? lk.AmbiguousValues("branch", $"{company.Id}|{branchCodeRaw}") : null;
        if (branchClash is not null)
        {
            r.Gaps.Add(new ImportGap("org:branch", "org",
                $"BranchCode '{branchCodeRaw}' matches more than one branch of '{company!.LegalNameEn}' ({Clash(branchClash)}) — not linked.", branchCodeRaw));
            r.Warnings.Add($"BranchCode '{branchCodeRaw}' matches more than one branch ({Clash(branchClash)}) — imported without a branch.");
        }
        else if (!string.IsNullOrWhiteSpace(branchCodeRaw) && branch is null)
        {
            r.Gaps.Add(new ImportGap("org:branch", "org",
                company is null
                    ? $"BranchCode '{branchCodeRaw}' supplied but no company resolved."
                    : $"BranchCode '{branchCodeRaw}' not found for company '{company.LegalNameEn}'.",
                branchCodeRaw));
            r.Warnings.Add(company is null
                ? $"BranchCode '{branchCodeRaw}' supplied but no company could be resolved — imported without a branch."
                : $"BranchCode '{branchCodeRaw}' not found for company '{company.LegalNameEn}' — imported without a branch.");
        }
        else if (string.IsNullOrWhiteSpace(branchCodeRaw) && company is not null && companyBranchCount > 0)
        {
            // Case 2 — the file did not say which of the company's branches this person works at.
            // RawValue stays null: there is no branch code to create, only one to choose.
            r.Gaps.Add(new ImportGap("org:branch", "org",
                $"No BranchCode supplied — branch left unassigned ('{company.LegalNameEn}' has {companyBranchCount} branch(es) to choose from).",
                null));
            r.Warnings.Add($"No BranchCode supplied for company '{company.LegalNameEn}' — imported without a branch (a branch is no longer assumed); assign the correct branch.");
        }
        else if (string.IsNullOrWhiteSpace(branchCodeRaw) && company is not null)
        {
            // Case 3 — tenant-level: the company has no branches yet.
            r.Warnings.Add($"Company '{company.LegalNameEn}' has no branches yet — everyone imported without a branch. Create the company's branches in Organization setup, then assign them.");
        }
        r.BranchId = branch?.Id;
        r.BranchNameEn = branch?.NameEn ?? string.Empty;

        // ── Cost center: optional; unknown → null + warning (no gap; auto-creating from a typo pollutes finance master data).
        var ccRaw = V("CostCenterCode").ToUpperInvariant();
        CostCenter? cc = company is not null && !string.IsNullOrWhiteSpace(ccRaw)
            ? lk.CostCentersByCode.GetValueOrDefault(((Guid?)company.Id, ccRaw))
            : null;
        var ccClash = company is not null && !string.IsNullOrWhiteSpace(ccRaw)
            ? lk.AmbiguousValues("costcenter", $"{company.Id}|{ccRaw}") : null;
        if (ccClash is not null)
            r.Warnings.Add($"CostCenterCode '{ccRaw}' matches more than one cost center ({Clash(ccClash)}) — imported without a cost center.");
        else if (!string.IsNullOrWhiteSpace(ccRaw) && cc is null)
            r.Warnings.Add(company is null
                ? $"CostCenterCode '{ccRaw}' supplied but no company could be resolved — imported without a cost center."
                : $"CostCenterCode '{ccRaw}' not found for company '{company.LegalNameEn}' — imported without a cost center.");
        r.CostCenterId = cc?.Id;
        r.CostCenterCode = cc?.Code ?? string.Empty;

        // ── Department: unknown → free text, no FK + gap + warning (was a silent leak).
        var deptNameRaw = V("Department");
        var deptCodeRaw = V("DepartmentCode").ToUpperInvariant();
        Guid? deptId = null;
        IReadOnlyList<string>? deptClash = null;
        var deptNameKey = deptNameRaw.ToLowerInvariant();
        if (!string.IsNullOrEmpty(deptCodeRaw) && lk.DeptByCode.TryGetValue(deptCodeRaw, out var d1)) deptId = d1;
        else if (!string.IsNullOrEmpty(deptCodeRaw) && lk.AmbiguousValues("department-code", deptCodeRaw) is { } codeClash) deptClash = codeClash;
        else if (!string.IsNullOrEmpty(deptNameRaw) && lk.DeptByName.TryGetValue(deptNameKey, out var d2)) deptId = d2;
        else if (!string.IsNullOrEmpty(deptNameRaw) && lk.AmbiguousValues("department-name", deptNameKey) is { } nameClash)
        {
            // Same-named departments in different branches are ordinary; the row's branch tells them apart.
            var inBranch = r.BranchId is Guid rowBranch && lk.DeptNameCandidates.TryGetValue(deptNameKey, out var candidates)
                ? candidates.Where(c => c.BranchId == rowBranch).ToList() : new List<DepartmentRef>();
            if (inBranch.Count == 1) deptId = inBranch[0].Id;
            else deptClash = nameClash;
        }
        if (deptId is null && deptClash is not null)
        {
            var raw = !string.IsNullOrEmpty(deptCodeRaw) ? deptCodeRaw : deptNameRaw;
            r.Gaps.Add(new ImportGap("org:department", "org",
                $"Department '{raw}' matches more than one department ({Clash(deptClash)}) — stored as text, not linked; use a DepartmentCode that names one.", raw));
            r.Warnings.Add($"Department '{raw}' matches more than one department ({Clash(deptClash)}) — imported as free text without an org link.");
        }
        else if (deptId is null && (!string.IsNullOrEmpty(deptCodeRaw) || !string.IsNullOrEmpty(deptNameRaw)))
        {
            var raw = !string.IsNullOrEmpty(deptCodeRaw) ? deptCodeRaw : deptNameRaw;
            r.Gaps.Add(new ImportGap("org:department", "org", $"Department '{raw}' not found — stored as text, not linked.", raw));
            r.Warnings.Add($"Department '{raw}' not found — imported as free text without an org link.");
        }
        r.DepartmentId = deptId;

        // ── Designation: unknown → free text, no FK + gap + warning.
        var desigTitleRaw = V("Designation");
        Guid? desigId = null;
        Guid? designationGradeId = null;
        var desigKey = desigTitleRaw.ToLowerInvariant();
        DesignationRef? desig = null;
        IReadOnlyList<string>? desigClash = null;
        if (!string.IsNullOrEmpty(desigTitleRaw) && lk.DesigByTitle.TryGetValue(desigKey, out var byTitle)) desig = byTitle;
        else if (!string.IsNullOrEmpty(desigTitleRaw) && lk.AmbiguousValues("designation", desigKey) is { } titleClash)
        {
            // The same title in several departments is ordinary; the row's department tells them apart.
            var inDept = deptId is Guid rowDept && lk.DesigTitleCandidates.TryGetValue(desigKey, out var candidates)
                ? candidates.Where(c => c.DepartmentId == rowDept).ToList() : new List<DesignationRef>();
            if (inDept.Count == 1) desig = inDept[0];
            else desigClash = titleClash;
        }
        if (desig is not null && !desig.IsActive)
        {
            // The form refuses an inactive designation ("Selected designation is not active"); so does the import.
            r.Gaps.Add(new ImportGap("org:designation", "org", $"Designation '{desigTitleRaw}' is inactive — stored as text, not linked.", desigTitleRaw));
            r.Warnings.Add($"Designation '{desigTitleRaw}' is inactive — imported as free text without an org link.");
        }
        else if (desig is not null)
        {
            desigId = desig.Id;
            designationGradeId = desig.GradeId;
        }
        else if (desigClash is not null)
        {
            r.Gaps.Add(new ImportGap("org:designation", "org",
                $"Designation '{desigTitleRaw}' matches more than one designation ({Clash(desigClash)}) — stored as text, not linked.", desigTitleRaw));
            r.Warnings.Add($"Designation '{desigTitleRaw}' matches more than one designation — imported as free text without an org link.");
        }
        else if (!string.IsNullOrEmpty(desigTitleRaw))
        {
            r.Gaps.Add(new ImportGap("org:designation", "org", $"Designation '{desigTitleRaw}' not found — stored as text, not linked.", desigTitleRaw));
            r.Warnings.Add($"Designation '{desigTitleRaw}' not found — imported as free text without an org link.");
        }

        // ── Grade (Leak #2): unknown → null + gap + warning.
        var gradeRaw = V("Grade");
        Grade? resolvedGrade = null;
        if (!string.IsNullOrWhiteSpace(gradeRaw))
        {
            var gradeClash = lk.AmbiguousValues("grade-code", gradeRaw.ToUpperInvariant());
            if (gradeClash is null && !lk.GradeByCode.TryGetValue(gradeRaw.ToUpperInvariant(), out resolvedGrade))
            {
                gradeClash = lk.AmbiguousValues("grade-name", gradeRaw.ToLowerInvariant());
                if (gradeClash is null) lk.GradeByName.TryGetValue(gradeRaw.ToLowerInvariant(), out resolvedGrade);
            }
            if (resolvedGrade is not null && !resolvedGrade.IsActive)
            {
                // The form refuses an inactive grade ("Selected grade is not active"); so does the import.
                r.Gaps.Add(new ImportGap("org:grade", "org", $"Grade '{gradeRaw}' is inactive — grade left unassigned.", gradeRaw));
                r.Warnings.Add($"Grade '{gradeRaw}' is inactive — imported without a grade.");
                resolvedGrade = null;
            }
            else if (gradeClash is not null)
            {
                r.Gaps.Add(new ImportGap("org:grade", "org", $"Grade '{gradeRaw}' matches more than one grade ({Clash(gradeClash)}) — grade left unassigned.", gradeRaw));
                r.Warnings.Add($"Grade '{gradeRaw}' matches more than one grade — imported without a grade.");
            }
            else if (resolvedGrade is null)
            {
                r.Gaps.Add(new ImportGap("org:grade", "org", $"Grade '{gradeRaw}' not found — grade left unassigned.", gradeRaw));
                r.Warnings.Add($"Grade '{gradeRaw}' not found — imported without a grade.");
            }
        }

        // ── Designation/grade conflict (Leak #3): keep the explicit grade, drop the weaker designation link.
        if (designationGradeId is not null && resolvedGrade is not null && designationGradeId != resolvedGrade.Id)
        {
            r.Gaps.Add(new ImportGap("org:grade", "org",
                $"Designation '{desigTitleRaw}' is not eligible for grade '{resolvedGrade.Code}' — designation link dropped, explicit grade kept.", desigTitleRaw));
            r.Warnings.Add($"Designation '{desigTitleRaw}' is not eligible for grade '{resolvedGrade.Code}' — imported with the grade only, designation unlinked.");
            desigId = null;
            designationGradeId = null;
        }
        r.DesignationId = desigId;

        var finalGradeId = resolvedGrade?.Id ?? designationGradeId;
        Grade? finalGrade = resolvedGrade ?? (finalGradeId is not null ? lk.GradeById.GetValueOrDefault(finalGradeId.Value) : null);
        if (finalGrade is { IsActive: false })
        {
            // A designation's own grade that has since been deactivated is not inherited either.
            r.Gaps.Add(new ImportGap("org:grade", "org", $"Grade '{finalGrade.Code}' (from designation '{desigTitleRaw}') is inactive — grade left unassigned.", finalGrade.Code));
            r.Warnings.Add($"Grade '{finalGrade.Code}' from designation '{desigTitleRaw}' is inactive — imported without a grade.");
            finalGrade = null;
            finalGradeId = null;
        }
        r.GradeId = finalGradeId;
        r.FinalGradeCode = finalGrade?.Code ?? string.Empty;

        // ── Position (Leak #4a/b/c): not-found / unavailable / ineligible → null + gap + warning. NEVER drops.
        var posCodeRaw = V("PositionCode").ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(posCodeRaw))
        {
            var positionClash = lk.AmbiguousValues("position", posCodeRaw);
            if (positionClash is not null)
            {
                r.Gaps.Add(new ImportGap("org:position", "org", $"PositionCode '{posCodeRaw}' matches more than one position ({Clash(positionClash)}) — position left unassigned.", posCodeRaw));
                r.Warnings.Add($"PositionCode '{posCodeRaw}' matches more than one position — imported without a position.");
            }
            else if (!lk.PositionsByCode.TryGetValue(posCodeRaw, out var position))
            {
                r.Gaps.Add(new ImportGap("org:position", "org", $"PositionCode '{posCodeRaw}' not found — position left unassigned.", posCodeRaw));
                r.Warnings.Add($"PositionCode '{posCodeRaw}' not found — imported without a position.");
            }
            else
            {
                // The SAME decision as the Add Employee form (EmployeeAssignmentRules.CheckPosition), plus the
                // import-only in-file claim (two rows in one file cannot both take one position).
                var refusal = claimedPositionCodes.Contains(posCodeRaw)
                    ? PositionRefusal.Occupied
                    : EmployeeAssignmentRules.CheckPosition(position, null, joiningDate,
                        r.CompanyId, r.BranchId, r.DepartmentId, r.CostCenterId, r.DesignationId, finalGradeId);
                var (detail, warning) = refusal switch
                {
                    PositionRefusal.None => (null, null),
                    PositionRefusal.FrozenOrClosed or PositionRefusal.Occupied =>
                        ($"PositionCode '{posCodeRaw}' is not available for assignment.",
                         $"PositionCode '{posCodeRaw}' is not available for assignment — imported without a position."),
                    PositionRefusal.JoiningDateUnknown =>
                        ($"PositionCode '{posCodeRaw}' cannot be checked without a readable joining date — position left unassigned.",
                         $"PositionCode '{posCodeRaw}' needs a readable joining date — imported without a position."),
                    PositionRefusal.NotYetEffective =>
                        ($"PositionCode '{posCodeRaw}' is not effective on the joining date (it starts {position.EffectiveFrom:yyyy-MM-dd}).",
                         $"PositionCode '{posCodeRaw}' is not effective on the joining date — imported without a position."),
                    PositionRefusal.Expired =>
                        ($"PositionCode '{posCodeRaw}' expired ({position.EffectiveTo:yyyy-MM-dd}) before the joining date.",
                         $"PositionCode '{posCodeRaw}' expired before the joining date — imported without a position."),
                    _ =>
                        ($"PositionCode '{posCodeRaw}' is not eligible for the supplied organization, designation, or grade.",
                         $"PositionCode '{posCodeRaw}' is not eligible for the supplied org/designation/grade — imported without a position."),
                };
                if (detail is not null)
                {
                    r.Gaps.Add(new ImportGap("org:position", "org", detail, posCodeRaw));
                    r.Warnings.Add(warning!);
                }
                else
                {
                    claimedPositionCodes.Add(posCodeRaw);
                    r.PositionId = position.Id;
                }
            }
        }

        // ── Salary (Leak #5/#6): no valid grade → HOLD; outside band → REVIEW. NEVER drops.
        var gross = GrossSalaryFromRow(row);
        r.GrossSalary = gross;
        if (gross > 0 && finalGrade is null)
        {
            r.SalaryDecision = ImportSalaryDecision.Hold;
            r.Gaps.Add(new ImportGap("pay:salaryHeld", "pay", "Salary supplied without a valid grade — salary held pending a grade.", gross.ToString("N2")));
            r.Warnings.Add("Salary supplied but no valid grade — person imported, salary held until a grade is assigned.");
        }
        else if (finalGrade is not null && gross > 0
                 && ((finalGrade.MinSalary > 0 && gross < finalGrade.MinSalary) || (finalGrade.MaxSalary > 0 && gross > finalGrade.MaxSalary)))
        {
            r.SalaryDecision = ImportSalaryDecision.Review;
            r.Gaps.Add(new ImportGap("pay:salaryReview", "pay",
                $"Salary {gross:N2} is outside grade {finalGrade.Code} range {finalGrade.MinSalary:N2}-{finalGrade.MaxSalary:N2}.", gross.ToString("N2")));
            r.Warnings.Add($"Salary {gross:N2} is outside grade {finalGrade.Code} band — imported and marked for review.");
        }

        return r;
    }
}

/// <summary>
/// Durable-advisory self-heal for the readiness stamp paths. On every recompute-and-stamp, open import
/// gaps for the employee are re-derived against the employee's CURRENT state: a gap that the operator has
/// since completed (e.g. a department was linked) is stamped resolved; still-open gaps are folded back into
/// the readiness as advisory items so the NeedsAttention signal (and its People-list deep-link) survives an
/// unrelated edit instead of being wiped back to Ready. Mutates gap rows on the TRACKED context; the caller
/// persists. Best-effort: any failure returns the input readiness unchanged.
/// </summary>
public static class ImportGapHealer
{
    public static async Task<EmployeeReadiness> HealAndMergeAsync(
        ZayraDbContext db, Employee employee, EmployeeReadiness readiness, CancellationToken ct)
    {
        if (employee.TenantId is null || employee.Id == 0) return readiness;
        List<EmployeeImportGap> open;
        try
        {
            open = await db.EmployeeImportGaps
                .Where(g => g.TenantId == employee.TenantId!.Value && g.EmployeeId == employee.Id && g.ResolvedAtUtc == null)
                .ToListAsync(ct);
        }
        catch { return readiness; }
        if (open.Count == 0) return readiness;

        // Load the employing company's email domain once, only when an email gap is present, so
        // email:domain-mismatch can self-heal once the address is edited to match the domain.
        string? companyDomain = null;
        if (open.Any(g => g.GapType.StartsWith("email:", StringComparison.OrdinalIgnoreCase)) && employee.CompanyId is Guid cid)
        {
            try
            {
                companyDomain = ((await db.Companies.AsNoTracking()
                    .Where(c => c.TenantId == employee.TenantId!.Value && c.Id == cid)
                    .Select(c => c.EmailDomain).FirstOrDefaultAsync(ct)) ?? string.Empty).Trim().ToLowerInvariant();
            }
            catch { /* best-effort: leave null → email:domain-mismatch stays human-resolved */ }
        }

        var now = DateTime.UtcNow;
        var stillOpen = new List<ReadinessItem>();
        foreach (var g in open)
        {
            if (IsHealed(g.GapType, employee, companyDomain)) g.ResolvedAtUtc = now;
            else stillOpen.Add(EmployeeReadinessEvaluator.ImportGapToItem(g.GapType, g.GapCategory));
        }
        return EmployeeReadinessEvaluator.MergeAdvisoryGaps(readiness, stillOpen);
    }

    private static bool IsHealed(string gapType, Employee e, string? companyDomain) => gapType switch
    {
        "org:company" => e.CompanyId is not null,
        "org:branch" => e.BranchId is not null,
        "org:department" => e.DepartmentId is not null,
        "org:designation" => e.DesignationId is not null,
        "org:grade" => e.GradeId is not null,
        "org:position" => e.PositionId is not null,
        "pay:salaryHeld" => e.GradeId is not null && (e.Salary ?? 0m) > 0m,
        // Work-email gaps: needs-info clears once any work email is present; domain-mismatch clears once the
        // address is edited to end with the company domain (self-heal parity with the org gaps). A duplicate
        // needs a human/re-derive decision — never auto-heals.
        "email:needs-info" => !string.IsNullOrWhiteSpace(e.WorkEmail),
        "email:domain-mismatch" => !string.IsNullOrWhiteSpace(companyDomain)
            && !string.IsNullOrWhiteSpace(e.WorkEmail)
            && e.WorkEmail.Trim().EndsWith("@" + companyDomain, StringComparison.OrdinalIgnoreCase),
        "email:duplicate" => false,
        "link:manager" => e.ManagerEmployeeId is not null,
        "link:supervisor" => e.SupervisorEmployeeId is not null,
        // dup:* need an explicit human decision (confirm distinct / merge) — never auto-heal. The resolve
        // endpoint stamps ResolvedAtUtc directly. (The `_ => false` default already covers these; the
        // explicit arms document that a duplicate flag clearing is a human action, not a field completion.)
        "dup:strong" or "dup:possible" => false,
        // pay:salaryReview / org:establishment need a human decision — never auto-heal.
        _ => false,
    };
}
