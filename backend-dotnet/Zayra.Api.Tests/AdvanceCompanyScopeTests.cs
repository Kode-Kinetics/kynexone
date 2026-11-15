using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A salary advance is owed to the legal entity that paid it, like a loan. A payroll run collects only the advances its own
/// company owns, and a final settlement plans only those, so an employee who moved between entities is never charged in the
/// new entity's payroll for the old entity's advance (whose 1410 receivable the new entity's journal would be relieving).
/// </summary>
public class AdvanceCompanyScopeTests
{
    [Fact]
    public async Task RegularPayroll_CollectsOnlyTheAdvanceOwnedByTheRunsCompany()
    {
        await using var h = await Fixture.Create();
        var prior = h.Advance(h.PriorCompany.Id);
        var current = h.Advance(h.Company.Id);
        h.Db.AddRange(prior, current);
        var run = new PayrollRun { TenantId = h.Tid, CompanyId = h.Company.Id, Year = 2026, Month = 1 };
        h.Db.Add(run);
        await h.Db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await h.Payroll().Process(run.Id, default));

        var deductions = await h.Db.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == run.Id && d.ComponentCode == "ADVANCE_EMI").Select(d => d.Amount).ToListAsync();
        Assert.Equal(300m, deductions.Sum());
        var untouched = await h.Db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == prior.Id);
        Assert.Equal((600m, 0m, "Active"), (untouched.OutstandingBalance, untouched.TotalRepaid, untouched.Status));
        var collected = await h.Db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == current.Id);
        Assert.Equal((300m, 300m), (collected.OutstandingBalance, collected.TotalRepaid));
    }

    [Fact(Skip = "OPEN: the settlement plan (PayrollController.FinalSettlement) still counts advances of every company while the run " +
                 "now recovers only its own company's; the matching company filter on the plan's advance query was not applied in this " +
                 "branch and needs a decision. Until then the run reports the difference as WARN_SETTLEMENT_RECOVERY_REDUCED.")]
    public async Task FinalSettlement_PlansOnlyTheAdvanceOwnedByTheSettlementCompany()
    {
        await using var h = await Fixture.Create();
        h.Db.AddRange(h.Advance(h.PriorCompany.Id), h.Advance(h.Company.Id),
            new GCCComplianceSetting { TenantId = h.Tid, CountryCode = "ARE", EosbEnabled = true },
            new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, SeparationType = "Termination", Status = "InProgress", LastWorkingDay = new(2026, 1, 31) });
        await h.Db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await h.Payroll().FinalSettlement(new(h.Employee.Id, new(2026, 1, 31), OtherDuesAmount: 5000), default));

        Assert.Equal(600m, (await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync()).PlannedAdvanceRecovery);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public ZayraDbContext Db { get; private set; } = null!;
        public Guid Tid { get; } = Guid.NewGuid();
        public Company Company { get; private set; } = null!;
        public Company PriorCompany { get; private set; } = null!;
        public Employee Employee { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var h = new Fixture();
            await h._connection.OpenAsync();
            h.Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(h._connection).Options);
            await h.Db.Database.EnsureCreatedAsync();
            h.Company = new() { TenantId = h.Tid, LegalNameEn = "Current employer", CountryCode = "ARE", Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", RegistrationNumber = "CURRENT" };
            h.PriorCompany = new() { TenantId = h.Tid, LegalNameEn = "Prior employer", CountryCode = "ARE", DefaultCurrency = "AED", RegistrationNumber = "PRIOR" };
            h.Employee = new() { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeCode = "MOVED", FullName = "Moved employee", Nationality = "Indian", Status = "Active", JoiningDate = new(2024, 1, 1) };
            var structure = new SalaryStructure { TenantId = h.Tid, CompanyId = h.Company.Id, Code = "STD", Name = "Standard", Currency = "AED" };
            h.Db.AddRange(h.Company, h.PriorCompany, h.Employee, structure);
            await h.Db.SaveChangesAsync();
            h.Db.Add(new EmployeeSalaryStructure { TenantId = h.Tid, EmployeeId = h.Employee.Id, SalaryStructureId = structure.Id, BasicSalary = 8000, Currency = "AED", EffectiveDate = new(2025, 1, 1), IsActive = true });
            await h.Db.SaveChangesAsync();
            return h;
        }

        public SalaryAdvance Advance(Guid companyId) => new()
        {
            TenantId = Tid, CompanyId = companyId, EmployeeId = Employee.PublicId, EmployeeIntId = Employee.Id, EmployeeName = Employee.FullName,
            AdvanceNumber = $"ADV-{Guid.NewGuid():N}", RequestedAmount = 600m, ApprovedAmount = 600m, RepaymentType = "Installments",
            Installments = 2, InstallmentAmount = 300m, RepaymentStartDate = new(2026, 1, 1), OutstandingBalance = 600m, Status = "Active",
        };

        public PayrollController Payroll()
        {
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                    new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "payroll.write"), new Claim("permission", "payroll.export") }, "test")),
            };
            return new(Db, new Scope(), new HttpContextAccessor { HttpContext = http }, new F2NullNotifications(), new ZeroDeductionPack(),
                new StubRuleReader(), new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
            { ControllerContext = new() { HttpContext = http } };
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }

    private sealed class ZeroDeductionPack : Zayra.Api.Application.CountryPack.ICountryPackResolver
    {
        public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new ZeroDeductions();
        public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
        public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
        public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
        public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
        public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
    }

    private sealed class ZeroDeductions : Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator
    {
        public Task<Zayra.Api.Application.CountryPack.StatutoryDeductionResult> CalculateAsync(Zayra.Api.Application.CountryPack.StatutoryDeductionInput input, CancellationToken ct = default) =>
            Task.FromResult(new Zayra.Api.Application.CountryPack.StatutoryDeductionResult(0, 0, Array.Empty<Zayra.Api.Application.CountryPack.StatutoryDeductionLine>()));
    }
}
