using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>Re-check of the split at confirmation: a confirmed switch-off leaves no inactive version behind, a refusal names a way out that works, and the
/// closed version says why it ends where it does.</summary>
public partial class CashAllowanceRependKeepsPayingTests
{
    private static readonly System.Text.RegularExpressions.Regex Arabic = new("[؀-ۿ]");

    private static JsonElement BodyOf(IActionResult r) => JsonSerializer.SerializeToElement((r as ObjectResult)?.Value, Web);

    [Fact]
    public async Task ASwitchOffConfirmedAcrossACommittedMonth_LeavesOneActiveVersion_ThatCanBeSwitchedBackOnAndRedated()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default));
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        await w.MarkAsync(june, "Approved");
        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");

        var rows = await w.Db.PayComponents.AsNoTracking().Where(x => x.TenantId == w.Tid && x.Code == Car).ToListAsync();
        rows.Should().ContainSingle("a switch-off adds no version: the confirmed one simply ends with the committed month");
        (rows[0].IsActive, rows[0].EffectiveTo).Should().Be((true, (DateOnly?)new DateOnly(2026, 6, 30)));
        (await w.InEffectAsync(July1)).Should().BeNull("off from July");

        // Switched back on from July (re-dated: the end is lifted): allowed, and waits for Finance like any money change.
        var back = await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = true, EffectiveTo = null }, default);
        Status(back).Should200(Describe(back));
        Status(await w.As(w.FinanceA, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");
        (await w.InEffectAsync(July1)).Should().NotBeNull("back on from July");
        (await w.InEffectAsync(June1))!.GlDriverKey.Should().Be(CarDriver, "June keeps what it was paid on");
    }

    [Fact]
    public async Task AChangeThatOnlyRewritesCommittedMonths_IsRefused_AndTheWayOutTheRefusalNamesWorks()
    {
        await using var w = await World.Create();
        // Confirmed from August; HR moves it to May only (before any month is committed); then June is approved.
        Component(await w.As(w.Hr).AddBenefit(w.Service, Contract("Car", b =>
        {
            b.Code = "CAR"; b.Delivery = "SalaryAllowance"; b.Frequency = "Monthly"; b.GlDriverKey = CarDriver; b.Category = "Allowance"; b.PolicyClass = "Contractual";
            b.EffectiveFrom = new DateOnly(2026, 8, 1);
        }), default));
        Status(await w.As(w.FinanceA, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null)
            { EffectiveFrom = new DateOnly(2026, 5, 1), EffectiveTo = new DateOnly(2026, 5, 31) }, default));
        await w.MarkAsync(await w.RunAsync(2026, 6), "Approved");

        var refused = await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default);
        (Status(refused), Error(refused)).Should().Be(((int?)409, "period_committed"));
        BodyOf(refused).GetProperty("message").GetString().Should().Contain("Change it again so it starts in 2026-07 or later");
        Arabic.IsMatch(BodyOf(refused).GetProperty("messageAr").GetString()!).Should().BeTrue();

        // The way out: change it to start from July. Compared with what payroll paid (nothing before August), no committed month moves.
        var moved = await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null)
            { EffectiveFrom = July1, EffectiveTo = null }, default);
        Status(moved).Should200(Describe(moved));
        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");
        (await w.InEffectAsync(July1)).Should().NotBeNull();
        (await w.InEffectAsync(new DateOnly(2026, 5, 1))).Should().BeNull("May was never paid and stays unpaid");
    }

    [Fact]
    public async Task AStartMonthTakenByAnotherVersion_IsRefused_SayingItClearsOnceThatMonthIsApproved()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        Status(await w.As(w.FinanceA, finance: true).CorrectBenefit(w.Service, Car, new CorrectAllowanceRequest(June1, "Re-route", GlDriverKey: "EARN:CAR2"), default)).Should200("");
        await w.MarkAsync(await w.RunAsync(2026, 6), "Approved");
        w.Db.PayComponents.Add(new PayComponent
        {
            TenantId = w.Tid, CompanyId = null, IsOffered = true, Code = Car, NameEn = "Car", NameAr = "سيارة", ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
            GlDriverKey = CarDriver, IsActive = true, IsDeleted = true, EffectiveFrom = July1,
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        var refused = await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default);

        (Status(refused), Error(refused)).Should().Be(((int?)409, "period_committed"));
        BodyOf(refused).GetProperty("message").GetString().Should().Contain("can be confirmed once payroll for 2026-07 is approved");
        Arabic.IsMatch(BodyOf(refused).GetProperty("messageAr").GetString()!).Should().BeTrue();
    }

    [Fact]
    public async Task TheClosedVersion_IsExplainedInTheConfirmationEntry_NamingTheMonthAndTheConfirmer()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        Status(await w.As(w.FinanceA, finance: true).CorrectBenefit(w.Service, Car, new CorrectAllowanceRequest(June1, "Re-route", GlDriverKey: "EARN:CAR2"), default)).Should200("");
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        await w.MarkAsync(june, "Approved");

        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");

        var entry = JsonDocument.Parse((await w.Db.AuditLogs.AsNoTracking().Where(a => a.Action == EntitlementMatrixService.AuditBenefitConfirmed)
            .OrderByDescending(a => a.CreatedAtUtc).FirstAsync()).Metadata!).RootElement;
        entry.GetProperty("note").GetString().Should().Be($"Closed at the end of 2026-06 when the pending change was confirmed by {w.FinanceB}.");
        Arabic.IsMatch(entry.GetProperty("noteAr").GetString()!).Should().BeTrue();
    }
}
