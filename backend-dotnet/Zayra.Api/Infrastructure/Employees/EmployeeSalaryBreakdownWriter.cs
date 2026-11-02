using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>Shared effective-dated salary writer.
/// <para><see cref="ApplyAsync"/> writes a salary row DIRECTLY and is therefore only for callers that sit behind a decision
/// by a second person: the approval of a hire DRAFT (the Approvals workflow decides it) and an approved employee-change
/// request (<c>EmployeeChangeApplier</c>, whose decider must hold <c>payroll.approve</c>). It is NOT safe for a single
/// HR user: <c>POST /employees</c> and the employee import stage a new hire's first package with
/// <see cref="StageFirstPackageAsync"/> instead, as a <c>SalaryChangeRequest</c> that Finance (a different person) approves,
/// and the employee cannot be activated while that package waits. Anything that changes an EXISTING employee's pay goes
/// through <c>PayrollController</c>'s staging endpoints.</para>
/// </summary>
public static class EmployeeSalaryBreakdownWriter
{
    /// <param name="fillOmittedFromGrade">
    /// True only where the employee is being CREATED (the hire form, a draft's approval): a breakdown that was not sent at
    /// all is then filled wholly from the grade's pay scale. False everywhere else (a profile update, an approved change):
    /// an omitted breakdown writes nothing, so a grade change on an existing employee never rewrites their pay behind the
    /// approved salary-change path. A breakdown that IS sent is merged with the grade field by field in both cases.
    /// </param>
    public static async Task ApplyAsync(ZayraDbContext db, Employee employee, EmployeeSalaryBreakdownRequest? request, RequestContext context, CancellationToken cancellationToken,
        bool fillOmittedFromGrade = false)
    {
        // A GRADE IS NO LONGER THE GATE. It used to be: an employee without a grade returned here
        // before the breakdown was ever read, so an explicit salaryBreakdown on the create/update
        // request was accepted with 200 and silently dropped — employee.Salary stayed 0, no
        // EmployeeSalaryStructure row was written, and the employee joined payroll on nothing. A
        // 14-employee run then reported a gross total of the one person who happened to have a
        // grade. The repo rule is refuse rather than guess; silent partial success is neither, so
        // the operator's numbers are now honoured whether or not a grade resolves.
        if (employee.TenantId is null) return;
        var (grade, salary, releaseA, components) = await ResolveSalaryAsync(db, employee, request, fillOmittedFromGrade, cancellationToken);
        var effectiveDate = SalaryEffectiveDate(employee, request);
        // Nothing to assign: no grade pay scale AND no supplied figures. Unchanged behaviour — this
        // is the ordinary "the salary section of the form was left empty" case, not a dropped value.
        if (GrossSalary(salary) <= 0) return;

        var structureCode = Clean(request?.SalaryStructureCode);
        if (string.IsNullOrWhiteSpace(structureCode))
            structureCode = DefaultStructureCode(grade);
        // Refuse rather than guess, BEFORE anything is staged (see ResolveStructureAsync). null = the default
        // structure does not exist yet and is created below.
        var structure = await ResolveStructureAsync(db, employee, grade, structureCode, cancellationToken);

        var replaced = await db.EmployeeSalaryStructures
            .Where(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && x.IsActive && x.EffectiveDate == effectiveDate)
            .ToListAsync(cancellationToken);
        var previousStructureCodes = await (from activeRow in db.EmployeeSalaryStructures
                                            join activeStructure in db.SalaryStructures on activeRow.SalaryStructureId equals activeStructure.Id
                                            where activeRow.TenantId == employee.TenantId && activeRow.EmployeeId == employee.Id && activeRow.IsActive
                                            select activeStructure.Code).Distinct().ToListAsync(cancellationToken);
        foreach (var prior in replaced) prior.IsActive = false;

        structure ??= await CreateDefaultStructureAsync(db, employee, grade, request, structureCode, effectiveDate, releaseA, components, context, cancellationToken);
        var assignment = new EmployeeSalaryStructure
        {
            TenantId = employee.TenantId.Value,
            EmployeeId = employee.Id,
            SalaryStructureId = structure.Id,
            BasicSalary = salary.BasicSalary ?? 0m,
            HousingAllowance = salary.HousingAllowance ?? 0m,
            TransportAllowance = salary.TransportAllowance ?? 0m,
            FoodAllowance = salary.FoodAllowance ?? 0m,
            MobileAllowance = salary.MobileAllowance ?? 0m,
            OtherAllowance = salary.OtherAllowance ?? 0m,
            FixedDeduction = salary.FixedDeduction ?? 0m,
            EffectiveDate = effectiveDate,
            Currency = Clean(request?.Currency) is { Length: > 0 } currency ? currency.ToUpperInvariant() : structure.Currency,
            CreatedBy = context.UserId
        };
        db.EmployeeSalaryStructures.Add(assignment);
        employee.Salary = GrossSalary(salary);
        employee.PayrollProfileCode = string.IsNullOrWhiteSpace(employee.PayrollProfileCode) ? structure.Code : employee.PayrollProfileCode;
        await MirrorStructureReferenceAsync(db, employee, structure.Code, previousStructureCodes, cancellationToken);
    }


