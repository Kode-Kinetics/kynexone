using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Mid-year cutover — the sections a consultant needs in order to go live in September rather than
/// January, bolted onto the existing migration engine rather than beside it.
///
/// <para>WHY EXTEND RATHER THAN BUILD ALONGSIDE. The engine already has the four things that are
/// expensive and dangerous to get wrong: a SHA-256 package checksum so a resume cannot be fed a
/// different file, a Postgres advisory lease held for the whole import, a durable
/// <c>MigrationImportBatch</c> ledger with per-section counts and a recorded current section, and —
/// most importantly — per-row idempotent upserts keyed on natural keys, which is what makes a re-run
/// safe. Every new section below is a case in the same dispatch table and inherits all four for free.
/// A parallel engine would have had to reimplement them, and two import paths writing opening balances
/// is precisely the shape of mistake that pays someone twice.</para>
/// </summary>
public sealed partial class MigrationImportController
{
    // ── Cutover context ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cutover dates in force for this import, keyed by legal entity, plus the batch the rows
    /// belong to. Built from the persisted <c>CompanyCutover</c> rows UNIONED with any the package
    /// itself declares, because declaring the cutover and the balances in one package is the normal
    /// consultant workflow and refusing it would mean two round trips for every wave.
    /// </summary>
    private sealed record CutoverContext(
        Guid TenantId,
        Guid BatchId,
        IReadOnlyDictionary<Guid, DateOnly> CutoverByCompany);

    private async Task<CutoverContext> LoadCutoverContextAsync(
        Guid tenantId, MigrationPackageRequest request, Guid batchId, CancellationToken ct)
    {
        var map = await _db.CompanyCutovers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanyId != null && x.Status != CutoverStatuses.Planned)
            .ToDictionaryAsync(x => x.CompanyId!.Value, x => x.CutoverDate, ct);

        // Cutovers declared in this very package. A malformed row here is NOT thrown from: the
        // companyCutover section validates and reports its own rows, and throwing out of context
        // construction would fail the whole package with an error attributed to the wrong section.
        // A SHIFTED cutover file is the same case: Csv.Parse refuses the whole file, the companyCutover
        // section reports that refusal by row number, and context construction simply finds no declared
        // cutovers — rather than aborting the package with the error attributed to whichever section
        // happened to be first.
        if (request.Sections.TryGetValue("companyCutover", out var csv)
            && TryParseSection("companyCutover", csv, out var cutoverRows, out _))
        {
            foreach (var row in cutoverRows)
            {
                try
                {
                    var company = await ResolveCutoverCompanyAsync(row, tenantId, ct);
                    // Anything but Planned governs (CutoverStatuses.Governs): a Closed cutover still
                    // states where the carried figures end.
                    if (!CutoverStatuses.Governs(Val(row, "Status", CutoverStatuses.Active)))
                        continue;
                    map[company.Id] = ReadCutoverDate(row);
                }
                catch { /* reported by the section itself */ }
            }
        }

