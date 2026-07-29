using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// P0-2: PayrollController.Process must be all-or-nothing. A fault injected during the loan-ledger
/// decrement (between the two SaveChanges) must roll back the WHOLE run — no payslips persist, the
/// run stays Draft, and EmployeeLoan.OutstandingBalance is unchanged — leaving the run re-runnable.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayrollProcessAtomicityTests
{
    private readonly PostgresFixture _fx;
    public PayrollProcessAtomicityTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Process_FaultDuringLoanDecrement_RollsBackEverything_AndRunIsReRunnable()
    {
        Guid tenantId, runId, loanId;
        const decimal loanStart = 5_000m, installment = 1_000m;

        // ── Seed KSA company + Saudi employee (with salary) + active loan + Draft run ──
        await using (var seed = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company
            {
                TenantId = tenantId, LegalNameEn = "Atomicity KSA Co",
                CountryCode = "SAU", Jurisdiction = "KSA-mainland",
                RegistrationNumber = $"AT-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
                IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.Companies.Add(company);
            var emp = new Employee
            {
                TenantId = tenantId, EmployeeCode = $"AT-{Guid.NewGuid():N}", FullName = "Atomic Emp",
                Nationality = "Saudi", Status = "Active", CompanyId = company.Id,
                JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.Employees.Add(emp);
            await seed.SaveChangesAsync();

            seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = 10_000m, HousingAllowance = 2_000m,
                EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
            var loan = new EmployeeLoan
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
                LoanNumber = $"LN-{Guid.NewGuid():N}", Status = "Active",
                ApprovedAmount = loanStart, OutstandingBalance = loanStart, InstallmentAmount = installment,
                ApprovedInstallments = 5, TotalRepaid = 0m,
            };
            seed.EmployeeLoans.Add(loan);
            loanId = loan.Id;

            var run = new PayrollRun
            {
                TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft",
                CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.PayrollRuns.Add(run);
            runId = run.Id;
            await seed.SaveChangesAsync();
        }

        // ── Attempt 1: fault injected on the loan-ledger UPDATE (the second save) ──────
        var interceptor = new ThrowOnCommandInterceptor("employee_loans", "UPDATE");
        await using (var faultDb = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                         .UseNpgsql(_fx.ConnectionString).AddInterceptors(interceptor).Options))
        {
            var ctrl = BuildCtrl(faultDb, tenantId);
            var act = async () => await ctrl.Process(runId, CancellationToken.None);
            await act.Should().ThrowAsync<Exception>("the injected fault must surface, not be swallowed");
            interceptor.Fired.Should().BeTrue("the fault must have fired on the loan-ledger UPDATE (i.e. after slips were built)");
        }

        // ── Verify: complete rollback ────────────────────────────────────────────────
        await using (var check = _fx.CreateDb())
        {
            (await check.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId))
                .Status.Should().Be("Draft", "a faulted run must not be left Processed");
            (await check.PayrollSlips.CountAsync(s => s.RunId == runId))
                .Should().Be(0, "no payslips may persist after a mid-run fault");
            (await check.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == loanId))
                .OutstandingBalance.Should().Be(loanStart, "the loan ledger must be untouched after rollback");
        }

        // ── Attempt 2: clean re-run succeeds and decrements the loan EXACTLY once ──────
        await using (var cleanDb = _fx.CreateDb())
        {
            var ctrl = BuildCtrl(cleanDb, tenantId);
            var result = await ctrl.Process(runId, CancellationToken.None);
            result.Should().BeOfType<OkObjectResult>("the previously-faulted run must be fully re-processable");
        }

        await using (var check = _fx.CreateDb())
        {
            (await check.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId))
                .Status.Should().Be("Processed");
            (await check.PayrollSlips.CountAsync(s => s.RunId == runId)).Should().Be(1);
            var loan = await check.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == loanId);
            loan.OutstandingBalance.Should().Be(loanStart - installment, "the loan must be decremented exactly once");
            loan.TotalRepaid.Should().Be(installment);
        }
    }

    private static PayrollController BuildCtrl(ZayraDbContext db, Guid tenantId)
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m)
            .Set("gosi.saned_rate", 0.0075m).Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar", 45_000m)
            .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);

        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            new AtomNullNotifications(), new AtomKsaPackResolver(rules), rules,
            new AtomNullLetterService(), new NullDocumentStorage(), new PdfRenderGate(8));

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test")),
            },
        };
        return ctrl;
    }
}

// Throws when a command's text contains both needles (case-insensitive) — used to simulate a
// crash on the loan-ledger UPDATE that runs after the payslips have already been written.
file sealed class ThrowOnCommandInterceptor : DbCommandInterceptor
{
    private readonly string _a, _b;
    public bool Fired { get; private set; }
    public ThrowOnCommandInterceptor(string a, string b) { _a = a; _b = b; }

    private void Check(DbCommand command)
    {
        var t = command.CommandText;
        if (t.Contains(_a, StringComparison.OrdinalIgnoreCase) && t.Contains(_b, StringComparison.OrdinalIgnoreCase))
        {
            Fired = true;
            throw new InvalidOperationException("Injected fault: simulated crash during loan-ledger decrement.");
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData e, InterceptionResult<DbDataReader> r)
    { Check(command); return base.ReaderExecuting(command, e, r); }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
    { Check(command); return base.ReaderExecutingAsync(command, e, r, ct); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData e, InterceptionResult<int> r)
    { Check(command); return base.NonQueryExecuting(command, e, r); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default)
    { Check(command); return base.NonQueryExecutingAsync(command, e, r, ct); }
}

file sealed class AtomKsaPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public AtomKsaPackResolver(IStatutoryRuleReader r) => _r = r;
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? new KsaDeductionCalculator(_r) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class AtomNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class AtomNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
