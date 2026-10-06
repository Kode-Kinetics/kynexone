using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// One evaluated limit, in the shape the UI renders as a sentence — e.g. a policy salary multiple reads
/// "up to SAR 18,000 = 2 × gross SAR 12,000 − outstanding SAR 6,000".
/// <list type="bullet">
/// <item><see cref="Limit"/> — a <see cref="LoanLimitKinds"/> code; the same vocabulary as <c>bindingLimit</c>.</item>
/// <item><see cref="Basis"/> — <c>Amount</c>, <c>MultipleOfBasic</c> or <c>MultipleOfGross</c>
/// (<c>Count</c> for the concurrent-loan rule, which limits a number of loans, not money).</item>
/// <item><see cref="Cap"/> / <see cref="OutstandingNow"/> are in <see cref="Unit"/>: <c>Principal</c> for
/// every money limit, <c>MonthlyInstalment</c> for the instalment-percentage rule (the cap and the existing
/// instalments are monthly figures; <see cref="Available"/> is still principal, for <see cref="Installments"/>
/// instalments), and <c>Loans</c> for the concurrent-loan rule.</item>
/// <item><see cref="OutstandingNow"/> is null when the limit is per loan (nothing is subtracted).</item>
/// <item><see cref="Available"/> is the principal this limit still allows; null when it does not cap the amount.</item>
/// </list>
/// </summary>
public sealed record LoanLimitBreakdown(
    string Limit, string Basis, decimal? Multiple, decimal? SalaryBasisAmount, decimal Cap,
    decimal? OutstandingNow, decimal? Available, string Unit = LoanLimitUnits.Principal, int? Installments = null);

public static class LoanLimitKinds
{
    public const string GradePerLoan = "GradePerLoan";
    public const string GradeOutstanding = "GradeOutstanding";
    public const string PolicyMaxAmount = "PolicyMaxAmount";
    public const string PolicyTotalOutstanding = "PolicyTotalOutstanding";
    public const string PolicySalaryMultiple = "PolicySalaryMultiple";
    public const string PolicyInstallmentPercent = "PolicyInstallmentPercent";
    public const string PolicyConcurrentLoans = "PolicyConcurrentLoans";

    /// <summary>Tie-break order for <c>bindingLimit</c>: the grade is named first when it binds equally.</summary>
    public static readonly string[] Order =
        [GradePerLoan, GradeOutstanding, PolicyMaxAmount, PolicyTotalOutstanding, PolicySalaryMultiple, PolicyInstallmentPercent, PolicyConcurrentLoans];
}

public static class LoanLimitUnits
{
    public const string Principal = "Principal";
    public const string MonthlyInstalment = "MonthlyInstalment";
    public const string Loans = "Loans";
}

/// <summary>Stable reason codes for the grade check. All HARD: none is ever in
/// <see cref="LoanLifecycleService.ExceptionCodes"/>, so no policy exception can waive them.</summary>
public static class GradeLimitCodes
{
    public const string NotEligible = "GradeNotEligible";
    public const string PerLoan = "GradeLimitPerLoan";
    public const string Outstanding = "GradeLimitOutstanding";
    public const string Missing = "GradeMissing";
    public const string NotConfigured = "GradeLimitNotConfigured";
    public const string SalaryMissing = "GradeSalaryMissing";
    public const string CurrencyAmbiguous = "GradeLimitCurrencyAmbiguous";
    public static readonly string[] All = [NotEligible, PerLoan, Outstanding, Missing, NotConfigured, SalaryMissing, CurrencyAmbiguous];

    public const string NotConfiguredText = "Your loan limit hasn't been set up yet — HR has been notified.";
}