    /// <summary>
    /// A NEW hire's first package, STAGED for a second person instead of written: a <c>SalaryChangeRequest</c> (Source
    /// "NewHire", or the caller's own source) that a Finance approver who is neither the maker nor the employee applies. No
    /// salary row, and so nothing payroll can see, exists until then; while it is pending the employee cannot be activated
    /// (see <c>EmployeeReadinessEvaluator</c>). Same resolution and structure rules as <see cref="ApplyAsync"/> (one
    /// computation), so the approver applies exactly what the form showed. Returns null when there is no salary to stage.
    /// The employee must already have an id.
    /// </summary>
    public static async Task<SalaryChangeRequest?> StageFirstPackageAsync(ZayraDbContext db, Employee employee, EmployeeSalaryBreakdownRequest? request,
        RequestContext context, CancellationToken cancellationToken, bool fillOmittedFromGrade = false, string source = SalaryChangeSources.NewHire, Guid? batchId = null)
    {
        if (employee.TenantId is null || employee.Id == 0) return null;
        var (grade, salary, releaseA, components) = await ResolveSalaryAsync(db, employee, request, fillOmittedFromGrade, cancellationToken);
        if (GrossSalary(salary) <= 0) return null;
        var effectiveDate = SalaryEffectiveDate(employee, request);
        var structureCode = Clean(request?.SalaryStructureCode);
        if (string.IsNullOrWhiteSpace(structureCode)) structureCode = DefaultStructureCode(grade);
        var structure = await ResolveStructureAsync(db, employee, grade, structureCode, cancellationToken)
                        ?? await CreateDefaultStructureAsync(db, employee, grade, request, structureCode, effectiveDate, releaseA, components, context, cancellationToken);
        var makerName = context.UserId is Guid maker
            ? await db.Users.AsNoTracking().Where(u => u.TenantId == employee.TenantId && u.Id == maker).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken) ?? string.Empty
            : string.Empty;
        var change = new SalaryChangeRequest
        {
            TenantId = employee.TenantId.Value, CompanyId = employee.CompanyId, BatchId = batchId, Source = source,
            EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
            SalaryStructureId = structure.Id,
            BasicSalary = salary.BasicSalary ?? 0m, HousingAllowance = salary.HousingAllowance ?? 0m, TransportAllowance = salary.TransportAllowance ?? 0m,
            FoodAllowance = salary.FoodAllowance ?? 0m, MobileAllowance = salary.MobileAllowance ?? 0m, OtherAllowance = salary.OtherAllowance ?? 0m,
            FixedDeduction = salary.FixedDeduction ?? 0m, EffectiveDate = effectiveDate,
            Currency = Clean(request?.Currency) is { Length: > 0 } currency ? currency.ToUpperInvariant() : structure.Currency,
            PreviousBasicSalary = null, RequestedByUserId = context.UserId, RequestedByName = makerName,
        };
        // A draft saved again: the same figures keep the pending request (no duplicate); different figures replace it, so Finance only
        // ever decides the latest proposal. The replacing writer becomes the maker, so they can never approve it.
        var pending = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.SalaryChangeRequests, employee.TenantId.Value,
                "Staging a first package: the employee's own pending first packages, to keep or replace them; tenant and employee pinned.")
            .Where(x => x.EmployeeId == employee.Id && x.Status == SalaryChangeRequest.Pending
                && (x.Source == SalaryChangeSources.NewHire || x.Source == SalaryChangeSources.EmployeeImport))
            .ToListAsync(cancellationToken);
        var same = pending.FirstOrDefault(x => x.SalaryStructureId == change.SalaryStructureId && x.BasicSalary == change.BasicSalary
            && x.HousingAllowance == change.HousingAllowance && x.TransportAllowance == change.TransportAllowance && x.FoodAllowance == change.FoodAllowance
            && x.MobileAllowance == change.MobileAllowance && x.OtherAllowance == change.OtherAllowance && x.FixedDeduction == change.FixedDeduction
            && x.EffectiveDate == change.EffectiveDate && string.Equals(x.Currency, change.Currency, StringComparison.Ordinal));
        if (same is not null) return same;
        foreach (var old in pending)
        {
            old.Status = SalaryChangeRequest.Cancelled;
            old.DecidedAtUtc = DateTime.UtcNow;
            old.DecisionNote = "Superseded by a newer proposal while the draft was edited.";
        }
        db.SalaryChangeRequests.Add(change);
        return change;
    }

    /// <summary>The employee-side effects of an approved FIRST package, which <c>ApplyAsync</c> performs inline: the gross on the
    /// employee row, the payroll profile code, and the profile's structure reference. Called by the approver's apply step.</summary>
    public static async Task ApplyFirstPackageEffectsAsync(ZayraDbContext db, Employee employee, SalaryStructure structure, SalaryChangeRequest change, CancellationToken cancellationToken)
    {
        var gross = change.BasicSalary + change.HousingAllowance + change.TransportAllowance + change.FoodAllowance + change.MobileAllowance + change.OtherAllowance;
        if (employee.Salary is null or 0m) employee.Salary = gross;
        employee.PayrollProfileCode = string.IsNullOrWhiteSpace(employee.PayrollProfileCode) ? structure.Code : employee.PayrollProfileCode;
        await MirrorStructureReferenceAsync(db, employee, structure.Code, Array.Empty<string>(), cancellationToken);
    }

    private static async Task<SalaryStructure> CreateDefaultStructureAsync(ZayraDbContext db, Employee employee, Grade? grade, EmployeeSalaryBreakdownRequest? request,
        string structureCode, DateOnly effectiveDate, bool releaseA, List<GradePayScaleComponent> components, RequestContext context, CancellationToken cancellationToken)
    {
        // Currency, most specific first: what the operator typed, then the grade's, then the
        // employing company's default. Never a hard-coded literal — the entity default is "AED".
        var structureCurrency = Clean(request?.Currency) is { Length: > 0 } requestCurrency
            ? requestCurrency.ToUpperInvariant()
            : grade?.Currency is { Length: > 0 } gradeCurrency
                ? gradeCurrency
                : await ResolveCompanyCurrencyAsync(db, employee, cancellationToken);
        var structure = new SalaryStructure
        {
            TenantId = employee.TenantId!.Value,
            CompanyId = employee.CompanyId,
            Code = structureCode,
            Name = grade is null ? "Direct salary structure" : $"{grade.Name} salary structure",
            Currency = structureCurrency,
            EffectiveDate = effectiveDate,
            CreatedBy = context.UserId
        };
        db.SalaryStructures.Add(structure);

        if (releaseA && grade is not null)
            db.SalaryComponents.AddRange(EntitlementMatrixService.SalaryComponentsFor(
                await EntitlementMatrixService.CashAllowancesAsync(db, employee.TenantId.Value, grade.Id, employee.CompanyId, effectiveDate, cancellationToken),
                employee.TenantId.Value, structure.Id));
        foreach (var component in components)
        {
            db.SalaryComponents.Add(new SalaryComponent
            {
                TenantId = employee.TenantId.Value,
                SalaryStructureId = structure.Id,
                Code = component.ComponentCode,
                Name = component.ComponentName,
                ComponentType = component.ComponentType,
                CalculationType = component.CalculationType,
                Amount = component.Amount,
                Percentage = component.Percentage,
                IsTaxable = component.IsTaxable
            });
        }
        return structure;
    }

    /// <summary>The code a salary gets when no structure is chosen: the grade's, or DIRECT for a gradeless employee.</summary>
    public static string DefaultStructureCode(Grade? grade) => grade is null ? DirectSalaryStructureCode : $"GRADE-{grade.Code}";

    private static bool IsDefaultStructureCode(string code, Grade? grade) =>
        string.Equals(code, DefaultStructureCode(grade), StringComparison.Ordinal);

    /// <summary>
    /// The employing company's own structure first, then a group-wide one (CompanyId null): the same rule the import
    /// uses, so company B's new hire is never attached to company A's structure lines. An active row wins over an
    /// inactive one with the same code.
    /// </summary>
    public static Task<SalaryStructure?> FindStructureAsync(ZayraDbContext db, Employee employee, string code, CancellationToken cancellationToken) =>
        db.SalaryStructures
            .Where(x => x.TenantId == employee.TenantId && x.Code == code && !x.IsDeleted
                && (x.CompanyId == employee.CompanyId || x.CompanyId == null))
            .OrderByDescending(x => x.IsActive)
            .ThenByDescending(x => x.CompanyId == employee.CompanyId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The ONE rule for which structure a salary may be put on — create, draft activation, approved changes, merit and
    /// the change-request pre-check all use it, so they refuse the same things with the same error:
    /// <list type="bullet">
    /// <item>a named code that matches no structure of the employee's company or group-wide → <see cref="UnknownSalaryStructureException"/>;</item>
    /// <item>a named code whose structure is inactive (retired, or superseded when it was edited while in use) →
    /// <see cref="InactiveSalaryStructureException"/>;</item>
    /// <item>blank, or the employee's own default code (DIRECT / GRADE-x): the active default, following a superseded
    /// default to its active successor version; null when no default exists yet (the writer creates it); refused as
    /// inactive only when every version of it is inactive.</item>
    /// </list>
    /// </summary>
    public static async Task<SalaryStructure?> ResolveStructureAsync(ZayraDbContext db, Employee employee, Grade? grade, string? requestedCode, CancellationToken cancellationToken)
    {
        var code = Clean(requestedCode);
        if (code.Length == 0) code = DefaultStructureCode(grade);
        var isDefault = IsDefaultStructureCode(code, grade);
        var structure = await FindStructureAsync(db, employee, code, cancellationToken);
        if (structure is null)
            return isDefault ? null : throw new UnknownSalaryStructureException(code);
        if (structure.IsActive) return structure;
        if (isDefault)
        {
            var seen = new HashSet<Guid> { structure.Id };
            var current = structure;
            while (await db.SalaryStructures
                       .Where(x => x.TenantId == employee.TenantId && x.PreviousVersionId == current.Id && !x.IsDeleted)
                       .OrderByDescending(x => x.VersionNumber)
                       .FirstOrDefaultAsync(cancellationToken) is { } next && seen.Add(next.Id))
            {
                if (next.IsActive) return next;
                current = next;
            }
        }
        throw new InactiveSalaryStructureException(code);
    }

    /// <summary>
    /// The pre-save structure check, for exactly the case <see cref="ApplyAsync"/> writes: only when the request, or the
    /// grade's pay scale, yields a positive salary. With nothing to write (a create with no salary, an update that omits
    /// the breakdown) no structure is used, so a retired grade default must not refuse an unrelated save.
    /// Returns the breakdown <see cref="ApplyAsync"/> would write (same arguments, same answer), so the caller checks the
    /// grade's salary range against the package that is actually paid, not only the figures that were typed.
    /// </summary>
    public static async Task<EmployeeSalaryBreakdownRequest> EnsureKnownStructureAsync(ZayraDbContext db, Employee employee, EmployeeSalaryBreakdownRequest? request,
        CancellationToken cancellationToken, bool fillOmittedFromGrade = false)
    {
        if (employee.TenantId is null) return request ?? EmptyBreakdown;
        var (grade, salary, _, _) = await ResolveSalaryAsync(db, employee, request, fillOmittedFromGrade, cancellationToken);
        if (GrossSalary(salary) <= 0) return salary;
        await ResolveStructureAsync(db, employee, grade, request?.SalaryStructureCode, cancellationToken);
        return salary;
    }

    /// <summary>The grade and the breakdown <see cref="ApplyAsync"/> would write — one computation for the write and its pre-check.</summary>
    private static async Task<(Grade? Grade, EmployeeSalaryBreakdownRequest Salary, bool ReleaseA, List<GradePayScaleComponent> Components)> ResolveSalaryAsync(ZayraDbContext db, Employee employee,
        EmployeeSalaryBreakdownRequest? request, bool fillOmittedFromGrade, CancellationToken cancellationToken)
    {
        var grade = employee.GradeId is null
            ? null
            : await db.Grades.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.Id == employee.GradeId && !x.IsDeleted, cancellationToken);
        // Release A: one fact in one place. The grade's cash allowances are the matrix (Benefits by grade); the frozen
        // legacy pay scale is never read for a release_a tenant. The request was already prefilled from the matrix
        // (PrefillSalaryFromMatrixAsync) and is used as it stands. Tenants without the flag are unchanged.
        var releaseA = await EntitlementMatrixService.ReleaseAEnabledAsync(db, employee.TenantId!.Value, cancellationToken);
        var components = grade is null || releaseA
            ? new List<GradePayScaleComponent>()
            : await db.GradePayScaleComponents
                .AsNoTracking()
                .Where(x => x.TenantId == employee.TenantId && x.GradeId == grade.Id && x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ToListAsync(cancellationToken);
        var salary = releaseA
            ? request ?? EmptyBreakdown
            : request is null && !fillOmittedFromGrade
                ? EmptyBreakdown
                : MergeGradePayScale(request, components);
        return (grade, salary, releaseA, components);
    }

    /// <summary>
    /// The payroll profile's free-text "salary structure reference" is derived from the structure the salary is
    /// assigned to — the forms no longer ask for it. It follows the assignment while it is blank or still names the
    /// previous structure; a reference somebody set to something else (an import's own code, an explicit API value
    /// in the same change) is left alone rather than overwritten.
    /// </summary>
    private static async Task MirrorStructureReferenceAsync(ZayraDbContext db, Employee employee, string structureCode,
        IReadOnlyCollection<string> previousStructureCodes, CancellationToken cancellationToken)
    {
        var profile = db.EmployeePayrollProfiles.Local
                          .FirstOrDefault(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && !x.IsDeleted)
                      ?? await db.EmployeePayrollProfiles
                          .FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && !x.IsDeleted, cancellationToken);
        if (profile is null) return;
        var current = Clean(profile.SalaryStructureReference);
        if (current.Length == 0 || previousStructureCodes.Contains(current, StringComparer.OrdinalIgnoreCase))
            profile.SalaryStructureReference = structureCode;
    }
    private static async Task<string> ResolveCompanyCurrencyAsync(ZayraDbContext db, Employee employee, CancellationToken cancellationToken)
    {
        if (employee.CompanyId is not Guid companyId) return "AED";
        var currency = await db.Companies.AsNoTracking()
            .Where(x => x.TenantId == employee.TenantId && x.Id == companyId)
            .Select(x => x.DefaultCurrency)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(currency) ? "AED" : currency.Trim().ToUpperInvariant();
    }
    /// <summary>The monthly salary field a grade pay-scale line lands on, or null when it is not monthly cash.</summary>
    public enum GradeSalaryField { Basic, Housing, Transport, Food, Mobile, Other, FixedDeduction }

    /// <summary>
    /// Which field of the monthly salary row a grade pay-scale line belongs to — null when the line is not part of the
    /// monthly salary at all. Only an active MONTHLY line counts: an Annual line (a yearly ticket, a yearly bonus) is not
    /// a monthly payment. An Earning line is cash by its code (BASIC, HOUS…, TRANS…, FOOD…, MOBILE…, anything else Other);
    /// a Deduction line is the fixed deduction; a Benefit line (medical cover, a ticket in kind) is not cash and is never
    /// paid as an allowance. Blank type or frequency mean the entity defaults, Earning and Monthly. The frontend mirrors
    /// this rule in <c>frontend/src/lib/gradeSalaryDefaults.ts</c>.
    /// </summary>
    public static GradeSalaryField? MonthlySalaryField(GradePayScaleComponent component)
    {
        if (!component.IsActive) return null;
        var frequency = (component.Frequency ?? string.Empty).Trim();
        if (frequency.Length > 0 && !frequency.Equals("Monthly", StringComparison.OrdinalIgnoreCase)) return null;
        var type = (component.ComponentType ?? string.Empty).Trim();
        if (type.Equals(PayComponentTypes.Deduction, StringComparison.OrdinalIgnoreCase)) return GradeSalaryField.FixedDeduction;
        if (type.Length > 0 && !type.Equals(PayComponentTypes.Earning, StringComparison.OrdinalIgnoreCase)) return null;
        var code = (component.ComponentCode ?? string.Empty).ToUpperInvariant();
        if (code.Contains("BASIC")) return GradeSalaryField.Basic;
        if (code.Contains("HOUS")) return GradeSalaryField.Housing;
        if (code.Contains("TRANS")) return GradeSalaryField.Transport;
        if (code.Contains("FOOD")) return GradeSalaryField.Food;
        if (code.Contains("MOBILE")) return GradeSalaryField.Mobile;
        return GradeSalaryField.Other;
    }

    private static bool IsPercentOfBasic(GradePayScaleComponent component) =>
        string.Equals((component.CalculationType ?? string.Empty).Trim(), "PercentOfBasic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// THE grade pay scale → salary rule for tenants without release_a (release_a reads Benefits by grade instead, in
    /// EmployeeManagementService.PrefillSalaryFromMatrixAsync, with the same per-field semantics). Field by field: a
    /// figure the operator supplied — an explicit 0 included — is kept; a field left blank (null) is filled from the
    /// grade's monthly lines for it (see <see cref="MonthlySalaryField"/>). The basic salary resolves first (the
    /// request's, else the grade's fixed BASIC), then every percent-of-basic line is that basic × its percentage, to the
    /// halala (2 dp, half away from zero, as the matrix rounds). A field the grade has no line for stays blank.
    /// <para>It used to be all-or-nothing: one typed figure (a basic of 4,800) dropped the whole pay scale, so the hire was
    /// paid basic only; percent lines were read as their (zero) amount; and Benefit and Annual lines were paid as monthly
    /// "other allowance" cash.</para>
    /// </summary>
    public static EmployeeSalaryBreakdownRequest MergeGradePayScale(EmployeeSalaryBreakdownRequest? request, IReadOnlyCollection<GradePayScaleComponent> components)
    {
        request ??= EmptyBreakdown;
        var lines = components
            .Select(component => (Component: component, Field: MonthlySalaryField(component)))
            .Where(x => x.Field is not null)
            .Select(x => (x.Component, Field: x.Field!.Value))
            .ToList();
        if (lines.Count == 0) return request;

        var gradeBasicLines = lines.Where(x => x.Field == GradeSalaryField.Basic && !IsPercentOfBasic(x.Component)).ToList();
        var basic = request.BasicSalary ?? (gradeBasicLines.Count > 0 ? gradeBasicLines.Sum(x => x.Component.Amount) : null);
        var basisForPercent = basic ?? 0m;

        decimal? Fill(decimal? supplied, GradeSalaryField field)
        {
            if (supplied is not null) return supplied;
            var forField = lines.Where(x => x.Field == field).ToList();
            if (forField.Count == 0) return null;
            return forField.Sum(x => IsPercentOfBasic(x.Component)
                ? Math.Round(basisForPercent * x.Component.Percentage / 100m, 2, MidpointRounding.AwayFromZero)
                : x.Component.Amount);
        }

        return request with
        {
            BasicSalary = basic,
            HousingAllowance = Fill(request.HousingAllowance, GradeSalaryField.Housing),
            TransportAllowance = Fill(request.TransportAllowance, GradeSalaryField.Transport),
            FoodAllowance = Fill(request.FoodAllowance, GradeSalaryField.Food),
            MobileAllowance = Fill(request.MobileAllowance, GradeSalaryField.Mobile),
            OtherAllowance = Fill(request.OtherAllowance, GradeSalaryField.Other),
            FixedDeduction = Fill(request.FixedDeduction, GradeSalaryField.FixedDeduction),
        };
    }
    private const string DirectSalaryStructureCode = "DIRECT";
    private static string Clean(string? text) => (text ?? string.Empty).Trim();
    private static readonly EmployeeSalaryBreakdownRequest EmptyBreakdown = new(null, null, null, null, null, null, null, null, null, null);
    private static DateOnly SalaryEffectiveDate(Employee employee, EmployeeSalaryBreakdownRequest? request) =>
        request?.EffectiveDate ?? DateOnly.FromDateTime(employee.JoiningDate == default ? DateTime.UtcNow : employee.JoiningDate);
    private static decimal GrossSalary(EmployeeSalaryBreakdownRequest? salary) =>
        (salary?.BasicSalary ?? 0m) + (salary?.HousingAllowance ?? 0m) + (salary?.TransportAllowance ?? 0m)
        + (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m);
}

