using System.Net;
using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// A status read is applied to ONE batch. The snapshot must fit that batch (count, total, dates), lines we
/// sent that the bank does not list are reported, a rejected payroll fails its lines, a completed payroll
/// pays nothing by itself, paging is cross-checked and time-boxed.
/// </summary>
public class AnbStatusBindingTests
{
    private static AnbStatusLine Bank(AnbPaymentInput r, string status, string? paymentId = null, decimal? amount = null,
        DateTimeOffset? createdAt = null, bool noCreatedAt = false, string? description = null) =>
        new(r.EmployeeNationalId!, r.AccountNumber!, amount ?? r.SalaryAmount, r.BicCode, r.EmployeeName, paymentId, status,
            description, paymentId is null ? null : "TRX" + paymentId, noCreatedAt ? null : createdAt ?? IntentAt.AddHours(1), null);

    private static AnbPayrollStatusSnapshot Snapshot(string status, IReadOnlyList<AnbStatusLine> lines, int itemCount,
        string id = PayrollId) =>
        new(id, status, null, lines, itemCount, 1, 0,
            itemCount > 0 && lines.Select(l => (l.EmployeeId, l.EmployeeAccountNumber)).Distinct().Count() == itemCount);

    // ── Binding to the batch ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnotherPayrollWithTheSamePeopleButADifferentCount_IsRefused()
    {
        var batch = BuildBatch(3);
        var lastMonth = Snapshot("COMPLETED", batch.Rows.Take(2).Select(r => Bank(r, "PAID", "P")).ToList(), 2);
        var act = () => AnbPayrollStatusMapper.Map(lastMonth, Binding(batch));
        act.Should().Throw<BankChannelException>().Which.Code.Should().Be(BankChannelCodes.PayrollNotThisBatch);
    }

    [Fact]
    public void AnotherPayrollWithTheSamePeopleAndCountButOtherAmounts_IsRefused()
    {
        var batch = BuildBatch(3);
        var lastMonth = Snapshot("COMPLETED", batch.Rows.Select(r => Bank(r, "PAID", "P", r.SalaryAmount - 100m)).ToList(), 3);
        var act = () => AnbPayrollStatusMapper.Map(lastMonth, Binding(batch));
        act.Should().Throw<BankChannelException>().Which.Code.Should().Be(BankChannelCodes.PayrollNotThisBatch);
    }

    [Fact]
    public void AnotherPayrollCreatedBeforeThisBatchWasSubmitted_IsRefused()
    {
        var batch = BuildBatch(3);
        var older = Snapshot("COMPLETED", batch.Rows.Select(r => Bank(r, "PAID", "P", createdAt: IntentAt.AddDays(-30))).ToList(), 3);
        var act = () => AnbPayrollStatusMapper.Map(older, Binding(batch));
        act.Should().Throw<BankChannelException>().Which.Code.Should().Be(BankChannelCodes.PayrollNotThisBatch);
    }

    [Fact]
    public void ALineWithoutACreationDate_CannotBeProvedToBelongToThisBatch()
    {
        var batch = BuildBatch(2);
        var snap = Snapshot("COMPLETED", new[] { Bank(batch.Rows[0], "PAID", "P"), Bank(batch.Rows[1], "PAID", "P", noCreatedAt: true) }, 2);
        var act = () => AnbPayrollStatusMapper.Map(snap, Binding(batch));
        act.Should().Throw<BankChannelException>().Which.Code.Should().Be(BankChannelCodes.PayrollNotThisBatch);
    }

    [Fact]
    public void ASmallClockSkewBeforeTheIntent_IsTolerated()
    {
        var batch = BuildBatch(1);
        var snap = Snapshot("COMPLETED", new[] { Bank(batch.Rows[0], "PAID", "P", createdAt: IntentAt.AddMinutes(-4)) }, 1);
        AnbPayrollStatusMapper.Map(snap, Binding(batch)).Rows.Should().ContainSingle();
    }

    // ── Lines the bank did not list ─────────────────────────────────────────────────────────────

    [Fact]
    public void ZeroItems_WhileWeSentLines_IsNotComplete_AndEveryLineIsMissing()
    {
        var batch = BuildBatch(3);
        var mapped = AnbPayrollStatusMapper.Map(Snapshot("ACCEPTED", Array.Empty<AnbStatusLine>(), 0) with { Complete = true }, Binding(batch));

        mapped.Complete.Should().BeFalse();
        mapped.Rows.Should().BeEmpty();
        mapped.MissingFromBank.Should().Equal(WpsReference(1), WpsReference(2), WpsReference(3));
    }

