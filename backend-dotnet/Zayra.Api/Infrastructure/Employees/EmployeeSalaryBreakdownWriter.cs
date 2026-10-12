using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>Shared effective-dated salary writer for employee creation and approved profile changes.</summary>
public static class EmployeeSalaryBreakdownWriter
{
    public static async Task ApplyAsync(ZayraDbContext db, Employee employee, EmployeeSalaryBreakdownRequest? request, RequestContext context, CancellationToken cancellationToken)
    {
        // A GRADE IS NO LONGER THE GATE. It used to be: an employee without a grade returned here
        // before the breakdown was ever read, so an explicit salaryBreakdown on the create/update
        // request was accepted with 200 and silently dropped — employee.Salary stayed 0, no
        // EmployeeSalaryStructure row was written, and the employee joined payroll on nothing. A
        // 14-employee run then reported a gross total of the one person who happened to have a
        // grade. The repo rule is refuse rather than guess; silent partial success is neither, so
        // the operator's numbers are now honoured whether or not a grade resolves.
        if (employee.TenantId is null) return;
        var grade = employee.GradeId is null
            ? null
            : await db.Grades.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.Id == employee.GradeId && !x.IsDeleted, cancellationToken);

        var effectiveDate = SalaryEffectiveDate(employee, request);
        // Release A: one fact in one place. The grade's cash allowances are the matrix (Benefits by grade); the frozen
        // legacy pay scale is never read for a release_a tenant. The request was already prefilled from the matrix
        // (PrefillSalaryFromMatrixAsync) and is used as it stands. Tenants without the flag are unchanged.
        var releaseA = await EntitlementMatrixService.ReleaseAEnabledAsync(db, employee.TenantId.Value, cancellationToken);
        var components = grade is null || releaseA
            ? new List<GradePayScaleComponent>()
            : await db.GradePayScaleComponents
                .AsNoTracking()
                .Where(x => x.TenantId == employee.TenantId && x.GradeId == grade.Id && x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ToListAsync(cancellationToken);

        var salary = releaseA ? request ?? EmptyBreakdown : BuildSalaryBreakdown(request, components);
        // Nothing to assign: no grade pay scale AND no supplied figures. Unchanged behaviour — this
        // is the ordinary "the salary section of the form was left empty" case, not a dropped value.
        if (GrossSalary(salary) <= 0) return;

        var replaced = await db.EmployeeSalaryStructures
            .Where(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && x.IsActive && x.EffectiveDate == effectiveDate)
            .ToListAsync(cancellationToken);
        foreach (var prior in replaced) prior.IsActive = false;

        var structureCode = Clean(request?.SalaryStructureCode);
        if (string.IsNullOrWhiteSpace(structureCode))
            structureCode = grade is null ? DirectSalaryStructureCode : $"GRADE-{grade.Code}";
        // The employing company's own structure first, then a group-wide one (CompanyId null): the same rule the import uses,
        // so company B's new hire is never attached to company A's structure lines.
        var structure = await db.SalaryStructures
            .Where(x => x.TenantId == employee.TenantId && x.Code == structureCode && !x.IsDeleted
                && (x.CompanyId == employee.CompanyId || x.CompanyId == null))
            .OrderByDescending(x => x.CompanyId == employee.CompanyId)
            .FirstOrDefaultAsync(cancellationToken);
        if (structure is null)
        {
            // Currency, most specific first: what the operator typed, then the grade's, then the
            // employing company's default. Never a hard-coded literal — the entity default is "AED".
            var structureCurrency = Clean(request?.Currency) is { Length: > 0 } requestCurrency
                ? requestCurrency.ToUpperInvariant()
                : grade?.Currency is { Length: > 0 } gradeCurrency
                    ? gradeCurrency
                    : await ResolveCompanyCurrencyAsync(db, employee, cancellationToken);
            structure = new SalaryStructure
            {
                TenantId = employee.TenantId.Value,
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
        }

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
    private static EmployeeSalaryBreakdownRequest BuildSalaryBreakdown(EmployeeSalaryBreakdownRequest? request, IReadOnlyCollection<GradePayScaleComponent> components)
    {
        if (request is not null && GrossSalary(request) > 0) return request;

        decimal basic = 0, housing = 0, transport = 0, food = 0, mobile = 0, other = 0, fixedDeduction = 0;
        foreach (var component in components)
        {
            var code = component.ComponentCode.ToUpperInvariant();
            var amount = component.Amount;
            if (code.Contains("BASIC")) basic += amount;
            else if (code.Contains("HOUS")) housing += amount;
            else if (code.Contains("TRANS")) transport += amount;
            else if (code.Contains("FOOD")) food += amount;
            else if (code.Contains("MOBILE")) mobile += amount;
            else if (component.ComponentType.Equals("Deduction", StringComparison.OrdinalIgnoreCase)) fixedDeduction += amount;
            else other += amount;
        }

        return new EmployeeSalaryBreakdownRequest(basic, housing, transport, food, mobile, other, fixedDeduction, request?.SalaryStructureCode, request?.EffectiveDate, request?.Currency);
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
