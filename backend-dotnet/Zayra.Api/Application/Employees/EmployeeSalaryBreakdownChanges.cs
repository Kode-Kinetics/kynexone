using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>Full salary packages stay together through maker-checker and effective-date application.</summary>
public static class EmployeeSalaryBreakdownChanges
{
    public const string Key = "salaryBreakdown";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Employee.Salary is numeric(12,2), narrower than each salary assignment component.
    private const decimal MaximumGrossSalary = 9999999999.99m;
    private const string Plain = "0.############################";
    private static readonly string[] AmountKeys = ["basicSalary", "housingAllowance", "transportAllowance", "foodAllowance", "mobileAllowance", "otherAllowance", "fixedDeduction"];

    public static EmployeeSalaryBreakdownRequest Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Salary breakdown must be a complete object.");
        var allowed = AmountKeys.Concat(["salaryStructureCode", "effectiveDate", "currency", "allowances", "reductionConsentReference"]).ToHashSet(StringComparer.Ordinal);
        if (value.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new InvalidOperationException("Salary breakdown contains an unrecognised field.");
        foreach (var key in AmountKeys)
            if (!value.TryGetProperty(key, out var amount) || amount.ValueKind != JsonValueKind.Number || !amount.TryGetDecimal(out var number)
                || number < 0 || number > MaximumGrossSalary || decimal.Round(number, 2) != number)
                throw new InvalidOperationException($"{key} must be a non-negative amount up to {MaximumGrossSalary.ToString(CultureInfo.InvariantCulture)} with at most two decimal places; provide every salary component explicitly.");
        if (!value.TryGetProperty("effectiveDate", out var date) || date.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var effective) || effective == default)
            throw new InvalidOperationException("Salary effective date must be a date in YYYY-MM-DD form.");
        if (!value.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String
            || currency.GetString() is not { Length: 3 } code || !code.All(c => c is >= 'A' and <= 'Z'))
            throw new InvalidOperationException("Salary currency must be a three-letter uppercase currency code.");
        if (value.TryGetProperty("salaryStructureCode", out var structureCode)
            && (structureCode.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || structureCode.GetString()?.Length > 80))
            throw new InvalidOperationException("Salary structure code must be text of 80 characters or fewer.");
        if (value.TryGetProperty("allowances", out var allowanceLines) && allowanceLines.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            throw new InvalidOperationException("Allowances must be a list of lines, each with a code, a basis and an amount or a rate.");
        if (value.TryGetProperty("reductionConsentReference", out var consent) && consent.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new InvalidOperationException("The consent reference must be text.");
        var salary = value.Deserialize<EmployeeSalaryBreakdownRequest>(Json)!;
        if (Zayra.Api.Infrastructure.Payroll.SalaryReductions.CheckReference(salary.ReductionConsentReference) is { } badReference)
            throw new InvalidOperationException(badReference.Message);
        if (salary.BasicSalary <= 0) throw new InvalidOperationException("Basic salary must be greater than zero.");
        // The whole pay, the allowance lines this package states included (a percentage worked on its own basic): carried lines are added once they are resolved.
        if (Gross(salary) + StatedLinesTotal(salary) > MaximumGrossSalary) throw new InvalidOperationException("Gross salary exceeds the supported amount.");
        if (salary.FixedDeduction > Gross(salary)) throw new InvalidOperationException("Fixed deduction cannot exceed the gross salary.");
        return salary;
    }

    /// <summary>True when the change set proposes a salary package that states at least one allowance line (even a malformed one): staging it names an allowance, so
    /// it must be staged under the tenant lock a remove of that allowance holds (<c>SalaryAllowanceLines.LockForStagingAsync</c>).</summary>
    public static bool StatesAllowanceLines(IReadOnlyDictionary<string, JsonElement> changes) =>
        changes.TryGetValue(Key, out var package) && package.ValueKind == JsonValueKind.Object
        && package.TryGetProperty("allowances", out var lines) && lines.ValueKind == JsonValueKind.Array && lines.GetArrayLength() > 0;

    /// <summary>What the lines a package states come to, for the limit check only (full validation is <c>SalaryAllowanceLines.ValidateAsync</c>): a fixed amount as
    /// stated, a percentage as rate × basic. A line that is not a line, or states nothing usable, counts as nothing here and is refused by the full validation.</summary>
    private static decimal StatedLinesTotal(EmployeeSalaryBreakdownRequest salary) =>
        (salary.Allowances ?? []).Where(l => l is not null).Sum(l =>
            string.Equals(l.Basis?.Trim(), Zayra.Api.Models.AllowanceBases.PercentOfBasic, StringComparison.OrdinalIgnoreCase)
                ? l.Rate is decimal rate && rate > 0m && rate <= 1m ? Zayra.Api.Infrastructure.Payroll.SalaryAllowanceLines.PercentAmount(rate, salary.BasicSalary.GetValueOrDefault()) : 0m
                : l.Amount is decimal amount && amount > 0m ? amount : 0m);

    public static decimal Gross(EmployeeSalaryBreakdownRequest salary) =>
        salary.BasicSalary.GetValueOrDefault() + salary.HousingAllowance.GetValueOrDefault() + salary.TransportAllowance.GetValueOrDefault()
        + salary.FoodAllowance.GetValueOrDefault() + salary.MobileAllowance.GetValueOrDefault() + salary.OtherAllowance.GetValueOrDefault();

    /// <param name="restoresSalaryRowId">Set only by the merit-award reversal path (the salary record the award was planned against, found server-side from the
    /// reversal link on the award line, never from the package): the reduction rule is then judged against that pre-award record, and the reversal needs no
    /// consent reference while it lowers nothing below it.</param>
    public static async Task ValidateAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct,
        Guid? restoresSalaryRowId = null)
    {
        if (!changes.TryGetValue(Key, out var value)) return;
        if (changes.ContainsKey("salary")) throw new InvalidOperationException("Submit the salary breakdown or the legacy salary total, not both.");
        var salary = Read(value);
        var grade = await GradeForRangeCheckAsync(db, employee, changes, ct);
        // The customer-defined cash allowances this package pays: stated lines are checked against the live definitions at submit, and a package that
        // does not mention them carries the current ones, so the grade range below is checked on the whole pay.
        var allowances = employee.TenantId is Guid allowanceTenant
            ? await Zayra.Api.Infrastructure.Payroll.SalaryAllowanceLines.ResolveForProposalAsync(
                db, allowanceTenant, employee.Id, salary.EffectiveDate!.Value, salary.BasicSalary!.Value, salary.Allowances, ct, slotsGross: Gross(salary))
            : new Zayra.Api.Infrastructure.Payroll.AllowanceResolution([], false, null);
        if (allowances.Problem is { } allowanceProblem) throw new InvalidOperationException(allowanceProblem.Message);
        // Lowering pay needs the employee's written agreement. Judged against the record in force when this runs — at submit AND again when the package is
        // applied (an approved change calls this again), so a change that was fine when proposed is refused if the pay it would lower has moved.
        if (employee.TenantId is Guid reductionTenant)
        {
            var reduction = await Zayra.Api.Infrastructure.Payroll.SalaryReductions.EvaluateAsync(db, reductionTenant, employee.Id, salary.EffectiveDate!.Value,
                new Zayra.Api.Infrastructure.Payroll.PaySlots(salary.BasicSalary!.Value, salary.HousingAllowance!.Value, salary.TransportAllowance!.Value,
                    salary.FoodAllowance!.Value, salary.MobileAllowance!.Value, salary.OtherAllowance!.Value, salary.FixedDeduction.GetValueOrDefault()),
                allowances.Lines, ct);
            if (Zayra.Api.Infrastructure.Payroll.SalaryReductions.RequireConsent(reduction, salary.ReductionConsentReference) is { } noConsent
                && !(restoresSalaryRowId is Guid preAwardRow
                     && await Zayra.Api.Infrastructure.Compensation.MeritAwardReversals.RestoresPreAwardPayAsync(db, reductionTenant, preAwardRow, reduction, ct)))
                throw new Zayra.Api.Infrastructure.Payroll.SalaryReductionConsentRequiredException(noConsent, reduction.Items);
        }
        var wholePay = Gross(salary) + allowances.Total;
        if (wholePay > MaximumGrossSalary) throw new InvalidOperationException("Gross salary exceeds the supported amount.");
        if (grade is not null && ((grade.MinSalary > 0 && wholePay < grade.MinSalary) || (grade.MaxSalary > 0 && wholePay > grade.MaxSalary)))
            throw new InvalidOperationException($"Salary package is outside grade {grade.Code}'s salary range.");
        // Refused at submit, not at approval, by the same rule the writer applies: an unknown or inactive named
        // structure (SalaryStructureRefusedException). null = the default structure, which the writer creates.
        var structure = await EmployeeSalaryBreakdownWriter.ResolveStructureAsync(db, employee, grade, salary.SalaryStructureCode, ct);
        if (structure is not null)
        {
            var request = new EmployeeSalaryStructureRequest(employee.Id, structure.Id, salary.BasicSalary!.Value, salary.HousingAllowance!.Value,
                salary.TransportAllowance!.Value, salary.FoodAllowance!.Value, salary.MobileAllowance!.Value, salary.OtherAllowance!.Value,
                salary.FixedDeduction!.Value, salary.EffectiveDate!.Value, salary.Currency);
            if (PayrollController.ValidateEmployeeSalaryAssignment(structure, employee, request) is { } error) throw new InvalidOperationException(error);
        }
        // Those richer contracts are maintained by compensation/renewal workflows, not this amount-only form.
        var richerContract = await db.EmployeeSalaryStructures.AnyAsync(s => s.TenantId == employee.TenantId && s.EmployeeId == employee.Id
            && s.IsActive && s.EffectiveDate == salary.EffectiveDate && (s.HousingBasis != AllowanceBases.Amount
                || s.TransportBasis != AllowanceBases.Amount || s.QiwaConfirmedOn != null || s.RenewalCaseId != null), ct);
        // Not "update it through Compensation": merit planning refuses these rows too. The governed salary change (the
        // employee's change request, approved in Approvals) can take a different effective date; the renewal owns the rest.
        if (richerContract) throw new InvalidOperationException(
            $"The salary dated {salary.EffectiveDate:yyyy-MM-dd} has a percent-of-basic or in-kind allowance, or a Qiwa-confirmed contract, so an amount-only package cannot replace it on that date. "
            + "Request the change as a governed salary change with a different effective date (approved in Approvals), or through the contract renewal workflow.");
    }

