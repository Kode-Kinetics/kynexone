using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The legacy reconcile report reads only the companies the caller reaches: the company scope is in the query (never the whole tenant loaded and filtered
/// afterwards), and an enrolment with no company is tenant-wide, so only a group-level caller sees it.
/// </summary>
public sealed class LegacyReconcileScopeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ACompanyScopedCaller_SeesOnlyTheirCompanysEnrolments_AndTheQueryCarriesTheScope()
    {
        await using var h = await Harness.CreateAsync();

        h.Sql.Clear();
        var report = Ok(await h.Controller(scope: [h.CompanyA]).LegacyReconcile(default));

        report.Items.Select(i => i.EnrollmentId).Should().BeEquivalentTo([h.InA]);
        // The caller's company list is sent to the database as a parameter of the read (the global filter alone carries none here): the scope is applied by
        // the query, never after loading the whole tenant.
        var read = h.Sql.Where(s => s.Contains("FROM \"benefit_enrollments\"", StringComparison.OrdinalIgnoreCase)).Should().ContainSingle().Subject;
        read.Should().Contain("@__companies_", "the scope is applied by the database, not after loading the tenant");
    }

    [Fact]
    public async Task AGroupLevelCaller_SeesEveryCompany_AndTheTenantWideRow()
    {
        await using var h = await Harness.CreateAsync();

        var report = Ok(await h.Controller(scope: null).LegacyReconcile(default));

        report.Items.Select(i => i.EnrollmentId).Should().BeEquivalentTo([h.InA, h.InB, h.NoCompany]);
    }

    private static LegacyBenefitReconcileReport Ok(IActionResult r) => (LegacyBenefitReconcileReport)r.Should().BeOfType<OkObjectResult>().Subject.Value!;

    private sealed class Harness : IAsyncDisposable
    {
        private SqliteConnection _conn = null!;
        public ZayraDbContext Db { get; private set; } = null!;
        public List<string> Sql { get; } = new();
        public Guid Tid { get; } = Guid.NewGuid();
        public Guid CompanyA { get; private set; }
        public Guid InA { get; private set; }
        public Guid InB { get; private set; }
        public Guid NoCompany { get; private set; }

        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            h._conn = new SqliteConnection("DataSource=:memory:");
            await h._conn.OpenAsync();
            h.Db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(h._conn)
                .LogTo(s => h.Sql.Add(s), [DbLoggerCategory.Database.Command.Name], LogLevel.Information).Options);
            await h.Db.Database.EnsureCreatedAsync();
            var a = new Company { TenantId = h.Tid, LegalNameEn = "Co A", CountryCode = "SAU", IsActive = true, DefaultCurrency = "SAR" };
            var b = new Company { TenantId = h.Tid, LegalNameEn = "Co B", CountryCode = "SAU", IsActive = true, DefaultCurrency = "SAR" };
            h.Db.Companies.AddRange(a, b);
            h.Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = h.Tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
            var employee = new Employee { TenantId = h.Tid, CompanyId = a.Id, EmployeeCode = "LR1", FullName = "Synthetic Reconcile", Status = "Active", JoiningDate = new DateTime(2024, 1, 1) };
            var plan = new BenefitPlan { TenantId = h.Tid, CompanyId = null, Code = "LEG", Name = "Legacy", PlanType = "Allowance", Currency = "SAR", EffectiveFrom = new DateOnly(2024, 1, 1) };
            h.Db.AddRange(employee, plan);
            await h.Db.SaveChangesAsync();
            BenefitEnrollment Enrol(Guid? company) => new()
            {
                TenantId = h.Tid, CompanyId = company, EmployeeId = employee.Id, EmployeeName = employee.FullName, BenefitPlanId = plan.Id,
                EffectiveFrom = new DateOnly(2026, 1, 1), Status = "Active",
                PaymentPolicySnapshotJson = JsonSerializer.Serialize(new BenefitPayrollPolicyEnvelope(1, plan.Id, "SAR", new BenefitPayrollPolicy("Coverage"), null), Web),
            };
            var inA = Enrol(a.Id); var inB = Enrol(b.Id); var none = Enrol(null);
            h.Db.BenefitEnrollments.AddRange(inA, inB, none);
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();
            (h.CompanyA, h.InA, h.InB, h.NoCompany) = (a.Id, inA.Id, inB.Id, none.Id);
            return h;
        }

        public BenefitsController Controller(Guid[]? scope)
        {
            var claims = new List<Claim>
            {
                new("tenant_id", Tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "HR Manager"),
                new("permission", "employees.write"),
                new(EntityScopeContext.V2ClaimType, scope is null
                    ? JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })
                    : JsonSerializer.Serialize(new { v = 2, m = "companies", c = scope })),
            };
            return new BenefitsController(Db)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
            };
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _conn.DisposeAsync(); }
    }
}