/// <summary>What the grade allows one employee for one loan type on one date. <see cref="Applies"/> false
/// means the type is not grade-limited and nothing here constrains the loan.</summary>
public sealed record GradeLoanLimitResult(
    bool Applies, bool Eligible, Guid? GradeId, string? GradeCode, string? GradeName, Guid? CellId,
    bool IsCompanyOverride, string? ValueType, decimal? Multiple, decimal? SalaryBasisAmount,
    decimal? PerLoanCap, decimal? OutstandingCap, decimal OutstandingNow, decimal? Available,
    string? ReasonCode, string? ReasonText, string[] Codes, string[] Reasons, IReadOnlyList<LoanLimitBreakdown> Limits,
    string? Currency = null, string? GradeNameAr = null)
{
    public static GradeLoanLimitResult NotApplicable { get; } =
        new(false, true, null, null, null, null, false, null, null, null, null, null, 0m, null, null, null, [], [], []);

    /// <summary>The cheapest limit, or null when the grade sets no money cap.</summary>
    public string? BindingLimit => Limits.Where(x => x.Available.HasValue)
        .OrderBy(x => x.Available).ThenBy(x => Array.IndexOf(LoanLimitKinds.Order, x.Limit)).FirstOrDefault()?.Limit;
}

/// <summary>
/// Slice L1 — resolves the grade loan limit for (tenant, employee, loan type, date).
///
/// <para>The ONE resolver for application preview, submission, the approval re-check and release of funds
/// (all reach it through <see cref="LoanEligibilityService"/>), so an approver can never push a loan past a
/// limit the applicant was shown. The grade is the one on the employee record on <c>asOf</c>; the cell is the
/// most specific one in effect (company override beats tenant-wide); a multiple is taken of the salary
/// structure in force on <c>asOf</c>.</para>
///
/// <para><b>Outstanding</b> is supplied by the caller — the same commitment list the eligibility service has
/// already read inside the loan-creation lock — so the grade and the policy can never count debt differently,
/// and two concurrent applications cannot both see the old total.</para>
/// </summary>
public sealed class GradeLoanLimitResolver(ZayraDbContext db)
{
    public async Task<GradeLoanLimitResult> ResolveAsync(Guid tid, Employee employee, LoanType loanType, DateOnly asOf,
        decimal outstandingNow, decimal? requestedAmount, string currency, CancellationToken ct = default)
    {
        if (!loanType.GradeLimited) return GradeLoanLimitResult.NotApplicable;

        GradeLoanLimitResult Blocked(string code, string text, Grade? grade = null, GradeEntitlement? cell = null) =>
            new(true, false, grade?.Id ?? employee.GradeId, grade?.Code, grade?.Name, cell?.Id,
                cell?.CompanyId != null, cell?.ValueType, cell?.Rate, null, null, cell?.MaxOutstandingAmount, outstandingNow, 0m,
                code, text, [code], [text], [], currency, grade?.NameAr);

        if (string.IsNullOrWhiteSpace(loanType.EntitlementComponentCode))
            return Blocked(GradeLimitCodes.NotConfigured, GradeLimitCodes.NotConfiguredText);
        if (employee.GradeId is not Guid gradeId)
            return Blocked(GradeLimitCodes.Missing, "No grade is recorded on the employee record, so the loan limit can't be worked out. HR needs to set the grade.");
        var grade = await db.Grades.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == gradeId && !x.IsDeleted, ct);
        if (grade == null || !grade.IsActive)
            return Blocked(GradeLimitCodes.Missing, "The grade on the employee record is no longer in use, so the loan limit can't be worked out. HR needs to update the grade.");

