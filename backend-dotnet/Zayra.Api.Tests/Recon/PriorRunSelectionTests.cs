using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

public class PriorRunSelectionTests
{
    private sealed class Kit
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid CompanyId { get; } = Guid.NewGuid();
        public Guid OtherCompanyId { get; } = Guid.NewGuid();

        public PayrollRun Run(int year, int month, string status = "Processed", string type = PayrollRunTypes.Regular,
            Guid? company = null, Guid? tenant = null, Guid? parent = null)
        {
            var r = new PayrollRun
            {
                TenantId = tenant ?? TenantId, CompanyId = company ?? CompanyId, Year = year, Month = month, Status = status,
                RunType = type, ParentRunId = parent,
            };
            Db.PayrollRuns.Add(r);
            Db.SaveChanges();
            return r;
        }

        public Task<PriorRunSelection> Select(PayrollRun run, Guid? overrideId = null) =>
            PriorRunSelector.SelectAsync(Db, TenantId, run, overrideId, CancellationToken.None);
    }

    [Fact]
    public async Task TheImmediatelyPrecedingMonthsRegularRun_IsThePrior()
    {
        var k = new Kit();
        var prior = k.Run(2026, 9, "Locked");
        var run = k.Run(2026, 10);
        var sel = await k.Select(run);
        sel.Mode.Should().Be(ReconModes.Full);
        sel.Prior!.Id.Should().Be(prior.Id);
        sel.PriorVoided.Should().BeFalse();
    }

    [Fact]
    public async Task ADecemberPrior_IsFoundForAJanuaryRun()
    {
        var k = new Kit();
        var prior = k.Run(2025, 12, "Approved");
        var sel = await k.Select(k.Run(2026, 1));
        sel.Prior!.Id.Should().Be(prior.Id);
    }

    [Fact]
    public async Task AReplacementRun_IsAPeriodOwningPrior_ThisWasABugInTheOldLookup()
    {
        var k = new Kit();
        var voided = k.Run(2026, 9, "Voided");
        var replacement = k.Run(2026, 9, "Locked", PayrollRunTypes.Replacement, parent: voided.Id);
        var sel = await k.Select(k.Run(2026, 10));
        sel.Mode.Should().Be(ReconModes.Full);
        sel.Prior!.Id.Should().Be(replacement.Id);
    }

    [Fact]
    public async Task OnlyAVoidedPrior_IsNoPrior_AndSaysItWasVoided()
    {
        var k = new Kit();
        k.Run(2026, 9, "Voided");
        var sel = await k.Select(k.Run(2026, 10));
        sel.Mode.Should().Be(ReconModes.NoPrior);
        sel.Prior.Should().BeNull();
        sel.PriorVoided.Should().BeTrue();
    }

    [Fact]
    public async Task ADraftPrior_IsNotAComparison()
    {
        var k = new Kit();
        k.Run(2026, 9, "Draft");
        var sel = await k.Select(k.Run(2026, 10));
        sel.Mode.Should().Be(ReconModes.NoPrior);
        sel.PriorVoided.Should().BeFalse();
    }

    [Fact]
    public async Task AGapInTheCalendar_IsNoPrior_AndReportsTheLastRunPeriod()
    {
        var k = new Kit();
        k.Run(2026, 7, "Locked");
        k.Run(2026, 6, "Locked");
        var sel = await k.Select(k.Run(2026, 10));
        sel.Mode.Should().Be(ReconModes.NoPrior);
        sel.LastRunPeriod.Should().Be("2026-07");
    }

    [Fact]
    public async Task AnOffCycleRun_IsNeverThePrior()
    {
        var k = new Kit();
        k.Run(2026, 9, "Locked", PayrollRunTypes.OffCycle);
        k.Run(2026, 9, "Locked", PayrollRunTypes.Supplementary);
        var sel = await k.Select(k.Run(2026, 10));
        sel.Mode.Should().Be(ReconModes.NoPrior);
    }

    [Fact]
    public async Task AnotherCompanysRun_IsNeverThePrior()
    {
        var k = new Kit();
        k.Run(2026, 9, "Locked", company: k.OtherCompanyId);
        var sel = await k.Select(k.Run(2026, 10));
        sel.Mode.Should().Be(ReconModes.NoPrior);
    }

    [Fact]
    public async Task AnotherTenantsRun_IsNeverThePrior()
    {
        var k = new Kit();
        k.Run(2026, 9, "Locked", tenant: Guid.NewGuid());
        var sel = await k.Select(k.Run(2026, 10));
        sel.Mode.Should().Be(ReconModes.NoPrior);
    }

    [Theory]
    [InlineData("Processed", true)]
    [InlineData("PendingFinanceReview", true)]
    [InlineData("Approved", false)]
    [InlineData("Locked", false)]
    [InlineData("Paid", false)]
    public async Task APriorThatIsNotFinalYet_IsMarked(string priorStatus, bool notFinal)
    {
        var k = new Kit();
        k.Run(2026, 9, priorStatus);
        var sel = await k.Select(k.Run(2026, 10));
        sel.Mode.Should().Be(ReconModes.Full);
        sel.PriorNotFinal.Should().Be(notFinal);
    }

    // ── The priorRunId override ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnOverride_ToAnEarlierPeriodOwningRunOfTheSameCompany_IsAccepted()
    {
        var k = new Kit();
        var gapPrior = k.Run(2026, 6, "Locked");
        var sel = await k.Select(k.Run(2026, 10), gapPrior.Id);
        sel.Mode.Should().Be(ReconModes.Full);
        sel.Prior!.Id.Should().Be(gapPrior.Id);
        sel.OverrideError.Should().BeNull();
    }

    [Fact]
    public async Task AnOverride_BeatsTheDefaultPrior()
    {
        var k = new Kit();
        k.Run(2026, 9, "Locked");
        var chosen = k.Run(2026, 8, "Locked");
        var sel = await k.Select(k.Run(2026, 10), chosen.Id);
        sel.Prior!.Id.Should().Be(chosen.Id);
    }

    [Theory]
    [InlineData("other-company")]
    [InlineData("other-tenant")]
    [InlineData("later")]
    [InlineData("same-period")]
    [InlineData("off-cycle")]
    [InlineData("voided")]
    [InlineData("draft")]
    [InlineData("missing")]
    public async Task AnInvalidOverride_IsRefused_AndNeverSilentlyReplacedByTheDefault(string kind)
    {
        var k = new Kit();
        k.Run(2026, 9, "Locked"); // a perfectly good default prior exists
        var run = k.Run(2026, 10);
        var candidate = kind switch
        {
            "other-company" => k.Run(2026, 8, "Locked", company: k.OtherCompanyId).Id,
            "other-tenant" => k.Run(2026, 8, "Locked", tenant: Guid.NewGuid()).Id,
            "later" => k.Run(2026, 12, "Locked").Id,
            "same-period" => k.Run(2026, 10, "Locked", PayrollRunTypes.Replacement).Id,
            "off-cycle" => k.Run(2026, 8, "Locked", PayrollRunTypes.OffCycle).Id,
            "voided" => k.Run(2026, 8, "Voided").Id,
            "draft" => k.Run(2026, 8, "Draft").Id,
            _ => Guid.NewGuid(),
        };
        var sel = await k.Select(run, candidate);
        sel.OverrideError.Should().Be("prior_run_invalid");
        sel.Prior.Should().BeNull();
    }

    [Fact]
    public async Task TheRunItself_CannotBeItsOwnPrior()
    {
        var k = new Kit();
        var run = k.Run(2026, 10);
        (await k.Select(run, run.Id)).OverrideError.Should().Be("prior_run_invalid");
    }
}
