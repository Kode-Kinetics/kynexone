using System.Net;
using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// <c>GET /payroll/v2/{id}</c> (paged, take at most 50) and <c>GET /payroll/{id}/WPS</c>. Reads may be
/// repeated, but a snapshot is only "complete" when every line the bank counts has been seen, and a
/// page that is not the page we asked for is an error, never data.
/// </summary>
public class AnbPayrollApiPollTests
{
    [Fact]
    public async Task A3000LineBatch_IsReadOver60PagesOf50_AndIsComplete()
    {
        var batch = BuildBatch(3000);
        var lines = batch.Rows.Select((r, i) => Line(r, i % 3 == 0 ? "PAID" : "IN PROGRESS", i % 3 == 0 ? $"PMT{i}" : null)).ToList();
        var handler = new StubAnbHandler(PagedStatus(lines));

        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);

        handler.ApiRequests.Should().HaveCount(60);
        handler.ApiRequests.Select(r => ParseQuery(r.Uri)["take"]).Should().OnlyContain(t => t == "50");
        handler.ApiRequests.Select(r => ParseQuery(r.Uri)["page"]).Should().Equal(Enumerable.Range(1, 60).Select(p => p.ToString()));
        handler.ApiRequests.Should().OnlyContain(r => r.Uri.AbsolutePath == "/api/payroll/v2/" + PayrollId);
        handler.TokenRequests.Should().ContainSingle();
        snapshot.PagesFetched.Should().Be(60);
        snapshot.ItemCount.Should().Be(3000);
        snapshot.Lines.Should().HaveCount(3000);
        snapshot.Complete.Should().BeTrue();
        snapshot.PayrollStatus.Should().Be("IN_PROGRESS");
        snapshot.Lines.Sum(l => l.SalaryAmount).Should().Be(batch.Total);
    }

    [Fact]
    public async Task ADuplicatedPage_CollapsesLines_AndTheSnapshotIsIncomplete()
    {
        var batch = BuildBatch(150);
        var lines = batch.Rows.Select(r => Line(r, "PENDING")).ToList();
        var paged = PagedStatus(lines);
        // Page 3 answers with page 2's lines (ordering shifted under the bank's paging).
        var handler = new StubAnbHandler(req =>
        {
            var q = ParseQuery(req.Uri);
            if (q["page"] != "3") return paged(req);
            var take = int.Parse(q["take"]);
            return Json(HttpStatusCode.OK, Page("IN_PROGRESS", lines.Skip(take).Take(take).ToList(), 3, take, lines.Count));
        });

        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);

        snapshot.Lines.Should().HaveCount(100);
        snapshot.DuplicateLinesCollapsed.Should().Be(50);
        snapshot.Complete.Should().BeFalse("50 lines the bank counts were never seen");
    }

    [Fact]
    public async Task ADuplicateLineWithAnOlderStatus_NeverMovesTheLineBackwards()
    {
        var batch = BuildBatch(60);
        var lines = batch.Rows.Select(r => Line(r, "PAID", "PMT" + r.EmployeeRef)).ToList();
        var paged = PagedStatus(lines);
        var handler = new StubAnbHandler(req =>
        {
            var q = ParseQuery(req.Uri);
            if (q["page"] != "2") return paged(req);
            // Page 2 repeats line 1 with a stale status instead of line 51.
            var data = lines.Skip(51).Take(9).Prepend(Line(batch.Rows[0], "IN PROGRESS")).ToList();
            return Json(HttpStatusCode.OK, Page("IN_PROGRESS", data, 2, 50, lines.Count));
        });

        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);

        snapshot.Lines.Single(l => l.EmployeeId == NationalId(1)).PaymentStatus.Should().Be("PAID");
        snapshot.Complete.Should().BeFalse();
    }

    [Fact]
    public async Task TwoDifferentTerminalStatusesForOneLine_AreBothKept_SoTheImportRefusesThatEmployee()
    {
        var batch = BuildBatch(2);
        var data = new[] { Line(batch.Rows[0], "PAID", "PMT1"), Line(batch.Rows[1], "PAID", "PMT2"), Line(batch.Rows[0], "FAILED") };
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, Page("COMPLETED", data, 1, 50, 2)));

        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);

        snapshot.Lines.Where(l => l.EmployeeId == NationalId(1)).Select(l => l.PaymentStatus).Should().BeEquivalentTo("PAID", "FAILED");
    }

    [Fact]
    public async Task AnOutOfOrderPage_IsAnError_NeverData()
    {
        var batch = BuildBatch(150);
        var lines = batch.Rows.Select(r => Line(r, "PENDING")).ToList();
        var handler = new StubAnbHandler(req =>
        {
            var q = ParseQuery(req.Uri);
            var asked = int.Parse(q["page"]);
            var served = asked == 3 ? 2 : asked;
            return Json(HttpStatusCode.OK, Page("IN_PROGRESS", lines.Skip((served - 1) * 50).Take(50).ToList(), served, 50, lines.Count));
        });

        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);

        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PageMismatch);
    }

    [Fact]
    public async Task AnItemCountThatChangesMidPoll_IsInconsistent()
    {
        var batch = BuildBatch(120);
        var lines = batch.Rows.Select(r => Line(r, "PENDING")).ToList();
        var handler = new StubAnbHandler(req =>
        {
            var page = int.Parse(ParseQuery(req.Uri)["page"]);
            var count = page == 1 ? 120 : 140;
            return Json(HttpStatusCode.OK, Page("IN_PROGRESS", lines.Skip((page - 1) * 50).Take(50).ToList(), page, 50, count));
        });

        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollInconsistent);
    }

    [Fact]
    public async Task AHasNextPageThatNeverEnds_IsCappedByPageCount()
    {
        var batch = BuildBatch(10);
        var lines = batch.Rows.Select(r => Line(r, "PENDING")).ToList();
        var handler = new StubAnbHandler(req =>
        {
            var page = int.Parse(ParseQuery(req.Uri)["page"]);
            return Json(HttpStatusCode.OK, new
            {
                id = PayrollId, status = "IN_PROGRESS", reason = "", payrollId = PayrollId,
                data = page == 1 ? lines : new List<object>(),
                meta = new { page, take = 50, itemCount = 10, pageCount = 1, hasPreviousPage = page > 1, hasNextPage = true },
            });
        });

        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PollInconsistent);
        handler.ApiRequests.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task APageForAnotherPayroll_IsRefused()
    {
        var batch = BuildBatch(2);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK,
            Page("COMPLETED", batch.Rows.Select(r => Line(r, "PAID", "P")).ToList(), 1, 50, 2, id: "9e0c0000-0000-4000-8000-000000000000")));

        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.PagePayrollMismatch);
    }

    [Fact]
    public async Task A401MidPoll_ReacquiresTheTokenOnce_AndFinishes()
    {
        var batch = BuildBatch(150);
        var lines = batch.Rows.Select(r => Line(r, "PENDING")).ToList();
        var paged = PagedStatus(lines);
        var served401 = false;
        var handler = new StubAnbHandler(req =>
        {
            if (ParseQuery(req.Uri)["page"] == "2" && !served401)
            {
                served401 = true;
                return Json(HttpStatusCode.Unauthorized, new { message = "token expired" });
            }
            return paged(req);
        }, _ => TokenOk("tok-" + Guid.NewGuid().ToString("N")));

        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);

        snapshot.Complete.Should().BeTrue();
        snapshot.Lines.Should().HaveCount(150);
        handler.TokenRequests.Should().HaveCount(2);
        handler.ApiRequests.Should().HaveCount(4, "pages 1, 2 (401), 2 again, 3");
        handler.ApiRequests[2].Headers["Authorization"].Should().NotBe(handler.ApiRequests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task A401ThatPersists_FailsThePoll_WithNoPartialSnapshot()
    {
        var batch = BuildBatch(150);
        var lines = batch.Rows.Select(r => Line(r, "PENDING")).ToList();
        var paged = PagedStatus(lines);
        var handler = new StubAnbHandler(req => ParseQuery(req.Uri)["page"] == "2"
            ? Json(HttpStatusCode.Unauthorized, new { message = "no" })
            : paged(req));
        var client = Client(handler, out _);

        var act = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<BankChannelException>()).Which;
        ex.Code.Should().Be(BankChannelCodes.Unauthorized);
        ex.HttpStatus.Should().Be(401);
        handler.TokenRequests.Should().HaveCount(2, "exactly one re-acquire, no loop");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task OtherHttpErrors_FailThePoll(int status)
    {
        var handler = new StubAnbHandler(_ => Json((HttpStatusCode)status, new { message = "x" }));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<BankChannelException>()).Which;
        ex.Code.Should().Be(BankChannelCodes.HttpError);
        ex.HttpStatus.Should().Be(status);
        handler.ApiRequests.Should().ContainSingle("a failed read is not retried in a loop by the client");
    }

    [Fact]
    public async Task AMalformedPage_IsUnparsable()
    {
        var handler = new StubAnbHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":") });
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.ResponseUnparsable);
    }

    [Fact]
    public async Task AnUnknownLineStatus_IsKeptForTheMapperToHold_NotThrown()
    {
        var batch = BuildBatch(2);
        var data = new[] { Line(batch.Rows[0], "PAID", "P1"), Line(batch.Rows[1], "SETTLED_MAYBE") };
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, Page("IN_PROGRESS", data, 1, 50, 2)));

        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        snapshot.Lines.Select(l => l.PaymentStatus).Should().BeEquivalentTo("PAID", "SETTLED_MAYBE");
    }

    [Fact]
    public async Task AnUnknownPayrollStatus_StillFailsLoudly()
    {
        var batch = BuildBatch(1);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, Page("ALMOST_DONE", new[] { Line(batch.Rows[0], "PAID", "P1") }, 1, 50, 1)));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<AnbUnknownStatusException>()).Which.Kind.Should().Be("payroll");
    }

    [Fact]
    public async Task NullPaymentIdBeforePaid_IsRead()
    {
        var batch = BuildBatch(1);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, Page("VALIDATED", new[] { Line(batch.Rows[0], "INITIATED") }, 1, 50, 1)));
        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        snapshot.Lines.Single().PaymentId.Should().BeNull();
        snapshot.Complete.Should().BeTrue();
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("id?x=1")]
    public async Task APayrollIdThatIsNotAPlainToken_IsRefusedBeforeAnySocket(string id)
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, id, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.PayrollIdInvalid);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProfileId_IsSentOnReads_WhenGiven()
    {
        var batch = BuildBatch(1);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, Page("VALIDATED", new[] { Line(batch.Rows[0], "INITIATED") }, 1, 50, 1)));
        await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, "P-77", CancellationToken.None);
        handler.ApiRequests.Single().Headers["profileid"].Should().Be("P-77");
    }

    // ── WPS file ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Wps_DownloadsTheBytesWithTheirHash()
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic wps file\r\n");
        var handler = new StubAnbHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            resp.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "wps.txt" };
            return resp;
        });

        var file = await Client(handler, out _).DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);

        file.Content.Should().Equal(bytes);
        file.Sha256.Should().Be(Zayra.Api.Infrastructure.Payroll.SaudiBankExports.AnbConnectCsvGenerator.Sha256Hex(bytes));
        file.FileName.Should().Be("wps.txt");
        handler.ApiRequests.Single().Uri.AbsolutePath.Should().Be("/api/payroll/" + PayrollId + "/WPS");
    }

    [Fact]
    public async Task Wps_NotFound_IsNotReady()
    {
        var handler = new StubAnbHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var act = () => Client(handler, out _).DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.WpsNotReady);
    }

    [Fact]
    public async Task Wps_AnOversizedFile_IsRefused()
    {
        var handler = new StubAnbHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[AnbPayrollApiClient.MaxWpsBytes + 1]) });
        var act = () => Client(handler, out _).DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.WpsTooLarge);
    }

    [Fact]
    public async Task Wps_A401_ReacquiresOnce()
    {
        var calls = 0;
        var handler = new StubAnbHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        var file = await Client(handler, out _).DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);
        file.Content.Should().Equal(new byte[] { 1, 2, 3 });
        handler.TokenRequests.Should().HaveCount(2);
    }
}
