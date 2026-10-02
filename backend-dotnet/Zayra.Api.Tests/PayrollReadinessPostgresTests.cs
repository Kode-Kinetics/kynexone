using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F06/I01 against REAL PostgreSQL: the payment-prerequisite queries behind GET /api/payroll/readiness
/// and the dashboard's live payroll KPIs must translate and return the same answers the in-memory
/// tests pin (id lists via ANY(), DateOnly ranges, correlated NOT EXISTS with a trimmed IBAN).
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PayrollReadinessPostgresTests
{
    private readonly PostgresFixture _fx;
    public PayrollReadinessPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Readiness_And_DashboardKpis_AgreeOnWhoCannotBePaid()
    {
        Guid tenantId, companyId;
        int paid, blankIban, noProfile;
        await using (var seed = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company
            {
                TenantId = tenantId, LegalNameEn = "PG Readiness KSA", RegistrationNumber = $"R-{Guid.NewGuid():N}",
                CountryCode = "SA", DefaultCurrency = "SAR", WpsEmployerId = "7001234567", IsActive = true,
            };
            var structure = new SalaryStructure
            {
                TenantId = tenantId, CompanyId = company.Id, Code = $"S{Guid.NewGuid():N}"[..10], Name = "Standard",
                Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            };
            seed.AddRange(company, structure);
            Employee Emp(string code) => new()
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"{code}{Guid.NewGuid():N}"[..12], FullName = code,
                Status = "Active", Nationality = "Saudi", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                // These employees now inherit the Saudi company's jurisdiction instead of being stored
                // with a blank country, so they carry the statutory identity it requires: the national ID
                // to activate, and the GOSI reference the PAY gate needs. This test is about who cannot be
                // paid for want of BANK details, so a statutory pay block would confuse what it asserts.
                CountryCode = "SA", IdNumber = $"10000000{code.Length:00}", GosiReference = $"GOSI-{code}",
            };
            var e1 = Emp("PAID"); var e2 = Emp("BLANK"); var e3 = Emp("NOPROF");
            seed.Employees.AddRange(e1, e2, e3);
            await seed.SaveChangesAsync();
            paid = e1.Id; blankIban = e2.Id; noProfile = e3.Id;
            companyId = company.Id;

            seed.EmployeeSalaryStructures.AddRange(new[] { e1, e2 }.Select(e => new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = structure.Id,
                BasicSalary = 5000, Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            }));
            seed.EmployeePayrollProfiles.AddRange(
                new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = e1.Id, Iban = "SA0380000000608010167519", MolId = "MOL-1", SalaryCurrency = "SAR" },
                new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = e2.Id, Iban = "   ", MolId = "MOL-2", SalaryCurrency = "SAR" });
            seed.AttendanceDailyRecords.Add(new AttendanceDailyRecord
            {
                TenantId = tenantId, EmployeeId = e1.Id, WorkDate = new DateOnly(2025, 7, 3), Status = "Present",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _fx.CreateDb();
        var readiness = Assert.IsType<OkObjectResult>(
            await PayComponentNetPayDefectTests.Build(db, tenantId).PayrollReadiness(companyId, 2025, 7, CancellationToken.None)).Value!;
        var pre = (PayrollPaymentPrerequisitesDto)readiness.GetType().GetProperty("PaymentPrerequisites")!.GetValue(readiness)!;

        pre.EvaluatedEmployees.Should().Be(3);
        pre.Employees.Should().NotContain(e => e.EmployeeId == paid, "the fully prepared employee has nothing to fix");
        pre.Employees.Single(e => e.EmployeeId == blankIban).Blocking.Should().Equal(PayrollPaymentPrerequisites.MissingIban);
        pre.Employees.Single(e => e.EmployeeId == noProfile).Blocking.Should()
            .BeEquivalentTo(new[] { PayrollPaymentPrerequisites.MissingSalary, PayrollPaymentPrerequisites.MissingPayrollProfile });
        pre.Recommended.Should().ContainSingle(r => r.Code == PayrollPaymentPrerequisites.NoAttendance && r.Count == 2);
        ((bool)readiness.GetType().GetProperty("IsReadyToPay")!.GetValue(readiness)!).Should().BeFalse();

        // The dashboard counts the same people for the current month (every salary above is long effective).
        var dashboard = new DashboardController(
            db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new PgUnrestrictedScope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("tenant_id", tenantId.ToString()), new Claim("permission", "payroll.read")], "test")),
                },
            },
        };
        var kpis = Assert.IsType<DashboardKpisDto>(Assert.IsType<OkObjectResult>(await dashboard.Kpis(default)).Value);
        kpis.MissingSalaryAssignments.Should().Be(1);
        kpis.MissingBankDetails.Should().Be(2, "a whitespace IBAN and a missing profile both mean no bank details");
    }
}

file sealed class PgUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
        Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}