/// <summary>
/// A salary structure the salary may not be put on. Every employee salary path answers 422 with
/// <c>{ error, code, salaryStructureCode, message }</c>, where <c>error</c> is <see cref="ErrorCode"/>:
/// <c>unknown_salary_structure</c> (no such structure for the employee's company or group-wide) or
/// <c>inactive_salary_structure</c> (it exists but is retired or superseded). The client's fix is the same — choose
/// another structure or leave it on the default — but the message says which.
/// </summary>
public abstract class SalaryStructureRefusedException(string code, string message) : InvalidOperationException(message)
{
    public abstract string ErrorCode { get; }
    public string StructureCode { get; } = code;
}

public sealed class UnknownSalaryStructureException(string code)
    : SalaryStructureRefusedException(code, $"Salary structure '{code}' does not exist for this employee's company. Choose one of the company's salary structures, or leave it blank to use the default.")
{
    public const string Code = "unknown_salary_structure";
    public override string ErrorCode => Code;
}

public sealed class InactiveSalaryStructureException(string code)
    : SalaryStructureRefusedException(code, $"Salary structure '{code}' is no longer active. Choose one of the company's active salary structures, or leave it blank to use the default.")
{
    public const string Code = "inactive_salary_structure";
    public override string ErrorCode => Code;
}

/// <summary>Where a <see cref="SalaryChangeRequest"/> came from. "Manual" and "Import" (payroll CSV) change an existing
/// employee's pay; the two below stage a NEW hire's first package.</summary>
public static class SalaryChangeSources
{
    public const string NewHire = "NewHire";
    public const string EmployeeImport = "EmployeeImport";
    /// <summary>First-package sources: the employee has no earlier package, and activation waits for the approval.</summary>
    public static bool IsFirstPackage(string? source) => source is NewHire or EmployeeImport;
}
