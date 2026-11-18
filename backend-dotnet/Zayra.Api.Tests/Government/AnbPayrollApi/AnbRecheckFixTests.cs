using System.Net;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// Security re-check of cef5ac176: R-1 (lines with itemCount 0 skipped the batch binding), R-2 (createdAt
/// is not a required spec field, so a payroll-level date must be accepted as the fallback). R-4 (the client's own host-suffix
/// allow-list) is gone with the list: hosts now come only from GovernmentHttp's per-environment allow-list (AnbTransportHardeningTests).
/// </summary>
public class AnbRecheckFixTests
{
    private static AnbStatusLine Bank(AnbPaymentInput r, string status, DateTimeOffset? createdAt) =>
        new(r.EmployeeNationalId!, r.AccountNumber!, r.SalaryAmount, r.BicCode, r.EmployeeName, status == "PAID" ? "P" + r.EmployeeRef : null,
            status, null, null, createdAt, null);

    // ── R-1 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mapper_LinesWithAnItemCountOfZero_AreRefused_NotMapped()
    {
        var batch = BuildBatch(2);
        var lastMonth = new AnbPayrollStatusSnapshot(PayrollId, "COMPLETED", null,
            batch.Rows.Select(r => Bank(r, "PAID", IntentAt.AddDays(-30))).ToList(), ItemCount: 0, 1, 0, Complete: false);

        var act = () => AnbPayrollStatusMapper.Map(lastMonth, Binding(batch));

        act.Should().Throw<BankChannelException>().Which.Code.Should().Be(BankChannelCodes.PayrollNotThisBatch);
    }

    [Fact]
    public void Mapper_AnEmptySnapshotWithAnItemCountOfZero_IsStillJustIncomplete()
    {
        var batch = BuildBatch(2);
        var empty = new AnbPayrollStatusSnapshot(PayrollId, "ACCEPTED", null, Array.Empty<AnbStatusLine>(), 0, 1, 0, false);
        var mapped = AnbPayrollStatusMapper.Map(empty, Binding(batch));
        mapped.Complete.Should().BeFalse();
        mapped.MissingFromBank.Should().HaveCount(2);
    }

    [Fact]
    public async Task Client_APageWithLinesButAnItemCountOfZero_IsInconsistent()
    {
        var batch = BuildBatch(2);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK,
            Page("COMPLETED", batch.Rows.Select(r => Line(r, "PAID", "P" + r.EmployeeRef)).ToList(), 1, 50, 0)));

        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);

        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollInconsistent);
    }

    // ── R-2 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mapper_ALineWithoutCreatedAt_FallsBackToThePayrollCreationDate()
    {
        var batch = BuildBatch(2);
        var snap = new AnbPayrollStatusSnapshot(PayrollId, "COMPLETED", null,
            batch.Rows.Select(r => Bank(r, "PAID", null)).ToList(), 2, 1, 0, true, PayrollCreatedAt: IntentAt.AddMinutes(2));

        var mapped = AnbPayrollStatusMapper.Map(snap, Binding(batch));

        mapped.Rows.Should().HaveCount(2);
        mapped.Complete.Should().BeTrue();
    }

    [Fact]
    public void Mapper_APayrollCreationDateOlderThanTheIntent_IsRefused()
    {
        var batch = BuildBatch(2);
        var snap = new AnbPayrollStatusSnapshot(PayrollId, "COMPLETED", null,
            batch.Rows.Select(r => Bank(r, "PAID", null)).ToList(), 2, 1, 0, true, PayrollCreatedAt: IntentAt.AddDays(-30));

        var act = () => AnbPayrollStatusMapper.Map(snap, Binding(batch));
        act.Should().Throw<BankChannelException>().Which.Code.Should().Be(BankChannelCodes.PayrollNotThisBatch);
    }

    [Fact]
    public void Mapper_ALineDateStillWinsOverThePayrollDate()
    {
        // The payroll date is recent but one line says it was created last month: another payroll's line.
        var batch = BuildBatch(2);
        var snap = new AnbPayrollStatusSnapshot(PayrollId, "COMPLETED", null,
            new[] { Bank(batch.Rows[0], "PAID", IntentAt.AddDays(-30)), Bank(batch.Rows[1], "PAID", null) }, 2, 1, 0, true,
            PayrollCreatedAt: IntentAt.AddMinutes(2));

        var act = () => AnbPayrollStatusMapper.Map(snap, Binding(batch));
        act.Should().Throw<BankChannelException>().Which.Code.Should().Be(BankChannelCodes.PayrollNotThisBatch);
    }

    [Fact]
    public void Mapper_NoLineDateAndNoPayrollDate_IsRefused()
    {
        var batch = BuildBatch(1);
        var snap = new AnbPayrollStatusSnapshot(PayrollId, "COMPLETED", null, new[] { Bank(batch.Rows[0], "PAID", null) }, 1, 1, 0, true);
        var act = () => AnbPayrollStatusMapper.Map(snap, Binding(batch));
        act.Should().Throw<BankChannelException>().Which.Code.Should().Be(BankChannelCodes.PayrollNotThisBatch);
    }

    [Fact]
    public async Task Client_ReadsThePayrollLevelCreatedAt_SoLinesWithoutOneStillMap()
    {
        var batch = BuildBatch(2);
        var lines = batch.Rows.Select(r => (object)new
        {
            employeeId = r.EmployeeNationalId, employeeAccountNumber = r.AccountNumber, salaryAmount = r.SalaryAmount,
            bicCode = r.BicCode, employeeName = r.EmployeeName, paymentId = "P" + r.EmployeeRef, paymentStatus = "PAID",
        }).ToList();
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK,
            Page("COMPLETED", lines, 1, 50, 2, createdAt: "2026-11-27T05:30:00Z")));
        IBankPayrollChannel channel = new AnbPayrollChannel(Client(handler, out _));

        var snapshot = await channel.GetStatusAsync(TestScope, PayrollId, Binding(batch), CancellationToken.None);

        snapshot.Rows.Should().HaveCount(2);
        snapshot.Complete.Should().BeTrue();
    }
}
