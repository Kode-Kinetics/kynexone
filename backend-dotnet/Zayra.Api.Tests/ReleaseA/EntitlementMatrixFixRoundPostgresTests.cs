using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The R1 review fixes on real PostgreSQL 16: a company skip can never change what payroll pays; the two remaining legacy
/// grade-pay writers (AI setup, org-structure CSV) write nothing for a release_a tenant and say why; and salary prefill
/// reads the matrix — never the frozen legacy pay scale — refusing, not guessing, where the grade has no value.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class EntitlementMatrixFixRoundPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly NextMonth = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);

    // ── P1: a skip marker never reaches payroll ──────────────────────────────────────────────────

    [Fact]
    public async Task APaidAirTicketEarning_IsNeverTheSkipTemplate_AndThePayrollSetIsIdenticalBeforeAndAfterSkipAndPublish()
    {
        var seed = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            await PayComponentSeeder.EnsureEntitlementCatalogAsync(db, seed.TenantId, default);
            // A tenant that already paid a ticket allowance as an Earning, under the benefit's own code.
            db.PayComponents.Add(new PayComponent
            {
                TenantId = seed.TenantId, Code = "AIR_TICKET", NameEn = "Ticket allowance", NameAr = "بدل تذاكر",
                ComponentType = PayComponentTypes.Earning, CalcMethod = PayComponentCalcMethods.Fixed, Value = 1_500m, DisplayOrder = 90,
            });
            await db.SaveChangesAsync();
        }
        var before = await PayrollSetAsync(seed);
        before.Should().Contain(("AIR_TICKET", PayComponentTypes.Earning, (decimal?)1_500m));

        await using (var db = fixture.CreateDb())
        {
            var skipTicket = (ObjectResult)await Controller(seed).SetOffering(Service(db), new SetOfferingRequest(seed.CompanyId, "AIR_TICKET", false, NextMonth), default);
            skipTicket.StatusCode.Should().Be(409);
            JsonSerializer.SerializeToElement(skipTicket.Value, Web).GetProperty("error").GetString().Should().Be(ReleaseABlockReasons.EntitlementSkipPaidCode);
            // A benefit with no paid twin is switched off, as before.
            Ok(await Controller(seed).SetOffering(Service(db), new SetOfferingRequest(seed.CompanyId, "EDUCATION", false, NextMonth), default));
        }
        await using (var db = fixture.CreateDb())
        {
            var ticket = new MatrixCellInput { GradeId = seed.GradeId, ComponentCode = "AIR_TICKET", Eligible = true, ValueType = "Quantity", Quantity = 1, CoverageTier = "Economy" };
            Ok(await Controller(seed).Publish(Service(db), new PublishMatrixRequest(null, NextMonth, [ticket, Pct(seed.GradeId, "HOUSING", 0.25m)]), false, default));
            Ok(await Controller(seed).Publish(Service(db), new PublishMatrixRequest(seed.CompanyId, NextMonth,
                [new MatrixCellInput { GradeId = seed.GradeId, ComponentCode = "AIR_TICKET", Eligible = true, ValueType = "Quantity", Quantity = 2, CoverageTier = "Business" }]), false, default));
        }

        var after = await PayrollSetAsync(seed);
        after.Should().Equal(before, "skipping and publishing benefits never changes what payroll pays (code, type, value)");
        await using var verify = fixture.CreateDb();
        (await verify.PayComponents.IgnoreQueryFilters().Where(p => p.TenantId == seed.TenantId && p.CompanyId == seed.CompanyId).ToListAsync())
            .Should().ContainSingle().Which.Should().Match<PayComponent>(p => p.Code == "EDUCATION" && p.ComponentType == PayComponentTypes.Benefit && !p.IsOffered);
    }

    [Fact]
    public async Task ResolveInEffect_DropsARowThatIsNotOffered_EvenOnAPaidCode()
    {
        var seed = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            await PayComponentSeeder.EnsureEntitlementCatalogAsync(db, seed.TenantId, default);
            await db.SaveChangesAsync();
        }
        var before = await PayrollSetAsync(seed);
        await using (var db = fixture.CreateDb())
        {
            // Defensive: a not-offered company row on a paid, non-floor code (written by no service) must not shadow the group row.
            var other = await db.PayComponents.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(p => p.TenantId == seed.TenantId && p.CompanyId == null && p.Code == "OTHER_ALLOWANCES" && p.ComponentType == PayComponentTypes.Earning);
            db.PayComponents.Add(new PayComponent
            {
                TenantId = seed.TenantId, CompanyId = seed.CompanyId, Code = other.Code, NameEn = other.NameEn, NameAr = other.NameAr,
                ComponentType = other.ComponentType, CalcMethod = PayComponentCalcMethods.Fixed, Value = null, EntitlementClass = other.EntitlementClass,
                StatutoryFloor = other.StatutoryFloor, IsOffered = false, EffectiveFrom = NextMonth,
            });
            await db.SaveChangesAsync();
        }
        (await PayrollSetAsync(seed)).Should().Equal(before);
    }

    // ── P1: the two remaining legacy writers ─────────────────────────────────────────────────────

    [Fact]
    public async Task AiSetupApply_WritesNoGradePayLinesForAReleaseATenant_AndSaysWhy_ButStillDoesForOthers()
    {
        foreach (var releaseA in new[] { true, false })
        {
            var seed = await SeedAsync(releaseA);
            await using var db = fixture.CreateDb();
            var controller = new SetupAssistantController(db, new NoModel(), new AuditService(db))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(seed.TenantId, "HR Manager", "organization.setup.apply") } },
            };
            var draft = SetupDraft.Empty() with
            {
                Grades = [new DraftGrade("G3", "Supervisor", "Professional", 30, 0, 0, 0, "SAR")],
                GradePayComponents = [new DraftGradePayComponent("G3", "HOUSING", "Housing", "Earning", "PercentOfBasic", 0, 25, false, "Monthly")],
            };

            var result = await controller.Apply(new ApplySetupRequest(draft, "SA", "SAR", null), default);

            var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Web);
            var written = await db.GradePayScaleComponents.CountAsync(x => x.TenantId == seed.TenantId);
            if (releaseA)
            {
                written.Should().Be(0);
                body.GetProperty("applied").TryGetProperty("gradePayComponents", out _).Should().BeFalse();
                var skipped = body.GetProperty("skipped").GetProperty("gradePayComponents");
                (skipped.GetProperty("count").GetInt32(), skipped.GetProperty("reasonCode").GetString()).Should().Be((1, "moved_to_benefits_by_grade"));
            }
            else
            {
                written.Should().Be(1, "a tenant without Release A keeps the legacy writer");
                body.GetProperty("skipped").EnumerateObject().Should().BeEmpty();
            }
        }
    }

    [Fact]
    public async Task OrgStructureImport_SkipsGradePayLinesForAReleaseATenant_RowByRowWithTheReason()
    {
        foreach (var releaseA in new[] { true, false })
        {
            var seed = await SeedAsync(releaseA);
            await using var db = fixture.CreateDb();
            var controller = new OrganizationStructureImportController(db, new AuditService(db))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(seed.TenantId, "Admin") } },
            };
            var req = new OrganizationStructureImportRequest(null, null, null, null,
                GradesCsv: "Code,Name,MinSalary,MidSalary,MaxSalary,Currency\nG3,Supervisor,0,0,0,SAR\n",
                GradePayComponentsCsv: "GradeCode,ComponentCode,ComponentName,CalculationType,Percentage\nG3,HOUSING,Housing,PercentOfBasic,25\nG3,TRANSPORT,Transport,Fixed,0\n",
                DesignationsCsv: null);

            var commit = await controller.Commit(req, default);

            var result = (OrganizationStructureImportResult)Assert.IsType<OkObjectResult>(commit.Result).Value!;
            var written = await db.GradePayScaleComponents.CountAsync(x => x.TenantId == seed.TenantId);
            if (releaseA)
            {
                written.Should().Be(0);
                result.Applied.Should().NotContainKey("gradePayComponents");
                var rows = result.Rows.Where(r => r.EntityCode!.StartsWith("gradePayComponents:", StringComparison.Ordinal)).ToList();
                rows.Should().HaveCount(2).And.OnlyContain(r => r.Status == Zayra.Api.Application.Common.Import.ImportRowStatus.Skipped
                    && r.Warnings.Single().Contains("Benefits by grade"));
            }
            else written.Should().Be(2);
        }
    }

    // ── P1: salary prefill reads the matrix, one fact in one place ───────────────────────────────

    [Fact]
    public async Task SalaryPrefill_ForAReleaseATenant_FillsBlankAllowancesFromTheMatrix_NeverTheLegacyScale_AndRefusesToGuess()
    {
        var seed = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            // The frozen legacy scale says something else entirely: it must not be read.
            db.GradePayScaleComponents.AddRange(
                new GradePayScaleComponent { TenantId = seed.TenantId, GradeId = seed.GradeId, ComponentCode = "BASIC", ComponentName = "Basic", Amount = 7_000m },
                new GradePayScaleComponent { TenantId = seed.TenantId, GradeId = seed.GradeId, ComponentCode = "HOUSING", ComponentName = "Housing", Amount = 9_999m });
            await db.SaveChangesAsync();
            Ok(await Controller(seed).Publish(Service(db), new PublishMatrixRequest(null, NextMonth,
            [
                Pct(seed.GradeId, "HOUSING", 0.25m),
                new MatrixCellInput { GradeId = seed.GradeId, ComponentCode = "TRANSPORT", Eligible = true, ValueType = "Amount", Amount = 800m },
                new MatrixCellInput { GradeId = seed.GradeId, ComponentCode = "OTHER_ALLOWANCES", Eligible = false },
            ]), false, default));
        }

        // Basic entered, allowances left blank, effective when the matrix is in force: filled from the matrix.
        int employeeId;
        await using (var db = fixture.CreateDb())
        {
            var created = await Employees(db).CreateAsync(seed.TenantId, Request(seed, Salary(8_000m, NextMonth)), Ctx(seed.TenantId), default);
            employeeId = created.Id;
            var assignment = await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.EmployeeId == created.Id && x.IsActive);
            (assignment.BasicSalary, assignment.HousingAllowance, assignment.TransportAllowance, assignment.OtherAllowance)
                .Should().Be((8_000m, 2_000m, 800m, 0m));
            var lines = await db.SalaryComponents.AsNoTracking().Where(x => x.SalaryStructureId == assignment.SalaryStructureId).ToListAsync();
            lines.Select(l => (l.Code, l.CalculationType, l.Amount, l.Percentage)).Should().BeEquivalentTo(new[]
            {
                ("HOUSING", "PercentOfBasic", 0m, 25m),
                ("TRANSPORT", "Fixed", 800m, 0m),
            }, "the structure describes the matrix; the legacy 9,999 housing and 7,000 basic are not read");
            (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == created.Id)).Salary.Should().Be(10_800m);
        }

        // Update: a raise to 10,000 re-reads the matrix for the blank allowances.
        await using (var db = fixture.CreateDb())
        {
            await Employees(db).UpdateAsync(seed.TenantId, employeeId, Request(seed, Salary(10_000m, NextMonth.AddDays(14))), Ctx(seed.TenantId), default);
            var latest = await db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.EmployeeId == employeeId && x.IsActive)
                .OrderByDescending(x => x.EffectiveDate).FirstAsync();
            (latest.HousingAllowance, latest.TransportAllowance).Should().Be((2_500m, 800m));
        }

        // Before the matrix is in force the grade has no value: nothing is guessed, the save is refused with the reason,
        // and no employee is left behind.
        await using (var db = fixture.CreateDb())
        {
            var count = await db.Employees.CountAsync(e => e.TenantId == seed.TenantId);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Employees(db).CreateAsync(seed.TenantId, Request(seed, Salary(8_000m, Today)), Ctx(seed.TenantId), default));
            refused.Message.Should().Contain("Housing allowance").And.Contain("Transport allowance").And.Contain("no value in Benefits by grade");
            (await db.Employees.CountAsync(e => e.TenantId == seed.TenantId)).Should().Be(count);

            // Entered explicitly, the figures are used exactly as typed.
            var explicitSalary = new EmployeeSalaryBreakdownRequest(8_000m, 1_000m, 300m, null, null, 0m, null, null, Today, "SAR");
            var created = await Employees(db).CreateAsync(seed.TenantId, Request(seed, explicitSalary), Ctx(seed.TenantId), default);
            var assignment = await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id && x.IsActive);
            (assignment.HousingAllowance, assignment.TransportAllowance, assignment.OtherAllowance).Should().Be((1_000m, 300m, 0m));
        }
    }

    [Fact]
    public async Task SalaryPrefill_ForATenantWithoutReleaseA_IsUnchanged()
    {
        var seed = await SeedAsync(releaseA: false);
        await using var db = fixture.CreateDb();
        db.GradePayScaleComponents.Add(new GradePayScaleComponent { TenantId = seed.TenantId, GradeId = seed.GradeId, ComponentCode = "HOUSING", ComponentName = "Housing", Amount = 9_999m, SortOrder = 1 });
        db.GradeEntitlements.Add(new GradeEntitlement { TenantId = seed.TenantId, GradeId = seed.GradeId, PayComponentCode = "HOUSING", EntitlementClass = "QiwaWage",
            Eligible = true, ValueType = "PercentOfBasic", Rate = 0.25m, EffectiveFrom = Today, SourceRule = EntitlementMatrixService.SourceRuleMatrix });
        await db.SaveChangesAsync();

        // Figures supplied: used as they are, exactly as before (no matrix fill, no refusal).
        var created = await Employees(db).CreateAsync(seed.TenantId, Request(seed, Salary(8_000m, Today)), Ctx(seed.TenantId), default);
        var assignment = await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id && x.IsActive);
        (assignment.BasicSalary, assignment.HousingAllowance).Should().Be((8_000m, 0m));
        (await db.SalaryComponents.AsNoTracking().Where(x => x.SalaryStructureId == assignment.SalaryStructureId).Select(x => x.Amount).ToListAsync())
            .Should().Equal([9_999m], "the legacy scale still describes the structure for a tenant without the flag");
    }

    [Fact]
    public async Task GradeSalaryStructure_IsPerCompany_SoCompanyBsHireIsNeverAttachedToCompanyAsStructure()
    {
        var seed = await SeedAsync(releaseA: false);
        Guid companyB;
        await using (var db = fixture.CreateDb())
        {
            var b = new Company { TenantId = seed.TenantId, LegalNameEn = $"Masar Trading {Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
            db.Companies.Add(b);
            await db.SaveChangesAsync();
            companyB = b.Id;
        }
        var explicitSalary = new EmployeeSalaryBreakdownRequest(8_000m, 1_000m, 300m, null, null, 0m, null, null, Today, "SAR");
        await using (var db = fixture.CreateDb())
        {
            await Employees(db).CreateAsync(seed.TenantId, Request(seed, explicitSalary), Ctx(seed.TenantId), default);
            var hireB = await Employees(db).CreateAsync(seed.TenantId, Request(seed with { CompanyId = companyB }, explicitSalary), Ctx(seed.TenantId), default);
            var assignment = await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == hireB.Id && x.IsActive);
            var structure = await db.SalaryStructures.AsNoTracking().SingleAsync(x => x.Id == assignment.SalaryStructureId);
            (structure.Code, structure.CompanyId).Should().Be(("GRADE-G3", (Guid?)companyB));
        }
        await using (var check = fixture.CreateDb())
            (await check.SalaryStructures.CountAsync(x => x.TenantId == seed.TenantId && x.Code == "GRADE-G3")).Should().Be(2);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Seed(Guid TenantId, Guid CompanyId, Guid GradeId);

    private async Task<Seed> SeedAsync(bool releaseA = true)
    {
        await using var db = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tid, LegalNameEn = $"Masar Logistics {Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var grade = new Grade { TenantId = tid, Code = "G3", Name = "Supervisor", Level = 30, Currency = "SAR" };
        db.AddRange(company, grade);
        if (releaseA) db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        return new Seed(tid, company.Id, grade.Id);
    }

    /// <summary>What payroll pays the company for next month: (code, type, value) of every resolved component.</summary>
    private async Task<List<(string Code, string Type, decimal? Value)>> PayrollSetAsync(Seed seed)
    {
        await using var db = fixture.CreateDb();
        var rows = await db.PayComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == seed.TenantId && p.IsActive && !p.IsDeleted && (p.CompanyId == null || p.CompanyId == seed.CompanyId))
            .ToListAsync();
        return PayComponentEngine.ResolveInEffect(rows, seed.TenantId, NextMonth)
            .Select(p => (p.Code, p.ComponentType, p.Value)).OrderBy(x => x.Code).ThenBy(x => x.ComponentType).ToList();
    }

    private static EntitlementMatrixService Service(ZayraDbContext db) => new(db, new FixedClock(Today));

    private static EmployeeManagementService Employees(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "tests", Guid.NewGuid(), tenantId);

    private static EmployeeSalaryBreakdownRequest Salary(decimal basic, DateOnly from) =>
        new(basic, null, null, null, null, null, null, null, from, "SAR");

    private static EmployeeCreateRequest Request(Seed seed, EmployeeSalaryBreakdownRequest salary) =>
        new(
            EmployeeCode: $"PF-{Guid.NewGuid():N}"[..14], ManualEmployeeCode: true, EnglishName: "Prefill Person",
            ArabicName: null, PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: "Indian", MaritalStatus: null,
            PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
            CompanyId: seed.CompanyId, BranchId: null, DepartmentId: null, DesignationId: null, GradeId: seed.GradeId, CostCenterId: null,
            JobTitle: null, ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
            EmploymentType: "Full-Time", ContractType: "Unlimited", JoiningDate: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null,
            PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null,
            PayrollProfile: null, SalaryBreakdown: salary, ComplianceRecords: null);

    private static ClaimsPrincipal Principal(Guid tenantId, string role, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new("sub", Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, role),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static EntitlementMatrixController Controller(Seed seed) => new()
    {
        ControllerContext = new() { HttpContext = new DefaultHttpContext { User = Principal(seed.TenantId, "HR Director") } },
    };

    private static void Ok(IActionResult result) =>
        ((ObjectResult)result).StatusCode.Should().Be(200, JsonSerializer.Serialize(((ObjectResult)result).Value));

    private static MatrixCellInput Pct(Guid gradeId, string code, decimal rate) =>
        new() { GradeId = gradeId, ComponentCode = code, Eligible = true, ValueType = "PercentOfBasic", Rate = rate };

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private sealed class NoModel : ISetupAssistantService
    {
        public Task<SetupPreviewResult> GenerateAsync(SetupRequester requester, CompanyProfile profile, CancellationToken ct) =>
            Task.FromResult(new SetupPreviewResult(SetupDraft.Empty(), [], "test"));
    }
}