    /// <summary>The grade whose range judges this package: the grade the same edit moves the employee TO when it names exactly one grade (the rule every grade
    /// edit uses, <see cref="EmployeeGradeResolution"/>), otherwise the grade the employee holds. Judged against the held grade alone, a promotion on a
    /// promotion-sized salary was refused as outside the grade being left, and the grade never changed.</summary>
    private static async Task<Grade?> GradeForRangeCheckAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        if (await EmployeeGradeResolution.FindAsync(db, employee, EmployeeGradeResolution.TermOf(changes), ct) is { } proposed) return proposed;
        return employee.GradeId is null ? null : await db.Grades.AsNoTracking()
            .FirstOrDefaultAsync(g => g.TenantId == employee.TenantId && g.Id == employee.GradeId && !g.IsDeleted, ct);
    }

    public static async Task ApplyAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, Guid? actorId, CancellationToken ct,
        Guid? changeRequestId = null)
    {
        if (!changes.TryGetValue(Key, out var value)) return;
        // Whether this change is a merit-award reversal is read from the server-held link on the award line, not from anything the change carries.
        var restores = changeRequestId is Guid id && employee.TenantId is Guid tenantId
            ? await Zayra.Api.Infrastructure.Compensation.MeritAwardReversals.PreAwardRowAsync(db, tenantId, employee.Id, id, ct) : null;
        await ValidateAsync(db, employee, changes, ct, restores);
        await EmployeeSalaryBreakdownWriter.ApplyAsync(db, employee, Read(value), new RequestContext(null, null, actorId, employee.TenantId), ct);
    }

    public static async Task<EmployeeSalaryBreakdownRequest?> ReadLatestAsync(ZayraDbContext db, Employee employee, CancellationToken ct)
        => (await ReadScheduleAsync(db, employee, ct)).FirstOrDefault();

    public static async Task<IReadOnlyList<EmployeeSalaryBreakdownRequest>> ReadScheduleAsync(ZayraDbContext db, Employee employee, CancellationToken ct)
    {
        var rows = await (from assignment in db.EmployeeSalaryStructures.AsNoTracking()
            join structure in db.SalaryStructures.AsNoTracking() on assignment.SalaryStructureId equals structure.Id
            where assignment.TenantId == employee.TenantId && assignment.EmployeeId == employee.Id && assignment.IsActive
                && structure.TenantId == employee.TenantId && !structure.IsDeleted
            orderby assignment.EffectiveDate descending, assignment.CreatedAtUtc descending
            select new { assignment, structure.Code }).ToListAsync(ct);
        // The customer-defined cash allowances each record carries (null for a record with none, so nothing changes for an employee without any).
        var recordIds = rows.Select(r => r.assignment.Id).ToList();
        var lines = recordIds.Count == 0 || employee.TenantId is not Guid tenantId ? Enumerable.Empty<EmployeeSalaryAllowance>().ToLookup(x => x.SalaryStructureId)
            : (await db.EmployeeSalaryAllowances.AsNoTracking().Where(x => x.TenantId == tenantId && recordIds.Contains(x.SalaryStructureId)).ToListAsync(ct))
                .ToLookup(x => x.SalaryStructureId);
        return rows.Select(r => new EmployeeSalaryBreakdownRequest(r.assignment.BasicSalary, r.assignment.HousingAllowance,
            r.assignment.TransportAllowance, r.assignment.FoodAllowance, r.assignment.MobileAllowance, r.assignment.OtherAllowance,
            r.assignment.FixedDeduction, r.Code, r.assignment.EffectiveDate, r.assignment.Currency,
            lines[r.assignment.Id].Any()
                ? lines[r.assignment.Id].OrderBy(l => l.Code, StringComparer.Ordinal).Select(l => new SalaryAllowanceLine(l.Code, l.Basis, l.Rate, l.Amount)).ToList()
                : null)).ToList();
    }

    /// <summary>
    /// The allowance lines of every record in the live schedule, in schedule order: per record the lines sorted by code, each as (code, basis, rate, amount),
    /// figures without trailing zeros (as <see cref="EmployeeChangeBaseline"/> writes every other decimal) so a decimal scale is not a change. A record with none is <c>[]</c>. Sealed under its OWN baseline key (<see cref="EmployeeChangeBaseline"/>),
    /// never inside <see cref="CanonicalSchedule"/>, so the schedule text of every baseline sealed before allowances existed stays byte-identical and still verifies.
    /// </summary>
    public static string CanonicalAllowances(IEnumerable<EmployeeSalaryBreakdownRequest> schedule) => JsonSerializer.Serialize(schedule.Select(s =>
        (s.Allowances ?? []).Where(l => l is not null).OrderBy(l => l.Code, StringComparer.Ordinal).Select(l => new[]
        {
            l.Code ?? "", l.Basis ?? "", l.Rate?.ToString(Plain, CultureInfo.InvariantCulture) ?? "", l.Amount?.ToString(Plain, CultureInfo.InvariantCulture) ?? "",
        }).ToList()));

    // The whole live schedule is baselined: an edit to a different effective date must not be hidden by the latest row.
    public static string CanonicalSchedule(IEnumerable<EmployeeSalaryBreakdownRequest> schedule) => JsonSerializer.Serialize(schedule.Select(s => new[]
    {
        s.BasicSalary.GetValueOrDefault().ToString("G29", CultureInfo.InvariantCulture), s.HousingAllowance.GetValueOrDefault().ToString("G29", CultureInfo.InvariantCulture),
        s.TransportAllowance.GetValueOrDefault().ToString("G29", CultureInfo.InvariantCulture), s.FoodAllowance.GetValueOrDefault().ToString("G29", CultureInfo.InvariantCulture),
        s.MobileAllowance.GetValueOrDefault().ToString("G29", CultureInfo.InvariantCulture), s.OtherAllowance.GetValueOrDefault().ToString("G29", CultureInfo.InvariantCulture),
        s.FixedDeduction.GetValueOrDefault().ToString("G29", CultureInfo.InvariantCulture), s.SalaryStructureCode ?? "",
        s.EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", s.Currency ?? "",
    }));
}
