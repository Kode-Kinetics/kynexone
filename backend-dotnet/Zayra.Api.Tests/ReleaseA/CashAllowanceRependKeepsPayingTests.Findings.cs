using FluentAssertions;
using Zayra.Api.Infrastructure.Entitlements;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>A run that pays an allowance on its held-back terms says so, once per allowance, as a Warning that never stops the run.</summary>
public partial class CashAllowanceRependKeepsPayingTests
{
    private static Task<List<PayrollValidationResult>> HeldBackFindingsAsync(World w, PayrollRun run) =>
        w.Db.PayrollValidationResults.AsNoTracking().Where(v => v.PayrollRunId == run.Id && v.Code == CashAllowanceDefinitions.HeldBackWarningCode).ToListAsync();

    [Fact]
    public async Task ARunPayingHeldBackTerms_WarnsOncePerAllowance_NamingItAndSinceWhen_AndTheWarningSurvivesARefresh()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        await w.GiveAsync(w.B, 300m);
        Status(await w.As(w.FinanceA, finance: true).CorrectBenefit(w.Service, Car, new CorrectAllowanceRequest(June1, "Re-route", GlDriverKey: "EARN:CAR2"), default)).Should200("");
        var june = await w.RunAsync(2026, 6);

        (await w.Process(june)).Should().BeOfType<OkObjectResult>();

        var finding = (await HeldBackFindingsAsync(w, june)).Should().ContainSingle("one per allowance, not one per employee").Subject;
        finding.Severity.Should().Be("Warning");
        finding.Message.Should().Contain("Car (ALW_CAR): a change is waiting for Finance since " + DateTime.UtcNow.ToString("yyyy-MM-dd"))
            .And.Contain("last confirmed terms");
        (await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Validate(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        w.Db.ChangeTracker.Clear();
        (await HeldBackFindingsAsync(w, june)).Should().ContainSingle();

        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");
        await w.ReopenAsync(june);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        (await HeldBackFindingsAsync(w, june)).Should().BeEmpty("nothing is held back once the change is confirmed");
    }
}
