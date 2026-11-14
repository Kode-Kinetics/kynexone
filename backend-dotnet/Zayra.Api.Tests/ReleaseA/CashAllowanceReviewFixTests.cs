using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The confirmed security-review findings on customer-defined monthly cash allowances (<c>ALW_*</c>): the Approvals card shows the lines
/// (H1), arrears for an allowance off the current record post to its own GL driver or refuse the run (M1), the Approvals drift check sees
/// allowances and old sealed baselines still verify (M2), amounts fit the payslip columns (L2), a null line is a 400 (L3), a corrupt
/// staged package does not 500 the employee page (L5) and Finance sees where a line posts (L1). The remove guard (L4) is in
/// <see cref="CashAllowancePayrollTests"/> beside the guard it extends. Nobody without an allowance line moves: those cases are pinned too.
/// </summary>
public partial class CashAllowanceReviewFixTests
{
    private const string Car = "ALW_CAR";
    private const string Phone = "ALW_PHONE";
    private const string CarDriver = "EARN:CAR";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ══ harness ═════════════════════════════════════════════════════════════════════════════════

    private sealed class World : IAsyncDisposable
    {
        public ZayraDbContext Db = null!;
        public SqliteConnection Conn = null!;
        public Guid Tid;
        public Guid CompanyId;
        public Employee A = null!, B = null!;
        public Guid StructureId;
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Conn.DisposeAsync(); }
    }

    private static async Task<World> NewWorld(bool definitions = true)
    {
        var w = new World();
        (w.Db, w.Conn) = PayrollProrationArrearsTests.CreateDb();
        w.Tid = Guid.NewGuid();
        (w.CompanyId, w.A, w.B) = await PayrollProrationArrearsTests.SeedAsync(w.Db, w.Tid);
        w.StructureId = (await w.Db.SalaryStructures.AsNoTracking().FirstAsync()).Id;
        if (definitions)
        {
            await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210");
            await AddDefinitionAsync(w.Db, w.Tid, Phone, "EARN:PHONE", "Phone allowance", "5211", eosb: false, eosbReason: "Not part of the contractual wage");
        }
        SalaryApprovalKit.EnsureApprover(w.Db, w.Tid);
        return w;
    }

    private static async Task AddDefinitionAsync(ZayraDbContext db, Guid tid, string code, string driver, string name, string account, bool eosb = true, string? eosbReason = null)
    {
        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = driver, Label = name + " expense", Category = GlDriverCategories.Earning, PostingSide = "DR",
            DefaultCode = account, DefaultName = $"{name} Expense", MatchMode = "Exact", MatchComponentCode = code, SortOrder = 60,
        });
        db.PayComponents.Add(new PayComponent
        {
            TenantId = tid, CompanyId = null, Code = code, NameEn = name, NameAr = name + " ar", ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, FinanceConfirmedAtUtc = DateTime.UtcNow, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
            GlDriverKey = driver, GosiSubject = false, WpsIncluded = true, IsTaxable = false, EosbIncluded = eosb, EosbExclusionReason = eosbReason, DisplayOrder = 9100,
        });
        await db.SaveChangesAsync();
    }

    private static async Task GiveAsync(World w, Employee e, string code, decimal amount, decimal? rate = null)
    {
        var salary = await w.Db.EmployeeSalaryStructures.OrderByDescending(x => x.EffectiveDate).FirstAsync(x => x.EmployeeId == e.Id);
        w.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance
        {
            TenantId = w.Tid, SalaryStructureId = salary.Id, Code = code, Amount = amount,
            Basis = rate is null ? AllowanceBases.Amount : AllowanceBases.PercentOfBasic, Rate = rate,
        });
        await w.Db.SaveChangesAsync();
    }

    private static PayrollController Hr(World w) => SalaryApprovalKit.FinanceController(w.Db, w.Tid, Guid.NewGuid(), "payroll.write");
    private static PayrollController Finance(World w) => SalaryApprovalKit.FinanceController(w.Db, w.Tid);

    private static SalaryAllowanceLine Amt(string code, decimal amount) => new(code, AllowanceBases.Amount, null, amount);

    private static EmployeeSalaryStructureRequest Package(World w, Employee e, decimal basic, string date, IReadOnlyList<SalaryAllowanceLine>? lines) =>
        new(e.Id, w.StructureId, basic, 2_000m, 1_000m, 0m, 0m, 0m, 0m, DateOnly.Parse(date), "SAR", lines);

    private static string Describe(IActionResult r) => r is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value)}" : r.GetType().Name;

    private static async Task<SalaryChangeRequest> ProposeAsync(World w, Employee e, decimal basic, string date, IReadOnlyList<SalaryAllowanceLine>? lines)
    {
        var result = await Hr(w).AssignEmployeeSalary(Package(w, e, basic, date, lines), CancellationToken.None);
        result.Should().BeOfType<AcceptedResult>(Describe(result));
        w.Db.ChangeTracker.Clear();
        return await w.Db.SalaryChangeRequests.AsNoTracking().OrderByDescending(x => x.RequestedAtUtc).FirstAsync(x => x.EmployeeId == e.Id);
    }

    private static async Task ApproveAsync(World w, Guid id)
    {
        var result = await Finance(w).ApproveSalaryChange(id, null, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(Describe(result));
        w.Db.ChangeTracker.Clear();
    }

    private static async Task<Employee> EmployeeAsync(World w, Employee e) =>
        await w.Db.Employees.AsNoTracking().SingleAsync(x => x.Id == e.Id);

    // ══ H1. The Approvals card shows the allowance lines ════════════════════════════════════════

    private static EmployeeChangeRequest ProfileEdit(World w, Employee e, string salaryBreakdownJson) => new()
    {
        TenantId = w.Tid, EmployeeId = e.Id, RequestedByUserId = Guid.NewGuid(), SensitiveFields = "salaryBreakdown",
        EffectiveDate = new DateOnly(2026, 3, 1),
        ProposedChangesJson = "{\"salaryBreakdown\":" + salaryBreakdownJson + "}",
    };

    private static string Breakdown(decimal basic, string? allowances) =>
        "{\"basicSalary\":" + basic + ",\"housingAllowance\":2000,\"transportAllowance\":1000,\"foodAllowance\":0,\"mobileAllowance\":0,\"otherAllowance\":0," +
        "\"fixedDeduction\":0,\"effectiveDate\":\"2026-03-01\",\"currency\":\"SAR\"" + (allowances is null ? string.Empty : ",\"allowances\":" + allowances) + "}";

    private static async Task<EmployeeChangeDetailDto> CardAsync(World w, Employee e, EmployeeChangeRequest change, params string[] permissions)
    {
        var detail = await EmployeeChangeDetail.BuildAsync(w.Db, new ApprovalRequest { TenantId = w.Tid, Title = "Change" }, change, permissions, CancellationToken.None);
        detail.Should().NotBeNull();
        return detail!;
    }

    [Fact]
    public async Task TheApprovalsCard_ShowsAnAddedAllowanceLine_AndTheGrossCountsTheLinesOnBothSides()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Phone, 200m);                                                           // on the record in force: the "before" side
        var change = ProfileEdit(w, w.A, Breakdown(10_000m, "[{\"code\":\"ALW_PHONE\",\"basis\":\"Amount\",\"amount\":200},{\"code\":\"ALW_CAR\",\"basis\":\"Amount\",\"amount\":500}]"));

        var field = (await CardAsync(w, w.A, change, "employees.sensitive")).Fields.Single(f => f.Key == "salaryBreakdown");

        field.Masked.Should().BeFalse();
        field.CurrentValue.Should().Contain("Phone allowance").And.Contain("200").And.Contain("gross 13,200", "the whole pay on file counts its allowance");
        field.CurrentValue.Should().NotContain("Car allowance");
        field.ProposedValue.Should().Contain("Car allowance").And.Contain("Car allowance ar", "the Arabic name is shown beside the English one")
            .And.Contain("ALW_CAR").And.Contain("500").And.Contain("Phone allowance").And.Contain("gross 13,700");
        field.CurrentValue.Should().NotBe(field.ProposedValue, "a change that only adds a line is not an invisible change");
    }

    [Fact]
    public async Task TheApprovalsCard_ShowsAPercentageLinesRateAndAmount()
    {
        await using var w = await NewWorld();
        var change = ProfileEdit(w, w.A, Breakdown(10_000m, "[{\"code\":\"ALW_CAR\",\"basis\":\"PercentOfBasic\",\"rate\":0.10}]"));

        var proposed = (await CardAsync(w, w.A, change, "payroll.read")).Fields.Single().ProposedValue;

        proposed.Should().Contain("Car allowance").And.Contain("10%").And.Contain("of basic").And.Contain("1,000").And.Contain("gross 14,000");
    }

    [Fact]
    public async Task ACardThatDoesNotMentionAllowances_SaysTheCurrentOnesAreCarriedForward_ReworkedOnTheNewBasic()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 1_000m, rate: 0.10m);                                                // 10% of the 10,000 basic
        var change = ProfileEdit(w, w.A, Breakdown(12_000m, allowances: null));                           // a merit-style package: no "allowances" key

        var field = (await CardAsync(w, w.A, change, "employees.sensitive")).Fields.Single();

        field.CurrentValue.Should().Contain("Car allowance").And.Contain("1,000").And.Contain("gross 14,000");
        field.ProposedValue.Should().Contain("carried forward").And.Contain("Car allowance").And.Contain("1,200", "10% of the new basic of 12,000")
            .And.Contain("gross 16,200");
    }

    [Fact]
    public async Task ACardForAnEmployeeWithoutAllowances_ShowsNoAllowanceText_AndTheSameGross()
    {
        await using var w = await NewWorld();
        var change = ProfileEdit(w, w.A, Breakdown(12_000m, allowances: null));

        var field = (await CardAsync(w, w.A, change, "employees.sensitive")).Fields.Single();

        field.CurrentValue.Should().Contain("gross 13,000").And.NotContainEquivalentOf("allowance").And.NotContain("carried");
        field.ProposedValue.Should().Contain("gross 15,000").And.NotContainEquivalentOf("allowance").And.NotContain("carried");
    }

    [Fact]
    public async Task AViewerWithoutPayAccess_SeesNoAllowanceLine_OnEitherSide()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Phone, 200m);
        var change = ProfileEdit(w, w.A, Breakdown(10_000m, "[{\"code\":\"ALW_CAR\",\"basis\":\"Amount\",\"amount\":500}]"));

        var detail = await CardAsync(w, w.A, change, "approvals.read");

        var field = detail.Fields.Single();
        field.Masked.Should().BeTrue();
        field.CurrentValue.Should().Be(EmployeeChangeDetail.MaskedPay);
        field.ProposedValue.Should().Be(EmployeeChangeDetail.MaskedPay);
        // Random ids can contain "500" or "200" (e.g. 200611d8-…), so they are removed before looking for the amounts.
        var withoutIds = System.Text.RegularExpressions.Regex.Replace(JsonSerializer.Serialize(detail),
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<id>");
        withoutIds.Should().NotContain("Car allowance").And.NotContain("Phone allowance").And.NotContain("500").And.NotContain("200");
    }

    // ══ M1. Arrears for an allowance that is no longer on the current record ═══════════════════

    /// <summary>Jan and Feb locked without the allowance; then a one-off Jan-Feb allowance is keyed backdated (record from 1 Jan) and a later record
    /// from 1 Mar carries none, so in March the allowance is back pay that is no longer on the record in force.</summary>
    private static async Task<(World W, PayrollRun March)> JanFebOneOffAsync(Action<World>? beforeMarch = null)
    {
        var w = await NewWorld();
        foreach (var month in new[] { 1, 2 })
        {
            var r = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, month);
            await w.Db.SaveChangesAsync();
            (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(r.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
            w.Db.ChangeTracker.Clear();
            (await w.Db.PayrollRuns.FirstAsync(x => x.Id == r.Id)).Status = "Locked";
            await w.Db.SaveChangesAsync();
        }
        var backdated = new EmployeeSalaryStructure
        {
            TenantId = w.Tid, EmployeeId = w.A.Id, SalaryStructureId = w.StructureId, BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true, CreatedAtUtc = new DateTime(2026, 3, 1),
        };
        var current = new EmployeeSalaryStructure
        {
            TenantId = w.Tid, EmployeeId = w.A.Id, SalaryStructureId = w.StructureId, BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2026, 3, 1), IsActive = true, CreatedAtUtc = new DateTime(2026, 3, 1, 0, 0, 1),
        };
        w.Db.EmployeeSalaryStructures.AddRange(backdated, current);
        w.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance { TenantId = w.Tid, SalaryStructureId = backdated.Id, Code = Car, Amount = 700m });
        var march = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 3);
        await w.Db.SaveChangesAsync();
        beforeMarch?.Invoke(w);
        await w.Db.SaveChangesAsync();
        return (w, march);
    }

    [Fact]
    public async Task ArrearsForAnAllowanceNoLongerOnTheCurrentRecord_PostOnTheAllowancesOwnGlDriver()
    {
        var (w, march) = await JanFebOneOffAsync();
        await using var _ = w;

        await BonusLifecycleGlTests.LockRun(w.Db, PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid), march);

        var lines = await w.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == march.Id && e.EmployeeId == w.A.Id && e.Source == "Arrears").ToListAsync();
        lines.Should().HaveCount(2).And.OnlyContain(l => l.ComponentCode == "ARREARS_ALW_CAR");
        lines.Should().OnlyContain(l => l.GlDriverKey == CarDriver, "on the allowance's own driver, never the EARN:OTHER fallback");
        lines.Sum(l => l.Amount).Should().Be(1_400m);
        var gl = await BonusLifecycleGlTests.AllGl(w.Db, w.Tid);
        BonusLifecycleGlTests.Debits(gl, "5210").Should().Be(1_400m);
    }

    [Fact]
    public async Task ArrearsForARemovedAndSwitchedOffDefinition_StillPostOnItsDriver()
    {
        var (w, march) = await JanFebOneOffAsync(world =>
        {
            var definition = world.Db.PayComponents.Single(c => c.Code == Car);
            definition.IsDeleted = true; definition.IsActive = false;
        });
        await using var _ = w;

        await BonusLifecycleGlTests.LockRun(w.Db, PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid), march);

        var lines = await w.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == march.Id && e.EmployeeId == w.A.Id && e.Source == "Arrears").ToListAsync();
        lines.Should().HaveCount(2).And.OnlyContain(l => l.GlDriverKey == CarDriver, "a removed definition still has the driver its back pay posts to");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("no-driver")]
    public async Task ArrearsForAnAllowanceWithNoDefinitionOrNoGlDriver_RefuseTheRun_NamingTheEmployeeAndCode(string fault)
    {
        var (w, march) = await JanFebOneOffAsync(world =>
        {
            var definition = world.Db.PayComponents.Single(c => c.Code == Car);
            if (fault == "missing") world.Db.PayComponents.Remove(definition); else definition.GlDriverKey = null;
        });
        await using var _ = w;

        var result = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(march.Id, CancellationToken.None);

        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(refusal.Value).Should().Contain("cash_allowance_definition_invalid").And.Contain(w.A.EmployeeCode).And.Contain("ARREARS_ALW_CAR");
        (await w.Db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.PayrollRunId == march.Id)).Should().BeFalse("nothing is paid or saved");
        (await w.Db.PayrollArrearsLines.AsNoTracking().AnyAsync(l => l.PayrollRunId == march.Id)).Should().BeFalse();
    }

    // ══ M2. The Approvals drift check sees allowances, and old sealed baselines still verify ════

    private static readonly IDataProtector Protector = EmployeeChangeBaseline.CreateProtector(new EphemeralDataProtectionProvider());

    private static async Task<IReadOnlyDictionary<string, string>> BaselineAsync(World w, Employee e) =>
        (await EmployeeChangeBaseline.CaptureAsync(w.Db, await EmployeeAsync(w, e), null, [EmployeeSalaryBreakdownChanges.Key], CancellationToken.None))!;

    /// <summary>A baseline as it was sealed before allowances were part of it: the same dictionary without the keys this change added.</summary>
    private static IReadOnlyDictionary<string, string> AsSealedBeforeThisChange(IReadOnlyDictionary<string, string> current) =>
        current.Where(p => p.Key.StartsWith("employee:", StringComparison.Ordinal) || p.Key.StartsWith("salary:", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public async Task AnAllowanceFinanceApprovesAfterTheReviewWasRaised_IsDrift_AndTheApprovalIsRefused()
    {
        await using var w = await NewWorld();
        var employee = await EmployeeAsync(w, w.A);
        var changeId = Guid.NewGuid();
        // The change was returned for review: what the reviewer was shown is sealed.
        w.Db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = w.Tid, EmployeeId = w.A.Id, EventType = EmployeeChangeBaseline.ReturnedForReviewEventType, EffectiveDate = new DateOnly(2026, 3, 1),
            SnapshotJson = EmployeeChangeBaseline.SnapshotJson(changeId, EmployeeChangeBaseline.Protect(Protector, changeId, await BaselineAsync(w, w.A))),
        });
        await w.Db.SaveChangesAsync();
        (await EmployeeChangeBaseline.CheckUnchangedSinceReviewAsync(w.Db, Protector, w.Tid, changeId, employee, [EmployeeSalaryBreakdownChanges.Key], default))
            .Unchanged.Should().BeTrue("nothing has moved yet");

        // In between, Finance approves an allowance on this employee's salary: same date, same figures, same slots, one more line.
        var proposal = await ProposeAsync(w, w.A, 10_000m, "2025-01-01", [Amt(Car, 500m)]);
        await ApproveAsync(w, proposal.Id);

        var check = await EmployeeChangeBaseline.CheckUnchangedSinceReviewAsync(w.Db, Protector, w.Tid, changeId, await EmployeeAsync(w, w.A), [EmployeeSalaryBreakdownChanges.Key], default);
        check.Verified.Should().BeTrue();
        check.ChangedFields.Should().Equal(EmployeeSalaryBreakdownChanges.Key);
        check.Unchanged.Should().BeFalse();
        check.Refusal(k => k).Should().Contain("changed after this re-review was raised");
    }

    [Fact]
    public async Task ABaselineSealedBeforeAllowancesWereInIt_StillVerifies_ItIsNotDriftJustBecauseTheFormatChanged()
    {
        await using var w = await NewWorld();
        var oldFormat = AsSealedBeforeThisChange(await BaselineAsync(w, w.A));

        // Nothing moved: the old-format baseline must not be reported as drifted.
        EmployeeChangeBaseline.Drifted(oldFormat, await BaselineAsync(w, w.A)).Should().BeEmpty();

        // The salary's own figures still count against an old baseline, exactly as before.
        var salary = await w.Db.EmployeeSalaryStructures.SingleAsync(x => x.EmployeeId == w.A.Id);
        salary.BasicSalary = 10_500m;
        await w.Db.SaveChangesAsync();
        EmployeeChangeBaseline.Drifted(oldFormat, await BaselineAsync(w, w.A)).Should().Equal(EmployeeSalaryBreakdownChanges.Key);
    }

    [Fact]
    public async Task ABaselineSealedBeforeAllowancesWereInIt_CannotKnowOfALine_SoItIsNotReportedAsDrift()
    {
        // Pinned so the trade-off is deliberate: an old baseline holds no allowance state at all, so it can neither confirm nor deny one.
        await using var w = await NewWorld();
        var oldFormat = AsSealedBeforeThisChange(await BaselineAsync(w, w.A));
        await GiveAsync(w, w.A, Car, 500m);

        EmployeeChangeBaseline.Drifted(oldFormat, await BaselineAsync(w, w.A)).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewBaseline_SeesALineAddedChangedOrRemoved_AndNotAReorderOrARescale()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        var sealedNow = await BaselineAsync(w, w.A);
        EmployeeChangeBaseline.Drifted(sealedNow, await BaselineAsync(w, w.A)).Should().BeEmpty();

        var line = await w.Db.EmployeeSalaryAllowances.SingleAsync();
        line.Amount = 500.00m;                                                                         // the same figure at another scale
        await w.Db.SaveChangesAsync();
        EmployeeChangeBaseline.Drifted(sealedNow, await BaselineAsync(w, w.A)).Should().BeEmpty();

        line.Amount = 600m;
        await w.Db.SaveChangesAsync();
        EmployeeChangeBaseline.Drifted(sealedNow, await BaselineAsync(w, w.A)).Should().Equal(EmployeeSalaryBreakdownChanges.Key);

        w.Db.EmployeeSalaryAllowances.Remove(line);
        await w.Db.SaveChangesAsync();
        EmployeeChangeBaseline.Drifted(sealedNow, await BaselineAsync(w, w.A)).Should().Equal(EmployeeSalaryBreakdownChanges.Key);
    }

    [Fact]
    public void TheScheduleFormat_ForARecordWithoutAllowances_IsByteIdenticalToBefore()
    {
        var schedule = new[]
        {
            new EmployeeSalaryBreakdownRequest(10_000m, 2_000m, 1_000m, 0m, 0m, 0m, 0m, "STR", new DateOnly(2026, 1, 1), "SAR"),
        };

        EmployeeSalaryBreakdownChanges.CanonicalSchedule(schedule)
            .Should().Be("[[\"10000\",\"2000\",\"1000\",\"0\",\"0\",\"0\",\"0\",\"STR\",\"2026-01-01\",\"SAR\"]]");
    }

    // ══ L2. Amounts fit the payslip columns (numeric(12,2)) ═════════════════════════════════════

    private const decimal Limit = 9_999_999_999.99m;

    private static string Error(IActionResult r) =>
        JsonSerializer.SerializeToElement(((ObjectResult)r).Value, Web).GetProperty("error").GetString()!;

    [Fact]
    public async Task ALineAboveTheColumnLimit_IsRefusedAtStaging_WithAPlainMessage()
    {
        await using var w = await NewWorld();

        var result = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 12_000m, "2026-03-01", [Amt(Car, Limit + 0.01m)]), CancellationToken.None);

        var refused = result.Should().BeAssignableTo<ObjectResult>(Describe(result)).Subject;
        refused.StatusCode.Should().Be(400);
        Error(result).Should().Be("salary_allowance_invalid");
        JsonSerializer.Serialize(refused.Value).Should().Contain("9,999,999,999.99");
        (await w.Db.SalaryChangeRequests.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TheSlotsPlusTheLines_AboveTheColumnLimit_AreRefusedAtStaging()
    {
        await using var w = await NewWorld();

        // Each figure is within its limit: 9,999,996,500 basic + 3,000 of slots, then a line that takes the whole pay over.
        var result = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 9_999_996_500m, "2026-03-01", [Amt(Car, 1_000m)]), CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>(Describe(result)).Which.StatusCode.Should().Be(400);
        Error(result).Should().Be("salary_allowance_invalid");
        (await w.Db.SalaryChangeRequests.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ThePackageJustAtTheLimit_IsStillAccepted()
    {
        await using var w = await NewWorld();

        // 9,999,996,500 + 3,000 of slots + 499.99 line = 9,999,999,999.99 exactly.
        var result = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 9_999_996_500m, "2026-03-01", [Amt(Car, 499.99m)]), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>(Describe(result));
    }

    [Fact]
    public async Task TheSlotsPlusTheLines_AboveTheColumnLimit_AreRefusedAtApproval_NothingIsWritten()
    {
        await using var w = await NewWorld();
        // A change that reached the queue by another door (or before the limit existed): the approver is refused, not the database.
        var change = new SalaryChangeRequest
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, EmployeeId = w.A.Id, EmployeeCode = w.A.EmployeeCode, EmployeeName = w.A.FullName,
            SalaryStructureId = w.StructureId, BasicSalary = 9_999_996_500m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2026, 3, 1), Currency = "SAR", Status = SalaryChangeRequest.Pending, RequestedByUserId = Guid.NewGuid(), RequestedByName = "HR",
            AllowancesJson = SalaryAllowanceLines.Serialize([new ResolvedAllowanceLine(Car, AllowanceBases.Amount, null, 1_000m)]),
        };
        w.Db.SalaryChangeRequests.Add(change);
        await w.Db.SaveChangesAsync();

        var result = await Finance(w).ApproveSalaryChange(change.Id, null, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>(Describe(result));
        (await w.Db.EmployeeSalaryAllowances.AsNoTracking().AnyAsync()).Should().BeFalse();
        (await w.Db.EmployeeSalaryStructures.AsNoTracking().CountAsync(x => x.EmployeeId == w.A.Id)).Should().Be(1);
    }

    [Fact]
    public void TheApprovalsPackageGrossCheck_CountsTheLines()
    {
        var package = JsonDocument.Parse(Breakdown(9_999_996_500m, "[{\"code\":\"ALW_CAR\",\"basis\":\"Amount\",\"amount\":1000}]")).RootElement;

        var refused = Assert.Throws<InvalidOperationException>(() => EmployeeSalaryBreakdownChanges.Read(package));

        refused.Message.Should().Contain("exceeds");
        // The same package without the line is within the limit, as before.
        EmployeeSalaryBreakdownChanges.Read(JsonDocument.Parse(Breakdown(9_999_996_500m, allowances: null)).RootElement).BasicSalary.Should().Be(9_999_996_500m);
    }

    // ══ L3. A null line is a 400, not a 500 ═════════════════════════════════════════════════════

    [Fact]
    public async Task ANullAllowanceLine_IsRefusedWith400_NotAnException()
    {
        await using var w = await NewWorld();

        var result = await Hr(w).AssignEmployeeSalary(Package(w, w.A, 12_000m, "2026-03-01", [null!]), CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>(Describe(result)).Which.StatusCode.Should().Be(400);
        Error(result).Should().Be("salary_allowance_invalid");
        (await w.Db.SalaryChangeRequests.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ANullLineInAnApprovalsPackage_IsAValidationRefusal_NotAnException()
    {
        await using var w = await NewWorld();
        var employee = await EmployeeAsync(w, w.A);
        var changes = new Dictionary<string, JsonElement> { ["salaryBreakdown"] = JsonDocument.Parse(Breakdown(12_000m, "[null]")).RootElement.Clone() };

        await Assert.ThrowsAsync<InvalidOperationException>(() => EmployeeSalaryBreakdownChanges.ValidateAsync(w.Db, employee, changes, CancellationToken.None));
    }

    // ══ L5. A corrupt staged package does not break the employee page ═══════════════════════════

    [Fact]
    public async Task ACorruptStagedAllowanceList_DoesNotMake_GetEmployee_Fail()
    {
        await using var w = await NewWorld();
        // A new hire with no salary yet whose pending first package carries unreadable lines.
        var hire = new Employee
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, EmployeeCode = "C900", FullName = "New Hire", Status = "Draft", JoiningDate = new DateTime(2026, 3, 1), Nationality = "Indian",
        };
        w.Db.Employees.Add(hire);
        await w.Db.SaveChangesAsync();
        w.Db.SalaryChangeRequests.Add(new SalaryChangeRequest
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, Source = SalaryChangeSources.NewHire, EmployeeId = hire.Id, EmployeeCode = hire.EmployeeCode, EmployeeName = hire.FullName,
            SalaryStructureId = w.StructureId, BasicSalary = 9_000m, EffectiveDate = new DateOnly(2026, 3, 1), Currency = "SAR", Status = SalaryChangeRequest.Pending,
            RequestedByUserId = Guid.NewGuid(), RequestedByName = "HR", AllowancesJson = "{this is not json",
        });
        await w.Db.SaveChangesAsync();
        var service = new EmployeeManagementService(w.Db, new AuditService(w.Db), new NullDocumentStorage(), TestNotifications.For(w.Db));

        var detail = await service.GetAsync(w.Tid, hire.Id, includeSensitive: true, new RequestContext(null, "test", Guid.NewGuid(), w.Tid), CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.SalaryBreakdown.Should().NotBeNull();
        detail.SalaryBreakdown!.BasicSalary.Should().Be(9_000m);
        detail.SalaryBreakdown.Allowances.Should().BeNull("unreadable lines are never shown as lines, and never as 'none' that would hide them");
        detail.SalaryApprovalState.Should().Be("Pending");
    }

    // ══ L1 (display). Finance sees where each line posts and whether it raises end of service ═══

    [Fact]
    public async Task TheApprovalScreen_ShowsEachLinesGlDriver_AndWhetherItCountsInEndOfService()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Phone, 200m);                                                           // phone: excluded from EOSB, with a reason
        await ProposeAsync(w, w.A, 12_000m, "2026-03-01", [Amt(Car, 500m), Amt(Phone, 200m)]);

        var listed = (OkObjectResult)await Hr(w).ListSalaryChanges("Pending", null, CancellationToken.None);
        var item = JsonSerializer.SerializeToElement(listed.Value, Web).EnumerateArray().Single();

        var proposed = item.GetProperty("proposedAllowances").EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!);
        proposed[Car].GetProperty("glDriverKey").GetString().Should().Be(CarDriver);
        proposed[Car].GetProperty("glDriverLabel").GetString().Should().Be("Car allowance expense");
        proposed[Car].GetProperty("eosbIncluded").GetBoolean().Should().BeTrue();
        proposed[Car].GetProperty("eosbExclusionReason").ValueKind.Should().Be(JsonValueKind.Null);
        proposed[Phone].GetProperty("glDriverKey").GetString().Should().Be("EARN:PHONE");
        proposed[Phone].GetProperty("eosbIncluded").GetBoolean().Should().BeFalse();
        proposed[Phone].GetProperty("eosbExclusionReason").GetString().Should().Be("Not part of the contractual wage");

        var current = item.GetProperty("currentAllowances").EnumerateArray().Single();
        current.GetProperty("glDriverKey").GetString().Should().Be("EARN:PHONE");
        current.GetProperty("eosbIncluded").GetBoolean().Should().BeFalse();
        // The fields that were already there are untouched.
        current.GetProperty("nameEn").GetString().Should().Be("Phone allowance");
        current.GetProperty("amount").GetDecimal().Should().Be(200m);
    }

    [Fact]
    public async Task ALineWhoseDefinitionIsGone_ShowsNoDriverAndNoEosbAnswer_RatherThanAGuess()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, "ALW_GHOST", 50m);

        w.Db.SalaryChangeRequests.Add(new SalaryChangeRequest
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, EmployeeId = w.A.Id, EmployeeCode = w.A.EmployeeCode, EmployeeName = w.A.FullName, SalaryStructureId = w.StructureId,
            BasicSalary = 11_000m, EffectiveDate = new DateOnly(2026, 3, 1), Currency = "SAR", Status = SalaryChangeRequest.Pending, RequestedByUserId = Guid.NewGuid(), RequestedByName = "HR",
        });
        await w.Db.SaveChangesAsync();
        var listed = (OkObjectResult)await Hr(w).ListSalaryChanges("Pending", null, CancellationToken.None);
        var ghost = JsonSerializer.SerializeToElement(listed.Value, Web).EnumerateArray().Single().GetProperty("currentAllowances").EnumerateArray().Single();

        ghost.GetProperty("code").GetString().Should().Be("ALW_GHOST");
        ghost.GetProperty("glDriverKey").ValueKind.Should().Be(JsonValueKind.Null);
        ghost.GetProperty("eosbIncluded").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheGlDriversLabel_FallsBackToTheBuiltInCatalogue_AndIsNullWhenNeitherKnowsTheKey()
    {
        await using var w = await NewWorld();
        foreach (var (code, key) in new[] { ("ALW_MISC", "EARN:OTHER"), ("ALW_ODD", "EARN:NOWHERE") })
            w.Db.PayComponents.Add(new PayComponent
            {
                TenantId = w.Tid, CompanyId = null, Code = code, NameEn = code, NameAr = code, ComponentType = PayComponentTypes.Earning, CalcMethod = PayComponentCalcMethods.GradePackage, FinanceConfirmedAtUtc = DateTime.UtcNow,
                EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None, GlDriverKey = key, EosbIncluded = true, DisplayOrder = 9100,
            });
        await w.Db.SaveChangesAsync();
        await GiveAsync(w, w.A, "ALW_MISC", 10m);
        await GiveAsync(w, w.A, "ALW_ODD", 20m);
        w.Db.SalaryChangeRequests.Add(new SalaryChangeRequest
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, EmployeeId = w.A.Id, EmployeeCode = w.A.EmployeeCode, EmployeeName = w.A.FullName, SalaryStructureId = w.StructureId,
            BasicSalary = 11_000m, EffectiveDate = new DateOnly(2026, 3, 1), Currency = "SAR", Status = SalaryChangeRequest.Pending, RequestedByUserId = Guid.NewGuid(), RequestedByName = "HR",
        });
        await w.Db.SaveChangesAsync();

        var listed = (OkObjectResult)await Hr(w).ListSalaryChanges("Pending", null, CancellationToken.None);
        var lines = JsonSerializer.SerializeToElement(listed.Value, Web).EnumerateArray().Single().GetProperty("currentAllowances").EnumerateArray()
            .ToDictionary(l => l.GetProperty("code").GetString()!);

        lines["ALW_MISC"].GetProperty("glDriverLabel").GetString().Should().Be("Earning — Other");
        lines["ALW_ODD"].GetProperty("glDriverKey").GetString().Should().Be("EARN:NOWHERE");
        lines["ALW_ODD"].GetProperty("glDriverLabel").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
