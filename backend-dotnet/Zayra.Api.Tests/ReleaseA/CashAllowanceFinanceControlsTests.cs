using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Wave D of the customer-defined monthly cash allowances (<c>ALW_*</c>): the controls Finance and the employee have over them.
///
/// <para>1. Finance confirms a new allowance before it is used (maker is not checker). 2. Finance corrects a confirmed allowance's GL driver or
/// end-of-service setting from a month on, as a new effective-dated version every reader picks by its own date. 3. Lowering an employee's pay needs a
/// consent reference. 4. The salary change says it changes the contract wage. 5. The end-of-service exclusion is a category plus a note, with cautions.
/// 6. The pre-run readiness lists allowance lines the run could not pay.</para>
/// </summary>
public class CashAllowanceFinanceControlsTests
{
    private const string Car = "ALW_CAR";
    private const string CarDriver = "EARN:CAR";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ══ harness: the matrix (definitions) ═══════════════════════════════════════════════════════

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private sealed class Mx : IAsyncDisposable
    {
        public static readonly DateOnly Today = new(2026, 10, 6);
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Grade[] G { get; } = new Grade[3];
        public EntitlementMatrixService Service { get; private set; } = null!;
        public readonly Guid Hr = Guid.NewGuid();
        public readonly Guid FinanceA = Guid.NewGuid();
        public readonly Guid FinanceB = Guid.NewGuid();

        public static async Task<Mx> Create()
        {
            var h = new Mx();
            h.Db.AddRange(new Company { TenantId = h.Tid, LegalNameEn = "Masar Facility Services Co.", DefaultCurrency = "SAR" },
                new Company { TenantId = h.Tid, LegalNameEn = "Masar Logistics Co.", DefaultCurrency = "SAR" });
            for (var i = 1; i <= 2; i++) { h.G[i] = new Grade { TenantId = h.Tid, Code = $"G{i}", Name = $"Grade {i}", Level = i * 10 }; h.Db.Add(h.G[i]); }
            h.Db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = h.Tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
            h.Db.GlDrivers.AddRange(Driver(h.Tid, "EARN:CAR", "5210"), Driver(h.Tid, "EARN:CAR2", "5211"), Driver(h.Tid, "EARN:NOWHERE", ""));
            await h.Db.SaveChangesAsync();
            h.Service = new EntitlementMatrixService(h.Db, new FixedClock(Today));
            return h;
        }

        private static GlDriver Driver(Guid tid, string key, string account) => new()
        {
            TenantId = tid, CompanyId = null, Key = key, Label = key, Category = GlDriverCategories.Earning, PostingSide = "DR",
            DefaultCode = account, DefaultName = account.Length == 0 ? "" : $"Account {account}", MatchMode = "Exact", MatchComponentCode = key[(key.IndexOf(':') + 1)..], SortOrder = 70,
        };

        public EntitlementMatrixController As(Guid user, bool finance)
        {
            var claims = new List<Claim> { new("tenant_id", Tid.ToString()), new(ClaimTypes.NameIdentifier, user.ToString()), new(ClaimTypes.Role, "HR Director") };
            if (finance) claims.Add(new Claim("permission", "payroll.approve"));
            return new EntitlementMatrixController
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
            };
        }

        public Task<IActionResult> Add(Guid user, bool finance, string name, string? driver = CarDriver, bool eosb = true, string? category = null, string? reason = null) =>
            As(user, finance).AddBenefit(Service, new CreateBenefitRequest(name, null, BenefitKinds.Cash, driver, eosb, reason, category), default);

        public Task<IActionResult> Confirm(Guid user, bool finance, string code, ConfirmAllowanceRequest? req = null) =>
            As(user, finance).ConfirmBenefit(Service, code, req, default);

        public Task<IActionResult> Correct(Guid user, string code, CorrectAllowanceRequest req) =>
            As(user, true).CorrectBenefit(Service, code, req, default);

        public Task<IActionResult> PublishGroup(DateOnly from, List<MatrixCellInput> cells) =>
            As(Hr, false).Publish(Service, new PublishMatrixRequest(null, from, cells), false, default);

        public static MatrixCellInput Amount(Grade g, string code, decimal amount) =>
            new() { GradeId = g.Id, ComponentCode = code, Eligible = true, ValueType = "Amount", Amount = amount };

