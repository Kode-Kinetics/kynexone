using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-C2 harness: a SAR company with cost accounting ON, a seeded chart (Expense = Required, Asset / Liability = NotAllowed,
/// the explicit preset), cost centres, departments, positions and employees, and runs that go through the REAL
/// Approve and Lock endpoints (the approval hash is part of what is under test).
/// SQLite for the unit-level tests; the Postgres factory flavour for the oracle.
/// </summary>
internal sealed class CostingLockKit : IAsyncDisposable
{
    public GlPostingKit K { get; }
    public ZayraDbContext Db => K.Db;
    public Guid TenantId => K.TenantId;
    public Guid CompanyId => K.CompanyId;
    public Guid ApproverId { get; } = Guid.NewGuid();
    public Guid LockerId { get; } = Guid.NewGuid();

    public Dictionary<string, CostCenter> Cc { get; } = new();
    public Dictionary<string, Department> Dept { get; } = new();
    public Dictionary<string, Position> Pos { get; } = new();
    public Dictionary<string, Employee> Emp { get; } = new();

    private CostingLockKit(GlPostingKit k) => K = k;

    public static async Task<CostingLockKit> CreateAsync(bool costing = true, bool preset = true, Func<ZayraDbContext>? factory = null, Guid? tenantId = null)
    {
        var inner = factory is null ? await GlPostingKit.CreateAsync() : await GlPostingKit.CreateAsync(factory, tenantId ?? Guid.NewGuid());
        var kit = new CostingLockKit(inner);
        await kit.K.SeedGlDefaultsAsync();
        var company = await kit.Db.Companies.SingleAsync(c => c.Id == kit.CompanyId);
        company.CostAccountingEnabled = costing;
        if (preset)
            foreach (var a in await kit.Db.GlAccounts.Where(a => a.TenantId == kit.TenantId).ToListAsync())
                a.CostCenterRule = a.AccountType is "Expense" or "Revenue" ? "Required" : "NotAllowed";
        await kit.Db.SaveChangesAsync();
        // Preparing, approving and releasing a bonus batch each need someone else who could decide it (up-front second-approver check).
        SalaryApprovalKit.EnsureApprover(kit.Db, kit.TenantId);
        return kit;
    }

    public async Task<CostCenter> AddCc(string code, string? cls = null, string? parent = null, string? segment = null, bool active = true)
    {
        var cc = new CostCenter
        {
            TenantId = TenantId, CompanyId = CompanyId, Code = code, Name = $"Name {code}", FunctionalClass = cls,
            GlSegment = segment, ParentId = parent is null ? null : Cc[parent].Id, IsActive = active,
        };
        Db.CostCenters.Add(cc);
        await Db.SaveChangesAsync();
        Cc[code] = cc;
        return cc;
    }

    public async Task<Department> AddDept(string code, string? cc = null, string? parent = null)
    {
        var d = new Department
        {
            TenantId = TenantId, CompanyId = CompanyId, Code = code, NameEn = code,
            CostCenterId = cc is null ? null : Cc[cc].Id, ParentDepartmentId = parent is null ? null : Dept[parent].Id,
        };
        Db.Departments.Add(d);
        await Db.SaveChangesAsync();
        Dept[code] = d;
        return d;
    }

    public async Task<Position> AddPosition(string code, string? cc = null)
    {
        var p = new Position { TenantId = TenantId, CompanyId = CompanyId, Code = code, Title = code, CostCenterId = cc is null ? null : Cc[cc].Id };
        Db.Positions.Add(p);
        await Db.SaveChangesAsync();
        Pos[code] = p;
        return p;
    }

    public async Task<Employee> AddEmployee(string code, string? dept = null, string? pos = null, string? overrideCc = null)
    {
        var e = new Employee
        {
            TenantId = TenantId, CompanyId = CompanyId, EmployeeCode = code, FullName = $"Person {code}", Status = "Active",
            DepartmentId = dept is null ? null : Dept[dept].Id, PositionId = pos is null ? null : Pos[pos].Id,
            CostCenterId = overrideCc is null ? null : Cc[overrideCc].Id, CostCenterIsOverride = overrideCc is not null,
        };
        Db.Employees.Add(e);
        await Db.SaveChangesAsync();
        Emp[code] = e;
        return e;
    }

    public async Task SetDefaultCc(string? code)
    {
        var company = await Db.Companies.SingleAsync(c => c.Id == CompanyId);
        company.DefaultCostCenterId = code is null ? null : Cc[code].Id;
        await Db.SaveChangesAsync();
    }

    /// <summary>A Processed run with the given register (employee code, component, source, amount).</summary>
    public async Task<PayrollRun> SeedRunAsync(
        IEnumerable<(string Emp, string Code, string Source, decimal Amount)> earnings,
        IEnumerable<(string Emp, string Code, string Source, decimal Amount, bool Employer)> deductions,
        decimal? net = null, int month = 9, int year = 2026, string runType = "Regular")
    {
        var e = earnings.ToList();
        var d = deductions.ToList();
        var employeeDeductions = d.Where(x => !x.Employer).Sum(x => x.Amount);
        var run = new PayrollRun
        {
            TenantId = TenantId, CompanyId = CompanyId, Year = year, Month = month, Status = "Processed", RunType = runType,
            TotalGrossSalary = e.Sum(x => x.Amount), TotalDeductions = employeeDeductions,
            TotalNetSalary = net ?? (e.Sum(x => x.Amount) - employeeDeductions),
            EmployeeCount = e.Select(x => x.Emp).Concat(d.Select(x => x.Emp)).Distinct().Count(),
            CreatedByUserId = Guid.NewGuid(), ProcessedByUserId = Guid.NewGuid(),
        };
        Db.PayrollRuns.Add(run);
        foreach (var x in e)
            Db.PayrollEarnings.Add(new PayrollEarning { TenantId = TenantId, PayrollRunId = run.Id, EmployeeId = Emp[x.Emp].Id, ComponentCode = x.Code, ComponentName = $"{x.Code} name", Amount = x.Amount, Source = x.Source });
        foreach (var x in d)
            Db.PayrollDeductions.Add(new PayrollDeduction { TenantId = TenantId, CompanyId = CompanyId, PayrollRunId = run.Id, EmployeeId = Emp[x.Emp].Id, ComponentCode = x.Code, ComponentName = $"{x.Code} name", Amount = x.Amount, Source = x.Source, IsEmployerContribution = x.Employer });
        foreach (var code in e.Select(x => x.Emp).Concat(d.Select(x => x.Emp)).Distinct())
            Db.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = TenantId, CompanyId = CompanyId, RunId = run.Id, EmployeeId = Emp[code].Id, EmployeeCode = code, Status = "Processed",
                GrossSalary = e.Where(x => x.Emp == code).Sum(x => x.Amount),
                NetSalary = e.Where(x => x.Emp == code).Sum(x => x.Amount) - d.Where(x => x.Emp == code && !x.Employer).Sum(x => x.Amount),
            });
        await Db.SaveChangesAsync();
        return run;
    }

    /// <summary>Re-attaches the cached master-data entities (a controller call clears the change tracker) so a test can edit them.</summary>
    public void Touch()
    {
        Db.UpdateRange(Cc.Values); Db.UpdateRange(Dept.Values); Db.UpdateRange(Pos.Values); Db.UpdateRange(Emp.Values);
    }

    public static (string, string, string, decimal) E(string emp, string code, decimal amount, string source = "Salary") => (emp, code, source, amount);
    public static (string, string, string, decimal, bool) D(string emp, string code, string source, decimal amount, bool employer = false) => (emp, code, source, amount, employer);

    public PayrollController Payroll(Guid? userId = null, string role = "Admin") => K.Payroll(Db, userId ?? LockerId, role);

    public async Task<IActionResult> ApproveAsync(Guid runId, Guid? as_ = null)
    {
        Db.ChangeTracker.Clear();
        return await Payroll(as_ ?? ApproverId).Approve(runId, new PayrollDecisionRequest("ok"), default);
    }

    public async Task<IActionResult> LockAsync(Guid runId, Guid? as_ = null)
    {
        Db.ChangeTracker.Clear();
        return await Payroll(as_ ?? LockerId).Lock(runId, default);
    }

    /// <summary>Approve then Lock; both must succeed.</summary>
    public async Task<IActionResult> ApproveAndLockAsync(Guid runId)
    {
        var a = await ApproveAsync(runId);
        if (a is not OkObjectResult) throw new InvalidOperationException($"Approve failed: {Json(a)}");
        return await LockAsync(runId);
    }

    public async Task<List<FinanceGlEntry>> GlAsync(Guid runId)
    {
        Db.ChangeTracker.Clear();
        return await K.RunGlAsync(runId);
    }

    public static JsonElement Json(IActionResult r) => JsonSerializer.SerializeToElement((r as ObjectResult)?.Value);
    public static int Status(IActionResult r) => r is ObjectResult o ? o.StatusCode ?? 200 : r is StatusCodeResult s ? s.StatusCode : 200;
    public static string Err(IActionResult r) => Json(r).TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";

    public ValueTask DisposeAsync() => K.DisposeAsync();
}
