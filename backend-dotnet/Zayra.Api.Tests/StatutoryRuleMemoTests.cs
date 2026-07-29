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
/// P0-3: StatutoryRuleReader must memoize per request/run so its EF query count is flat vs
/// headcount (was 5-6 uncached FirstOrDefault per employee → timeout on large tenants), while
/// producing byte-identical payroll values.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class StatutoryRuleMemoTests
{
    private readonly PostgresFixture _fx;
    public StatutoryRuleMemoTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Process_ManyEmployees_StatutoryRuleQueries_AreFlatVsHeadcount()
    {
        const int headcount = 25;
        Guid tenantId, runId; int firstEmpId;

        await using (var seed = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company
            {
                TenantId = tenantId, LegalNameEn = "Memo KSA Co",
                CountryCode = "SAU", Jurisdiction = "KSA-mainland",
                RegistrationNumber = $"MM-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
                IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.Companies.Add(company);

            var emps = new List<Employee>();
            for (int i = 0; i < headcount; i++)
                emps.Add(new Employee
                {
                    TenantId = tenantId, EmployeeCode = $"MM-{i}-{Guid.NewGuid():N}", FullName = $"Emp {i}",
                    Nationality = "Saudi", Status = "Active", CompanyId = company.Id,
                    JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
            seed.Employees.AddRange(emps);
            await seed.SaveChangesAsync();
            firstEmpId = emps[0].Id;

            foreach (var e in emps)
                seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
                {
                    TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(),
                    BasicSalary = 10_000m, HousingAllowance = 2_000m,
                    EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
                });

            var run = new PayrollRun
            {
                TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 7, Status = "Draft",
                CreatedAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.PayrollRuns.Add(run);
            runId = run.Id;
            await seed.SaveChangesAsync();
        }

        // Count every SQL command that touches statutory_rules while the REAL reader runs a run.
        var counter = new CommandCounterInterceptor("statutory_rules");
        await using (var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                         .UseNpgsql(_fx.ConnectionString).AddInterceptors(counter).Options))
        {
            var realReader = new StatutoryRuleReader(db); // ONE instance = one run, shared with the KSA calc
            var ctrl = BuildCtrl(db, tenantId, realReader);
            (await ctrl.Process(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        }

        // ≤ ~6 distinct rule keys for the KSA deduction calc + a handful of OT/LOP pre-loop reads.
        // Without memoization this would be ~5 × headcount. The gate: flat, not proportional.
        counter.Count.Should().BeLessThan(headcount,
            $"statutory_rules queries ({counter.Count}) must be flat, not ~5×{headcount} — memoization must collapse repeats");
        counter.Count.Should().BeLessThanOrEqualTo(12);

        // Value invariance: KSA Saudi GOSI EE = 9% + 0.75% = 9.75% of covered wage (12,000) = 1,170.
        await using (var check = _fx.CreateDb())
        {
            var slip = await check.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == runId && s.EmployeeId == firstEmpId);
            slip.EmployeeStatutoryTotal.Should().Be(1_170m, "memoization must not change computed statutory values");
        }
    }

    private static PayrollController BuildCtrl(ZayraDbContext db, Guid tenantId, IStatutoryRuleReader reader)
    {
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            new MemoNullNotifications(), new MemoKsaPackResolver(reader), reader,
            new MemoNullLetterService(), new NullDocumentStorage(), new PdfRenderGate(8));

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

file sealed class CommandCounterInterceptor : DbCommandInterceptor
{
    private readonly string _needle;
    public int Count { get; private set; }
    public CommandCounterInterceptor(string needle) => _needle = needle;

    private void Bump(DbCommand command)
    {
        if (command.CommandText.Contains(_needle, StringComparison.OrdinalIgnoreCase)) Count++;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData e, InterceptionResult<DbDataReader> r)
    { Bump(command); return base.ReaderExecuting(command, e, r); }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
    { Bump(command); return base.ReaderExecutingAsync(command, e, r, ct); }
}

file sealed class MemoKsaPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public MemoKsaPackResolver(IStatutoryRuleReader r) => _r = r;
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? new KsaDeductionCalculator(_r) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class MemoNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class MemoNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