        // Company filter dropped on purpose: the limit is a property of the EMPLOYEE's company, not of whoever
        // is looking. Tenant is pinned by the bypass; company is re-applied explicitly below.
        var code = loanType.EntitlementComponentCode;
        var candidates = await ScopedBypass.TenantWide(db.GradeEntitlements, tid,
                "Grade loan limit for the employee's own company and the tenant-wide default, whoever evaluates it.")
            .AsNoTracking()
            .Where(x => x.GradeId == gradeId && x.PayComponentCode == code
                && (x.CompanyId == null || x.CompanyId == employee.CompanyId)
                && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo >= asOf))
            .ToListAsync(ct);
        var cell = candidates.OrderByDescending(x => x.CompanyId.HasValue).ThenByDescending(x => x.EffectiveFrom).FirstOrDefault();
        if (cell == null) return Blocked(GradeLimitCodes.NotConfigured, GradeLimitCodes.NotConfiguredText, grade);
        if (!cell.Eligible)
            return Blocked(GradeLimitCodes.NotEligible, $"Employees in {grade.Name} aren't eligible for this loan type ({loanType.NameEn}).", grade, cell);

        // A tenant-wide cell's fixed figures carry no currency. Publishing refuses them once companies pay in
        // different currencies, but a company can change currency (or a new one can be added) afterwards: never
        // apply "10,000" as SAR to one company and AED to another — block until HR sets per-company cells.
        if (cell.CompanyId == null && (cell.ValueType == GradeEntitlementValueTypes.Amount || cell.MaxOutstandingAmount != null))
        {
            var currencies = await ScopedBypass.TenantWide(db.Companies, tid, "A tenant-wide fixed grade limit must mean one currency across every active company.")
                .AsNoTracking().Where(x => !x.IsDeleted && x.IsActive).Select(x => x.DefaultCurrency).ToListAsync(ct);
            if (currencies.Select(c => (c ?? string.Empty).Trim().ToUpperInvariant()).Distinct().Count() > 1)
                return Blocked(GradeLimitCodes.CurrencyAmbiguous,
                    "The loan limit for this grade is a fixed amount set for all companies, but the companies pay in different currencies. HR needs to set this company's own limit.",
                    grade, cell);
        }

        decimal? salaryBasis = null;
        decimal? perLoanCap = cell.ValueType == GradeEntitlementValueTypes.Amount ? cell.Amount : null;
        if (cell.ValueType is GradeEntitlementValueTypes.MultipleOfBasic or GradeEntitlementValueTypes.MultipleOfGross)
        {
            var salary = await db.EmployeeSalaryStructures.AsNoTracking()
                .Where(x => x.TenantId == tid && x.EmployeeId == employee.Id && x.IsActive && x.EffectiveDate <= asOf)
                .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (salary == null || !string.Equals(salary.Currency, currency, StringComparison.OrdinalIgnoreCase))
                return Blocked(GradeLimitCodes.SalaryMissing,
                    "The loan limit is based on salary, and no current salary in the company's currency is on file. HR needs to complete it.", grade, cell);
            salaryBasis = cell.ValueType == GradeEntitlementValueTypes.MultipleOfBasic
                ? salary.BasicSalary
                : salary.BasicSalary + salary.HousingAllowance + salary.TransportAllowance
                  + salary.FoodAllowance + salary.MobileAllowance + salary.OtherAllowance;
            if (salaryBasis <= 0)
                return Blocked(GradeLimitCodes.SalaryMissing,
                    "The loan limit is based on salary, and the salary on file is zero. HR needs to complete it.", grade, cell);
            // Rounded DOWN to the cent: a limit is never rounded in the borrower's favour past the multiple.
            perLoanCap = decimal.Floor(salaryBasis.Value * cell.Rate!.Value * 100m) / 100m;
        }

        var limits = new List<LoanLimitBreakdown>();
        if (perLoanCap.HasValue)
            limits.Add(new(LoanLimitKinds.GradePerLoan, cell.ValueType, cell.Rate, salaryBasis, perLoanCap.Value, null, perLoanCap.Value));
        if (cell.MaxOutstandingAmount is decimal outstandingCap)
            limits.Add(new(LoanLimitKinds.GradeOutstanding, GradeEntitlementValueTypes.Amount, null, null, outstandingCap,
                outstandingNow, Math.Max(0m, outstandingCap - outstandingNow)));
        decimal? available = limits.Count == 0 ? null : limits.Min(x => x.Available!.Value);

        var codes = new List<string>();
        var reasons = new List<string>();
        if (requestedAmount is decimal amount)
        {
            if (perLoanCap.HasValue && amount > perLoanCap.Value)
            {
                codes.Add(GradeLimitCodes.PerLoan);
                reasons.Add($"{grade.Name} allows up to {perLoanCap.Value:N2} {currency} per loan of this type ({loanType.NameEn}).");
            }
            if (cell.MaxOutstandingAmount is decimal cap && amount > Math.Max(0m, cap - outstandingNow))
            {
                codes.Add(GradeLimitCodes.Outstanding);
                reasons.Add($"{grade.Name} allows {cap:N2} {currency} outstanding on this loan type ({loanType.NameEn}); "
                    + $"{outstandingNow:N2} is already outstanding or awaiting approval, so {Math.Max(0m, cap - outstandingNow):N2} is available.");
            }
        }
        return new(true, codes.Count == 0, grade.Id, grade.Code, grade.Name, cell.Id, cell.CompanyId.HasValue,
            cell.ValueType, cell.Rate, salaryBasis, perLoanCap, cell.MaxOutstandingAmount, outstandingNow, available,
            codes.FirstOrDefault(), reasons.FirstOrDefault(), codes.ToArray(), reasons.ToArray(), limits, currency, grade.NameAr);
    }

    /// <summary>
    /// "HR has been notified" must be true when the employee is told it. Called on SUBMIT only (never on a
    /// read), it raises one in-app item for each HR/Admin user who can act for the employee's company — at most
    /// once per (tenant, loan type, grade) in any 24 hours, whether or not the earlier one was read. Recipients
    /// are resolved in one query. Does not save — the caller owns the unit of work; the caller holds the
    /// tenant's loan-creation lock, so two submissions cannot both pass the 24-hour check.
    /// </summary>
    public async Task<int> NotifyLimitNotConfiguredAsync(Guid tid, Employee employee, LoanType loanType, CancellationToken ct = default)
    {
        const string title = "Loan limit by grade not set";
        var entityId = NotConfiguredEntityId(loanType.Id, employee.GradeId);
        var since = DateTime.UtcNow.AddHours(-24);
        if (db.ChangeTracker.Entries<Notification>().Any(e => e.State == EntityState.Added && e.Entity.TenantId == tid
                && e.Entity.EntityName == NotConfiguredEntity && e.Entity.EntityId == entityId)
            || await db.Notifications.AnyAsync(x => x.TenantId == tid && x.EntityName == NotConfiguredEntity
                    && x.EntityId == entityId && x.CreatedAtUtc >= since, ct))
            return 0;
        var roles = new[] { "Admin", "HR Manager", "HR Director" };
        var companyId = employee.CompanyId;
        var recipients = await (from user in db.Users.AsNoTracking()
                                join assignment in db.UserRoles on user.Id equals assignment.UserId
                                join role in db.Roles on assignment.RoleId equals role.Id
                                where user.TenantId == tid && user.IsActive && !user.IsDeleted
                                    && (role.TenantId == tid || role.TenantId == null) && role.IsActive && !role.IsDeleted && roles.Contains(role.Name)
                                    && (user.IsGroupScope || db.UserEntityAccesses.Any(x => x.TenantId == tid && x.UserId == user.Id && x.IsActive
                                        && (x.CompanyId == companyId || x.GrantMode == "AllCurrentCompanies" || x.GrantMode == "AllCurrentAndFutureCompanies")))
                                select user.Id).Distinct().ToListAsync(ct);
        if (recipients.Count == 0) return 0;
        var gradeName = employee.GradeId is Guid gid
            ? await db.Grades.AsNoTracking().Where(x => x.TenantId == tid && x.Id == gid).Select(x => x.Name).FirstOrDefaultAsync(ct)
            : null;
        var message = $"An employee could not apply for this loan type ({loanType.NameEn}) because no limit is set for "
            + (gradeName ?? "their grade") + ". Set it in Loans → Loan Policies → Limits by grade.";
        db.Notifications.AddRange(recipients.Select(userId => new Notification
        {
            TenantId = tid, UserId = userId, Title = title, Message = message, EntityName = NotConfiguredEntity, EntityId = entityId,
        }));
        return recipients.Count;
    }

    /// <summary>Notification entity for "a grade has no loan limit"; the id is "{loanTypeId}:{gradeId|none}".</summary>
    public const string NotConfiguredEntity = "LoanGradeLimit";
    public static string NotConfiguredEntityId(Guid loanTypeId, Guid? gradeId) => $"{loanTypeId}:{gradeId?.ToString() ?? "none"}";

    /// <summary>The Facility component a loan type's grade limits are keyed by, created on first use
    /// (with the system catalog beside it, so the store never holds a tenant row without the system set).
    /// Returns null with a plain reason when the code is already taken by a component that pays.</summary>
    public static async Task<(string? Code, string? Error)> EnsureFacilityComponentAsync(ZayraDbContext db, Guid tid, LoanType loanType, Guid? userId, CancellationToken ct)
    {
        var code = loanType.EntitlementComponentCode ?? FacilityCodeFor(loanType.Code);
        // One Facility code belongs to one loan type: two types sharing it would share (and silently merge)
        // their grade grids. Codes that differ only in case or punctuation normalise to the same code.
        var clash = await db.LoanTypes.AsNoTracking()
            .Where(x => x.TenantId == tid && x.Id != loanType.Id && !x.IsDeleted && x.EntitlementComponentCode == code)
            .Select(x => x.NameEn).FirstOrDefaultAsync(ct);
        if (clash != null)
            return (null, $"The loan type {clash} already uses the limit code {code}. Give this loan type a code that differs by more than case or punctuation.");
        var rows = await ScopedBypass.TenantWide(db.PayComponents, tid,
                "A pay component code is one identity across every company of the tenant (GL groups lines by code).")
            .Where(x => x.Code == code && !x.IsDeleted).ToListAsync(ct);
        if (rows.Any(x => x.EntitlementClass != PayEntitlementClasses.Facility))
            return (null, $"The pay component code {code} is already used by a payroll component, so it can't hold this loan type's limits. Rename that component or the loan type code.");
        if (!rows.Any(x => x.CompanyId == null))
        {
            await PayComponentSeeder.SeedTenantDefaultsAsync(db, tid, ct);
            db.PayComponents.Add(new PayComponent
            {
                TenantId = tid, CompanyId = null, Code = code,
                NameEn = $"Loan limit by grade: {loanType.NameEn}",
                NameAr = $"حد القرض حسب الدرجة: {(string.IsNullOrWhiteSpace(loanType.NameAr) ? loanType.NameEn : loanType.NameAr)}",
                // Every field that could make it pay is set to the non-paying value: an unknown type, no calc
                // method the engine values, no provider, nothing in WPS/EOSB/GOSI/tax, never emitted at zero.
                ComponentType = PayComponentTypes.Facility, CalcMethod = "None", Value = null, ProviderKey = null,
                StructureField = null, GlDriverKey = null, IsTaxable = false, GosiSubject = false, WpsIncluded = false,
                EosbIncluded = false, EmitWhenZero = false, IsFamily = false, IsSystem = false, IsStatutory = false,
                EntitlementClass = PayEntitlementClasses.Facility, StatutoryFloor = PayStatutoryFloors.None,
                DisplayOrder = 9000, IsActive = true, CreatedBy = userId,
            });
        }
        loanType.EntitlementComponentCode = code;
        return (code, null);
    }

    public static string FacilityCodeFor(string loanTypeCode)
    {
        var cleaned = new string((loanTypeCode ?? string.Empty).Trim().ToUpperInvariant()
            .Select(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9' ? ch : '_').ToArray()).Trim('_');
        var code = "LOAN_" + (cleaned.Length == 0 ? "TYPE" : cleaned);
        return code.Length > 64 ? code[..64] : code;
    }
}
