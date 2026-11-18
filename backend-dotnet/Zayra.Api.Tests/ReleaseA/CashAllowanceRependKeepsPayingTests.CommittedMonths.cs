using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Confirming a change that waited for Finance never rewrites a month already approved, locked or paid: payroll paid those months on the terms Finance
/// last confirmed, so they keep them, and the change applies from the month after the latest committed one (two dated versions).
/// </summary>
public partial class CashAllowanceRependKeepsPayingTests
{
    private static readonly DateOnly July1 = new(2026, 7, 1);

    [Fact]
    public async Task ACorrectionConfirmedAfterItsFirstMonthWasApproved_LeavesThatMonthOnTheOldTerms_AndAppliesFromTheNextMonth()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        Status(await w.As(w.FinanceA, finance: true).CorrectBenefit(w.Service, Car,
            new CorrectAllowanceRequest(June1, "Re-route and leave out of end of service", GlDriverKey: "EARN:CAR2", EosbIncluded: false, EosbExclusionCategory: "Reimbursement"), default))
            .Should200("Finance corrects from June");
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        await w.MarkAsync(june, "Approved");

        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("another Finance user confirms the correction");

        var juneTerms = await w.InEffectAsync(June1);
        (juneTerms!.GlDriverKey, juneTerms.EosbIncluded, juneTerms.FinanceConfirmedAtUtc is not null).Should().Be((CarDriver, true, true), "June was approved on these terms");
        var julyTerms = await w.InEffectAsync(July1);
        (julyTerms!.GlDriverKey, julyTerms.EosbIncluded, julyTerms.FinanceConfirmedAtUtc is not null).Should().Be(("EARN:CAR2", false, true), "the correction applies from July");
        var salaryId = (await w.Db.EmployeeSalaryStructures.AsNoTracking().FirstAsync(x => x.EmployeeId == w.A.Id)).Id;
        (await SalaryAllowanceLines.EosbIncludedAsync(w.Db, w.Tid, [salaryId], June1, default))[salaryId].Should().ContainSingle(x => x.Code == Car);
        (await SalaryAllowanceLines.EosbIncludedAsync(w.Db, w.Tid, [salaryId], July1, default)).Should().NotContainKey(salaryId);
        (await PayrollRunDefinitionStaleness.FindAsync(w.Db, w.Tid, await w.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == june.Id), default))
            .Should().BeNull("the approved run's month kept the terms it was paid on");
        var confirmed = JsonDocument.Parse((await w.Db.AuditLogs.AsNoTracking().Where(a => a.Action == EntitlementMatrixService.AuditBenefitConfirmed)
            .OrderByDescending(a => a.CreatedAtUtc).FirstAsync()).Metadata!).RootElement;
        confirmed.GetProperty("committedThrough").GetString().Should().Be("2026-06-01");
    }

    [Theory]
    [InlineData("end")]
    [InlineData("off")]
    public async Task AnEndDateOrSwitchOffConfirmedAfterAMonthWasApproved_LeavesThatMonthPaid_AndStopsFromTheNextMonth(string change)
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        var edit = change == "end"
            ? new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { EffectiveTo = new DateOnly(2026, 5, 31) }
            : new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false };
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, edit, default)).CashAllowance!.FinancePending.Should().BeTrue();
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        (await w.LineAsync(june, w.A)).Amount.Should().Be(500m, "June was paid on the last confirmed terms");
        await w.MarkAsync(june, "Locked");

        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");

        var juneTerms = await w.InEffectAsync(June1);
        juneTerms.Should().NotBeNull("June was paid, and stays paid");
        (juneTerms!.GlDriverKey, juneTerms.FinanceConfirmedAtUtc is not null).Should().Be((CarDriver, true));
        (await w.InEffectAsync(July1)).Should().BeNull("the change applies from the month after the latest committed one");
        (await PayrollRunDefinitionStaleness.FindAsync(w.Db, w.Tid, await w.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == june.Id), default)).Should().BeNull();
    }

    [Fact]
    public async Task TwoEditsBeforeTheReconfirm_KeepTheFirstConfirmedTerms_ForTheCommittedMonths()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { EffectiveTo = new DateOnly(2026, 8, 31) }, default));
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default));
        var pending = await w.Db.PayComponents.AsNoTracking().SingleAsync(x => x.TenantId == w.Tid && x.Code == Car);
        var kept = CashAllowanceDefinitions.KeptTerms(pending)!;
        (kept.IsActive, kept.EffectiveTo).Should().Be((true, (DateOnly?)null), "the second edit keeps the terms Finance confirmed, not the first edit's");
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        await w.MarkAsync(june, "Approved");

        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");

        (await w.InEffectAsync(June1)).Should().NotBeNull();
        (await w.InEffectAsync(July1)).Should().BeNull("switched off from July");
        (await w.InEffectAsync(new DateOnly(2026, 8, 1))).Should().BeNull();
    }

    [Fact]
    public async Task AChangeConfirmedBeforeAnyMonthItReachesIsCommitted_IsOneVersion_AsBefore()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default));
        var may = await w.RunAsync(2026, 5);
        await w.MarkAsync(may, "Voided");     // a voided run commits no month

        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");

        (await w.Db.PayComponents.AsNoTracking().CountAsync(x => x.TenantId == w.Tid && x.Code == Car)).Should().Be(1);
        (await w.InEffectAsync(June1)).Should().BeNull();
    }
}