    [Fact]
    public void ALineTheBankDidNotList_IsReportedMissing_AndTheSnapshotIsIncomplete()
    {
        var batch = BuildBatch(3);
        var snap = Snapshot("IN_PROGRESS", new[] { Bank(batch.Rows[0], "PAID", "P1"), Bank(batch.Rows[2], "PENDING") }, 3);
        var mapped = AnbPayrollStatusMapper.Map(snap, Binding(batch));

        mapped.MissingFromBank.Should().Equal(WpsReference(2));
        mapped.Complete.Should().BeFalse();
        mapped.Rows.Should().HaveCount(2);
    }

    // ── Payroll-level outcomes ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("FAILED")]
    public void ARejectedPayroll_FailsItsUnsettledLines_NeverLeavesThemPending(string payrollStatus)
    {
        var batch = BuildBatch(3);
        var snap = Snapshot(payrollStatus, batch.Rows.Select(r => Bank(r, "INITIATED")).ToList(), 3);

        var mapped = AnbPayrollStatusMapper.Map(snap, Binding(batch));

        mapped.Rows.Should().HaveCount(3).And.OnlyContain(r => r.Outcome == BankConfirmationOutcomes.Returned);
        mapped.Rows.Should().OnlyContain(r => r.ReasonCode == "PAYROLL_" + payrollStatus);
        mapped.Rows.Should().OnlyContain(r => r.RawOutcome == "INITIATED", "the bank's own line word is kept");
    }

    [Fact]
    public void APaidLineInsideARejectedPayroll_IsKeptAsTheBankSaid_AndFlagged()
    {
        var batch = BuildBatch(2);
        var snap = Snapshot("REJECTED", new[] { Bank(batch.Rows[0], "PAID", "P1"), Bank(batch.Rows[1], "INITIATED") }, 2);
        var mapped = AnbPayrollStatusMapper.Map(snap, Binding(batch));

        mapped.Rows.Single(r => r.WpsReference == WpsReference(1)).Outcome.Should().Be(BankConfirmationOutcomes.Paid);
        mapped.Anomalies.Should().Contain(new BankLineAnomaly(WpsReference(1), "paid_in_rejected_payroll"));
    }

    [Fact]
    public void ACompletedPayroll_NeverMarksALinePaid_ThatTheBankDidNotCallPaid()
    {
        var batch = BuildBatch(3);
        var snap = Snapshot("COMPLETED", new[] { Bank(batch.Rows[0], "PAID", "P1"), Bank(batch.Rows[1], "PENDING") }, 3);

        var mapped = AnbPayrollStatusMapper.Map(snap, Binding(batch));

        mapped.Rows.Single(r => r.WpsReference == WpsReference(2)).Outcome.Should().Be(BankConfirmationOutcomes.Pending);
        mapped.Rows.Should().NotContain(r => r.WpsReference == WpsReference(3));
        mapped.MissingFromBank.Should().Equal(WpsReference(3));
        mapped.Complete.Should().BeFalse();
    }

    // ── Rendering for the existing import ───────────────────────────────────────────────────────

    [Fact]
    public void TheConfirmationFileCarriesTheBankPayrollId_SoTwoPayrollsNeverCollide()
    {
        var batch = BuildBatch(2);
        var snap = Snapshot("IN_PROGRESS", batch.Rows.Select(r => Bank(r, "PENDING")).ToList(), 2);
        var rows = AnbPayrollStatusMapper.Map(snap, Binding(batch)).Rows;

        var a = BankChannelConfirmationFile.Render("payroll-a", rows);
        var b = BankChannelConfirmationFile.Render("payroll-b", rows);

        BankConfirmationService.Sha256(a).Should().NotBe(BankConfirmationService.Sha256(b));
        new GenericCsvBankConfirmationParser().Parse(a).Errors.Should().BeEmpty("the parser ignores the extra column");
    }

    [Fact]
    public void ConflictingTerminalWords_ReachTheParserAsTwoRows_SoTheImportRefusesThatEmployee()
    {
        var batch = BuildBatch(2);
        var snap = Snapshot("COMPLETED",
            new[] { Bank(batch.Rows[0], "PAID", "P1"), Bank(batch.Rows[0], "FAILED"), Bank(batch.Rows[1], "PAID", "P2") }, 2);

        var parsed = new GenericCsvBankConfirmationParser().Parse(
            BankChannelConfirmationFile.Render(PayrollId, AnbPayrollStatusMapper.Map(snap, Binding(batch)).Rows));

        parsed.Rows.Where(r => r.WpsReference == WpsReference(1)).Select(r => r.Outcome)
            .Should().BeEquivalentTo(new[] { BankConfirmationOutcomes.Paid, BankConfirmationOutcomes.Returned });
    }

