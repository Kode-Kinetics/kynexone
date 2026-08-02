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
public sealed record DesignationRef(Guid Id, Guid? GradeId, string TitleEn);

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
}

/// <summary>The resolved FKs + typed gaps for a single CSV row — the SINGLE SOURCE OF TRUTH for every
/// accept-never-block decision. It never signals a drop: a row is dropped only for no-name or duplicate
/// EmployeeCode, both decided by the caller against the DB/batch, never here.</summary>
public sealed class ResolvedImportRow
{
    public Guid? CompanyId { get; set; }
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
        // Company/Branch/CostCenter: IsActive && !IsDeleted (operational master data).
        var companiesByName = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .ToDictionaryAsync(c => c.LegalNameEn.ToUpperInvariant(), ct);
        var defaultCompany = companiesByName.Values.OrderBy(c => c.CreatedAtUtc).FirstOrDefault();

        var branchesByCode = (await db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.IsActive && !b.IsDeleted)
            .ToListAsync(ct))
            .GroupBy(b => (b.CompanyId, Code: b.Code.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).First());
        var branchesByCompany = branchesByCode.Values
            .GroupBy(b => b.CompanyId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).ToList());

        var costCentersByCode = (await db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .ToListAsync(ct))
            .GroupBy(c => (c.CompanyId, Code: c.Code.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).First());

        // Departments/Designations/Grades: !IsDeleted (matches the authoritative commit filter — parity).
        var deptByCode = await db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .ToDictionaryAsync(d => d.Code.ToUpperInvariant(), d => d.Id, ct);
        var deptByName = await db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .ToDictionaryAsync(d => d.NameEn.ToLowerInvariant(), d => d.Id, ct);
        var desigByTitle = (await db.Designations.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .Select(d => new { d.Id, d.GradeId, d.TitleEn })
            .ToListAsync(ct))
            .GroupBy(d => d.TitleEn.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => new DesignationRef(g.First().Id, g.First().GradeId, g.First().TitleEn));
        var grades = await db.Grades.AsNoTracking()
            .Where(g => g.TenantId == tenantId && !g.IsDeleted)
            .ToListAsync(ct);
        var gradeByCode = grades.GroupBy(g => g.Code.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        var gradeByName = grades.GroupBy(g => g.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var gradeById = grades.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
        var positionsByCode = await db.Positions.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Code.ToUpperInvariant(), ct);

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
        };
    }

    public static decimal GrossSalaryFromRow(Dictionary<string, string> row)
    {
        static decimal Amount(Dictionary<string, string> source, string key) =>
            decimal.TryParse(source.GetValueOrDefault(key, string.Empty), out var value) ? value : 0m;
        return Amount(row, "BasicSalary") + Amount(row, "HousingAllowance") + Amount(row, "TransportAllowance")
             + Amount(row, "FoodAllowance") + Amount(row, "MobileAllowance") + Amount(row, "OtherAllowance");
    }

    /// <summary>Resolve one row. NEVER drops. Every unresolved reference becomes a typed gap + a warning,
    /// and the person is imported with the weakest-safe FK (null / default company). <paramref name="claimedPositionCodes"/>
    /// is mutated cumulatively (file order wins) exactly as the legacy commit loop did.</summary>
    public static ResolvedImportRow ResolveRow(Dictionary<string, string> row, ImportLookups lk, HashSet<string> claimedPositionCodes)
    {
        var r = new ResolvedImportRow();
        string V(string k) => row.GetValueOrDefault(k, string.Empty).Trim();

        // ── Company (Leak #1): unknown → default company (or null if tenant has none). NEVER drops.
        var companyNameRaw = V("CompanyLegalName");
        Company? company = string.IsNullOrWhiteSpace(companyNameRaw)
            ? lk.DefaultCompany
            : lk.CompaniesByName.GetValueOrDefault(companyNameRaw.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(companyNameRaw) && company is null)
        {
            company = lk.DefaultCompany; // may still be null when the tenant has zero companies
            r.Gaps.Add(new ImportGap("org:company", "org",
                company is not null
                    ? $"Company '{companyNameRaw}' not found — assigned the default company."
                    : $"Company '{companyNameRaw}' not found and no company exists — imported without a company.",
                companyNameRaw));
            r.Warnings.Add(company is not null
                ? $"CompanyLegalName '{companyNameRaw}' not found — assigned default company '{company.LegalNameEn}'."
                : $"CompanyLegalName '{companyNameRaw}' not found and no company exists to default to — imported without a company.");
        }
        r.CompanyId = company?.Id;

        // ── Work email (auto-derive when blank / validate against company domain; accept-never-block) ──
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
            // Blank + domain present → derive a candidate local part from the name; controller finalizes uniqueness.
            var local = WorkEmailDeriver.BuildLocalPart(V("FullName"), V("ArabicName"), emailPattern);
            if (!string.IsNullOrEmpty(local))
                r.WorkEmailLocalPart = local;
            else
            {
                // No romanizable (Latin) name (e.g. Arabic-only) — cannot derive an ASCII local part.
                r.Gaps.Add(new ImportGap("email:needs-info", "readiness", "Work email not derived — name has no romanizable form; enter it manually.", null));
                r.Warnings.Add("Work email not derived — name has no romanizable (Latin) form; enter it manually.");
            }
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

        // ── Branch: optional; unknown → null + gap + warning.
        var branchCodeRaw = V("BranchCode").ToUpperInvariant();
        Branch? branch = company is not null
            ? (!string.IsNullOrWhiteSpace(branchCodeRaw)
                ? lk.BranchesByCode.GetValueOrDefault((company.Id, branchCodeRaw))
                : (lk.BranchesByCompany.GetValueOrDefault(company.Id)?.FirstOrDefault()))
            : null;
        if (!string.IsNullOrWhiteSpace(branchCodeRaw) && branch is null)
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
        r.BranchId = branch?.Id;
        r.BranchNameEn = branch?.NameEn ?? string.Empty;

        // ── Cost center: optional; unknown → null + warning (no gap; auto-creating from a typo pollutes finance master data).
        var ccRaw = V("CostCenterCode").ToUpperInvariant();
        CostCenter? cc = company is not null && !string.IsNullOrWhiteSpace(ccRaw)
            ? lk.CostCentersByCode.GetValueOrDefault(((Guid?)company.Id, ccRaw))
            : null;
        if (!string.IsNullOrWhiteSpace(ccRaw) && cc is null)
            r.Warnings.Add(company is null
                ? $"CostCenterCode '{ccRaw}' supplied but no company could be resolved — imported without a cost center."
                : $"CostCenterCode '{ccRaw}' not found for company '{company.LegalNameEn}' — imported without a cost center.");
        r.CostCenterId = cc?.Id;
        r.CostCenterCode = cc?.Code ?? string.Empty;

        // ── Department: unknown → free text, no FK + gap + warning (was a silent leak).
        var deptNameRaw = V("Department");
        var deptCodeRaw = V("DepartmentCode").ToUpperInvariant();
        Guid? deptId = null;
        if (!string.IsNullOrEmpty(deptCodeRaw) && lk.DeptByCode.TryGetValue(deptCodeRaw, out var d1)) deptId = d1;
        else if (!string.IsNullOrEmpty(deptNameRaw) && lk.DeptByName.TryGetValue(deptNameRaw.ToLowerInvariant(), out var d2)) deptId = d2;
        if (deptId is null && (!string.IsNullOrEmpty(deptCodeRaw) || !string.IsNullOrEmpty(deptNameRaw)))
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
        if (!string.IsNullOrEmpty(desigTitleRaw) && lk.DesigByTitle.TryGetValue(desigTitleRaw.ToLowerInvariant(), out var desig))
        {
            desigId = desig.Id;
            designationGradeId = desig.GradeId;
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
            if (!lk.GradeByCode.TryGetValue(gradeRaw.ToUpperInvariant(), out resolvedGrade))
                lk.GradeByName.TryGetValue(gradeRaw.ToLowerInvariant(), out resolvedGrade);
            if (resolvedGrade is null)
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
        r.GradeId = finalGradeId;
        r.FinalGradeCode = finalGrade?.Code ?? string.Empty;

        // ── Position (Leak #4a/b/c): not-found / unavailable / ineligible → null + gap + warning. NEVER drops.
        var posCodeRaw = V("PositionCode").ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(posCodeRaw))
        {
            if (!lk.PositionsByCode.TryGetValue(posCodeRaw, out var position))
            {
                r.Gaps.Add(new ImportGap("org:position", "org", $"PositionCode '{posCodeRaw}' not found — position left unassigned.", posCodeRaw));
                r.Warnings.Add($"PositionCode '{posCodeRaw}' not found — imported without a position.");
            }
            else if (position.Status is PositionStatuses.Frozen or PositionStatuses.Closed
                     || position.IncumbentEmployeeId is not null
                     || claimedPositionCodes.Contains(posCodeRaw))
            {
                r.Gaps.Add(new ImportGap("org:position", "org", $"PositionCode '{posCodeRaw}' is not available for assignment.", posCodeRaw));
                r.Warnings.Add($"PositionCode '{posCodeRaw}' is not available for assignment — imported without a position.");
            }
            else if ((position.CompanyId is not null && position.CompanyId != r.CompanyId)
                     || (position.BranchId is not null && position.BranchId != r.BranchId)
                     || (position.DepartmentId is not null && position.DepartmentId != r.DepartmentId)
                     || (position.DesignationId is not null && position.DesignationId != r.DesignationId)
                     || (position.GradeId is not null && position.GradeId != finalGradeId))
            {
                r.Gaps.Add(new ImportGap("org:position", "org", $"PositionCode '{posCodeRaw}' is not eligible for the supplied organization, designation, or grade.", posCodeRaw));
                r.Warnings.Add($"PositionCode '{posCodeRaw}' is not eligible for the supplied org/designation/grade — imported without a position.");
            }
            else
            {
                claimedPositionCodes.Add(posCodeRaw);
                r.PositionId = position.Id;
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