        return new CutoverContext(tenantId, batchId, map);
    }

    /// <summary>
    /// The cutover governing this employee's balance — the gate that makes "a cutover date per legal
    /// entity, not per tenant" real rather than decorative. An employee in an entity that has not
    /// declared its wave is refused by name, even if a sister entity in the same tenant went live
    /// months ago.
    ///
    /// <para>MANDATORY for <c>loans</c>, <c>advances</c> and <c>eosbOpeningProvision</c>. Those are new
    /// sections with no prior behaviour to preserve, and an outstanding balance or a provision with no
    /// date it is "as at" is not a fact — it is a number.</para>
    ///
    /// <para>CONDITIONAL for <c>leaveBalances</c> and <c>payrollOpeningBalances</c>, which shipped long
    /// before cutovers existed. A tenant that has declared no cutover at all is not doing a mid-year
    /// migration — it is doing a greenfield or 1-January load, which is the case those sections were
    /// written for and which must keep working unchanged. The moment ANY entity in the tenant declares
    /// a cutover, the tenant is doing a governed migration and every balance must name its wave.</para>
    /// </summary>
    private static DateOnly? ResolveCutoverFor(Employee employee, CutoverContext cutover, bool mandatory)
    {
        if (!mandatory && cutover.CutoverByCompany.Count == 0) return null;

        if (employee.CompanyId is null)
            throw new InvalidOperationException(
                $"Employee '{employee.EmployeeCode}' has no legal entity, so no cutover date governs it. Assign the employee to a company before importing opening balances.");
        if (!cutover.CutoverByCompany.TryGetValue(employee.CompanyId.Value, out var date))
            throw new InvalidOperationException(
                $"No Active or Closed cutover is declared for the legal entity that owns employee '{employee.EmployeeCode}'. Import a companyCutover row for it first — an opening balance is meaningless without the date it is 'as at'.");
        return date;
    }

    /// <summary>
    /// The cutover for a payroll opening-balance row. MANDATORY for the buckets the payslip's year-to-date
    /// sums (YTD_GROSS / YTD_DEDUCTIONS / YTD_NET), whatever the tenant's other cutovers: without one,
    /// nothing says which months the carried figures already contain, the second month's run is blocked
    /// (YTD_OPENING_BALANCE_OVERLAP_UNRESOLVED), and the import that caused it had been accepted. Refused
    /// up front instead, with OPENING_BALANCE_NEEDS_CUTOVER. Detail buckets (statutory, tax, covered wage)
    /// are never summed into a payslip and stay optional, as before.
    /// </summary>
    private static DateOnly? ResolveCutoverForPayrollBalance(Employee employee, CutoverContext cutover, string balanceType)
    {
        if (!OpeningBalanceTypes.PayslipAggregates.Contains(balanceType, StringComparer.OrdinalIgnoreCase))
            return ResolveCutoverFor(employee, cutover, mandatory: false);
        if (employee.CompanyId is { } companyId && cutover.CutoverByCompany.TryGetValue(companyId, out var date))
            return date;
        throw new InvalidOperationException(
            $"[{CutoverStatuses.BalanceNeedsCutoverCode}] {balanceType} for '{employee.EmployeeCode}' needs a cutover for the "
          + "employee's legal entity — Active or Closed, or declared in this same package (companyCutover section). The "
          + "payslip's year-to-date adds this figure to the payslips this product locks, and only the cutover says which "
          + "months the carried figure already contains. Nothing has been saved for this row. "
          + "/ تتطلب أرصدة السنة حتى تاريخه تاريخ انتقال للكيان القانوني للموظف، ساري المفعول أو مُعلَن في الحزمة نفسها. لم يتم حفظ هذا السطر.");
    }

    /// <summary>The mandatory form, for the sections where a cutover is not optional.</summary>
    private static DateOnly RequireCutoverFor(Employee employee, CutoverContext cutover)
        => ResolveCutoverFor(employee, cutover, mandatory: true)!.Value;

    private async Task<Company> ResolveCutoverCompanyAsync(Dictionary<string, string> row, Guid tenantId, CancellationToken ct)
    {
        var registration = Val(row, "CompanyRegistrationNumber").Trim();
        var legalName = Val(row, "CompanyLegalName").Trim();
        if (registration.Length == 0 && legalName.Length == 0)
            throw new InvalidOperationException("One of CompanyRegistrationNumber or CompanyLegalName is required to identify the legal entity.");

        var query = _db.Companies.Where(x => x.TenantId == tenantId);
        query = registration.Length > 0
            ? query.Where(x => x.RegistrationNumber == registration)
            : query.Where(x => x.LegalNameEn == legalName);

        var matches = await query.Take(2).ToListAsync(ct);
        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"No legal entity matches {(registration.Length > 0 ? $"registration number '{registration}'" : $"legal name '{legalName}'")}.");
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"More than one legal entity matches {(registration.Length > 0 ? $"registration number '{registration}'" : $"legal name '{legalName}'")}. Use the registration number, which is unique per entity.");
        return matches[0];
    }

    /// <summary>The row's CutoverDate, refused unless it is the 1st of a month (CUTOVER_NOT_FIRST_OF_MONTH).</summary>
    private static DateOnly ReadCutoverDate(Dictionary<string, string> row)
    {
        var date = DateReq(row, "CutoverDate");
        if (CutoverStatuses.FirstOfMonthRefusal(date) is { } refusal)
            throw new InvalidOperationException(refusal);
        return date;
    }

    private static void RequireCutoverStatus(Dictionary<string, string> row)
    {
        var status = Val(row, "Status", CutoverStatuses.Active).Trim();
        if (!CutoverStatuses.All.Contains(status, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Status '{status}' is not a cutover status. Use one of: {string.Join(", ", CutoverStatuses.All)}.");
    }

    /// <summary>
    /// The natural key a row upserts on, for the sections introduced by this stream — or null for a
    /// section that does not participate.
    ///
    /// <para>The upsert lookups below read the DATABASE, and a section's rows are not saved until the
    /// section finishes. Two rows in one file carrying the same LoanNumber therefore both miss, both
    /// create, and the employee ends up with the loan twice — deducted twice, every month. A duplicate
    /// natural key inside one file is always a mistake in the extract, so it is refused by name on the
    /// second occurrence rather than resolved by guessing which row was meant.</para>
    /// </summary>
    private static string? DuplicateGuardKey(string section, Dictionary<string, string> row) => section switch
    {
        "loans" => $"loan:{Val(row, "LoanNumber").Trim().ToUpperInvariant()}",
        "advances" => $"advance:{Val(row, "AdvanceNumber").Trim().ToUpperInvariant()}",
        "eosbOpeningProvision" => $"eosb:{Val(row, "EmployeeCode").Trim().ToUpperInvariant()}|{Val(row, "AsAtDate").Trim()}",
        "companyCutover" => $"cutover:{Val(row, "CompanyRegistrationNumber").Trim().ToUpperInvariant()}|{Val(row, "CompanyLegalName").Trim().ToUpperInvariant()}",
        _ => null,
    };

    private static void GuardDuplicate(string section, Dictionary<string, string> row, HashSet<string> seen)
    {
        GuardPayslipAggregateInFile(section, row, seen);
        var key = DuplicateGuardKey(section, row);
        if (key is null) return;
        if (!seen.Add(key))
            throw new InvalidOperationException(
                $"This row repeats a key already used earlier in the same file ({key}). Remove the duplicate — importing it twice would create the balance twice.");
    }

    // ── MI1: the payslip-aggregate double-count guard ───────────────────────────────────────────────
    //
    // OpeningBalanceTypes.PayslipAggregates has always carried the sentence "the importer rejects the
    // combination loudly" and the importer never did — the symbol had exactly one occurrence in the
    // solution, its own declaration. The hole is precise: the upsert key is
    // (TenantId, EmployeeId, Year, BalanceType, ComponentCode), so a YTD_GROSS/TOTAL row and a
    // YTD_GROSS/BASIC row are two different rows; PayrollController.SumOpeningBalance then matches on
    // BALANCE TYPE ALONE and adds BOTH to the payslip. Every payslip for the rest of the year reports
    // the year-to-date at roughly double, and the same applies to YTD_DEDUCTIONS and YTD_NET.
    //
    // The rule enforced here is therefore: for the three buckets the payslip consumes, ONE row per
    // (employee, year, bucket), whatever the ComponentCode says. The statutory/tax/covered-wage buckets
    // are untouched — they are carried detail, nothing sums them into a payslip, and per-component
    // splits there are exactly what the annual GOSI reconciliation needs.
    //
    // Per-component YTD detail is not lost by this: import it under a non-aggregate bucket, or carry the
    // aggregate and keep the component split in the source system. What is refused is the one
    // combination that silently doubles a number printed on an employee's payslip.

    private const string PayslipAggregateComponentAdvice =
        "The payslip's year-to-date block sums EVERY row of this balance type for the employee and year "
      + "and ignores ComponentCode entirely, so carrying a total row AND per-component detail rows under "
      + "the same bucket reports roughly double the year-to-date on every payslip for the rest of the "
      + "year. Carry ONE row per employee, year and bucket — normally ComponentCode=TOTAL. Per-component "
      + "detail belongs under the non-payslip buckets (YTD_STATUTORY_EE, YTD_STATUTORY_ER, YTD_TAX, "
      + "YTD_COVERED_WAGE), which are carried for reconciliation and are never summed into a payslip.";

    private static string? PayslipAggregateKey(string section, Dictionary<string, string> row, out string componentCode)
    {
        componentCode = string.Empty;
        if (section != "payrollOpeningBalances") return null;
        var raw = Val(row, "BalanceType").Trim();
        var canonical = OpeningBalanceTypes.PayslipAggregates
            .FirstOrDefault(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase));
        if (canonical is null) return null;
        componentCode = Val(row, "ComponentCode").Trim();
        var employeeCode = Val(row, "EmployeeCode").Trim().ToUpperInvariant();
        var year = Val(row, "Year").Trim();
        return $"ytd-aggregate:{employeeCode}|{year}|{canonical}";
    }

    /// <summary>MI1 — two rows in ONE file under the same payslip aggregate, differing only by component.</summary>
    private static void GuardPayslipAggregateInFile(string section, Dictionary<string, string> row, HashSet<string> seen)
    {
        var key = PayslipAggregateKey(section, row, out var componentCode);
        if (key is null) return;
        var marker = $"{key}=";
        var prior = seen.FirstOrDefault(s => s.StartsWith(marker, StringComparison.Ordinal));
        if (prior is not null)
        {
            var priorComponent = prior[marker.Length..];
            if (string.Equals(priorComponent, componentCode, StringComparison.OrdinalIgnoreCase)) return; // exact dup → DuplicateGuardKey's message
            throw new InvalidOperationException(
                $"This file already carries {key[("ytd-aggregate:".Length)..].Replace("|", " / ")} under ComponentCode "
              + $"'{priorComponent}', and this row adds '{componentCode}' to the same bucket. "
              + PayslipAggregateComponentAdvice);
        }
        seen.Add($"{marker}{componentCode}");
    }

    /// <summary>MI1 — a row that would join a payslip aggregate ALREADY STORED under a different component.</summary>
    private async Task GuardPayslipAggregateAgainstStoredAsync(
        Guid tenantId, Employee employee, int year, string balanceType, string componentCode, CancellationToken ct)
    {
        if (!OpeningBalanceTypes.PayslipAggregates.Contains(balanceType, StringComparer.OrdinalIgnoreCase)) return;
        var existing = await _db.PayrollOpeningBalances.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id
                     && x.Year == year && x.BalanceType == balanceType
                     && x.ComponentCode != componentCode)
            .Select(x => x.ComponentCode)
            .Take(3)
            .ToListAsync(ct);
        if (existing.Count == 0) return;
        throw new InvalidOperationException(
            $"{employee.EmployeeCode} already has {balanceType} for {year} stored under ComponentCode "
          + $"'{string.Join("', '", existing)}', and this row would add '{componentCode}' to the same bucket. "
          + PayslipAggregateComponentAdvice
          + " Correct the stored row or this one; they cannot both stand.");
    }

    private static string RequireBalanceType(Dictionary<string, string> row)
    {
        var raw = Require(row, "BalanceType").Trim();
        if (!OpeningBalanceTypes.IsKnown(raw))
            throw new InvalidOperationException(
                $"BalanceType '{raw}' is not a recognised opening-balance bucket. Use one of: {string.Join(", ", OpeningBalanceTypes.All)}. " +
                "A silently-accepted wrong YTD is worse than a rejected row, so unknown buckets are refused rather than stored and ignored.");
        return OpeningBalanceTypes.All.First(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Currency comes from the employee's own legal entity, then the tenant, and only then from the
    /// row. It is never defaulted to a literal.
    /// </summary>
    private async Task<string> ResolveCurrencyAsync(Dictionary<string, string> row, Employee employee, Guid tenantId, CancellationToken ct)
    {
        var explicitCurrency = Val(row, "Currency").Trim();
        if (explicitCurrency.Length > 0) return explicitCurrency.ToUpperInvariant();

        if (employee.CompanyId is not null)
        {
            var companyCurrency = await _db.Companies.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == employee.CompanyId.Value)
                .Select(x => x.DefaultCurrency)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(companyCurrency) && !string.Equals(companyCurrency, "USD", StringComparison.OrdinalIgnoreCase))
                return companyCurrency.ToUpperInvariant();
        }

        var tenantCurrency = await _db.ResolveTenantCurrencyAsync(tenantId, ct);
        return string.IsNullOrWhiteSpace(tenantCurrency) ? "SAR" : tenantCurrency.ToUpperInvariant();
    }

    // ── Locked-period refusal ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The package-level refusals, computed BEFORE anything is written. Two rules:
    ///
    /// <para><b>1. A cutover change may not move a boundary a locked run already used.</b> A companyCutover
    /// row that changes the date, or changes whether the cutover governs (Planned ↔ Active/Closed), is
    /// refused when a Locked run exists in or after the EARLIER of the old and new cutover months: that
    /// run's year-to-date was partitioned at the old boundary, and every later payslip would be
    /// partitioned at a different one. Active → Closed does not change the boundary and is allowed — a
    /// finished migration is closed precisely after months have been locked. This rule runs even when the
    /// package carries ONLY the cutover row.</para>
    ///
    /// <para><b>2. Opening balances may not restate a locked period.</b> A package carrying balances is
    /// refused for a legal entity that has LOCKED a run in the cutover month or later. Locked runs BEFORE
    /// the cutover are normal on a re-migration or a wave — they belong to a period this product
    /// legitimately owned; a locked run on or after it is a payslip in an employee's hands and a journal
    /// in the GL.</para>
    /// </summary>
    private async Task<List<LockedPeriodRefusal>> FindLockedPeriodRefusalsAsync(
        Guid tenantId, MigrationPackageRequest request, CancellationToken ct)
    {
        var refusals = new List<LockedPeriodRefusal>();

        // ── 1. Cutover changes — whatever else the package carries ─────────────────────────────────
        var changes = new List<(Guid CompanyId, DateOnly Boundary, string What)>();
        var firstDeclarationRefusals = new List<LockedPeriodRefusal>();
        if (request.Sections.TryGetValue("companyCutover", out var cutoverCsv)
            && TryParseSection("companyCutover", cutoverCsv, out var cutoverRows, out _))
        {
            foreach (var row in cutoverRows)
            {
                Company company; DateOnly newDate;
                try
                {
                    company = await ResolveCutoverCompanyAsync(row, tenantId, ct);
                    newDate = DateReq(row, "CutoverDate");
                }
                catch { continue; /* the section reports its own malformed rows */ }

                var newGoverns = CutoverStatuses.Governs(Val(row, "Status", CutoverStatuses.Active));
                var existing = await _db.CompanyCutovers.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.CompanyId == company.Id, ct);
                var oldGoverns = existing is not null && CutoverStatuses.Governs(existing.Status);

                // ── A FIRST declaration is not a change ───────────────────────────────────────────────
                // No cutover has governed this entity, so no locked run was partitioned by one — there is
                // no boundary to move. It is allowed on or before the month of the EARLIEST run this product
                // locked in that year (or later years): those payslips are then on the earned-here side, and
                // are left out of YTD only for employees whose carried balance stands in for them. A later
                // month would put locked product months on the carried side for everyone, so it is refused
                // naming the latest month that can be declared.
                if (!oldGoverns && newGoverns)
                {
                    var declaredMonth = new DateOnly(newDate.Year, newDate.Month, 1);
                    var earliest = await _db.PayrollRuns.AsNoTracking()
                        .Where(r => r.TenantId == tenantId && r.CompanyId == company.Id && r.Status == "Locked"
                                 && r.Year >= newDate.Year)
                        .OrderBy(r => r.Year).ThenBy(r => r.Month)
                        .Select(r => new { r.Id, r.Year, r.Month, r.RunType })
                        .FirstOrDefaultAsync(ct);
                    if (earliest is not null && declaredMonth > new DateOnly(earliest.Year, earliest.Month, 1))
                    {
                        var latestAllowed = new DateOnly(earliest.Year, earliest.Month, 1);
                        firstDeclarationRefusals.Add(new LockedPeriodRefusal(
                            Code: CutoverStatuses.FirstDeclarationTooLateCode,
                            CompanyId: company.Id,
                            CompanyName: company.LegalNameEn,
                            PayrollRunId: earliest.Id,
                            Period: $"{earliest.Year}-{earliest.Month:D2}",
                            RunType: earliest.RunType,
                            CutoverDate: newDate,
                            Reason: $"A first cutover of {newDate:yyyy-MM-dd} is later than payroll run {earliest.Id} for "
                                  + $"{earliest.Year}-{earliest.Month:D2}, which this product has already locked. The latest cutover "
                                  + $"that can be declared is {latestAllowed:yyyy-MM-dd}: declare that date (or an earlier 1st) and state "
                                  + $"the carried opening balances as at {latestAllowed.AddDays(-1):yyyy-MM-dd}.",
                            ReasonAr: $"لا يمكن أن يكون أول تاريخ انتقال لاحقاً لمسير رواتب مقفل. آخر تاريخ يمكن إعلانه هو "
                                    + $"{latestAllowed:yyyy-MM-dd}، مع بيان الأرصدة الافتتاحية كما في {latestAllowed.AddDays(-1):yyyy-MM-dd}."));
                    }
                    continue;
                }

                var boundaryChanges = oldGoverns != newGoverns
                    || (oldGoverns && newGoverns && existing!.CutoverDate != newDate);
                if (!boundaryChanges) continue;

                var months = new List<DateOnly>();
                if (oldGoverns) months.Add(new DateOnly(existing!.CutoverDate.Year, existing.CutoverDate.Month, 1));
                if (newGoverns) months.Add(new DateOnly(newDate.Year, newDate.Month, 1));
                var what = existing is null
                    ? $"declare a {newDate:yyyy-MM-dd} cutover"
                    : $"change the cutover from {existing.CutoverDate:yyyy-MM-dd} ({existing.Status}) to {newDate:yyyy-MM-dd} ({Val(row, "Status", CutoverStatuses.Active).Trim()})";
                changes.Add((company.Id, months.Min(), what));
            }
        }

        // ── 2. Balances against the governing cutovers ──────────────────────────────────────────────
        var carriesBalances = OpeningBalanceSections.Any(s => request.Sections.ContainsKey(s));
        var cutover = carriesBalances
            ? await LoadCutoverContextAsync(tenantId, request, Guid.Empty, ct)
            : new CutoverContext(tenantId, Guid.Empty, new Dictionary<Guid, DateOnly>());

        refusals.AddRange(firstDeclarationRefusals);
        var companyIds = changes.Select(c => c.CompanyId).Concat(cutover.CutoverByCompany.Keys).Distinct().ToList();
        if (companyIds.Count == 0) return refusals;

        var lockedRuns = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Status == "Locked"
                     && r.CompanyId != null && companyIds.Contains(r.CompanyId.Value))
            .Select(r => new { r.Id, r.CompanyId, r.Year, r.Month, r.RunType })
            .ToListAsync(ct);
        if (lockedRuns.Count == 0) return refusals;

        var companyNames = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, ct);

        foreach (var change in changes)
        {
            foreach (var run in lockedRuns.Where(r => r.CompanyId == change.CompanyId))
            {
                if (new DateOnly(run.Year, run.Month, 1) < change.Boundary) continue;
                refusals.Add(new LockedPeriodRefusal(
                    Code: CutoverStatuses.ChangeAfterLockedRunCode,
                    CompanyId: change.CompanyId,
                    CompanyName: companyNames.GetValueOrDefault(change.CompanyId, "(unnamed entity)"),
                    PayrollRunId: run.Id,
                    Period: $"{run.Year}-{run.Month:D2}",
                    RunType: run.RunType,
                    CutoverDate: change.Boundary,
                    Reason: $"This package would {change.What}, but payroll run {run.Id} for {run.Year}-{run.Month:D2} is Locked "
                          + "and its year-to-date was split at the current cutover. Moving the boundary now would split later "
                          + "payslips differently from the ones already paid. Void the locked run(s) from that month on first, "
                          + "or keep the cutover as it is. Closing a cutover (Active to Closed) is always allowed.",
                    ReasonAr: "لا يمكن تغيير تاريخ الانتقال أو حالته بعد إقفال مسير رواتب في شهر الانتقال أو بعده، لأن أرصدة "
                            + "السنة حتى تاريخه احتُسبت على أساس التاريخ الحالي. ألغِ المسيرات المقفلة أولاً أو أبقِ تاريخ الانتقال كما هو."));
            }
        }

        if (carriesBalances)
        {
            foreach (var run in lockedRuns)
            {
                if (!cutover.CutoverByCompany.TryGetValue(run.CompanyId!.Value, out var cutoverDate)) continue;
                var runStart = new DateOnly(run.Year, run.Month, 1);
                var cutoverMonth = new DateOnly(cutoverDate.Year, cutoverDate.Month, 1);
                if (runStart < cutoverMonth) continue;

                refusals.Add(new LockedPeriodRefusal(
                    Code: CutoverStatuses.BalanceIntoLockedPeriodCode,
                    CompanyId: run.CompanyId!.Value,
                    CompanyName: companyNames.GetValueOrDefault(run.CompanyId!.Value, "(unnamed entity)"),
                    PayrollRunId: run.Id,
                    Period: $"{run.Year}-{run.Month:D2}",
                    RunType: run.RunType,
                    CutoverDate: cutoverDate,
                    Reason: $"Payroll run {run.Id} for {run.Year}-{run.Month:D2} is Locked, and that period is on or after the {cutoverDate:yyyy-MM-dd} cutover for this legal entity. "
                          + "Opening balances may not restate a period that has already been paid and journalled. Void the locked run(s) "
                          + "from the cutover month on (each produces a Replacement) before re-importing; the cutover date itself cannot "
                          + "be moved once a run on or after it is locked.",
                    ReasonAr: "لا يمكن استيراد أرصدة افتتاحية لفترة يوجد بها مسير رواتب مقفل في شهر الانتقال أو بعده. "
                            + "ألغِ المسيرات المقفلة من شهر الانتقال فصاعداً قبل إعادة الاستيراد؛ ولا يمكن نقل تاريخ الانتقال بعد إقفال مسير."));
            }
        }
        return refusals;
    }

    private sealed record LockedPeriodRefusal(
        string Code,
        Guid CompanyId,
        string CompanyName,
        Guid PayrollRunId,
        string Period,
        string RunType,
        DateOnly CutoverDate,
        string Reason,
        string ReasonAr);

    // ── Provenance ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Record that one row was carried in. Idempotent on (EntityType, EntityId), so re-importing the
    /// same package refreshes the claim rather than appending a second one.
    ///
    /// <para><c>CarriedAmount</c> is frozen at import and deliberately never maintained afterwards. A
    /// loan carried at 17,000 that payroll has since worked down to 4,250 must still answer "17,000 was
    /// carried in on 2026-09-01" — that is the whole point of the table.</para>
    /// </summary>
    private async Task StampOriginAsync(
        string entityType, Guid entityId, Employee employee, DateOnly? cutoverDate, decimal carriedAmount,
        string currency, string sourceSystem, string sourceRecordId, CutoverContext cutover, CancellationToken ct)
    {
        // No cutover means an ungoverned legacy load (see ResolveCutoverFor). There is no wave to
        // attribute the row to, so no provenance claim is made — an origin row asserting a cutover
        // date that was never declared would be a worse answer than none.
        if (cutoverDate is null) return;

        var existing = await _db.OpeningBalanceOrigins.FirstOrDefaultAsync(
            x => x.TenantId == cutover.TenantId && x.EntityType == entityType && x.EntityId == entityId, ct);
        var created = existing is null;
        existing ??= new OpeningBalanceOrigin
        {
            TenantId = cutover.TenantId,
            EntityType = entityType,
            EntityId = entityId,
            CreatedBy = UserId()
        };
        existing.CompanyId = employee.CompanyId;
        existing.EmployeeId = employee.Id;
        existing.EmployeeCode = employee.EmployeeCode;
        existing.CutoverDate = cutoverDate.Value;
        existing.CarriedAmount = carriedAmount;
        existing.Currency = currency;
        existing.SourceSystem = sourceSystem;
        existing.SourceRecordId = sourceRecordId;
        existing.MigrationBatchId = cutover.BatchId;
        if (created) _db.OpeningBalanceOrigins.Add(existing);
        else { existing.UpdatedAtUtc = DateTime.UtcNow; existing.UpdatedBy = UserId(); }
    }

    // ── companyCutover ──────────────────────────────────────────────────────────────────────────────

    private async Task<string> UpsertCompanyCutoverAsync(Dictionary<string, string> row, Guid tenantId, CancellationToken ct)
    {
        var company = await ResolveCutoverCompanyAsync(row, tenantId, ct);
        RequireCutoverStatus(row);
        var item = await _db.CompanyCutovers.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.CompanyId == company.Id, ct);
        var created = item is null;
        item ??= new CompanyCutover { TenantId = tenantId, CompanyId = company.Id, CreatedBy = UserId() };
        item.CutoverDate = ReadCutoverDate(row);
        item.SourceSystem = Val(row, "SourceSystem");
        item.Status = CutoverStatuses.All.First(s => string.Equals(s, Val(row, "Status", CutoverStatuses.Active).Trim(), StringComparison.OrdinalIgnoreCase));
        item.Notes = Val(row, "Notes");
        if (created) _db.CompanyCutovers.Add(item);
        else { item.UpdatedAtUtc = DateTime.UtcNow; item.UpdatedBy = UserId(); }
        return created ? "created" : "updated";
    }

    // ── loans ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A loan's mid-life state, validated. Computed identically by the preview and the commit so the
    /// consultant cannot be shown a clean preview and then hit a different error on commit.
    /// </summary>
    private sealed record LoanPlan(
        Employee Employee,
        string LoanNumber,
        string LoanTypeCode,
        string LoanTypeName,
        decimal OriginalAmount,
        decimal InstallmentAmount,
        int TotalInstallments,
        int InstallmentsPaid,
        decimal OutstandingBalance,
        decimal TotalRepaid,
        DateOnly FirstUnpaidDueDate,
        DateOnly? DisbursementDate,
        DateOnly CutoverDate,
        string Currency,
        string SourceSystem,
        string SourceRecordId);

    private async Task<LoanPlan> PlanLoanAsync(Dictionary<string, string> row, Guid tenantId, CutoverContext cutover, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct);
        var cutoverDate = RequireCutoverFor(employee, cutover);
        var loanNumber = Require(row, "LoanNumber").Trim();
        var loanTypeCode = Require(row, "LoanTypeCode").Trim();
        var original = DecRequired(row, "OriginalAmount");
        var emi = DecRequired(row, "InstallmentAmount");
        var total = IntRequired(row, "TotalInstallments");
        var paid = IntRequired(row, "InstallmentsPaid");
        var outstanding = DecRequired(row, "OutstandingBalance");
        var firstUnpaidDue = DateReq(row, "FirstUnpaidDueDate");

        // ── Strict and loud. Each of these is a data error that would otherwise become a wrong
        //    deduction every month for the rest of the schedule, discovered by the employee. ──
        if (total <= 0)
            throw new InvalidOperationException($"TotalInstallments must be at least 1 (found {total}).");
        if (paid < 0 || paid >= total)
            throw new InvalidOperationException(
                $"InstallmentsPaid must be between 0 and TotalInstallments - 1 (found {paid} of {total}). A fully repaid loan is not an opening balance — omit it.");
        if (emi <= 0)
            throw new InvalidOperationException($"InstallmentAmount must be greater than zero (found {emi}).");
        if (outstanding <= 0)
            throw new InvalidOperationException(
                $"OutstandingBalance must be greater than zero (found {outstanding}). A settled loan is not carried in.");
        if (original <= 0)
            throw new InvalidOperationException($"OriginalAmount must be greater than zero (found {original}).");
        if (outstanding > original)
            throw new InvalidOperationException(
                $"OutstandingBalance ({outstanding}) exceeds OriginalAmount ({original}).");

        // The remaining schedule must be able to discharge the stated balance. Tolerance is one whole
        // instalment, which absorbs a legacy system's rounding and its final balloon row while still
        // catching the real error — a balance and a schedule that disagree by months of money.
        var remaining = total - paid;
        var scheduleValue = remaining * emi;
        if (Math.Abs(scheduleValue - outstanding) > emi)
            throw new InvalidOperationException(
                $"OutstandingBalance ({outstanding}) cannot be discharged by the remaining schedule: {remaining} instalments of {emi} is {scheduleValue}. "
                + "Correct the balance, the instalment or the count — they must agree to within one instalment.");

        // TotalRepaid is derived, then cross-checked against the column if the source system supplied
        // it. Deriving it means the balance sheet and the schedule cannot disagree; cross-checking means
        // a source extract that disagrees with itself is reported rather than silently overridden.
        var derivedRepaid = original - outstanding;
        var statedRepaidRaw = Val(row, "TotalRepaid").Trim();
        if (statedRepaidRaw.Length > 0)
        {
            var statedRepaid = DecRequired(row, "TotalRepaid");
            if (Math.Abs(statedRepaid - derivedRepaid) > 0.01m)
                throw new InvalidOperationException(
                    $"TotalRepaid ({statedRepaid}) disagrees with OriginalAmount - OutstandingBalance ({derivedRepaid}). One of the three is wrong.");
        }

        if (firstUnpaidDue < cutoverDate.AddMonths(-1))
            throw new InvalidOperationException(
                $"FirstUnpaidDueDate ({firstUnpaidDue:yyyy-MM-dd}) is more than a month before the {cutoverDate:yyyy-MM-dd} cutover. "
                + "The first unpaid instalment must fall in or after the first period this product runs, or payroll will take several instalments at once.");

        return new LoanPlan(
            employee, loanNumber, loanTypeCode,
            Val(row, "LoanTypeName", loanTypeCode).Trim(),
            original, emi, total, paid, outstanding, derivedRepaid,
            firstUnpaidDue, DateOnlyNullable(row, "DisbursementDate"), cutoverDate,
            await ResolveCurrencyAsync(row, employee, tenantId, ct),
            Val(row, "SourceSystem"), Val(row, "SourceRecordId"));
    }

    /// <summary>
    /// Import a loan directly into its ALREADY-DISBURSED state.
    ///
    /// <para>The alternative — the only path available before this existed — was to create the loan at
    /// its original amount, push it through the approval flow, and then call <c>PATCH
    /// installments/{id}/pay</c> once per instalment already repaid. That is N API calls per loan, it
    /// fabricates payment events dated today, and a single replayed approval resets
    /// <c>OutstandingBalance</c> to <c>ApprovedAmount</c> and destroys every one of them. Fifty-two
    /// loans at seven instalments each is 364 calls and one misclick from starting again.</para>
    ///
    /// <para>The row lands as <c>Status = "Active"</c> with the balance and the schedule stated, which
    /// is exactly what <c>PayrollController</c>'s deduction loop reads: it selects Active loans with
    /// <c>OutstandingBalance &gt; 0</c> and <c>RepaymentStartDate &lt;= periodEnd</c>, deducts
    /// <c>Math.Min(InstallmentAmount, OutstandingBalance)</c>, and stamps the earliest Pending
    /// instalment whose DueDate has arrived. A loan carried in at 7 of 24 therefore continues at 8 and
    /// finishes at 24.</para>
    /// </summary>
    private async Task<string> UpsertLoanOpeningBalanceAsync(
        Dictionary<string, string> row, Guid tenantId, CutoverContext cutover, SectionResult result, CancellationToken ct)
    {
        var plan = await PlanLoanAsync(row, tenantId, cutover, ct);

        var loanType = await ResolveLoanTypeAsync(plan, tenantId, ct);

        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.LoanNumber == plan.LoanNumber && !x.IsDeleted, ct);
        var created = loan is null;
        loan ??= new EmployeeLoan
        {
            TenantId = tenantId,
            LoanNumber = plan.LoanNumber,
            CreatedBy = UserId()
        };
        loan.CompanyId = plan.Employee.CompanyId;
        loan.EmployeeId = plan.Employee.PublicId;
        loan.EmployeeIntId = plan.Employee.Id;
        loan.EmployeeName = plan.Employee.FullName;
        loan.LoanTypeId = loanType.Id;
        loan.LoanTypeName = loanType.NameEn;
        loan.RequestedAmount = plan.OriginalAmount;
        loan.ApprovedAmount = plan.OriginalAmount;
        loan.RequestedInstallments = plan.TotalInstallments;
        loan.ApprovedInstallments = plan.TotalInstallments;
        loan.InstallmentAmount = plan.InstallmentAmount;
        // The resolved currency was stamped on the origin row but never on the loan itself, so every
        // carried-in loan had Currency = null — and the Loans page formats each row with it, so ONE
        // imported loan took the whole page down ("Invalid currency code : null").
        loan.Currency = plan.Currency;
        loan.RepaymentFrequency = "Monthly";
        loan.DisbursementDate = plan.DisbursementDate;
        loan.RepaymentStartDate = plan.FirstUnpaidDueDate;
        loan.TotalRepaid = plan.TotalRepaid;
        loan.OutstandingBalance = plan.OutstandingBalance;
        loan.Status = "Active";
        loan.Notes = $"Opening balance carried in from {(plan.SourceSystem.Length > 0 ? plan.SourceSystem : "a previous system")} at the {plan.CutoverDate:yyyy-MM-dd} cutover ({plan.InstallmentsPaid} of {plan.TotalInstallments} instalments already repaid).";
        if (created) _db.EmployeeLoans.Add(loan);
        else { loan.UpdatedAtUtc = DateTime.UtcNow; loan.UpdatedBy = UserId(); }

        await RebuildLoanScheduleAsync(loan, plan, ct);
        await StampOriginAsync(OpeningBalanceEntityTypes.Loan, loan.Id, plan.Employee, plan.CutoverDate,
            plan.OutstandingBalance, plan.Currency, plan.SourceSystem, plan.SourceRecordId, cutover, ct);
        return created ? "created" : "updated";
    }

    private async Task<LoanType> ResolveLoanTypeAsync(LoanPlan plan, Guid tenantId, CancellationToken ct)
    {
        var existing = await _db.LoanTypes.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.Code == plan.LoanTypeCode && !x.IsDeleted, ct);
        if (existing is not null) return existing;

        // Auto-created, but only from a code the ROW named — never invented. This matches what the
        // benefits section already does for BenefitPlan, and the alternative (failing 300 loan rows
        // because the consultant had not yet keyed a lookup value) is friction with no safety payoff.
        var created = new LoanType
        {
            TenantId = tenantId,
            Code = plan.LoanTypeCode,
            NameEn = plan.LoanTypeName,
            MaxAmount = 0m,
            MaxInstallments = Math.Max(plan.TotalInstallments, 12),
            IsInterestFree = true,
            RequiresApproval = true,
            IsActive = true,
            CreatedBy = UserId()
        };
        _db.LoanTypes.Add(created);
        return created;
    }

    /// <summary>
    /// Build the full schedule, instalment 1 to N, marking the first <c>InstallmentsPaid</c> as already
    /// settled elsewhere.
    ///
    /// <para>The paid rows carry <c>PayrollRunId = null</c>, which is load-bearing: they were paid in
    /// the previous system, and attributing them to a run here would put the loan sub-ledger and the
    /// 1400 receivable permanently out of step with the payslips this product actually produced.</para>
    ///
    /// <para>The final unpaid row absorbs the rounding residual so the remaining schedule sums EXACTLY
    /// to the carried outstanding balance. Without that, a loan whose legacy instalment was
    /// 1,000.0033 finishes a few fils short or over and never reaches Status = Closed.</para>
    /// </summary>
    private async Task RebuildLoanScheduleAsync(EmployeeLoan loan, LoanPlan plan, CancellationToken ct)
    {
        var existing = await _db.LoanInstallments
            .Where(x => x.TenantId == loan.TenantId && x.LoanId == loan.Id)
            .ToListAsync(ct);
        var byNumber = existing.ToDictionary(x => x.InstallmentNumber);

        var paidPerInstallment = plan.InstallmentsPaid == 0
            ? 0m
            : Math.Round(plan.TotalRepaid / plan.InstallmentsPaid, 2);
        var unpaidCount = plan.TotalInstallments - plan.InstallmentsPaid;
        var lastUnpaidResidual = plan.OutstandingBalance - (unpaidCount - 1) * plan.InstallmentAmount;

        for (var n = 1; n <= plan.TotalInstallments; n++)
        {
            var isPaid = n <= plan.InstallmentsPaid;
            var unpaidIndex = n - plan.InstallmentsPaid - 1; // 0-based position among unpaid rows
            var dueDate = isPaid
                ? plan.FirstUnpaidDueDate.AddMonths(n - plan.InstallmentsPaid - 1)
                : plan.FirstUnpaidDueDate.AddMonths(unpaidIndex);
            var amountDue = isPaid
                ? paidPerInstallment
                : (unpaidIndex == unpaidCount - 1 ? lastUnpaidResidual : plan.InstallmentAmount);

            if (!byNumber.TryGetValue(n, out var inst))
            {
                inst = new LoanInstallment
                {
                    TenantId = loan.TenantId,
                    LoanId = loan.Id,
                    InstallmentNumber = n
                };
                _db.LoanInstallments.Add(inst);
            }
            // An instalment this product has ALREADY collected is never rewritten by a re-import. The
            // package states the world as at cutover; payroll has moved on since, and re-running the
            // same file must not un-pay a month someone was actually deducted for.
            else if (inst.Status == "Paid" && inst.PayrollRunId is not null)
            {
                continue;
            }

            inst.DueDate = dueDate;
            inst.AmountDue = amountDue;
            inst.AmountPaid = isPaid ? amountDue : 0m;
            inst.Status = isPaid ? "Paid" : "Pending";
            inst.PaidDate = isPaid ? dueDate : null;
            inst.PayrollRunId = null;
        }
    }

    // ── advances ────────────────────────────────────────────────────────────────────────────────────

    private sealed record AdvancePlan(
        Employee Employee,
        string AdvanceNumber,
        decimal OriginalAmount,
        decimal InstallmentAmount,
        int TotalInstallments,
        int InstallmentsPaid,
        decimal OutstandingBalance,
        decimal TotalRepaid,
        DateOnly FirstUnpaidDueDate,
        DateOnly CutoverDate,
        string Currency,
        string SourceSystem,
        string SourceRecordId);

    private async Task<AdvancePlan> PlanAdvanceAsync(Dictionary<string, string> row, Guid tenantId, CutoverContext cutover, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct);
        var cutoverDate = RequireCutoverFor(employee, cutover);
        var original = DecRequired(row, "OriginalAmount");
        var emi = DecRequired(row, "InstallmentAmount");
        var total = IntRequired(row, "TotalInstallments");
        var paid = IntRequired(row, "InstallmentsPaid");
        var outstanding = DecRequired(row, "OutstandingBalance");

        if (total <= 0) throw new InvalidOperationException($"TotalInstallments must be at least 1 (found {total}).");
        if (paid < 0 || paid >= total)
            throw new InvalidOperationException($"InstallmentsPaid must be between 0 and TotalInstallments - 1 (found {paid} of {total}).");
        if (emi <= 0) throw new InvalidOperationException($"InstallmentAmount must be greater than zero (found {emi}).");
        if (outstanding <= 0) throw new InvalidOperationException($"OutstandingBalance must be greater than zero (found {outstanding}).");
        if (outstanding > original)
            throw new InvalidOperationException($"OutstandingBalance ({outstanding}) exceeds OriginalAmount ({original}).");
        var remaining = total - paid;
        if (Math.Abs(remaining * emi - outstanding) > emi)
            throw new InvalidOperationException(
                $"OutstandingBalance ({outstanding}) cannot be discharged by {remaining} instalments of {emi}.");

        return new AdvancePlan(
            employee, Require(row, "AdvanceNumber").Trim(), original, emi, total, paid, outstanding,
            original - outstanding, DateReq(row, "FirstUnpaidDueDate"), cutoverDate,
            await ResolveCurrencyAsync(row, employee, tenantId, ct),
            Val(row, "SourceSystem"), Val(row, "SourceRecordId"));
    }

    private async Task<string> UpsertAdvanceOpeningBalanceAsync(
        Dictionary<string, string> row, Guid tenantId, CutoverContext cutover, SectionResult result, CancellationToken ct)
    {
        var plan = await PlanAdvanceAsync(row, tenantId, cutover, ct);

        var advance = await _db.SalaryAdvances.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.AdvanceNumber == plan.AdvanceNumber && !x.IsDeleted, ct);
        var created = advance is null;
        advance ??= new SalaryAdvance
        {
            TenantId = tenantId,
            AdvanceNumber = plan.AdvanceNumber,
            CreatedBy = UserId()
        };
        advance.CompanyId = plan.Employee.CompanyId;
        advance.EmployeeId = plan.Employee.PublicId;
        advance.EmployeeIntId = plan.Employee.Id;
        advance.EmployeeName = plan.Employee.FullName;
        advance.RequestedAmount = plan.OriginalAmount;
        advance.ApprovedAmount = plan.OriginalAmount;
        advance.RepaymentType = plan.TotalInstallments > 1 ? "Installments" : "OneTime";
        advance.Installments = plan.TotalInstallments;
        advance.InstallmentAmount = plan.InstallmentAmount;
        advance.RepaymentStartDate = plan.FirstUnpaidDueDate;
        advance.TotalRepaid = plan.TotalRepaid;
        advance.OutstandingBalance = plan.OutstandingBalance;
        advance.Status = "Active";
        advance.Reason = $"Opening balance carried in from {(plan.SourceSystem.Length > 0 ? plan.SourceSystem : "a previous system")} at the {plan.CutoverDate:yyyy-MM-dd} cutover ({plan.InstallmentsPaid} of {plan.TotalInstallments} instalments already repaid).";
        if (created) _db.SalaryAdvances.Add(advance);
        else { advance.UpdatedAtUtc = DateTime.UtcNow; advance.UpdatedBy = UserId(); }

        await StampOriginAsync(OpeningBalanceEntityTypes.Advance, advance.Id, plan.Employee, plan.CutoverDate,
            plan.OutstandingBalance, plan.Currency, plan.SourceSystem, plan.SourceRecordId, cutover, ct);
        return created ? "created" : "updated";
    }

    // ── eosbOpeningProvision ────────────────────────────────────────────────────────────────────────

    private async Task<string> UpsertEosbOpeningProvisionAsync(
        Dictionary<string, string> row, Guid tenantId, CutoverContext cutover, SectionResult result, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct);
        var cutoverDate = RequireCutoverFor(employee, cutover);
        var asAt = DateReq(row, "AsAtDate");
        var accruedAmount = DecRequired(row, "AccruedAmount");
        var accruedMonths = DecRequired(row, "AccruedMonths");
        var priorServiceStart = DateOnlyNullable(row, "PriorServiceStartDate");

        if (accruedAmount < 0)
            throw new InvalidOperationException($"AccruedAmount cannot be negative (found {accruedAmount}). An end-of-service provision is a liability, never a credit.");
        if (accruedMonths < 0)
            throw new InvalidOperationException($"AccruedMonths cannot be negative (found {accruedMonths}).");
        if (asAt >= cutoverDate.AddMonths(1))
            throw new InvalidOperationException(
                $"AsAtDate ({asAt:yyyy-MM-dd}) is after the {cutoverDate:yyyy-MM-dd} cutover month. A provision carried in must be struck on or before cutover.");
        if (priorServiceStart is not null && priorServiceStart > asAt)
            throw new InvalidOperationException(
                $"PriorServiceStartDate ({priorServiceStart:yyyy-MM-dd}) is after AsAtDate ({asAt:yyyy-MM-dd}).");

        var currency = await ResolveCurrencyAsync(row, employee, tenantId, ct);

        var item = await _db.EmployeeEosbOpeningBalances.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.AsAtDate == asAt, ct);
        var created = item is null;
        item ??= new EmployeeEosbOpeningBalance
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            AsAtDate = asAt,
            CreatedBy = UserId()
        };
        item.CompanyId = employee.CompanyId;
        item.EmployeeCode = employee.EmployeeCode;
        item.PriorServiceStartDate = priorServiceStart;
        item.AccruedMonths = accruedMonths;
        item.AccruedAmount = accruedAmount;
        item.Currency = currency;
        item.SourceSystem = Val(row, "SourceSystem");
        item.SourceRecordId = Val(row, "SourceRecordId");
        if (created) _db.EmployeeEosbOpeningBalances.Add(item);
        else { item.UpdatedAtUtc = DateTime.UtcNow; item.UpdatedBy = UserId(); }

        await StampOriginAsync(OpeningBalanceEntityTypes.EosbProvision, item.Id, employee, cutoverDate,
            accruedAmount, currency, item.SourceSystem, item.SourceRecordId, cutover, ct);
        return created ? "created" : "updated";
    }
}
