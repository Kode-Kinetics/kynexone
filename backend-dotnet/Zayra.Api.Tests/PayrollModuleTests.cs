using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Covers: template download (both route aliases), export audit logging, tenant isolation,
/// company filter in overview, readiness step logic, employee salary import validation,
/// cross-tenant code rejection, AI insight engine anomaly generation, and deduplication.
/// </summary>
public class PayrollModuleTests
{
    // ── DB factories ─────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // SQLite in-memory is needed for endpoints that use ExecuteUpdateAsync
    private static (ZayraDbContext db, SqliteConnection conn) CreateSqliteDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>()
                .UseSqlite(conn)
                .Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    // ── Controller factory ───────────────────────────────────────────────────

    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId, string[]? permissions = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        if (permissions != null)
            claims.AddRange(permissions.Select(p => new Claim("permission", p)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };

        var ctrl = new PayrollController(
            db,
            new _UnrestrictedScope(),
            new _HttpAccessor(httpCtx),
            new _NullNotifications(),
            new _NullPackResolver(),
            new StubRuleReader(),
            new _NullLetterService(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // ── Section 1: Template download — both route aliases ────────────────────

    [Fact]
    public void SalaryStructureTemplate_ReturnsHeaderRow()
    {
        // Endpoint is route-aliased so both /structures/ and /salary-structures/ routes work.
        // We test the action method directly — both routes invoke the same method.
        var db = CreateDb();
        var ctrl = MakeCtrl(db, Guid.NewGuid());

        var result = ctrl.StructuresImportTemplate();

        var content = Assert.IsType<ContentResult>(result);
        content.ContentType.Should().Be("text/csv");
        // Must contain all four expected column headers
        content.Content.Should().Contain("Code");
        content.Content.Should().Contain("Name");
        content.Content.Should().Contain("Currency");
        content.Content.Should().Contain("EffectiveDate");
    }

    [Fact]
    public void EmployeeSalaryTemplate_ReturnsAllHeaders()
    {
        var db = CreateDb();
        var ctrl = MakeCtrl(db, Guid.NewGuid());

        var result = ctrl.EmployeeSalariesImportTemplate();

        var content = Assert.IsType<ContentResult>(result);
        content.ContentType.Should().Be("text/csv");
        foreach (var col in new[] { "EmployeeCode", "SalaryStructureCode", "BasicSalary",
            "HousingAllowance", "TransportAllowance", "FoodAllowance",
            "MobileAllowance", "OtherAllowance", "FixedDeduction", "Currency", "EffectiveDate" })
        {
            content.Content.Should().Contain(col, because: $"column {col} must appear in template");
        }
    }

    // ── Section 2: Salary structure export — both route aliases ──────────────

    [Fact]
    public async Task SalaryStructureExport_ReturnsCsvWithStructures()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.SalaryStructures.Add(new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-001", Name = "Staff Grade A",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1)
        });
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.ExportStructures(CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        content.ContentType.Should().Be("text/csv");
        content.Content.Should().Contain("STR-001");
        content.Content.Should().Contain("Staff Grade A");
    }

    // ── Section 3: Tenant isolation — overview only returns own companies ─────

    [Fact]
    public async Task Overview_TenantIsolation_OnlyReturnsTenantCompanies()
    {
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.Companies.AddRange(
            new Company { TenantId = tenantA, LegalNameEn = "Alpha Corp", CountryCode = "AE" },
            new Company { TenantId = tenantA, LegalNameEn = "Alpha Retail", CountryCode = "AE" },
            new Company { TenantId = tenantB, LegalNameEn = "Beta Corp", CountryCode = "KW" });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantA);
        var result = await ctrl.PayrollOverview(null, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var companies = (IEnumerable<object>)body.GetType().GetProperty("Companies")!.GetValue(body)!;
        companies.Should().HaveCount(2, "tenantA has 2 companies");

        // None of the returned companies should belong to tenantB
        var names = companies.Select(c => c.GetType().GetProperty("CompanyName")!.GetValue(c)!.ToString());
        names.Should().NotContain("Beta Corp");
    }

    // ── Section 4: Company filter in overview ─────────────────────────────────

    [Fact]
    public async Task Overview_CompanyFilter_ReturnsOnlyMatchingCompany()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantId, LegalNameEn = "Company A", CountryCode = "AE" };
        var companyB = new Company { TenantId = tenantId, LegalNameEn = "Company B", CountryCode = "AE" };
        db.Companies.AddRange(companyA, companyB);
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var result = await ctrl.PayrollOverview(companyA.Id, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var companies = (IEnumerable<object>)body.GetType().GetProperty("Companies")!.GetValue(body)!;
        companies.Should().HaveCount(1);
        companies.Single().GetType().GetProperty("CompanyName")!.GetValue(companies.Single())
            .Should().Be("Company A");
    }

    // ── Section 5: Readiness — zero completion when nothing configured ─────────

    [Fact]
    public async Task Readiness_NoConfiguration_ReturnsZeroCompletion()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.PayrollReadiness(null, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var pct = (double)body.GetType().GetProperty("CompletionPercent")!.GetValue(body)!;
        pct.Should().Be(0, "no components, structures, or assignments exist");
        var ready = (bool)body.GetType().GetProperty("IsReadyForProcessing")!.GetValue(body)!;
        ready.Should().BeFalse();
    }

    // ── Section 6: Readiness — steps complete when data is configured ─────────

    [Fact]
    public async Task Readiness_FullyConfigured_ReturnsHighCompletion()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();

        // Step 1: Salary component
        var component = new SalaryComponent
        {
            TenantId = tenantId, Code = "BASIC", Name = "Basic", ComponentType = "Earning",
            CalculationType = "Fixed", Amount = 5000, IsActive = true,
            SalaryStructureId = null
        };
        db.SalaryComponents.Add(component);

        // Step 2: Salary structure
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-001", Name = "Grade A",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        };
        db.SalaryStructures.Add(structure);

        // Step 3: Employee + salary assignment (80%+ coverage)
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Alice",
            Status = "Active", CompanyId = companyId, JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = 5000, Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        });

        // Step 4: Payroll run in Processed status
        db.PayrollRuns.Add(new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = 2025, Month = 1,
            Status = "Processed", TotalGrossSalary = 5000, TotalNetSalary = 5000
        });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var result = await ctrl.PayrollReadiness(null, 2025, 1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var pct = (double)body.GetType().GetProperty("CompletionPercent")!.GetValue(body)!;
        pct.Should().BeGreaterOrEqualTo(66, "at least steps 1, 2, 3, 4, and 6 should be complete");

        var ready = (bool)body.GetType().GetProperty("IsReadyForProcessing")!.GetValue(body)!;
        ready.Should().BeTrue();
    }

    // ── Section 7: Readiness — company-scoped isolation ──────────────────────

    [Fact]
    public async Task Readiness_CompanyScoped_OnlyCountsCompanyEmployees()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();

        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-A", Name = "Grade A",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        };
        db.SalaryStructures.Add(structure);

        // Employee in Company A (assigned)
        var empA = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EA01", FullName = "Bob", Status = "Active",
            CompanyId = companyA, JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        // Employee in Company B (no assignment)
        var empB = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EB01", FullName = "Carol", Status = "Active",
            CompanyId = companyB, JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.AddRange(empA, empB);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = empA.Id, SalaryStructureId = structure.Id,
            BasicSalary = 5000, Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);

        // Readiness for Company A — 1/1 employees assigned → coverage = 100%
        var resultA = await ctrl.PayrollReadiness(companyA, 2025, 1, CancellationToken.None);
        var bodyA = ((OkObjectResult)resultA).Value!;
        var coverageA = (double)bodyA.GetType().GetProperty("SalaryCoveragePercent")!.GetValue(bodyA)!;
        coverageA.Should().Be(100);

        // Readiness for Company B — 1/1 employees, 0 assigned → coverage = 0%
        var resultB = await ctrl.PayrollReadiness(companyB, 2025, 1, CancellationToken.None);
        var bodyB = ((OkObjectResult)resultB).Value!;
        var coverageB = (double)bodyB.GetType().GetProperty("SalaryCoveragePercent")!.GetValue(bodyB)!;
        coverageB.Should().Be(0);
    }

    // ── Section 8: Employee salary import — validation errors ─────────────────

    [Fact]
    public async Task EmployeeSalaryImport_UnknownEmployeeCode_ReturnsError()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId);

        // No employees in the tenant DB — code will never resolve
        var csv = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance," +
                  "FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                  "GHOST001,,5000,0,0,0,0,0,0,AED,2025-01-01\n";

        var result = await ctrl.ImportEmployeeSalaries(
            new ImportSalaryStructuresRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var skipped = (int)body.GetType().GetProperty("skipped")!.GetValue(body)!;
        skipped.Should().Be(1);
        var errors = (IEnumerable<object>)body.GetType().GetProperty("errors")!.GetValue(body)!;
        errors.Should().NotBeEmpty();
        errors.First().ToString().Should().Contain("GHOST001");
    }

    [Fact]
    public async Task EmployeeSalaryImport_NegativeBasicSalary_ReturnsError()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Alice",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var csv = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance," +
                  "FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                  "E001,,-500,0,0,0,0,0,0,AED,2025-01-01\n";

        var result = await ctrl.ImportEmployeeSalaries(
            new ImportSalaryStructuresRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var skipped = (int)body.GetType().GetProperty("skipped")!.GetValue(body)!;
        skipped.Should().Be(1);
        var errors = (IEnumerable<object>)body.GetType().GetProperty("errors")!.GetValue(body)!;
        errors.Should().NotBeEmpty();
        errors.First().ToString().Should().Contain("BasicSalary");
    }

    [Fact]
    public async Task EmployeeSalaryImport_CrossTenantCode_IsRejected()
    {
        // An employee that belongs to tenantB must never be resolvable from a tenantA import
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Employee belongs to tenant B only
        db.Employees.Add(new Employee
        {
            TenantId = tenantB, EmployeeCode = "E-CROSS", FullName = "Dave",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantA); // acting as tenant A
        var csv = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance," +
                  "FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                  "E-CROSS,,5000,0,0,0,0,0,0,AED,2025-01-01\n";

        var result = await ctrl.ImportEmployeeSalaries(
            new ImportSalaryStructuresRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var created = (int)body.GetType().GetProperty("created")!.GetValue(body)!;
        created.Should().Be(0, "tenant A cannot resolve tenant B employee codes");
        var skipped = (int)body.GetType().GetProperty("skipped")!.GetValue(body)!;
        skipped.Should().Be(1);
    }

    // ── Section 9: Employee salary import — valid row, uses SQLite ────────────

    [Fact]
    public async Task EmployeeSalaryImport_ValidRow_CreatesRecord()
    {
        // Uses SQLite because the deactivation step uses ExecuteUpdateAsync
        // which is not supported by the InMemory provider.
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-BASIC", Name = "Basic",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        };
        db.SalaryStructures.Add(structure);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Alice",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var csv = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance," +
                  "FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                  $"E001,STR-BASIC,7500,2000,500,300,200,0,0,AED,2025-01-01\n";

        var result = await ctrl.ImportEmployeeSalaries(
            new ImportSalaryStructuresRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var created = (int)body.GetType().GetProperty("created")!.GetValue(body)!;
        created.Should().Be(1);

        var savedAssignment = await db.EmployeeSalaryStructures
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EmployeeId == emp.Id);
        savedAssignment.Should().NotBeNull();
        savedAssignment!.BasicSalary.Should().Be(7500);
        savedAssignment.HousingAllowance.Should().Be(2000);
    }

    // ── Section 10: Export writes audit log ───────────────────────────────────

    [Fact]
    public async Task EmployeeSalaryExport_WritesAuditLog()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId, permissions: new[] { "payroll.export" });

        var result = await ctrl.ExportEmployeeSalaries(null, CancellationToken.None);

        Assert.IsType<ContentResult>(result);
        var auditLog = await db.PayrollAuditLogs.FirstOrDefaultAsync(
            l => l.TenantId == tenantId && l.Action == "payroll.employee_salary.exported");
        auditLog.Should().NotBeNull("export must always produce an audit log");
    }

    // ── Section 11: AI Insight Engine — anomaly generation ───────────────────
    // Tests call AnalyzeTenantAsync via reflection to bypass the per-tenant
    // try-catch that swallows exceptions when using NullLogger.

    [Fact]
    public async Task AiInsightEngine_MissingSalarySetup_GeneratesInsight()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        db.Employees.AddRange(
            new Employee { TenantId = tenantId, EmployeeCode = "E1", FullName = "Alice", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) },
            new Employee { TenantId = tenantId, EmployeeCode = "E2", FullName = "Bob", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) },
            new Employee { TenantId = tenantId, EmployeeCode = "E3", FullName = "Carol", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) });
        await db.SaveChangesAsync();

        // Verify the employee query works before calling the engine
        var empCount = await db.Employees
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .CountAsync();
        empCount.Should().Be(3);

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);
        await InvokeAnalyzeTenant(engine, db, tenantId);

        var insights = await db.AIInsights
            .Where(i => i.TenantId == tenantId && i.InsightType == "MissingSalarySetup")
            .ToListAsync();
        insights.Should().NotBeEmpty("3 employees have no salary assignment → engine must flag it");
        insights[0].Severity.Should().BeOneOf("Warning", "Critical");
        insights[0].Module.Should().Be("Payroll");
    }

    [Fact]
    public async Task AiInsightEngine_PayrollVariance_GeneratesInsightAboveThreshold()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        // 3-month baseline at 10 000, latest month at 14 000 (40% increase → Critical)
        var baseDate = DateTime.UtcNow.AddMonths(-3);
        for (var i = 0; i < 3; i++)
        {
            db.PayrollRuns.Add(new PayrollRun
            {
                TenantId = tenantId, Year = baseDate.AddMonths(i).Year,
                Month = baseDate.AddMonths(i).Month, Status = "Locked",
                TotalGrossSalary = 11000, TotalNetSalary = 10000
            });
        }
        db.PayrollRuns.Add(new PayrollRun
        {
            TenantId = tenantId, Year = DateTime.UtcNow.Year, Month = DateTime.UtcNow.Month,
            Status = "Locked", TotalGrossSalary = 15000, TotalNetSalary = 14000
        });
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);
        await InvokeAnalyzeTenant(engine, db, tenantId);

        var insight = await db.AIInsights
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.InsightType == "PayrollVariance");
        insight.Should().NotBeNull("40% payroll variance must trigger an insight");
        insight!.Severity.Should().Be("Critical", "40% exceeds the 20% critical threshold");
    }

    [Fact]
    public async Task AiInsightEngine_BelowVarianceThreshold_NoInsightGenerated()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        // All 4 runs at the same net salary — 0% variance
        var baseDate = DateTime.UtcNow.AddMonths(-3);
        for (var i = 0; i < 4; i++)
        {
            db.PayrollRuns.Add(new PayrollRun
            {
                TenantId = tenantId, Year = baseDate.AddMonths(i).Year,
                Month = baseDate.AddMonths(i).Month, Status = "Locked",
                TotalGrossSalary = 12000, TotalNetSalary = 10000
            });
        }
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);
        await InvokeAnalyzeTenant(engine, db, tenantId);

        var insight = await db.AIInsights
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.InsightType == "PayrollVariance");
        insight.Should().BeNull("0% variance must not produce an insight");
    }

    // ── Section 12: AI Insight Engine — deduplication ─────────────────────────

    [Fact]
    public async Task AiInsightEngine_Deduplication_SameTypeNotCreatedWithin24h()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        db.Employees.AddRange(
            new Employee { TenantId = tenantId, EmployeeCode = "X1", FullName = "Alice", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) },
            new Employee { TenantId = tenantId, EmployeeCode = "X2", FullName = "Bob", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) });
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);

        // First analysis pass — should create the insight
        await InvokeAnalyzeTenant(engine, db, tenantId);
        var countAfterFirst = await db.AIInsights
            .CountAsync(i => i.TenantId == tenantId && i.InsightType == "MissingSalarySetup");
        countAfterFirst.Should().Be(1);

        // Second pass immediately after — deduplicated within 24h
        await InvokeAnalyzeTenant(engine, db, tenantId);
        var countAfterSecond = await db.AIInsights
            .CountAsync(i => i.TenantId == tenantId && i.InsightType == "MissingSalarySetup");
        countAfterSecond.Should().Be(1, "deduplication window is 24h; second run must not create a duplicate");
    }

    [Fact]
    public async Task AiInsightEngine_CrossTenantIsolation_InsightsNotLeakedAcrossTenants()
    {
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Only tenant A has employees without salary
        db.Employees.Add(new Employee
        {
            TenantId = tenantA, EmployeeCode = "A1", FullName = "Alice",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        });
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);

        // Analyze tenant A
        await InvokeAnalyzeTenant(engine, db, tenantA);
        // Analyze tenant B (no anomalies)
        await InvokeAnalyzeTenant(engine, db, tenantB);

        var tenantBInsights = await db.AIInsights.Where(i => i.TenantId == tenantB).ToListAsync();
        tenantBInsights.Should().BeEmpty("tenant B has no anomalies — no insights must be written for it");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Calls the internal AnalyzeTenantAsync directly (InternalsVisibleTo in AssemblyInfo.cs),
    // bypassing the per-tenant try-catch so test failures surface as real exceptions.
    private static Task InvokeAnalyzeTenant(AiInsightEngine engine, ZayraDbContext db, Guid tenantId)
        => engine.AnalyzeTenantAsync(db, null, tenantId, CancellationToken.None);

    private static IServiceScopeFactory CreateScopeFactory(ZayraDbContext db) => new _TestScopeFactory(db);
}