        public async Task<PayComponent[]> Rows(string code) =>
            (await Db.PayComponents.AsNoTracking().Where(x => x.Code == code).ToListAsync()).OrderBy(x => x.EffectiveFrom ?? DateOnly.MinValue).ToArray();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static int? Status(IActionResult r) => (r as ObjectResult)?.StatusCode;
    private static string Describe(IActionResult r) => r is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value)}" : r.GetType().Name;
    private static JsonElement Json(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value, Web);
    private static string? Error(IActionResult r) => Json(r).GetProperty("error").GetString();

    private static MatrixComponentDto Component(IActionResult r)
    {
        Status(r).Should().Be(200, Describe(r));
        var value = ((ObjectResult)r).Value!;
        return value as MatrixComponentDto ?? (MatrixComponentDto)value.GetType().GetProperty("component")!.GetValue(value)!;
    }

    // ══ 1. Finance confirms a new cash allowance ════════════════════════════════════════════════

    [Fact]
    public async Task AnAllowanceAddedWithoutFinanceRights_IsPendingFinance_ButAFinanceMakerConfirmsItByAddingIt()
    {
        await using var h = await Mx.Create();

        var pending = Component(await h.Add(h.Hr, finance: false, "Car"));
        var confirmed = Component(await h.Add(h.FinanceA, finance: true, "Phone"));

        pending.CashAllowance!.FinancePending.Should().BeTrue();
        confirmed.CashAllowance!.FinancePending.Should().BeFalse();
        var car = (await h.Rows(Car)).Single();
        (car.FinanceConfirmedAtUtc, car.FinanceConfirmedByUserId, car.CreatedBy).Should().Be(((DateTime?)null, (Guid?)null, (Guid?)h.Hr));
        var phone = (await h.Rows("ALW_PHONE")).Single();
        phone.FinanceConfirmedAtUtc.Should().NotBeNull();
        phone.FinanceConfirmedByUserId.Should().Be(h.FinanceA);
    }

    [Fact]
    public async Task APendingAllowance_ShowsInTheGridAsPending_ButTakesNoGradeValues_UntilFinanceConfirmsIt()
    {
        await using var h = await Mx.Create();
        await h.Add(h.Hr, finance: false, "Car");
        var from = Mx.Today.AddDays(1);

        var refused = await h.PublishGroup(from, [Mx.Amount(h.G[1], Car, 500m)]);

        Status(refused).Should().Be(400);
        Json(refused).GetProperty("errors").EnumerateArray().Single().GetProperty("code").GetString().Should().Be("allowance_pending_finance");
        (await h.Db.GradeEntitlements.AnyAsync(x => x.PayComponentCode == Car)).Should().BeFalse("nothing is written for a pending allowance");
        var matrix = (EntitlementMatrixDto)((OkObjectResult)await h.As(h.Hr, false).Matrix(h.Service, null, Mx.Today, default)).Value!;
        matrix.Components.Single(c => c.Code == Car).CashAllowance!.FinancePending.Should().BeTrue("it shows in the grid, marked");

        Status(await h.Confirm(h.FinanceA, true, Car)).Should().Be(200);
        Status(await h.PublishGroup(from, [Mx.Amount(h.G[1], Car, 500m)])).Should().Be(200);
    }

    [Fact]
    public async Task AnotherFinanceApprover_Confirms_AndTheAuditSaysWhoAddedItAndWhoConfirmedIt()
    {
        await using var h = await Mx.Create();
        await h.Add(h.Hr, finance: false, "Car");

        var confirmed = Component(await h.Confirm(h.FinanceA, true, Car));

        confirmed.CashAllowance!.FinancePending.Should().BeFalse();
        var row = (await h.Rows(Car)).Single();
        (row.FinanceConfirmedByUserId, row.GlDriverKey).Should().Be(((Guid?)h.FinanceA, (string?)CarDriver));
        row.FinanceConfirmedAtUtc.Should().NotBeNull();
        var audit = await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitConfirmed);
        audit.UserId.Should().Be(h.FinanceA);
        audit.Metadata.Should().Contain(h.Hr.ToString()).And.Contain(h.FinanceA.ToString()).And.Contain(CarDriver);
    }

    [Fact]
    public async Task FinanceMayChangeTheGlDriverAndTheEosbSettingWhileConfirming_AndItIsAudited()
    {
        await using var h = await Mx.Create();
        await h.Add(h.Hr, finance: false, "Car");

        Status(await h.Confirm(h.FinanceA, true, Car, new ConfirmAllowanceRequest("EARN:CAR2", false, EosbExclusionCategories.Reimbursement, null))).Should().Be(200);

        var row = (await h.Rows(Car)).Single();
        (row.GlDriverKey, row.EosbIncluded, row.EosbExclusionCategory).Should().Be(("EARN:CAR2", false, (string?)"Reimbursement"));
        var audit = (await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitConfirmed)).Metadata!;
        audit.Should().Contain("EARN:CAR").And.Contain("EARN:CAR2").And.Contain("Reimbursement");
    }

    [Fact]
    public async Task TheMakerCanNeverConfirmTheirOwnAllowance_EvenAfterGainingFinanceRights()
    {
        await using var h = await Mx.Create();
        await h.Add(h.Hr, finance: false, "Car");

        var refused = await h.Confirm(h.Hr, finance: true, Car);

        Status(refused).Should().Be(403);
        Error(refused).Should().Be("maker_cannot_confirm");
        (await h.Rows(Car)).Single().FinanceConfirmedAtUtc.Should().BeNull();
        (await h.Db.AuditLogs.AnyAsync(a => a.Action == EntitlementMatrixService.AuditBenefitConfirmed)).Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmingNeedsTheFinancePermission_AWrongOrAlreadyConfirmedAllowance_IsRefusedInWords()
    {
        await using var h = await Mx.Create();
        await h.Add(h.Hr, finance: false, "Car");
        await h.As(h.Hr, false).AddBenefit(h.Service, new CreateBenefitRequest("Parking", null), default);

        Error(await h.Confirm(h.FinanceA, finance: false, Car)).Should().Be("finance_required");
        Error(await h.Confirm(h.FinanceA, true, "BEN_PARKING")).Should().Be("not_a_cash_allowance");
        Status(await h.Confirm(h.FinanceA, true, "ALW_NOPE")).Should().Be(404);
        Error(await h.Confirm(h.FinanceA, true, Car, new ConfirmAllowanceRequest("EARN:NOWHERE"))).Should().Be("gl_driver_invalid");
        Error(await h.Confirm(h.FinanceA, true, Car, new ConfirmAllowanceRequest(null, false, null, null))).Should().Be("eosb_reason_required");
        (await h.Rows(Car)).Single().FinanceConfirmedAtUtc.Should().BeNull("a refused confirmation changes nothing");

        Status(await h.Confirm(h.FinanceA, true, Car)).Should().Be(200);
        var twice = await h.Confirm(h.FinanceB, true, Car);
        Status(twice).Should().Be(409);
        Error(twice).Should().Be("already_confirmed");
    }

    [Fact]
    public async Task ARemovedAllowanceThatComesBack_IsAFreshProposal_ConfirmedAgain()
    {
        await using var h = await Mx.Create();
        await h.Add(h.FinanceA, finance: true, "Car");
        Status(await h.As(h.Hr, false).RemoveBenefit(h.Service, Car, default)).Should().Be(200);

        var again = Component(await h.Add(h.Hr, finance: false, "Car"));

        again.CashAllowance!.FinancePending.Should().BeTrue("the person who brought it back is its maker and Finance has not confirmed this proposal");
        (await h.Rows(Car)).Single().CreatedBy.Should().Be(h.Hr);
    }

    // ══ 2. Finance corrects a confirmed allowance from a month on ═══════════════════════════════

    [Fact]
    public async Task ACorrection_ClosesTheOldVersionTheDayBefore_AndOpensANewOne_WithAnAuditOfOldAndNewAndTheReason()
    {
        await using var h = await Mx.Create();
        await h.Add(h.FinanceA, finance: true, "Car");

        var result = await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), "Posts to the vehicle cost centre", "EARN:CAR2"));

        Component(result).CashAllowance!.GlDriverKey.Should().Be("EARN:CAR2");
        var versions = await h.Rows(Car);
        versions.Should().HaveCount(2);
        (versions[0].EffectiveFrom, versions[0].EffectiveTo, versions[0].GlDriverKey).Should().Be(((DateOnly?)null, (DateOnly?)new DateOnly(2026, 11, 30), (string?)CarDriver));
        (versions[1].EffectiveFrom, versions[1].EffectiveTo, versions[1].GlDriverKey).Should().Be(((DateOnly?)new DateOnly(2026, 12, 1), (DateOnly?)null, (string?)"EARN:CAR2"));
        versions[1].FinanceConfirmedAtUtc.Should().NotBeNull("the correction keeps Finance's confirmation");
        var audit = (await h.Db.AuditLogs.SingleAsync(a => a.Action == EntitlementMatrixService.AuditBenefitCorrected)).Metadata!;
        audit.Should().Contain(CarDriver).And.Contain("EARN:CAR2").And.Contain("Posts to the vehicle cost centre").And.Contain("2026-12-01");
        // The grid still has one row for the allowance (the version in effect today speaks for it).
        var matrix = (EntitlementMatrixDto)((OkObjectResult)await h.As(h.Hr, false).Matrix(h.Service, null, Mx.Today, default)).Value!;
        matrix.Components.Count(c => c.Code == Car).Should().Be(1);
        matrix.Components.Single(c => c.Code == Car).CashAllowance!.GlDriverKey.Should().Be(CarDriver);
    }

    [Fact]
    public async Task ACorrection_ChangesTheEosbSetting_AndRefusesAMonthThatIsLockedForAnyCompany()
    {
        await using var h = await Mx.Create();
        await h.Add(h.FinanceA, finance: true, "Car");
        h.Db.PayrollRuns.Add(new PayrollRun { TenantId = h.Tid, CompanyId = null, Year = 2026, Month = 11, Status = "Locked", RunType = PayrollRunTypes.Regular });
        await h.Db.SaveChangesAsync();

        var refused = await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 11, 1), "Late fix", null, false, EosbExclusionCategories.NonRecurring, null));

        Status(refused).Should().Be(409);
        Error(refused).Should().Be("period_committed");
        Json(refused).GetProperty("earliestEffectiveFrom").GetString().Should().Be("2026-12-01");
        (await h.Rows(Car)).Should().ContainSingle("a refused correction writes nothing");

        Status(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), "Not wage", null, false, EosbExclusionCategories.NonRecurring, null))).Should().Be(200);
        var versions = await h.Rows(Car);
        (versions[1].EosbIncluded, versions[1].EosbExclusionCategory).Should().Be((false, (string?)"NonRecurring"));
        versions[0].EosbIncluded.Should().BeTrue("the earlier months keep the answer they had");
    }

    [Fact]
    public async Task ACorrection_IsValidatedAndRefusedInWords()
    {
        await using var h = await Mx.Create();
        await h.Add(h.Hr, finance: false, "Car");
        var dec = new DateOnly(2026, 12, 1);

        Error(await h.Correct(h.FinanceA, Car, new CorrectAllowanceRequest(dec, "x", "EARN:CAR2"))).Should().Be("not_confirmed");
        await h.Confirm(h.FinanceA, true, Car);
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(null, "x", "EARN:CAR2"))).Should().Be("effective_from_required");
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 15), "x", "EARN:CAR2"))).Should().Be("effective_from_not_month_start");
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(dec, "  ", "EARN:CAR2"))).Should().Be("reason_required");
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(dec, "x"))).Should().Be("nothing_to_change");
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(dec, "x", CarDriver))).Should().Be("nothing_to_change");
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(dec, "x", "EARN:NOWHERE"))).Should().Be("gl_driver_invalid");
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(dec, "x", null, false))).Should().Be("eosb_reason_required");
        Error(await h.Correct(h.FinanceB, "BEN_X", new CorrectAllowanceRequest(dec, "x", "EARN:CAR2"))).Should().Be("not_a_cash_allowance");
        Status(await h.Correct(h.FinanceB, "ALW_NOPE", new CorrectAllowanceRequest(dec, "x", "EARN:CAR2"))).Should().Be(404);
        (await h.As(h.FinanceB, finance: false).CorrectBenefit(h.Service, Car, new CorrectAllowanceRequest(dec, "x", "EARN:CAR2"), default)).Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(403);
        (await h.Rows(Car)).Should().ContainSingle();

        Status(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2027, 2, 1), "later", "EARN:CAR2"))).Should().Be(200);
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2027, 1, 1), "earlier than the scheduled one", CarDriver))).Should().Be("later_version_exists");
    }

    [Fact]
    public async Task CorrectingTheSameMonthAgain_AmendsThatVersionInPlace_AndOlderMonthsAreUntouched()
    {
        await using var h = await Mx.Create();
        await h.Add(h.FinanceA, finance: true, "Car");
        await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), "first", "EARN:CAR2"));

        Status(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), "second", CarDriver, false, EosbExclusionCategories.Reimbursement, null))).Should().Be(200);

        var versions = await h.Rows(Car);
        versions.Should().HaveCount(2);
        (versions[0].GlDriverKey, versions[0].EosbIncluded).Should().Be((CarDriver, true));
        (versions[1].GlDriverKey, versions[1].EosbIncluded).Should().Be((CarDriver, false));
    }

    // ══ 5. The end-of-service exclusion: a category and a note, and the cautions ═════════════════

    [Fact]
    public async Task AnExcludedAllowance_NeedsACategory_AndOtherNeedsANote()
    {
        await using var h = await Mx.Create();

        Error(await h.Add(h.FinanceA, true, "Car", eosb: false, category: "Whim", reason: "x")).Should().Be("eosb_category_invalid");
        Error(await h.Add(h.FinanceA, true, "Car", eosb: false, category: "Other", reason: "  ")).Should().Be("eosb_reason_required");
        Error(await h.Add(h.FinanceA, true, "Car", eosb: false, category: null, reason: null)).Should().Be("eosb_reason_required");
        (await h.Db.PayComponents.AnyAsync(x => x.Code == Car)).Should().BeFalse();

        Status(await h.Add(h.FinanceA, true, "Car", eosb: false, category: "reimbursement", reason: null)).Should().Be(200);
        var row = (await h.Rows(Car)).Single();
        (row.EosbIncluded, row.EosbExclusionCategory, row.EosbExclusionReason).Should().Be((false, (string?)"Reimbursement", (string?)null));
    }

    [Fact]
    public async Task AReasonOnlyCaller_IsStillRead_AsCategoryOther_AndAnIncludedAllowanceKeepsNeitherCategoryNorNote()
    {
        await using var h = await Mx.Create();

        Status(await h.Add(h.FinanceA, true, "Car", eosb: false, category: null, reason: " Not wage ")).Should().Be(200);
        Status(await h.Add(h.FinanceA, true, "Phone", eosb: true, category: "Reimbursement", reason: "ignored")).Should().Be(200);

        var car = (await h.Rows(Car)).Single();
        (car.EosbExclusionCategory, car.EosbExclusionReason).Should().Be(((string?)"Other", (string?)"Not wage"));
        var phone = (await h.Rows("ALW_PHONE")).Single();
        (phone.EosbExclusionCategory, phone.EosbExclusionReason).Should().Be(((string?)null, (string?)null));
    }

    [Theory]
    [InlineData("Fuel allowance", true, null, "eosb_included_reimbursive")]
    [InlineData("Travel", true, null, "eosb_included_reimbursive")]
    [InlineData("بدل سفر", true, null, "eosb_included_reimbursive")]
    [InlineData("Mobile phone", true, null, "eosb_included_reimbursive")]
    [InlineData("Car", false, "Other", "eosb_excluded_fixed_monthly")]
    [InlineData("Car", false, "Discretionary", "eosb_excluded_fixed_monthly")]
    [InlineData("Sales commission", true, null, "commission_name")]
    [InlineData("حافز", true, null, "commission_name")]
    public async Task ACautionIsReturnedButNeverBlocks(string name, bool included, string? category, string code)
    {
        await using var h = await Mx.Create();

        var added = Component(await h.Add(h.FinanceA, true, name, eosb: included, category: category, reason: included ? null : "documented basis"));

        added.Warnings.Should().Contain(w => w.Code == code);
        added.Warnings!.Single(w => w.Code == code).Message.Should().NotBeNullOrWhiteSpace();
        (await h.Db.PayComponents.AnyAsync(x => x.Code.StartsWith("ALW_"))).Should().BeTrue("a caution never blocks");
    }

    [Fact]
    public async Task NoCautionForAnOrdinaryAllowance_OrAReimbursementThatIsExcluded()
    {
        await using var h = await Mx.Create();

        Component(await h.Add(h.FinanceA, true, "Car allowance")).Warnings.Should().BeEmpty();
        Component(await h.Add(h.FinanceA, true, "Fuel", eosb: false, category: "Reimbursement")).Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task TheCommissionCaution_IsSaidAtCreationOnly()
    {
        await using var h = await Mx.Create();
        await h.Add(h.FinanceA, true, "Sales commission");

        var correction = Component(await h.Correct(h.FinanceB, "ALW_SALES_COMMISSION", new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), "driver", "EARN:CAR2")));

        correction.Warnings.Should().NotContain(w => w.Code == "commission_name");
    }

    // ══ harness: salary, approval, payroll ══════════════════════════════════════════════════════

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

    private static async Task<World> NewWorld(bool definition = true)
    {
        var w = new World();
        (w.Db, w.Conn) = PayrollProrationArrearsTests.CreateDb();
        w.Tid = Guid.NewGuid();
        (w.CompanyId, w.A, w.B) = await PayrollProrationArrearsTests.SeedAsync(w.Db, w.Tid);
        w.StructureId = (await w.Db.SalaryStructures.AsNoTracking().FirstAsync()).Id;
        if (definition) await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210");
        SalaryApprovalKit.EnsureApprover(w.Db, w.Tid);
        return w;
    }

    private static async Task<PayComponent> AddDefinitionAsync(ZayraDbContext db, Guid tid, string code, string driver, string name, string account,
        bool confirmed = true, bool eosb = true, bool driverRow = true, DateOnly? from = null, DateOnly? to = null)
    {
        if (driverRow)
            db.GlDrivers.Add(new GlDriver
            {
                TenantId = tid, CompanyId = null, Key = driver, Label = name, Category = GlDriverCategories.Earning, PostingSide = "DR",
                DefaultCode = account, DefaultName = $"{name} Expense", MatchMode = "Exact", MatchComponentCode = code, SortOrder = 60,
            });
        var row = new PayComponent
        {
            TenantId = tid, CompanyId = null, Code = code, NameEn = name, NameAr = name + " ar", ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
            GlDriverKey = driver, GosiSubject = false, WpsIncluded = true, IsTaxable = false, EosbIncluded = eosb, DisplayOrder = 9100,
            EffectiveFrom = from, EffectiveTo = to,
            FinanceConfirmedAtUtc = confirmed ? DateTime.UtcNow : null,
        };
        db.PayComponents.Add(row);
        await db.SaveChangesAsync();
        return row;
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

    private static EmployeeSalaryStructureRequest Package(World w, Employee e, decimal basic, string date, IReadOnlyList<SalaryAllowanceLine>? lines,
        string? consent = null, decimal housing = 2_000m, decimal transport = 1_000m, decimal deduction = 0m) =>
        new(e.Id, w.StructureId, basic, housing, transport, 0m, 0m, 0m, deduction, DateOnly.Parse(date), "SAR", lines, consent);

    private static async Task<SalaryChangeRequest> ProposeAsync(World w, EmployeeSalaryStructureRequest req)
    {
        var result = await Hr(w).AssignEmployeeSalary(req, CancellationToken.None);
        result.Should().BeOfType<AcceptedResult>(Describe(result));
        w.Db.ChangeTracker.Clear();
        return await w.Db.SalaryChangeRequests.AsNoTracking().OrderByDescending(x => x.RequestedAtUtc).FirstAsync(x => x.EmployeeId == req.EmployeeId);
    }

    private static async Task<IActionResult> TryProposeAsync(World w, EmployeeSalaryStructureRequest req)
    {
        var result = await Hr(w).AssignEmployeeSalary(req, CancellationToken.None);
        w.Db.ChangeTracker.Clear();
        return result;
    }

    private static async Task<IActionResult> DecideAsync(World w, Guid id)
    {
        var result = await Finance(w).ApproveSalaryChange(id, null, CancellationToken.None);
        w.Db.ChangeTracker.Clear();
        return result;
    }

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement((r as ObjectResult)?.Value, Web);

    /// <summary>The DTO the list endpoint shows for a change.</summary>
    private static async Task<JsonElement> ListedAsync(World w, Guid id)
    {
        var listed = await Finance(w).ListSalaryChanges(null, null, CancellationToken.None);
        return JsonSerializer.SerializeToElement(((OkObjectResult)listed).Value, Web).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
    }

    // ══ 3. Reductions need a consent reference ══════════════════════════════════════════════════

    [Fact]
    public async Task LoweringTheBasic_IsRefusedWithoutAConsentReference_AndNothingIsStaged()
    {
        await using var w = await NewWorld();

        var refused = await TryProposeAsync(w, Package(w, w.A, 9_000m, "2026-07-01", null));

        var body = Body(refused);
        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("reduction_consent_required");
        body.GetProperty("message").GetString().Should().Contain("written agreement").And.Contain("BASIC");
        body.GetProperty("message").GetString().Should().NotContainEquivalentOf("article");
        (await w.Db.SalaryChangeRequests.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ALoweringWithAReference_IsStaged_ShownAsAReduction_AndTheReferenceIsKeptThroughApproval()
    {
        await using var w = await NewWorld();

        var change = await ProposeAsync(w, Package(w, w.A, 9_000m, "2026-07-01", null, consent: "  QIWA-2026-77  "));

        change.ReductionConsentReference.Should().Be("QIWA-2026-77");
        var listed = await ListedAsync(w, change.Id);
        listed.GetProperty("isReduction").GetBoolean().Should().BeTrue();
        listed.GetProperty("reductionConsentReference").GetString().Should().Be("QIWA-2026-77");
        var item = listed.GetProperty("reducedItems").EnumerateArray().Single();
        (item.GetProperty("item").GetString(), item.GetProperty("from").GetDecimal(), item.GetProperty("to").GetDecimal()).Should().Be(("BASIC", 10_000m, 9_000m));
        (listed.GetProperty("currentMonthlyPay").GetDecimal(), listed.GetProperty("proposedMonthlyPay").GetDecimal()).Should().Be((13_000m, 12_000m));

        (await DecideAsync(w, change.Id)).Should().BeOfType<OkObjectResult>();
        var audit = await w.Db.PayrollAuditLogs.AsNoTracking().SingleAsync(a => a.Action == "payroll.employee_salary.assigned");
        audit.MetadataJson.Should().Contain("QIWA-2026-77").And.Contain("isReduction");
    }

    [Fact]
    public async Task ARaise_NeedsNoReference()
    {
        await using var w = await NewWorld();

        var change = await ProposeAsync(w, Package(w, w.A, 11_000m, "2026-07-01", null));

        change.ReductionConsentReference.Should().BeNull();
        var listed = await ListedAsync(w, change.Id);
        listed.GetProperty("isReduction").GetBoolean().Should().BeFalse();
        listed.GetProperty("reducedItems").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task LoweringAnySingleItem_IsAReduction_EvenWhenTheTotalGoesUp()
    {
        await using var w = await NewWorld();

        // Basic +1,000 but housing -500: the total rises (13,500) and housing is lowered.
        var refused = await TryProposeAsync(w, Package(w, w.A, 11_000m, "2026-07-01", null, housing: 1_500m));
        Body(refused).GetProperty("code").GetString().Should().Be("reduction_consent_required");
        Body(refused).GetProperty("message").GetString().Should().Contain("HOUSING");

        (await TryProposeAsync(w, Package(w, w.A, 11_000m, "2026-07-01", null, consent: "AMEND-1", housing: 1_500m))).Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task RemovingOrLoweringAnAllowanceLine_IsAReduction_AndKeepingItIsNot()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);

        Body(await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", []))).GetProperty("message").GetString().Should().Contain(Car).And.Contain("removed");
        Body(await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", [Amt(Car, 400m)]))).GetProperty("code").GetString().Should().Be("reduction_consent_required");
        (await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", [Amt(Car, 500m)]))).Should().BeOfType<AcceptedResult>("the same line is not a reduction");
        (await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-02", null))).Should().BeOfType<AcceptedResult>("a carried line is the same line");
    }

    [Fact]
    public async Task ACarriedPercentageLine_FallsWithTheBasic_SoItIsAReductionToo()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 1_000m, rate: 0.10m);

        // Housing and transport are raised, the basic is lowered a little: the percentage line (10% of basic) goes down with it.
        var refused = await TryProposeAsync(w, Package(w, w.A, 9_900m, "2026-07-01", null, housing: 3_000m, transport: 2_000m));

        Body(refused).GetProperty("message").GetString().Should().Contain("BASIC").And.Contain(Car);
    }

    [Fact]
    public async Task AReferenceThatIsTooLong_IsRefused()
    {
        await using var w = await NewWorld();

        var refused = await TryProposeAsync(w, Package(w, w.A, 11_000m, "2026-07-01", null, consent: new string('x', 201)));

        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        Body(refused).GetProperty("code").GetString().Should().Be("reduction_consent_reference_too_long");
        (await TryProposeAsync(w, Package(w, w.A, 11_000m, "2026-07-01", null, consent: new string('x', 200)))).Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task NothingInForce_IsNeverAReduction_TheFirstPackageOfAHire()
    {
        SalaryReductions.Compare(null, [], new PaySlots(5_000m, 0m, 0m, 0m, 0m, 0m, 0m), []).Should().Be(ReductionResult.None);
        await using var w = await NewWorld();
        w.Db.EmployeeSalaryStructures.RemoveRange(w.Db.EmployeeSalaryStructures.Where(x => x.EmployeeId == w.B.Id));
        await w.Db.SaveChangesAsync();

        (await SalaryReductions.EvaluateAsync(w.Db, w.Tid, w.B.Id, new DateOnly(2026, 7, 1), new PaySlots(1m, 0m, 0m, 0m, 0m, 0m, 0m), null, default))
            .IsReduction.Should().BeFalse();
    }

    [Fact]
    public async Task FinanceApproval_ChecksAgain_AReductionThatOnlyBecameOneAfterTheProposal_IsRefusedAsStale()
    {
        await using var w = await NewWorld();
        // P1 (10,500 from July) is a raise over the 10,000 in force now, so it is staged without a reference.
        var p1 = await ProposeAsync(w, Package(w, w.A, 10_500m, "2026-07-01", null));
        // P2 (12,000 from June) is approved first: from July the record in force is 12,000, so P1 now LOWERS the pay.
        var p2 = await ProposeAsync(w, Package(w, w.A, 12_000m, "2026-06-01", null));
        (await DecideAsync(w, p2.Id)).Should().BeOfType<OkObjectResult>();

        var refused = await DecideAsync(w, p1.Id);

        refused.Should().BeOfType<ConflictObjectResult>();
        Body(refused).GetProperty("error").GetString().Should().Be("salary_change_stale");
        Body(refused).GetProperty("code").GetString().Should().Be("reduction_consent_required");
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == p1.Id)).Status.Should().Be(SalaryChangeRequest.Pending);
    }

    [Fact]
    public async Task TheCsvImport_RefusesAReductionRow_WithoutAReference_AndStagesItWithTheOptionalColumn()
    {
        await using var w = await NewWorld();
        const string header = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance,FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate";

        var plain = await Hr(w).ImportEmployeeSalaries(new ImportSalaryStructuresRequest($"{header}\nC001,STR-C3,9000,2000,1000,0,0,0,0,SAR,2026-07-01\nC002,STR-C3,11000,2000,1000,0,0,0,0,SAR,2026-07-01\n"), default);

        var body = Body(plain);
        body.GetProperty("pendingApproval").GetInt32().Should().Be(1, "the raise is staged");
        body.GetProperty("skipped").GetInt32().Should().Be(1, "the reduction is not");
        body.GetProperty("errors").EnumerateArray().Single().GetString().Should().Contain("Row 2").And.Contain("written agreement");

        var withReference = await Hr(w).ImportEmployeeSalaries(new ImportSalaryStructuresRequest(
            $"{header},ReductionConsentReference\nC001,STR-C3,9000,2000,1000,0,0,0,0,SAR,2026-07-01,AMEND-C001\n"), default);
        Body(withReference).GetProperty("pendingApproval").GetInt32().Should().Be(1);
        w.Db.ChangeTracker.Clear();
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.EmployeeCode == "C001" && x.Status == SalaryChangeRequest.Pending))
            .ReductionConsentReference.Should().Be("AMEND-C001");
    }

    [Fact]
    public async Task TheApprovalsPackagePath_RefusesAReductionWithoutAReference_AndAcceptsOneWithIt()
    {
        await using var w = await NewWorld();
        var employee = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.A.Id);
        static IReadOnlyDictionary<string, JsonElement> Changes(decimal basic, string? consent) => new Dictionary<string, JsonElement>
        {
            ["salaryBreakdown"] = (JsonDocument.Parse(
                "{\"basicSalary\":" + basic + ",\"housingAllowance\":2000,\"transportAllowance\":1000,\"foodAllowance\":0,\"mobileAllowance\":0,\"otherAllowance\":0," +
                "\"fixedDeduction\":0,\"effectiveDate\":\"2026-07-01\",\"currency\":\"SAR\"" + (consent is null ? "" : ",\"reductionConsentReference\":\"" + consent + "\"") + "}")).RootElement.Clone(),
        };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => EmployeeSalaryBreakdownChanges.ValidateAsync(w.Db, employee, Changes(9_000m, null), default));
        refused.Message.Should().Contain("written agreement");

        await EmployeeSalaryBreakdownChanges.ValidateAsync(w.Db, employee, Changes(9_000m, "AMEND-9"), default);
        await EmployeeSalaryBreakdownChanges.ValidateAsync(w.Db, employee, Changes(11_000m, null), default);
    }

    [Fact]
    public async Task TheApprovalsCard_ShowsAReductionAndItsReference()
    {
        await using var w = await NewWorld();
        EmployeeChangeRequest Edit(string consent) => new()
        {
            TenantId = w.Tid, EmployeeId = w.A.Id, RequestedByUserId = Guid.NewGuid(), SensitiveFields = "salaryBreakdown", EffectiveDate = new DateOnly(2026, 7, 1),
            ProposedChangesJson = "{\"salaryBreakdown\":{\"basicSalary\":9000,\"housingAllowance\":2000,\"transportAllowance\":1000,\"foodAllowance\":0,\"mobileAllowance\":0,\"otherAllowance\":0," +
                                  "\"fixedDeduction\":0,\"effectiveDate\":\"2026-07-01\",\"currency\":\"SAR\"" + consent + "}}",
        };

        var with = await EmployeeChangeDetail.BuildAsync(w.Db, new ApprovalRequest { TenantId = w.Tid, Title = "Change" }, Edit(",\"reductionConsentReference\":\"AMEND-9\""),
            ["employees.sensitive"], default);
        var without = await EmployeeChangeDetail.BuildAsync(w.Db, new ApprovalRequest { TenantId = w.Tid, Title = "Change" }, Edit(""), ["employees.sensitive"], default);

        with!.Fields.Single().ProposedValue.Should().Contain("lowers the employee's pay").And.Contain("BASIC").And.Contain("AMEND-9");
        without!.Fields.Single().ProposedValue.Should().Contain("lowers the employee's pay").And.Contain("none given");
    }

    // ══ 4. The change says it changes the contract wage ═════════════════════════════════════════

    [Theory]
    [InlineData(11_000, 2_000, 1_000, 0, true)]     // basic
    [InlineData(10_000, 2_500, 1_000, 0, true)]     // housing
    [InlineData(10_000, 2_000, 1_200, 0, true)]     // transport
    [InlineData(10_000, 2_000, 1_000, 300, false)]  // only a fixed deduction moves: pay is the same
    [InlineData(10_000, 2_000, 1_000, 0, false)]    // identical
    public async Task TheChangeFlagsWhenItChangesTheContractWage(int basic, int housing, int transport, int deduction, bool changes)
    {
        await using var w = await NewWorld();

        var change = await ProposeAsync(w, Package(w, w.A, basic, "2026-07-01", null, consent: "N/A", housing: housing, transport: transport, deduction: deduction));

        (await ListedAsync(w, change.Id)).GetProperty("changesContractWage").GetBoolean().Should().Be(changes);
    }

    [Fact]
    public async Task AnAllowanceLineThatChanges_ChangesTheContractWage_AndAFirstPackageDoesNot()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);

        var raised = await ProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", [Amt(Car, 600m)]));
        (await ListedAsync(w, raised.Id)).GetProperty("changesContractWage").GetBoolean().Should().BeTrue();

        w.Db.EmployeeSalaryStructures.RemoveRange(w.Db.EmployeeSalaryStructures.Where(x => x.EmployeeId == w.B.Id));
        await w.Db.SaveChangesAsync();
        var first = await ProposeAsync(w, Package(w, w.B, 10_000m, "2026-07-01", null));
        (await ListedAsync(w, first.Id)).GetProperty("changesContractWage").GetBoolean().Should().BeFalse("nothing in force: it sets the wage, it does not change it");
    }

    // ══ 1/6. A line goes only on a confirmed definition, at staging and at approval ══════════════

    [Fact]
    public async Task APendingDefinition_CannotBePutOnASalaryLine_ThePrefillDoesNotOfferIt_AndApprovalChecksAgain()
    {
        await using var w = await NewWorld(definition: false);
        var definition = await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", confirmed: false);

        var refused = await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", [Amt(Car, 500m)]));

        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        Body(refused).GetProperty("code").GetString().Should().Be("salary_allowance_pending_finance");
        Body(refused).GetProperty("message").GetString().Should().Contain("Finance").And.Contain(Car);
        (await w.Db.SalaryChangeRequests.AnyAsync()).Should().BeFalse();

        // The grade prefill never offers it.
        var grade = new Grade { TenantId = w.Tid, Code = "G1", Name = "Grade 1", Level = 10 };
        w.Db.Grades.Add(grade);
        w.Db.GradeEntitlements.Add(new GradeEntitlement
        {
            TenantId = w.Tid, GradeId = grade.Id, PayComponentCode = Car, EntitlementClass = PayEntitlementClasses.QiwaWage, Eligible = true,
            ValueType = GradeEntitlementValueTypes.Amount, Amount = 500m, DependantScope = DependantScopes.None, LimitPeriod = EntitlementLimitPeriods.Monthly, EffectiveFrom = new DateOnly(2026, 1, 1),
        });
        await w.Db.SaveChangesAsync();
        (await EntitlementMatrixService.CashAllowanceLinesAsync(w.Db, w.Tid, grade.Id, w.CompanyId, new DateOnly(2026, 7, 1), default)).Should().BeEmpty();

        // Confirmed it is accepted and offered; if it is un-confirmed before Finance approves, the approval refuses.
        (await w.Db.PayComponents.SingleAsync(x => x.Id == definition.Id)).FinanceConfirmedAtUtc = DateTime.UtcNow;
        await w.Db.SaveChangesAsync();
        (await EntitlementMatrixService.CashAllowanceLinesAsync(w.Db, w.Tid, grade.Id, w.CompanyId, new DateOnly(2026, 7, 1), default)).Should().ContainSingle();
        var change = await ProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", [Amt(Car, 500m)]));
        (await w.Db.PayComponents.SingleAsync(x => x.Id == definition.Id)).FinanceConfirmedAtUtc = null;
        await w.Db.SaveChangesAsync();

        var stale = await DecideAsync(w, change.Id);

        stale.Should().BeOfType<ConflictObjectResult>();
        Body(stale).GetProperty("code").GetString().Should().Be("salary_allowance_pending_finance");
        (await w.Db.EmployeeSalaryAllowances.AnyAsync(x => x.Code == Car)).Should().BeFalse("nothing was applied");
    }

    [Fact]
    public async Task ADefinitionWhoseOnlyVersionEndedInThePast_IsNotLive_ButOneWithALaterVersionIs()
    {
        await using var w = await NewWorld(definition: false);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var endedPast = new DateOnly(today.Year, today.Month, 1).AddMonths(-2).AddDays(-1);
        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", to: endedPast);

        Body(await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", [Amt(Car, 500m)]))).GetProperty("code").GetString().Should().Be("salary_allowance_unknown");

        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", driverRow: false, from: endedPast.AddDays(1));
        (await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", [Amt(Car, 500m)]))).Should().BeOfType<AcceptedResult>("a version in effect today or later is live");
    }

    // ══ 2. Every reader picks the version in effect for its own date ════════════════════════════

    [Fact]
    public async Task ThePayrollRun_PostsEachMonthToTheGlDriverOfTheVersionInEffectForThatMonth()
    {
        await using var w = await NewWorld(definition: false);
        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", to: new DateOnly(2026, 6, 30));
        await AddDefinitionAsync(w.Db, w.Tid, Car, "EARN:CAR2", "Car allowance v2", "5211", from: new DateOnly(2026, 7, 1));
        await GiveAsync(w, w.A, Car, 500m);
        var june = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
        var july = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 7);
        await w.Db.SaveChangesAsync();

        (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(july.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        async Task<string?> DriverOf(PayrollRun run) =>
            (await w.Db.PayrollEarnings.AsNoTracking().SingleAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == w.A.Id && e.ComponentCode == Car)).GlDriverKey;
        (await DriverOf(june)).Should().Be(CarDriver);
        (await DriverOf(july)).Should().Be("EARN:CAR2");
    }

    [Fact]
    public async Task EndOfService_ReadsTheEosbAnswerOfTheVersionInEffectOnTheAsOfDate()
    {
        await using var w = await NewWorld(definition: false);
        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", eosb: true, to: new DateOnly(2026, 6, 30));
        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", eosb: false, driverRow: false, from: new DateOnly(2026, 7, 1));
        await GiveAsync(w, w.A, Car, 500m);
        var salaryId = (await w.Db.EmployeeSalaryStructures.AsNoTracking().FirstAsync(x => x.EmployeeId == w.A.Id)).Id;

        var june = await SalaryAllowanceLines.EosbIncludedAsync(w.Db, w.Tid, [salaryId], new DateOnly(2026, 6, 1), default);
        var july = await SalaryAllowanceLines.EosbIncludedAsync(w.Db, w.Tid, [salaryId], new DateOnly(2026, 7, 1), default);

        june[salaryId].Should().ContainSingle(x => x.Code == Car);
        july.Should().NotContainKey(salaryId);
    }

    /// <summary>Jan and Feb locked without the allowance; a backdated one-off Jan-Feb allowance, then a record from March without it: March pays back pay.</summary>
    private static async Task<(World W, PayrollRun March)> JanFebOneOffAsync()
    {
        var w = await NewWorld(definition: false);
        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", to: new DateOnly(2026, 2, 28));
        await AddDefinitionAsync(w.Db, w.Tid, Car, "EARN:CAR2", "Car allowance v2", "5211", from: new DateOnly(2026, 3, 1));
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
        return (w, march);
    }

    [Fact]
    public async Task BackPay_PostsOnTheGlDriverInEffectForTheRunThatPaysIt_NotTheCoveredMonths()
    {
        var (w, march) = await JanFebOneOffAsync();
        await using var _ = w;

        (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(march.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var lines = await w.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == march.Id && e.EmployeeId == w.A.Id && e.Source == "Arrears").ToListAsync();
        lines.Should().HaveCount(2).And.OnlyContain(l => l.ComponentCode == "ARREARS_ALW_CAR");
        lines.Should().OnlyContain(l => l.GlDriverKey == "EARN:CAR2", "back pay posts in the current period, on the driver the allowance has now");
    }

    // ══ 6. The pre-run readiness ════════════════════════════════════════════════════════════════

    private static async Task<JsonElement> ReadinessAsync(World w, int year = 2026, int month = 6)
    {
        var result = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).PayrollReadiness(null, year, month, CancellationToken.None);
        return JsonSerializer.SerializeToElement(((OkObjectResult)result).Value, Web);
    }

    [Fact]
    public async Task TheReadiness_ListsEmployeesWhoseAllowanceLineCouldNotBePaid_BeforePayrollDay()
    {
        await using var w = await NewWorld(definition: false);
        await AddDefinitionAsync(w.Db, w.Tid, "ALW_PENDING", "EARN:PEND", "Pending allowance", "5212", confirmed: false);
        await AddDefinitionAsync(w.Db, w.Tid, "ALW_NODRIVER", "", "No driver allowance", "5213", driverRow: false);
        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210");
        await GiveAsync(w, w.A, "ALW_PENDING", 100m);
        await GiveAsync(w, w.A, "ALW_NODRIVER", 100m);
        await GiveAsync(w, w.A, Car, 100m);                  // fine
        await GiveAsync(w, w.B, "ALW_GONE", 100m);           // no definition at all

        var readiness = await ReadinessAsync(w);

        var issues = readiness.GetProperty("cashAllowanceIssues").EnumerateArray()
            .Select(i => (i.GetProperty("employeeCode").GetString(), i.GetProperty("code").GetString(), i.GetProperty("problem").GetString())).ToList();
        issues.Should().BeEquivalentTo(new[]
        {
            ("C001", "ALW_NODRIVER", "no_gl_driver"), ("C001", "ALW_PENDING", "not_confirmed"), ("C002", "ALW_GONE", "no_definition"),
        });
        readiness.GetProperty("isReadyForProcessing").GetBoolean().Should().BeFalse();
        readiness.GetProperty("cashAllowanceIssues").EnumerateArray().Select(i => i.GetRawText()).Should().OnlyContain(t => !t.Contains("100"), "no amounts");
    }

    [Fact]
    public async Task TheReadiness_ForAnEmployeeWithoutAllowanceLines_IsExactlyAsBefore()
    {
        await using var w = await NewWorld(definition: false);

        var readiness = await ReadinessAsync(w);

        readiness.GetProperty("cashAllowanceIssues").GetArrayLength().Should().Be(0);
        readiness.GetProperty("salaryCoveragePercent").GetDouble().Should().Be(100d, "the same coverage figure as before");
    }

    [Fact]
    public async Task TheReadiness_UsesTheDefinitionVersionForTheMonthAsked()
    {
        await using var w = await NewWorld(definition: false);
        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", from: new DateOnly(2026, 7, 1));
        await GiveAsync(w, w.A, Car, 100m);

        (await ReadinessAsync(w, 2026, 6)).GetProperty("cashAllowanceIssues").EnumerateArray().Single().GetProperty("problem").GetString().Should().Be("no_definition");
        (await ReadinessAsync(w, 2026, 7)).GetProperty("cashAllowanceIssues").GetArrayLength().Should().Be(0);
    }

    // ══ Security-review fixes (M1, L1, L2, L5, L6) ══════════════════════════════════════════════

    [Fact]
    public async Task ACorrection_IsRefused_AndSavesNothing_WhenARunBecomesCommittedAfterTheFirstCheck()
    {
        await using var h = await Mx.Create();
        await h.Add(h.FinanceA, finance: true, "Car");
        // Between the first read of the latest committed period and the write, payroll for December is approved.
        h.Service.BeforeCorrectionSaveHook = async () =>
        {
            h.Db.PayrollRuns.Add(new PayrollRun { TenantId = h.Tid, CompanyId = null, Year = 2026, Month = 12, Status = "Approved", RunType = PayrollRunTypes.Regular });
            await h.Db.SaveChangesAsync();
        };

        var refused = await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), "Raced a run", "EARN:CAR2"));

        Status(refused).Should().Be(409, Describe(refused));
        Error(refused).Should().Be("period_committed");
        var rows = await h.Rows(Car);
        rows.Should().ContainSingle("a refused correction writes no new version");
        (rows[0].GlDriverKey, rows[0].EffectiveTo).Should().Be((CarDriver, (DateOnly?)null));
        (await h.Db.AuditLogs.AnyAsync(a => a.Action == EntitlementMatrixService.AuditBenefitCorrected)).Should().BeFalse("and no audit row");
    }

    [Fact]
    public async Task ACorrection_MayStartAtMostTwelveMonthsAfterTheCurrentMonth()
    {
        await using var h = await Mx.Create();      // today = 2026-10-06
        await h.Add(h.FinanceA, finance: true, "Car");

        var tooFar = await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2027, 11, 1), "Too far ahead", "EARN:CAR2"));
        Status(tooFar).Should().Be(400, Describe(tooFar));
        Error(tooFar).Should().Be("effective_from_too_far");
        (await h.Rows(Car)).Should().ContainSingle();

        Status(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2027, 10, 1), "Exactly twelve months ahead", "EARN:CAR2"))).Should().Be(200);
    }

    [Theory]
    [InlineData("bell\u0007note")]
    [InlineData("flip\u202Eed")]
    [InlineData("isolate\u2066x\u2069")]
    public async Task AnEosbNote_AndACorrectionReason_RefuseControlAndBidiOverrideCharacters(string text)
    {
        await using var h = await Mx.Create();

        Error(await h.Add(h.FinanceA, true, "Car", eosb: false, category: "Other", reason: text)).Should().Be("eosb_reason_invalid");
        (await h.Db.PayComponents.AnyAsync(x => x.Code == Car)).Should().BeFalse();

        await h.Add(h.FinanceA, true, "Car");
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), text, "EARN:CAR2"))).Should().Be("reason_invalid");
        Error(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), "Fine reason", null, false, "Other", text))).Should().Be("eosb_reason_invalid");
        (await h.Rows(Car)).Should().ContainSingle();
    }

    [Fact]
    public async Task AnArabicNoteWithRightToLeftMarks_IsStillAccepted()
    {
        await using var h = await Mx.Create();

        Status(await h.Add(h.FinanceA, true, "Car", eosb: false, category: "Other", reason: "بدل غير ثابت\u200F (٢٠٢٦)")).Should().Be(200);
    }

    [Fact]
    public async Task ThePayrollRun_RefusesAnAllowanceLineWhoseDefinitionIsNotConfirmedByFinance()
    {
        await using var w = await NewWorld(definition: false);
        await AddDefinitionAsync(w.Db, w.Tid, Car, CarDriver, "Car allowance", "5210", confirmed: false);   // inserted past the write doors
        await GiveAsync(w, w.A, Car, 500m);
        var run = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
        await w.Db.SaveChangesAsync();

        var result = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(run.Id, CancellationToken.None);

        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(refusal.Value).Should().Contain("cash_allowance_definition_invalid").And.Contain(w.A.EmployeeCode).And.Contain(Car).And.Contain("Nothing was saved");
        (await w.Db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.PayrollRunId == run.Id)).Should().BeFalse("nothing is paid or saved");
    }

    [Fact]
    public async Task ThePayrollRun_RefusesBackPayForAnAllowanceWhoseDefinitionIsNotConfirmedByFinance()
    {
        var (w, march) = await JanFebOneOffAsync();
        await using var _ = w;
        (await w.Db.PayComponents.SingleAsync(c => c.Code == Car && c.EffectiveFrom == new DateOnly(2026, 3, 1))).FinanceConfirmedAtUtc = null;
        await w.Db.SaveChangesAsync();

        var result = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(march.Id, CancellationToken.None);

        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(refusal.Value).Should().Contain("cash_allowance_definition_invalid").And.Contain(w.A.EmployeeCode).And.Contain("ARREARS_ALW_CAR");
        (await w.Db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.PayrollRunId == march.Id)).Should().BeFalse("nothing is paid or saved");
    }

    [Fact]
    public async Task EndOfService_RefusesALineWhoseDefinitionIsGone_NamingTheLine_InsteadOfUnderstatingTheWage()
    {
        await using var w = await NewWorld(definition: false);
        await GiveAsync(w, w.A, Car, 500m);                      // an orphan: no definition at all
        var salaryId = (await w.Db.EmployeeSalaryStructures.AsNoTracking().FirstAsync(x => x.EmployeeId == w.A.Id)).Id;

        var act = () => SalaryAllowanceLines.EosbIncludedAsync(w.Db, w.Tid, [salaryId], new DateOnly(2026, 6, 1), default);

        (await act.Should().ThrowAsync<CashAllowanceDefinitionMissingException>()).Which.Message.Should().Contain(Car);
    }

    [Fact]
    public void AnOrphanAllowanceLine_IsAnswered422WithAStableCode_NotA500()
    {
        var (status, code, message) = Zayra.Api.Infrastructure.Http.ExceptionResponseMapper.Map(new CashAllowanceDefinitionMissingException(Car, "no longer exists"));

        (status, code).Should().Be((422, "cash_allowance_definition_invalid"));
        message.Should().Contain(Car);
    }

    // ══ L3. A higher fixed deduction lowers take-home pay: it needs the written agreement too ═════

    [Fact]
    public void RaisingTheFixedDeduction_OrAddingOne_IsAReduction_LoweringOrKeepingItIsNot()
    {
        var current = new PaySlots(10_000m, 2_000m, 1_000m, 0m, 0m, 0m, 100m);

        var raised = SalaryReductions.Compare(current, [], current with { FixedDeduction = 150m }, []);
        raised.IsReduction.Should().BeTrue();
        raised.Items.Should().ContainSingle().Which.Should().Be(new ReducedItem("FIXED_DEDUCTION", 100m, 150m));
        raised.ChangesContractWage.Should().BeFalse("a deduction is not part of the contract wage");
        (raised.CurrentTotal, raised.ProposedTotal).Should().Be((13_000m, 13_000m), "the deduction is not pay");

        SalaryReductions.Compare(new PaySlots(10_000m, 2_000m, 1_000m, 0m, 0m, 0m, 0m), [], current, []).Items
            .Should().ContainSingle().Which.Should().Be(new ReducedItem("FIXED_DEDUCTION", 0m, 100m), "a deduction where there was none");
        SalaryReductions.Compare(current, [], current with { FixedDeduction = 40m }, []).IsReduction.Should().BeFalse();
        SalaryReductions.Compare(current, [], current with { FixedDeduction = 0m }, []).IsReduction.Should().BeFalse();
        SalaryReductions.Compare(current, [], current, []).IsReduction.Should().BeFalse();
    }

    [Fact]
    public async Task ProposingAHigherFixedDeductionAlone_NeedsTheReference_AndALowerOrSameOneDoesNot()
    {
        await using var w = await NewWorld();

        var refused = await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", null, deduction: 300m));
        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        Body(refused).GetProperty("code").GetString().Should().Be("reduction_consent_required");
        Body(refused).GetProperty("message").GetString().Should().Contain("Fixed deduction");
        Body(refused).GetProperty("reducedItems").EnumerateArray().Single().GetProperty("item").GetString().Should().Be("FIXED_DEDUCTION");
        (await w.Db.SalaryChangeRequests.AnyAsync()).Should().BeFalse();

        var staged = await ProposeAsync(w, Package(w, w.A, 10_000m, "2026-07-01", null, consent: "AMEND-DED-1", deduction: 300m));
        var listed = await ListedAsync(w, staged.Id);
        listed.GetProperty("isReduction").GetBoolean().Should().BeTrue();
        listed.GetProperty("reducedItems").EnumerateArray().Single().GetProperty("item").GetString().Should().Be("FIXED_DEDUCTION");
        (await DecideAsync(w, staged.Id)).Should().BeOfType<OkObjectResult>();

        // 300 is now in force: the same or a lower deduction is not a reduction.
        (await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-08-01", null, deduction: 300m))).Should().BeOfType<AcceptedResult>();
        (await TryProposeAsync(w, Package(w, w.A, 10_000m, "2026-09-01", null, deduction: 100m))).Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task TheCsvImport_AndTheApprovalsPath_AlsoRefuseAHigherFixedDeduction_WithoutAReference()
    {
        await using var w = await NewWorld();
        const string header = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance,FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate";
        var import = await Hr(w).ImportEmployeeSalaries(new ImportSalaryStructuresRequest($"{header}\nC001,STR-C3,10000,2000,1000,0,0,0,300,SAR,2026-07-01\n"), default);
        Body(import).GetProperty("skipped").GetInt32().Should().Be(1);
        Body(import).GetProperty("errors").EnumerateArray().Single().GetString().Should().Contain("written agreement");

        var employee = await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.A.Id);
        static IReadOnlyDictionary<string, JsonElement> Changes(string? consent) => new Dictionary<string, JsonElement>
        {
            ["salaryBreakdown"] = (JsonDocument.Parse(
                "{\"basicSalary\":10000,\"housingAllowance\":2000,\"transportAllowance\":1000,\"foodAllowance\":0,\"mobileAllowance\":0,\"otherAllowance\":0," +
                "\"fixedDeduction\":300,\"effectiveDate\":\"2026-07-01\",\"currency\":\"SAR\"" + (consent is null ? "" : ",\"reductionConsentReference\":\"" + consent + "\"") + "}")).RootElement.Clone(),
        };
        (await Assert.ThrowsAsync<InvalidOperationException>(() => EmployeeSalaryBreakdownChanges.ValidateAsync(w.Db, employee, Changes(null), default)))
            .Message.Should().Contain("written agreement");
        await EmployeeSalaryBreakdownChanges.ValidateAsync(w.Db, employee, Changes("AMEND-DED-2"), default);
    }

    // ══ L4. Pending changes are judged against each other, in whichever order Finance approves ════

    [Fact]
    public async Task TwoPendingChanges_ApprovedLaterFirst_ThenTheEarlierOne_RefuseTheEarlierBecauseTheLaterWouldNowLowerPay()
    {
        await using var w = await NewWorld();
        var a = await ProposeAsync(w, Package(w, w.A, 12_000m, "2026-06-01", null));
        var b = await ProposeAsync(w, Package(w, w.A, 11_000m, "2026-07-01", null));      // fine against the 10,000 in force
        (await DecideAsync(w, b.Id)).Should().BeOfType<OkObjectResult>("against what is in force from July today, B is a raise");

        var refused = await DecideAsync(w, a.Id);

        refused.Should().BeOfType<ConflictObjectResult>();
        Body(refused).GetProperty("error").GetString().Should().Be("later_change_becomes_reduction");
        Body(refused).GetProperty("message").GetString().Should().Contain("2026-07-01");
        Body(refused).GetProperty("laterEffectiveDate").GetString().Should().Be("2026-07-01");
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == a.Id)).Status.Should().Be(SalaryChangeRequest.Pending);
        (await w.Db.EmployeeSalaryStructures.AsNoTracking().AnyAsync(x => x.EmployeeId == w.A.Id && x.BasicSalary == 12_000m)).Should().BeFalse("nothing was applied");
    }

    [Fact]
    public async Task TwoPendingChanges_ApprovedEarlierFirst_RefuseTheLaterOne_WithoutAReference()
    {
        await using var w = await NewWorld();
        var a = await ProposeAsync(w, Package(w, w.A, 12_000m, "2026-06-01", null));
        var b = await ProposeAsync(w, Package(w, w.A, 11_000m, "2026-07-01", null));
        (await DecideAsync(w, a.Id)).Should().BeOfType<OkObjectResult>();

        var refused = await DecideAsync(w, b.Id);

        refused.Should().BeOfType<ConflictObjectResult>();
        Body(refused).GetProperty("code").GetString().Should().Be("reduction_consent_required");
    }

    [Fact]
    public async Task TwoPendingChanges_WhereTheLaterOneCarriesAReference_OrNothingLowers_ApproveInEitherOrder()
    {
        await using var w = await NewWorld();
        // The later change is a reduction against the earlier one, but it was staged with the employee's written agreement.
        var a = await ProposeAsync(w, Package(w, w.A, 12_000m, "2026-06-01", null));
        var b = await ProposeAsync(w, Package(w, w.A, 11_000m, "2026-07-01", null, consent: "AMEND-B-1"));
        (await DecideAsync(w, b.Id)).Should().BeOfType<OkObjectResult>();
        (await DecideAsync(w, a.Id)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TwoPendingRaises_ApproveInEitherOrder()
    {
        await using var w = await NewWorld();
        var a = await ProposeAsync(w, Package(w, w.A, 12_000m, "2026-06-01", null));
        var b = await ProposeAsync(w, Package(w, w.A, 13_000m, "2026-07-01", null));
        (await DecideAsync(w, b.Id)).Should().BeOfType<OkObjectResult>();
        (await DecideAsync(w, a.Id)).Should().BeOfType<OkObjectResult>();
    }

    // ══ L7. The consent reference must be a real reference, not "x" ═══════════════════════════════

    [Theory]
    [InlineData("x")]
    [InlineData("ab")]
    [InlineData("---")]
    [InlineData(" . , ")]
    public async Task AConsentReferenceThatIsTooShortOrHasNoLetterOrDigit_IsRefused(string reference)
    {
        await using var w = await NewWorld();

        var refused = await TryProposeAsync(w, Package(w, w.A, 9_000m, "2026-07-01", null, consent: reference));

        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        Body(refused).GetProperty("code").GetString().Should().Be("reduction_consent_reference_too_short");
        (await w.Db.SalaryChangeRequests.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ABlankConsentReference_IsStillNoReference()
    {
        await using var w = await NewWorld();

        var refused = await TryProposeAsync(w, Package(w, w.A, 9_000m, "2026-07-01", null, consent: "   "));

        Body(refused).GetProperty("code").GetString().Should().Be("reduction_consent_required");
    }

    [Theory]
    [InlineData("HR-2026-014")]
    [InlineData("اتفاقية-٢٠٢٦")]
    [InlineData("N/A")]
    public async Task ARealReference_IsAccepted_ArabicIncluded(string reference)
    {
        await using var w = await NewWorld();

        (await TryProposeAsync(w, Package(w, w.A, 9_000m, "2026-07-01", null, consent: reference))).Should().BeOfType<AcceptedResult>();
    }

    // ══ M1 (closed): run approval and a correction take the same matrix lock ═══════════════════════

    private static List<(string Scope, Guid TenantId, Guid AggregateId)> Record()
    {
        var log = new List<(string Scope, Guid TenantId, Guid AggregateId)>();
        Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.Recorder.Value = log;
        return log;
    }

    [Theory]
    [InlineData("Admin", "Approved")]
    [InlineData("HR Manager", "PendingFinanceReview")]
    public async Task ApprovingARun_TakesTheMatrixLock_OnTheKeyACorrectionHolds_AtBothLevels(string role, string expectedStatus)
    {
        await using var w = await NewWorld();
        var run = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
        await w.Db.SaveChangesAsync();
        (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        w.Db.ChangeTracker.Clear();
        await ReconTestSupport.SignOffAsync(w.Db, run.Id);   // approval needs a recorded reconciliation review
        w.Db.ChangeTracker.Clear();
        var approver = PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid);
        var user = approver.ControllerContext.HttpContext.User;
        approver.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            user.Claims.Where(c => c.Type != System.Security.Claims.ClaimTypes.Role).Append(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)), "test"));
        var log = Record();
        try
        {
            var result = await approver.Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>(Describe(result));
        }
        finally { Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.Recorder.Value = null; }

        (await w.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be(expectedStatus);
        log.Should().Contain((EntitlementMatrixService.LockScope, w.Tid, w.Tid), "approval takes the lock a correction holds: same scope, tenant, and aggregate = tenant");
    }

    [Fact]
    public async Task ACorrection_TakesTheMatrixLock_OnTheTenantKey()
    {
        await using var h = await Mx.Create();
        await h.Add(h.FinanceA, finance: true, "Car");
        var log = Record();
        try { Status(await h.Correct(h.FinanceB, Car, new CorrectAllowanceRequest(new DateOnly(2026, 12, 1), "Posts elsewhere", "EARN:CAR2"))).Should().Be(200); }
        finally { Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.Recorder.Value = null; }

        log.Should().Contain((EntitlementMatrixService.LockScope, h.Tid, h.Tid));
    }

    [Fact]
    public async Task Process_NeverTakesTheMatrixLock_OnlyTheApproveTransitionDoes()
    {
        await using var w = await NewWorld();
        var run = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
        await w.Db.SaveChangesAsync();
        var log = Record();
        try { (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>(); }
        finally { Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.Recorder.Value = null; }

        log.Should().NotContain(l => l.Scope == EntitlementMatrixService.LockScope, "the long-running Process must not hold the lock every matrix write waits on");
    }

    [Fact]
    public async Task ClosingAGlPeriod_TakesTheMatrixLock_OnTheTenantKey()
    {
        await using var w = await NewWorld();
        var claims = new List<Claim> { new("tenant_id", w.Tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "finance"), new("permission", "finance.gl.manage") };
        var gl = new FinanceGlController(w.Db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } } };
        var log = Record();
        try { (await gl.ClosePeriod("2026-06", null, new GlPeriodActionRequest("month-end"), CancellationToken.None)).Should().BeOfType<OkObjectResult>(); }
        finally { Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.Recorder.Value = null; }

        log.Should().Contain((EntitlementMatrixService.LockScope, w.Tid, w.Tid), "a closed period makes the month committed, so it is taken under the lock a correction holds");
    }

    // ══ migration/schema shape ══════════════════════════════════════════════════════════════════

    [Fact]
    public void TheModelCarriesTheNewColumnsAndChecks()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var model = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(db).Model;
        var pay = model.FindEntityType(typeof(PayComponent))!;
        pay.GetCheckConstraints().Select(c => c.Name).Should().Contain(
            ["ck_pay_components__eosb_exclusion_category", "ck_pay_components__eosb_exclusion_other_note", "ck_pay_components__finance_confirmation"]);
        pay.FindProperty(nameof(PayComponent.FinanceConfirmedAtUtc))!.IsNullable.Should().BeTrue();
        pay.FindProperty(nameof(PayComponent.EosbExclusionCategory))!.GetMaxLength().Should().Be(20);
        var change = model.FindEntityType(typeof(SalaryChangeRequest))!;
        change.GetCheckConstraints().Select(c => c.Name).Should().Contain("ck_salary_change_requests__reduction_consent_reference");
        change.FindProperty(nameof(SalaryChangeRequest.ReductionConsentReference))!.GetMaxLength().Should().Be(200);
    }
}
