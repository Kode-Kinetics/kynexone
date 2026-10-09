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
    private static readonly string[] AmountKeys = ["basicSalary", "housingAllowance", "transportAllowance", "foodAllowance", "mobileAllowance", "otherAllowance", "fixedDeduction"];

    public static EmployeeSalaryBreakdownRequest Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Salary breakdown must be a complete object.");
        var allowed = AmountKeys.Concat(["salaryStructureCode", "effectiveDate", "currency"]).ToHashSet(StringComparer.Ordinal);
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
        var salary = value.Deserialize<EmployeeSalaryBreakdownRequest>(Json)!;
        if (salary.BasicSalary <= 0) throw new InvalidOperationException("Basic salary must be greater than zero.");
        if (Gross(salary) > MaximumGrossSalary) throw new InvalidOperationException("Gross salary exceeds the supported amount.");
        if (salary.FixedDeduction > Gross(salary)) throw new InvalidOperationException("Fixed deduction cannot exceed the gross salary.");
        return salary;
    }

    public static decimal Gross(EmployeeSalaryBreakdownRequest salary) =>
        salary.BasicSalary.GetValueOrDefault() + salary.HousingAllowance.GetValueOrDefault() + salary.TransportAllowance.GetValueOrDefault()
        + salary.FoodAllowance.GetValueOrDefault() + salary.MobileAllowance.GetValueOrDefault() + salary.OtherAllowance.GetValueOrDefault();

    public static async Task ValidateAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        if (!changes.TryGetValue(Key, out var value)) return;
        if (changes.ContainsKey("salary")) throw new InvalidOperationException("Submit the salary breakdown or the legacy salary total, not both.");
        var salary = Read(value);
        var grade = employee.GradeId is null ? null : await db.Grades.AsNoTracking()
            .FirstOrDefaultAsync(g => g.TenantId == employee.TenantId && g.Id == employee.GradeId && !g.IsDeleted, ct);
        if (grade is not null && ((grade.MinSalary > 0 && Gross(salary) < grade.MinSalary) || (grade.MaxSalary > 0 && Gross(salary) > grade.MaxSalary)))
            throw new InvalidOperationException($"Salary package is outside grade {grade.Code}'s salary range.");
        var code = string.IsNullOrWhiteSpace(salary.SalaryStructureCode) ? grade is null ? "DIRECT" : $"GRADE-{grade.Code}" : salary.SalaryStructureCode.Trim();
        var structure = await db.SalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == employee.TenantId && s.Code == code && !s.IsDeleted && (s.CompanyId == employee.CompanyId || s.CompanyId == null))
            .OrderByDescending(s => s.CompanyId == employee.CompanyId).FirstOrDefaultAsync(ct);
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
        if (richerContract) throw new InvalidOperationException("This salary has allowance-basis or confirmed contract details. Update it through Compensation or its contract renewal workflow.");
    }

    public static async Task ApplyAsync(ZayraDbContext db, Employee employee, IReadOnlyDictionary<string, JsonElement> changes, Guid? actorId, CancellationToken ct)
    {
        if (!changes.TryGetValue(Key, out var value)) return;
        await ValidateAsync(db, employee, changes, ct);
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
        return rows.Select(r => new EmployeeSalaryBreakdownRequest(r.assignment.BasicSalary, r.assignment.HousingAllowance,
            r.assignment.TransportAllowance, r.assignment.FoodAllowance, r.assignment.MobileAllowance, r.assignment.OtherAllowance,
            r.assignment.FixedDeduction, r.Code, r.assignment.EffectiveDate, r.assignment.Currency)).ToList();
    }

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
