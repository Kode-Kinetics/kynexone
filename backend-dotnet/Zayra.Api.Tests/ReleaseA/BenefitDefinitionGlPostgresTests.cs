using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// What a customer-defined monthly cash allowance posts to the general ledger, proven end to end on real PostgreSQL 16 through the real doors:
/// the allowance is created through the entitlements controller (flag <c>benefit_definition_v1</c> on), given a grade value, put on the employee's
/// salary through the HR-proposes / Finance-approves salary flow, and the payroll run is Processed and Locked by the payroll controller. Every
/// assertion reads the journal (<c>finance_gl_entries</c>) and the earnings the run wrote, never a service return value.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class BenefitDefinitionGlPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Code = "ALW_CAR";
    private const string CarDriver = "EARN:CAR";
    private const string OtherCarDriver = "EARN:CAR2";
    private const decimal GradeValue = 600m;

    // ── 1. Posts to the chosen driver's account, not the fallback ───────────────────────────────

    [Fact]
    public async Task AConfirmedAllowance_PostsItsAmountToTheAccountOfTheChosenDriver_NotTheFallback_AndTheJournalBalances()
    {
        var w = await SeedAsync();
        await CreateAllowanceAsync(w, finance: true);
        await PublishGradeValueAsync(w);
        await GiveEmployeeTheGradeValueAsync(w);

        var run = await ProcessAndLockAsync(w, 11);

        run.Earnings.Single(e => e.ComponentCode == Code).Should().Match<PayrollEarning>(e => e.Amount == GradeValue && e.GlDriverKey == CarDriver);
        var lines = run.Gl.Where(g => g.Description == $"Payroll earning: {Code}").ToList();
        lines.Should().ContainSingle();
        lines[0].DebitAccount.Should().StartWith("5210", "the customer chose EARN:CAR, which maps to 5210");
        lines[0].Amount.Should().Be(GradeValue);
        run.Gl.Where(g => g.DebitAccount.StartsWith("5099")).Sum(g => g.Amount).Should().Be(0m, "nothing fell through to the EARN:OTHER catch-all (5099)");
        run.CatchAll.Should().BeEmpty("no component of this run was routed to a catch-all driver");
        Balanced(run.Gl);
        // Every earned pound is expensed once: the "Payroll earning" journal lines add up to the earnings the run wrote (employer statutory cost is a separate line).
        run.Gl.Where(g => g.Description.StartsWith("Payroll earning:")).Sum(g => g.Amount).Should().Be(run.Earnings.Sum(e => e.Amount));
    }

    /// <summary>
    /// The control that gives test 1 its teeth: the same allowance, same amount, but its confirmed definition names the catch-all driver. The journal then
    /// shows 5099 and no 5210, so the assertion above really does tell the two destinations apart.
    /// </summary>
    [Fact]
    public async Task Control_AnAllowanceOnTheCatchAllDriver_ShowsUpIn5099_NotIn5210()
    {
        var w = await SeedAsync();
        await CreateAllowanceAsync(w, finance: true);
        await PublishGradeValueAsync(w);
        await using (var db = fixture.CreateDb())
            await db.PayComponents.Where(c => c.TenantId == w.TenantId && c.Code == Code).ExecuteUpdateAsync(s => s.SetProperty(c => c.GlDriverKey, "EARN:OTHER"));
        await GiveEmployeeTheGradeValueAsync(w);

        var run = await ProcessAndLockAsync(w, 11);

        run.Gl.Where(g => g.Description == $"Payroll earning: {Code}").Should().ContainSingle().Which.DebitAccount.Should().StartWith("5099");
        run.Gl.Where(g => g.DebitAccount.StartsWith("5210")).Should().BeEmpty();
        Balanced(run.Gl);
    }

    // ── 2. Outside its window, or switched off ──────────────────────────────────────────────────

    [Fact]
    public async Task AnAllowance_OutsideItsEffectiveWindow_IsRefusedByThePayrollRun_AndPostsNothing()
    {
        var w = await SeedAsync();
        await CreateAllowanceAsync(w, finance: true, effectiveTo: new DateOnly(2026, 11, 30));
        await PublishGradeValueAsync(w);
        await GiveEmployeeTheGradeValueAsync(w);

        var inWindow = await ProcessAndLockAsync(w, 11);                         // control: November, inside the window, is paid and posted
        inWindow.Gl.Single(g => g.Description == $"Payroll earning: {Code}").Amount.Should().Be(GradeValue);

        // Today's product behaviour: the employee still has the line on their salary but the definition is not in effect in December, so the run is
        // REFUSED (422 cash_allowance_definition_invalid) rather than silently skipping the allowance. Nothing is saved and nothing reaches the journal.
        var december = await NewRunAsync(w, 12);
        var refused = await ProcessAsync(w, december);
        Refusal(refused).Should().Be(("cash_allowance_definition_invalid", 422));
        await using var db = fixture.CreateDb();
        (await db.PayrollEarnings.AsNoTracking().CountAsync(e => e.PayrollRunId == december)).Should().Be(0);
        (await db.FinanceGlEntries.AsNoTracking().CountAsync(g => g.SourceEntityId == december)).Should().Be(0);
        (await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == december)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task AnAllowance_SwitchedOff_IsRefusedByThePayrollRun_AndPostsNothing_WhileTheMonthBeforeStillPosted()
    {
        var w = await SeedAsync();
        var created = await CreateAllowanceAsync(w, finance: true);
        await PublishGradeValueAsync(w);
        await GiveEmployeeTheGradeValueAsync(w);
        var november = await ProcessAsync(w, await NewRunAsync(w, 11));          // control: processed while on (not locked, so December may still change)
        november.Should().BeOfType<OkObjectResult>();

        var off = await PutAsync(w, finance: true, Edit(created, "Car allowance", b => b.IsActive = false));
        Outcome(off).Status.Should().Be(200, JsonSerializer.Serialize(((ObjectResult)off).Value, Web));

        var december = await NewRunAsync(w, 12);
        var refused = await ProcessAsync(w, december);
        Refusal(refused).Should().Be(("cash_allowance_definition_invalid", 422));
        await using var db = fixture.CreateDb();
        (await db.PayrollEarnings.AsNoTracking().CountAsync(e => e.PayrollRunId == december)).Should().Be(0);
        (await db.FinanceGlEntries.AsNoTracking().CountAsync(g => g.SourceEntityId == december)).Should().Be(0);
    }

    // ── 3. A contract benefit never reaches payroll or the journal ──────────────────────────────

    [Fact]
    public async Task AContractBenefit_WithAValue_ProducesNoPayrollLineAndNoJournalLine()
    {
        var w = await SeedAsync();
        var created = await AddAsync(w, finance: false, Request("Parking", b => { b.Code = "PARK"; b.Delivery = "Coverage"; b.Category = "Facilities"; b.PolicyClass = "Discretionary"; b.EffectiveFrom = Nov1; }));
        Outcome(created).Status.Should().Be(200, JsonSerializer.Serialize(((ObjectResult)created).Value, Web));
        await PublishAsync(w, "BEN_PARK", 1_200m);
        await using (var db = fixture.CreateDb())
            (await db.GradeEntitlements.AsNoTracking().SingleAsync(g => g.TenantId == w.TenantId && g.PayComponentCode == "BEN_PARK")).Amount.Should().Be(1_200m);

        var run = await ProcessAndLockAsync(w, 11);

        run.Earnings.Select(e => e.ComponentCode).Should().NotContain(c => c.Contains("PARK"));
        run.Deductions.Select(d => d.ComponentCode).Should().NotContain(c => c.Contains("PARK"));
        run.Gl.Should().NotContain(g => g.Description.Contains("PARK") || g.Description.Contains("1200"));
        run.Gl.Sum(g => g.Amount).Should().NotBe(0m, "the run did post: only the benefit is absent");
        run.SlipGross.Should().Be(run.Earnings.Sum(e => e.Amount), "the slip carries no benefit money");
        Balanced(run.Gl);
    }

    // ── 4. A confirmed allowance's driver is changed by Finance's correction only ───────────────

    [Fact]
    public async Task ChangingTheDriverOfAConfirmedAllowance_ByANonFinanceEditor_IsRefused_AndTheNextJournalIsUnchanged()
    {
        var w = await SeedAsync();
        var created = await CreateAllowanceAsync(w, finance: true);
        await PublishGradeValueAsync(w);
        await GiveEmployeeTheGradeValueAsync(w);
        var first = await ProcessAndLockAsync(w, 11);

        var edit = await PutAsync(w, finance: false, Edit(created, "Car allowance", b => b.GlDriverKey = OtherCarDriver));

        Outcome(edit).Should().Be((409, "finance_correction_required"));
        await using (var db = fixture.CreateDb())
        {
            var row = await db.PayComponents.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.TenantId == w.TenantId && c.Code == Code);
            row.GlDriverKey.Should().Be(CarDriver);
            row.FinanceConfirmedAtUtc.Should().NotBeNull("a refused edit does not even re-pend the allowance");
        }
        var second = await ProcessAndLockAsync(w, 12);

        Posting(second).Should().Equal(Posting(first), "the December journal posts the allowance exactly where November's did");
        Posting(second).Should().ContainSingle().Which.Should().Be(("5210", GradeValue));
        Balanced(second.Gl);
    }

    // ══ harness ═════════════════════════════════════════════════════════════════════════════════

    private sealed record World(Guid TenantId, Guid CompanyId, Guid GradeId, int EmployeeId, Guid StructureId);

    private sealed class RunResult
    {
        public List<PayrollEarning> Earnings { get; init; } = [];
        public List<PayrollDeduction> Deductions { get; init; } = [];
        public List<FinanceGlEntry> Gl { get; init; } = [];
        public List<string> CatchAll { get; init; } = [];
        public decimal SlipGross { get; init; }
    }

    private async Task<World> SeedAsync()
    {
        await using var db = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"Masar {Guid.NewGuid():N}", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"MS-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var grade = new Grade { TenantId = tenantId, Code = "G3", Name = "Supervisor", Level = 30 };
        var structure = new SalaryStructure { TenantId = tenantId, CompanyId = company.Id, Code = "STR", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true };
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "MS-1", FullName = "Mohammed Abdelrahman", Nationality = "Saudi",
            ContractType = "Indefinite", Status = "Active", JoiningDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), GradeId = grade.Id,
        };
        db.AddRange(company, grade, structure, emp);
        db.TenantFeatureFlags.AddRange(
            new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true },
            new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = true });
        // The drivers a customer may choose: two of their own earning drivers, each with its own account (the catch-all EARN:OTHER -> 5099 is seeded).
        db.GlDrivers.AddRange(
            CustomDriver(tenantId, CarDriver, "5210", "CAR"),
            CustomDriver(tenantId, OtherCarDriver, "5211", "CAR2"));
        // Lock refuses a driver whose account is not in the chart (gl_account_not_in_chart), so Finance adds the two accounts to the chart.
        db.GlAccounts.AddRange(
            new GlAccount { TenantId = tenantId, CompanyId = null, Code = "5210", Name = "Car Allowance Expense 5210", AccountType = "Expense" },
            new GlAccount { TenantId = tenantId, CompanyId = null, Code = "5211", Name = "Car Allowance Expense 5211", AccountType = "Expense" });
        await db.SaveChangesAsync();
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
        });
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id, BasicSalary = 8_000m, HousingAllowance = 2_000m, TransportAllowance = 800m,
            Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        SalaryApprovalKit.EnsureApprover(db, tenantId);
        await db.SaveChangesAsync();
        return new World(tenantId, company.Id, grade.Id, emp.Id, structure.Id);
    }

    private static GlDriver CustomDriver(Guid tid, string key, string account, string match) => new()
    {
        TenantId = tid, CompanyId = null, Key = key, Label = key, Category = GlDriverCategories.Earning, PostingSide = "DR",
        DefaultCode = account, DefaultName = $"Car Allowance Expense {account}", MatchMode = "Exact", MatchComponentCode = match, SortOrder = 70,
    };

    // ── the entitlements controller ──────────────────────────────────────────────────────────────

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private static readonly Guid HrUser = Guid.NewGuid();
    private static readonly Guid FinanceUser = Guid.NewGuid();

    private static EntitlementMatrixController As(World w, bool finance)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, (finance ? FinanceUser : HrUser).ToString()), new(ClaimTypes.Role, "HR Director"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        if (finance) claims.Add(new Claim("permission", "payroll.approve"));
        return new EntitlementMatrixController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private static CreateBenefitRequest Request(string name, Action<CreateBenefitRequestBuilder> more)
    {
        var b = new CreateBenefitRequestBuilder(name);
        more(b);
        return b.Build();
    }

    private async Task<IActionResult> AddAsync(World w, bool finance, CreateBenefitRequest req)
    {
        await using var db = fixture.CreateDb();
        return await As(w, finance).AddBenefit(new EntitlementMatrixService(db, new FixedClock(Today)), req, default);
    }

    private async Task<IActionResult> PutAsync(World w, bool finance, UpdateBenefitRequest req)
    {
        await using var db = fixture.CreateDb();
        return await As(w, finance).UpdateBenefit(new EntitlementMatrixService(db, new FixedClock(Today)), Code, req, default);
    }

    /// <summary>The allowance as the real create door writes it: ALW_CAR, salary allowance, monthly, on the chosen earning driver; confirmed when a Finance user creates it.</summary>
    private async Task<IActionResult> CreateAllowanceAsync(World w, bool finance, DateOnly? effectiveTo = null)
    {
        var created = await AddAsync(w, finance, Request("Car allowance", b =>
        {
            b.Code = "CAR"; b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Vehicle"; b.PolicyClass = "Contractual";
            b.EffectiveFrom = Nov1; b.EffectiveTo = effectiveTo;
        }));
        Outcome(created).Status.Should().Be(200, JsonSerializer.Serialize(((ObjectResult)created).Value, Web));
        await using var db = fixture.CreateDb();
        (await db.PayComponents.AsNoTracking().SingleAsync(c => c.TenantId == w.TenantId && c.Code == Code && c.CompanyId == null))
            .Should().Match<PayComponent>(c => c.GlDriverKey == CarDriver && c.FinanceConfirmedAtUtc != null && c.CalcMethod == PayComponentCalcMethods.GradePackage);
        return created;
    }

    private static UpdateBenefitRequest Edit(IActionResult created, string name, Action<EditBuilder> change)
    {
        var e = new EditBuilder();
        change(e);
        var value = ((ObjectResult)created).Value!;
        var component = value as MatrixComponentDto ?? (MatrixComponentDto)value.GetType().GetProperty("component")!.GetValue(value)!;
        return new UpdateBenefitRequest(component.Definition!.Version, name, null)
        {
            Category = "Vehicle", PolicyClass = "Contractual", EffectiveFrom = Nov1, IsActive = e.IsActive, GlDriverKey = e.GlDriverKey,
        };
    }

    private sealed class EditBuilder { public bool? IsActive; public string? GlDriverKey; }

    private async Task PublishAsync(World w, string code, decimal amount)
    {
        await using var db = fixture.CreateDb();
        var res = await As(w, false).Publish(new EntitlementMatrixService(db, new FixedClock(Today)),
            new PublishMatrixRequest(null, Nov1, [new MatrixCellInput { GradeId = w.GradeId, ComponentCode = code, Eligible = true, ValueType = "Amount", Amount = amount }]), false, default);
        Outcome(res).Status.Should().Be(200, JsonSerializer.Serialize(((ObjectResult)res).Value, Web));
    }

    private Task PublishGradeValueAsync(World w) => PublishAsync(w, Code, GradeValue);

    // ── the salary flow: HR proposes with the grade's value, a different Finance login approves ───

    private async Task GiveEmployeeTheGradeValueAsync(World w)
    {
        decimal gradeAmount;
        await using (var read = fixture.CreateDb())
            gradeAmount = (await read.GradeEntitlements.AsNoTracking().SingleAsync(g => g.TenantId == w.TenantId && g.GradeId == w.GradeId && g.PayComponentCode == Code)).Amount!.Value;
        gradeAmount.Should().Be(GradeValue);

        Guid changeId;
        await using (var db = fixture.CreateDb())
        {
            var hr = SalaryApprovalKit.FinanceController(db, w.TenantId, Guid.NewGuid(), "payroll.write");
            var result = await hr.AssignEmployeeSalary(new EmployeeSalaryStructureRequest(w.EmployeeId, w.StructureId, 8_000m, 2_000m, 800m, 0m, 0m, 0m, 0m, Nov1, "SAR",
                new[] { new SalaryAllowanceLine(Code, AllowanceBases.Amount, null, gradeAmount) }, null), CancellationToken.None);
            result.Should().BeOfType<AcceptedResult>(Describe(result));
            changeId = (await db.SalaryChangeRequests.AsNoTracking().Where(x => x.TenantId == w.TenantId && x.EmployeeId == w.EmployeeId)
                .OrderByDescending(x => x.RequestedAtUtc).FirstAsync()).Id;
        }
        await using (var db = fixture.CreateDb())
        {
            var approved = await SalaryApprovalKit.FinanceController(db, w.TenantId).ApproveSalaryChange(changeId, null, CancellationToken.None);
            approved.Should().BeOfType<OkObjectResult>(Describe(approved));
        }
    }

    // ── the payroll run ──────────────────────────────────────────────────────────────────────────

    private async Task<Guid> NewRunAsync(World w, int month)
    {
        await using var db = fixture.CreateDb();
        var run = new PayrollRun
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = month, Status = "Draft",
            CreatedAtUtc = new DateTime(2026, month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<IActionResult> ProcessAsync(World w, Guid runId)
    {
        await using var db = fixture.CreateDb();
        return await PayComponentNetPayDefectTests.Build(db, w.TenantId).Process(runId, CancellationToken.None);
    }

    private async Task<RunResult> ProcessAndLockAsync(World w, int month)
    {
        var runId = await NewRunAsync(w, month);
        var processed = await ProcessAsync(w, runId);
        processed.Should().BeOfType<OkObjectResult>(Describe(processed));
        await using (var db = fixture.CreateDb())
        {
            (await db.PayrollValidationResults.AsNoTracking().Where(v => v.PayrollRunId == runId && v.Severity == "Error").Select(v => v.Code).ToListAsync())
                .Should().BeEmpty("nothing in this scenario should raise a blocking validation error");
            await db.PayrollRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Approved"));
        }
        IActionResult locked;
        await using (var db = fixture.CreateDb())
            locked = await PayComponentNetPayDefectTests.Build(db, w.TenantId, "payroll.lock").Lock(runId, CancellationToken.None);
        locked.Should().BeOfType<OkObjectResult>(Describe(locked));

        await using var read = fixture.CreateDb();
        var catchAll = JsonSerializer.SerializeToElement(((OkObjectResult)locked).Value, Web).TryGetProperty("catchAllComponents", out var c)
            ? c.EnumerateArray().Select(x => x.GetProperty("componentCode").GetString()!).ToList() : [];
        return new RunResult
        {
            Earnings = await read.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == runId).ToListAsync(),
            Deductions = await read.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == runId).ToListAsync(),
            Gl = await read.FinanceGlEntries.AsNoTracking().Where(g => g.TenantId == w.TenantId && g.SourceEntityId == runId && g.EventType == GlEventTypes.Accrual).ToListAsync(),
            CatchAll = catchAll,
            SlipGross = (await read.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId)).GrossSalary,
        };
    }

    // ── reading outcomes ─────────────────────────────────────────────────────────────────────────

    private static void Balanced(IEnumerable<FinanceGlEntry> gl)
    {
        var lines = gl.ToList();
        lines.Should().NotBeEmpty();
        lines.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount)
            .Should().Be(lines.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount), "debits equal credits");
    }

    /// <summary>The allowance's journal lines as (debit account code, amount).</summary>
    private static List<(string Account, decimal Amount)> Posting(RunResult run) =>
        run.Gl.Where(g => g.Description == $"Payroll earning: {Code}").Select(g => (g.DebitAccount.Split(' ')[0], g.Amount)).ToList();

    private static (int? Status, string? Error) Outcome(IActionResult r)
    {
        var o = (ObjectResult)r;
        var json = JsonSerializer.SerializeToElement(o.Value, Web);
        return (o.StatusCode, json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() : null);
    }

    private static (string? Error, int? Status) Refusal(IActionResult r)
    {
        var o = r.Should().BeAssignableTo<ObjectResult>(Describe(r)).Subject;
        return (JsonSerializer.SerializeToElement(o.Value, Web).GetProperty("error").GetString(), o.StatusCode);
    }

    private static string Describe(IActionResult r) => r is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value, Web)}" : r.GetType().Name;
}