    [Fact]
    public void ARepeatedSubmittedPair_IsAProgrammingError()
    {
        var batch = BuildBatch(2);
        var binding = Binding(batch) with { Lines = batch.Submitted.Append(batch.Submitted[0]).ToList() };
        var act = () => AnbPayrollStatusMapper.Map(Snapshot("COMPLETED", batch.Rows.Select(r => Bank(r, "PAID", "P")).ToList(), 2), binding);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TheEmployeesNameEchoedInTheBankDescription_IsRemoved()
    {
        var batch = BuildBatch(1);
        var name = batch.Rows[0].EmployeeName!;
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("COMPLETED", new[] { Bank(batch.Rows[0], "FAILED", description: $"Beneficiary {name} account closed") }, 1), Binding(batch));
        mapped.Rows.Single().ReasonText.Should().NotContain(name).And.Contain("account closed");
    }

    // ── Paging cross-checks and the read deadline (client) ──────────────────────────────────────

    [Fact]
    public async Task APageCountThatDoesNotFitTheItemCount_IsRefusedAfterThePageItArrivedOn()
    {
        var batch = BuildBatch(1);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new
        {
            id = PayrollId, status = "IN_PROGRESS", reason = "", payrollId = PayrollId,
            data = new[] { Line(batch.Rows[0], "PENDING") },
            meta = new { page = 1, take = 50, itemCount = 1, pageCount = 2000, hasPreviousPage = false, hasNextPage = true },
        }));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollInconsistent);
        handler.ApiRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task AnItemCountBeyondAnyRealPayroll_IsRefused()
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new
        {
            id = PayrollId, status = "IN_PROGRESS", reason = "", payrollId = PayrollId, data = Array.Empty<object>(),
            meta = new { page = 1, take = 50, itemCount = 5_000_000, pageCount = 100_000, hasPreviousPage = false, hasNextPage = true },
        }));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollInconsistent);
        handler.ApiRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task HasNextPageFalseBeforeTheLastPage_IsInconsistent()
    {
        var batch = BuildBatch(120);
        var lines = batch.Rows.Select(r => Line(r, "PENDING")).ToList();
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new
        {
            id = PayrollId, status = "IN_PROGRESS", reason = "", payrollId = PayrollId, data = lines.Take(50).ToList(),
            meta = new { page = 1, take = 50, itemCount = 120, pageCount = 3, hasPreviousPage = false, hasNextPage = false },
        }));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollInconsistent);
    }

    [Fact]
    public async Task MoreThanFiftyLinesInAPage_IsInconsistent()
    {
        var batch = BuildBatch(51);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK,
            Page("IN_PROGRESS", batch.Rows.Select(r => Line(r, "PENDING")).ToList(), 1, 50, 51)));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollInconsistent);
    }

    [Fact]
    public async Task APayrollThatGoesBackFromTerminalBetweenPages_IsInconsistent()
    {
        var batch = BuildBatch(60);
        var lines = batch.Rows.Select(r => Line(r, "PAID", "P")).ToList();
        var handler = new StubAnbHandler(req =>
        {
            var page = int.Parse(ParseQuery(req.Uri)["page"]);
            return Json(HttpStatusCode.OK, Page(page == 1 ? "COMPLETED" : "IN_PROGRESS", lines.Skip((page - 1) * 50).Take(50).ToList(), page, 50, 60));
        });
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollInconsistent);
    }

    [Fact]
    public async Task AStatusReadThatOverrunsItsDeadline_StopsWithADeadlineCode()
    {
        var batch = BuildBatch(200);
        var paged = PagedStatus(batch.Rows.Select(r => Line(r, "PENDING")).ToList());
        var handler = new StubAnbHandler(async (req, ct) =>
        {
            await Task.Delay(150, ct);
            return paged(req);
        });
        var act = () => Client(handler, out _, deadline: TimeSpan.FromMilliseconds(350)).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollDeadline);
        handler.ApiRequests.Count.Should().BeLessThan(4);
    }

    [Fact]
    public async Task ZeroItems_IsNotAComplete_Snapshot()
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, Page("ACCEPTED", Array.Empty<object>(), 1, 50, 0)));
        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        snapshot.Complete.Should().BeFalse("the bank may still be loading the payroll");
    }
}
