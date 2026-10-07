using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A leaver paid by cheque whose cheque bounces. Reversing the recorded outside payment re-opened 2100 but
/// left the final settlement Paid, so the offboarding could be completed on money that never arrived and the
/// settlement could not be paid again. The reversal now returns the settlement to Disbursing (the state the
/// payment took it from), clears the checklist tick the payment set, audits it, and re-recording the payment
/// marks it Paid again.
/// </summary>
public class OutsidePaymentSettlementReversalTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ZayraDbContext Db, Guid Tenant, PayrollController Ops, Guid BatchId, Employee Cash,
        EmployeeFinalSettlement Settlement, EmployeeOffboarding Offboarding)> ArrangeAsync(string offboardingStatus = "InProgress")
    {
        var (db, tenant, runId, cash, settlement) = await KsaWpsReviewFixesTests.ArrangeCashRunAsync(
            NewDb(), Guid.NewGuid(), frozenAtLock: true, withSettlement: true);
        var offboarding = new EmployeeOffboarding
        {
            Id = settlement!.OffboardingId, TenantId = tenant, EmployeeId = cash.Id, EmployeeCode = cash.EmployeeCode,
            Status = offboardingStatus, FinalSettlementDone = offboardingStatus == "Completed",
        };
        db.EmployeeOffboardings.Add(offboarding);
        await db.SaveChangesAsync();

        var ops = KsaWpsReviewFixesTests.Payroll(db, tenant, Guid.NewGuid());
        var created = (CreatedResult)await ops.CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), default);
        var batchId = (Guid)created.Value!.GetType().GetProperty("Id")!.GetValue(created.Value)!;
        return (db, tenant, ops, batchId, cash, settlement, offboarding);
    }

    private static Task<EmployeeFinalSettlement> Reload(ZayraDbContext db, Guid id) =>
        db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(s => s.Id == id);

    [Fact]
    public async Task A_bounced_cheque_returns_the_paid_settlement_to_disbursing_and_re_recording_pays_it_again()
    {
        var (db, _, ops, batchId, cash, settlement, offboarding) = await ArrangeAsync();
        var cheque = new OutsidePaymentRequest(cash.Id, "Cheque", "CHQ-1", DateOnly.FromDateTime(DateTime.UtcNow));

        (await ops.RecordOutsidePayment(batchId, cheque, default)).Should().BeOfType<OkObjectResult>();
        var paid = await Reload(db, settlement.Id);
        paid.Status.Should().Be(FinalSettlementStatuses.Paid);
        paid.PaymentBatchId.Should().Be(batchId);
        (await db.EmployeeOffboardings.AsNoTracking().SingleAsync(o => o.Id == offboarding.Id)).FinalSettlementDone.Should().BeTrue();

        var reversed = await ops.ReverseOutsidePayment(batchId, cash.Id, new PayrollReasonRequest("Cheque bounced"), default);

        reversed.Should().BeOfType<OkObjectResult>();
        var reopened = await Reload(db, settlement.Id);
        reopened.Status.Should().Be(FinalSettlementStatuses.Disbursing, "the leaver was not paid: the cheque bounced");
        reopened.PaymentBatchId.Should().BeNull();
        reopened.PaidAtUtc.Should().BeNull();
        (await db.EmployeeOffboardings.AsNoTracking().SingleAsync(o => o.Id == offboarding.Id)).FinalSettlementDone
            .Should().BeFalse("the checklist tick was set by the payment that has just been reversed");
        db.PayrollAuditLogs.Should().Contain(a => a.Action == "payroll.final_settlement.payment_reversed"
            && a.EntityId == settlement.Id.ToString() && a.MetadataJson.Contains("Cheque bounced"));

        (await ops.RecordOutsidePayment(batchId, cheque with { Reference = "CHQ-2" }, default)).Should().BeOfType<OkObjectResult>();
        var repaid = await Reload(db, settlement.Id);
        repaid.Status.Should().Be(FinalSettlementStatuses.Paid, "recording the replacement cheque pays the settlement again");
        repaid.PaymentBatchId.Should().Be(batchId);
        (await db.EmployeeOffboardings.AsNoTracking().SingleAsync(o => o.Id == offboarding.Id)).FinalSettlementDone.Should().BeTrue();
    }

    [Fact]
    public async Task A_completed_offboarding_is_not_reopened_but_the_audit_row_says_so()
    {
        var (db, _, ops, batchId, cash, settlement, offboarding) = await ArrangeAsync("Completed");
        (await ops.RecordOutsidePayment(batchId, new OutsidePaymentRequest(cash.Id, "Cheque", "CHQ-9"), default))
            .Should().BeOfType<OkObjectResult>();

        (await ops.ReverseOutsidePayment(batchId, cash.Id, new PayrollReasonRequest("Cheque cancelled"), default))
            .Should().BeOfType<OkObjectResult>();

        (await Reload(db, settlement.Id)).Status.Should().Be(FinalSettlementStatuses.Disbursing);
        var off = await db.EmployeeOffboardings.AsNoTracking().SingleAsync(o => o.Id == offboarding.Id);
        off.Status.Should().Be("Completed");
        off.FinalSettlementDone.Should().BeTrue("a completed offboarding is history, not a checklist");
        db.PayrollAuditLogs.Should().Contain(a => a.Action == "payroll.final_settlement.payment_reversed"
            && a.MetadataJson.Contains("\"offboardingCompleted\":true"));
    }
}
