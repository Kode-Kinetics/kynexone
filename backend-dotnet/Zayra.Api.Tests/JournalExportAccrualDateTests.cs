using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The ERP-facing accounting date of the payroll accrual is ALWAYS the last day of the payroll month. The
/// ledger EntryDate is the date the run was locked, and an ERP derives the posting period (and the
/// document date finance reads) from the line date, so a November run locked on 12 November used to
/// export as 2026-11-12 instead of 2026-11-30. Payment and remittance journals keep their own dates.
/// </summary>
public class JournalExportAccrualDateTests
{
    private static FinanceGlEntry Row(string eventType, string period, DateOnly entryDate, string module = "Payroll") => new()
    {
        TenantId = Guid.NewGuid(), SourceModule = module, SourceEntityId = Guid.NewGuid(), SourceEntityRef = "REF",
        EventType = eventType, DebitAccount = "5001 - Basic Salary Expense", CreditAccount = string.Empty,
        Amount = 100m, Currency = "SAR", Period = period, EntryDate = entryDate, Description = "line",
    };

    [Fact]
    public void AccrualLockedInsideThePeriod_ExportsOnThePeriodEnd()
    {
        var e = Row(GlEventTypes.Accrual, "2026-11", new DateOnly(2026, 11, 12));

        var line = JournalExportBuilder.Expand(new[] { e }, "CO").Single();

        line.AccountingDate.Should().Be(new DateOnly(2026, 11, 30));
        line.SystemPostedDate.Should().Be(new DateOnly(2026, 11, 12), "the raw posting date is still carried separately");
    }

    [Fact]
    public void AccrualLockedAfterThePeriod_StillExportsOnThePeriodEnd()
    {
        var e = Row(GlEventTypes.Accrual, "2026-11", new DateOnly(2026, 12, 3));

        JournalExportBuilder.Expand(new[] { e }, "CO").Single().AccountingDate.Should().Be(new DateOnly(2026, 11, 30));
    }

    [Fact]
    public void AccrualLockedOnTheLastDay_ExportsOnThePeriodEnd()
    {
        var e = Row(GlEventTypes.Accrual, "2026-02", new DateOnly(2026, 2, 28));

        JournalExportBuilder.Expand(new[] { e }, "CO").Single().AccountingDate.Should().Be(new DateOnly(2026, 2, 28));
    }

    [Theory]
    [InlineData(GlEventTypes.NetSettlement)]
    [InlineData(GlEventTypes.NetSettlementReversal)]
    [InlineData("PayrollRemit:GOSI")]
    [InlineData("PayrollRemitReversal:GOSI")]
    [InlineData(GlEventTypes.Void)]
    public void PaymentRemittanceAndContraJournals_KeepTheirOwnDate(string eventType)
    {
        // Settled/remitted on 20 Dec into the December period: the date must stay the payment date.
        var inPeriod = Row(eventType, "2026-12", new DateOnly(2026, 12, 20));
        JournalExportBuilder.Expand(new[] { inPeriod }, "CO").Single().AccountingDate.Should().Be(new DateOnly(2026, 12, 20));

        // And the pre-existing out-of-period rule is unchanged: clamped to the period end.
        var outOfPeriod = Row(eventType, "2026-11", new DateOnly(2026, 12, 3));
        JournalExportBuilder.Expand(new[] { outOfPeriod }, "CO").Single().AccountingDate.Should().Be(new DateOnly(2026, 11, 30));
    }

    // Everything posted as part of a payroll period's Lock is a month-end journal: the clearing rows that retire
    // a bonus / settlement payable and the EOSB provision movement are dated the period end like the accrual.
    [Theory]
    [InlineData(GlEventTypes.BonusPayrollClearing)]
    [InlineData(GlEventTypes.SettlementPayrollClearing)]
    [InlineData(GlEventTypes.EosbProvisionAccrual)]
    [InlineData(GlEventTypes.LeaveProvisionAccrual)]
    public void EveryJournalPostedAtLock_ExportsOnThePeriodEnd(string eventType)
    {
        var inPeriod = Row(eventType, "2026-11", new DateOnly(2026, 11, 12));
        var line = JournalExportBuilder.Expand(new[] { inPeriod }, "CO").Single();
        line.AccountingDate.Should().Be(new DateOnly(2026, 11, 30));
        line.SystemPostedDate.Should().Be(new DateOnly(2026, 11, 12));

        var afterPeriod = Row(eventType, "2026-11", new DateOnly(2026, 12, 3));
        JournalExportBuilder.Expand(new[] { afterPeriod }, "CO").Single().AccountingDate.Should().Be(new DateOnly(2026, 11, 30));
    }

    // Not part of a Lock: these happen when they happen, so an in-period posting keeps its own day.
    [Theory]
    [InlineData(GlEventTypes.SettlementAccrual)]
    [InlineData(GlEventTypes.SettlementAccrualReversal)]
    [InlineData(GlEventTypes.SettlementExternalPayment)]
    [InlineData(GlEventTypes.SettlementResidualReclass)]
    [InlineData(GlEventTypes.EosbProvisionConsumption)]
    [InlineData(GlEventTypes.EosbProvisionConsumptionReversal)]
    [InlineData(GlEventTypes.LeaveProvisionConsumption)]
    [InlineData(GlEventTypes.LeaveProvisionConsumptionReversal)]
    [InlineData(GlEventTypes.EosbActuarialTrueUp)]
    [InlineData(GlEventTypes.NetSettlementReclass)]
    [InlineData("PayrollRemitReclass:GOSI")]
    public void JournalsNotPostedAtLock_KeepTheirOwnDate(string eventType)
    {
        var e = Row(eventType, "2026-11", new DateOnly(2026, 11, 12));
        JournalExportBuilder.Expand(new[] { e }, "CO").Single().AccountingDate.Should().Be(new DateOnly(2026, 11, 12));
    }

    [Theory]
    [InlineData(GlEventTypes.BonusPayrollClearing)]
    [InlineData(GlEventTypes.EosbProvisionAccrual)]
    [InlineData(GlEventTypes.LeaveProvisionAccrual)]
    public void ALockEventFromAnotherModule_IsNotAffected(string eventType)
    {
        var e = Row(eventType, "2026-11", new DateOnly(2026, 11, 12), module: "Bonus");
        JournalExportBuilder.Expand(new[] { e }, "CO").Single().AccountingDate.Should().Be(new DateOnly(2026, 11, 12));
    }

    [Fact]
    public void AnAccrualEventFromAnotherModule_IsNotAffected()
    {
        var e = Row(GlEventTypes.Accrual, "2026-11", new DateOnly(2026, 11, 12), module: "Loan");

        JournalExportBuilder.Expand(new[] { e }, "CO").Single().AccountingDate.Should().Be(new DateOnly(2026, 11, 12));
    }
}