// ── File-scoped test stubs ────────────────────────────────────────────────────

file sealed class _UnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _HttpAccessor : IHttpContextAccessor
{
    public _HttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _NullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

// No-op pack resolver — returns default (zero-result) implementations. Only used in PayrollModule
// unit tests that do not exercise the statutory calculation or WPS export paths.
file sealed class _NullPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
}

// Minimal IServiceScopeFactory that hands a fixed ZayraDbContext to any scope.
// The engine calls CreateAsyncScope() twice (outer query + per-tenant), both get same DB.
file sealed class _TestScopeFactory : IServiceScopeFactory
{
    private readonly ZayraDbContext _db;
    public _TestScopeFactory(ZayraDbContext db) => _db = db;
    public IServiceScope CreateScope() => new _TestScope(_db);
}

file sealed class _TestScope : IServiceScope
{
    public _TestScope(ZayraDbContext db) => ServiceProvider = new _TestProvider(db);
    public IServiceProvider ServiceProvider { get; }
    public void Dispose() { }
}

file sealed class _TestProvider : IServiceProvider
{
    private readonly ZayraDbContext _db;
    public _TestProvider(ZayraDbContext db) => _db = db;
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ZayraDbContext)) return _db;
        // ILlmClient is optional in the engine (GetService, not GetRequiredService)
        return null;
    }
}

file sealed class _NullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

