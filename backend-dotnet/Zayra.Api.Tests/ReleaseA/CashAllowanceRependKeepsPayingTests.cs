using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A change to a CONFIRMED monthly cash allowance waits for another Finance user, and while it waits the company's payroll keeps paying the last
/// confirmed terms (CTO decision implementing "company Finance confirms"): an edit or a Finance correction never stops the whole company's run. The new
/// terms take effect on confirmation. An allowance that was NEVER confirmed is still refused (422), and nobody confirms what they changed.
/// SQLite payroll (the real PayrollRunProcessor) and the real matrix service on the same database.
/// </summary>
public partial class CashAllowanceRependKeepsPayingTests
{
    private const string Car = "ALW_CAR";
    private static readonly DateOnly June1 = new(2026, 6, 1);

    // ══ An edit (switched off) ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnEditedConfirmedAllowance_KeepsPayingTheLastConfirmedTerms_UntilFinanceConfirmsTheEdit()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);

        var edited = Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default));
        edited.CashAllowance!.FinancePending.Should().BeTrue("switching a confirmed allowance off is a money change Finance confirms");

        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>("a change waiting for Finance must not stop the company's payroll");
        var line = await w.LineAsync(june, w.A);
        (line.Amount, line.GlDriverKey).Should().Be((500m, CarDriver), "the last confirmed terms keep paying");

        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("another Finance user confirms the edit");
        await w.ReopenAsync(june);
        var refused = await w.Process(june);
        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422, "confirmed, the allowance is off: the line on the salary is no longer paid");
        JsonSerializer.Serialize(((ObjectResult)refused).Value).Should().Contain("cash_allowance_definition_invalid");
    }

    [Fact]
    public async Task WhileTheEditWaits_ThePreRunCheckDoesNotWarnOfARefusalThatWillNotHappen_AndTheRunCanBeApprovedAndLocked()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default));

        var issues = await CashAllowanceDefinitions.ReadinessIssuesAsync(w.Db, w.Tid, [(w.A.Id, w.A.EmployeeCode, w.A.FullName, (Guid?)w.CompanyId)],
            June1, new DateOnly(2026, 6, 30), default);
        issues.Should().BeEmpty();

        var june = await w.RunAsync(2026, 6);
        await BonusLifecycleGlTests.LockRun(w.Db, PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid), june);
        var gl = await BonusLifecycleGlTests.AllGl(w.Db, w.Tid);
        BonusLifecycleGlTests.Debits(gl.Where(g => g.Description == $"Payroll earning: {Car}"), "5210").Should().Be(500m);
        BonusLifecycleGlTests.AssertBalanced(gl);
    }

    // ══ A Finance correction (new GL driver from June) ══════════════════════════════════════════

    [Fact]
    public async Task ACorrectedAllowance_PaysTheOldDriverUntilAnotherFinanceUserConfirms_ThenTheNewOne()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        Status(await w.As(w.FinanceA, finance: true).CorrectBenefit(w.Service, Car, new CorrectAllowanceRequest(June1, "Re-route the expense", GlDriverKey: "EARN:CAR2"), default))
            .Should200("Finance corrects from June");

        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        (await w.LineAsync(june, w.A)).GlDriverKey.Should().Be(CarDriver, "the correction is not confirmed yet");

        var self = await w.As(w.FinanceA, finance: true).ConfirmBenefit(w.Service, Car, null, default);
        (Status(self), Error(self)).Should().Be(((int?)403, "maker_cannot_confirm"), "nobody confirms what they changed");
        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("another Finance user");

        await w.ReopenAsync(june);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        var line = await w.LineAsync(june, w.A);
        (line.Amount, line.GlDriverKey).Should().Be((500m, "EARN:CAR2"), "confirmed, the new terms pay");
    }

    [Fact]
    public async Task ACorrectionOfTheEosbSetting_DoesNotReachTheEndOfServiceWage_UntilItIsConfirmed()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        var salaryId = (await w.Db.EmployeeSalaryStructures.AsNoTracking().FirstAsync(x => x.EmployeeId == w.A.Id)).Id;
        Status(await w.As(w.FinanceA, finance: true).CorrectBenefit(w.Service, Car,
            new CorrectAllowanceRequest(June1, "Reimbursement in nature", EosbIncluded: false, EosbExclusionCategory: "Reimbursement"), default)).Should200("");

        (await SalaryAllowanceLines.EosbIncludedAsync(w.Db, w.Tid, [salaryId], June1, default))[salaryId].Should().ContainSingle(x => x.Code == Car,
            "the last confirmed answer (included) holds while the correction waits");

        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");
        (await SalaryAllowanceLines.EosbIncludedAsync(w.Db, w.Tid, [salaryId], June1, default)).Should().NotContainKey(salaryId);
    }

    // ══ What has not changed ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnAllowanceThatWasNeverConfirmed_StillRefusesTheRun_With422()
    {
        await using var w = await World.Create();
        Component(await w.As(w.Hr).AddBenefit(w.Service, CashCar(), default)).CashAllowance!.FinancePending.Should().BeTrue();
        await w.GiveAsync(w.A, 500m);

        var june = await w.RunAsync(2026, 6);
        var refused = await w.Process(june);

        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(((ObjectResult)refused).Value).Should().Contain("cash_allowance_definition_invalid").And.Contain("Finance has not confirmed");
        (await w.Db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == june.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task TheEditor_CannotConfirmTheirOwnEdit_AndTheAuditTrailKeepsBothVersions()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        Component(await w.As(w.FinanceB, finance: true).UpdateBenefit(w.Service, Car,
            new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default)).CashAllowance!.FinancePending.Should().BeTrue();

        var own = await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default);
        (Status(own), Error(own)).Should().Be(((int?)403, "maker_cannot_confirm"));
        Status(await w.As(w.FinanceA, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");

        var trail = await w.Db.AuditLogs.AsNoTracking().Where(a => a.TenantId == w.Tid && a.EntityName != null).OrderBy(a => a.CreatedAtUtc)
            .Select(a => new { a.Action, a.Metadata }).ToListAsync();
        var update = JsonDocument.Parse(trail.Single(a => a.Action == EntitlementMatrixService.AuditBenefitUpdated).Metadata!).RootElement;
        update.GetProperty("repended").GetBoolean().Should().BeTrue();
        update.GetProperty("before").GetProperty("isActive").GetBoolean().Should().BeTrue("the confirmed version");
        update.GetProperty("after").GetProperty("isActive").GetBoolean().Should().BeFalse("the version waiting for Finance");
        trail.Count(a => a.Action == EntitlementMatrixService.AuditBenefitConfirmed).Should().Be(2, "confirmed once as added, once after the edit");
        (await w.Db.PayComponents.AsNoTracking().SingleAsync(x => x.TenantId == w.Tid && x.Code == Car)).LastConfirmedTermsJson
            .Should().BeNull("once confirmed, the new version is the confirmed one: nothing is held back");
    }

    // ══ harness ═════════════════════════════════════════════════════════════════════════════════

    private static CreateBenefitRequest CashCar() => Contract("Car", b =>
    {
        b.Code = "CAR"; b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Allowance"; b.PolicyClass = "Contractual";
    });

    private sealed class World : IAsyncDisposable
    {
        public ZayraDbContext Db { get; private set; } = null!;
        private SqliteConnection _conn = null!;
        public Guid Tid { get; } = Guid.NewGuid();
        public Guid CompanyId { get; private set; }
        public Employee A { get; private set; } = null!;
        public Employee B { get; private set; } = null!;
        public EntitlementMatrixService Service { get; private set; } = null!;
        public readonly Guid Hr = Guid.NewGuid();
        public readonly Guid FinanceA = Guid.NewGuid();
        public readonly Guid FinanceB = Guid.NewGuid();

        public static async Task<World> Create()
        {
            var w = new World();
            (w.Db, w._conn) = PayrollProrationArrearsTests.CreateDb();
            (w.CompanyId, w.A, w.B) = await PayrollProrationArrearsTests.SeedAsync(w.Db, w.Tid);
            w.Db.TenantFeatureFlags.AddRange(
                new TenantFeatureFlag { TenantId = w.Tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true },
                new TenantFeatureFlag { TenantId = w.Tid, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = true });
            w.Db.GlDrivers.AddRange(Bd.Driver(w.Tid, CarDriver, "5210"), Bd.Driver(w.Tid, "EARN:CAR2", "5211"));
            await w.Db.SaveChangesAsync();
            w.Service = new EntitlementMatrixService(w.Db, new FixedTenantClock(Bd.Today));
            return w;
        }

        public EntitlementMatrixController As(Guid user, bool finance = false)
        {
            var claims = new List<Claim> { new("tenant_id", Tid.ToString()), new(ClaimTypes.NameIdentifier, user.ToString()), new(ClaimTypes.Role, "HR Director") };
            if (finance) claims.Add(new Claim("permission", "payroll.approve"));
            return new EntitlementMatrixController
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
            };
        }

        /// <summary>HR adds the car allowance and Finance user A confirms it.</summary>
        public async Task AddConfirmedCarAsync()
        {
            Component(await As(Hr).AddBenefit(Service, CashCar(), default));
            Status(await As(FinanceA, finance: true).ConfirmBenefit(Service, Car, null, default)).Should200("FinanceA confirms what HR added");
            Db.ChangeTracker.Clear();
        }

        public async Task<string> VersionAsync() =>
            ((EntitlementMatrixDto)((OkObjectResult)await As(Hr).Matrix(Service, null, Bd.Today, default)).Value!).Components.Single(c => c.Code == Car).Definition!.Version;

        public async Task GiveAsync(Employee e, decimal amount)
        {
            var salary = await Db.EmployeeSalaryStructures.FirstAsync(x => x.EmployeeId == e.Id);
            Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance { TenantId = Tid, SalaryStructureId = salary.Id, Code = Car, Amount = amount, Basis = AllowanceBases.Amount });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task<PayrollRun> RunAsync(int year, int month)
        {
            var run = PayrollProrationArrearsTests.AddRun(Db, Tid, CompanyId, year, month);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return run;
        }

        public async Task<IActionResult> Process(PayrollRun run)
        {
            var result = await PayrollProrationArrearsTests.Ctrl(Db, Tid).Process(run.Id, CancellationToken.None);
            Db.ChangeTracker.Clear();
            return result;
        }

        /// <summary>The run back to Draft, as Reopen leaves it, so it can be processed again with today's definitions.</summary>
        public async Task ReopenAsync(PayrollRun run)
        {
            var row = await Db.PayrollRuns.SingleAsync(r => r.Id == run.Id);
            row.Status = "Draft";
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>Sets a run's status as approval, lock or void leave it (the month counts as committed from Approved on).</summary>
        public async Task MarkAsync(PayrollRun run, string status)
        {
            var row = await Db.PayrollRuns.SingleAsync(r => r.Id == run.Id);
            row.Status = status;
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>The car allowance version in effect on a date, as stored (not payroll's view).</summary>
        public async Task<PayComponent?> InEffectAsync(DateOnly date) =>
            CashAllowanceDefinitions.InEffect((await CashAllowanceDefinitions.VersionsAsync(Db, Tid, [Car], default, companyId: CompanyId))[Car], date);

        public Task<PayrollEarning> LineAsync(PayrollRun run, Employee e) =>
            Db.PayrollEarnings.AsNoTracking().SingleAsync(x => x.PayrollRunId == run.Id && x.EmployeeId == e.Id && x.ComponentCode == Car);

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _conn.DisposeAsync(); }
    }
}
