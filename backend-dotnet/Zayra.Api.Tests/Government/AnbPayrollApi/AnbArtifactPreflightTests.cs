using System.Net;
using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// The last look at the frozen files before they are sent. Each guard has its own case, so removing any one
/// of them turns exactly that case red. Every tampered file below is re-hashed, so the hash check passes
/// and the guard under test is the only thing that can refuse it.
/// </summary>
public class AnbArtifactPreflightTests
{
    private static readonly Batch Plain = BuildBatch(3);
    private static readonly Batch WithWps = BuildBatch(3, autoWps: true);

    private static BankArtifactFile Rewrite(BankArtifactFile file, Func<string, string> change)
    {
        var bytes = Encoding.UTF8.GetBytes(change(Encoding.UTF8.GetString(file.Content)));
        return file with { Content = bytes, ExpectedSha256 = AnbConnectCsvGenerator.Sha256Hex(bytes) };
    }

    private static BankArtifactFile RewriteBytes(BankArtifactFile file, Func<byte[], byte[]> change)
    {
        var bytes = change(file.Content);
        return file with { Content = bytes, ExpectedSha256 = AnbConnectCsvGenerator.Sha256Hex(bytes) };
    }

    /// <summary>Replaces one cell of one data line (1-based line after the column row).</summary>
    private static Func<string, string> Cell(int line, int column, string value) => text =>
    {
        var lines = text.Split("\r\n");
        var cells = lines[line].Split(',');
        cells[column] = value;
        lines[line] = string.Join(",", cells);
        return string.Join("\r\n", lines);
    };

    private static async Task<BankSubmitOutcome> Send(AnbSubmitRequest request)
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));
        var outcome = await Client(handler, out _).SubmitAsync(WithReceiptForItsFiles(request), CancellationToken.None);
        if (outcome.Kind == BankSubmitOutcomeKind.Refused) handler.Requests.Should().BeEmpty();
        return outcome;
    }

    private static void ShouldBeInconsistent(BankSubmitOutcome outcome)
    {
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.ArtifactInconsistent);
    }

    [Fact]
    public async Task TheUntouchedFiles_PassPreflight() =>
        (await Send(Submit(Plain))).Kind.Should().Be(BankSubmitOutcomeKind.Accepted);

    [Fact]
    public async Task SameCount_DifferentTotal_IsRefused()
    {
        var amount = Plain.Rows.OrderBy(r => r.EmployeeRef).First().SalaryAmount + 1m;
        var body = Rewrite(Plain.Body, Cell(1, 2, amount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        ShouldBeInconsistent(await Send(Submit(Plain) with { Body = body }));
    }

    [Fact]
    public async Task SameTotal_DifferentCount_IsRefused()
    {
        var header = Rewrite(Plain.Header, Cell(1, 9, "4"));
        ShouldBeInconsistent(await Send(Submit(Plain) with { Header = header }));
    }

    [Fact]
    public async Task AutoWpsRequested_ButTheFrozenHeaderSaysNo_IsRefused()
    {
        var header = Rewrite(WithWps.Header, Cell(1, 13, "NO"));
        ShouldBeInconsistent(await Send(Submit(WithWps, new AnbSubmitHeaders(AutoWpsFileUpload: true, NationalUnifiedNo: "7001234567"))
            with { Header = header }));
    }

    [Fact]
    public async Task AUnifiedNumberWithoutAutoWps_IsRefused() =>
        ShouldBeInconsistent(await Send(Submit(Plain, new AnbSubmitHeaders(NationalUnifiedNo: "7001234567"))));

    [Fact]
    public async Task LfLineEndings_AreRefused() =>
        ShouldBeInconsistent(await Send(Submit(Plain) with { Body = Rewrite(Plain.Body, t => t.Replace("\r\n", "\n")) }));

    [Fact]
    public async Task AByteOrderMark_IsRefused() =>
        ShouldBeInconsistent(await Send(Submit(Plain) with
        {
            Body = RewriteBytes(Plain.Body, b => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(b).ToArray()),
        }));

    [Fact]
    public async Task AMissingFinalLineEnding_IsRefused() =>
        ShouldBeInconsistent(await Send(Submit(Plain) with { Body = Rewrite(Plain.Body, t => t.TrimEnd('\r', '\n')) }));

    [Fact]
    public async Task ColumnsInAnotherOrder_AreRefused() =>
        ShouldBeInconsistent(await Send(Submit(Plain) with
        {
            Body = Rewrite(Plain.Body, t => "employeeAccountNumber,employeeId" + t[t.IndexOf(",salaryAmount", StringComparison.Ordinal)..]),
        }));

    [Theory]
    [InlineData("0")]
    [InlineData("100.125")]
    [InlineData("-5")]
    [InlineData(" 100")]
    [InlineData("1e3")]
    public async Task AnAmountTheGeneratorWouldNeverWrite_IsRefused(string amount)
    {
        // Header total adjusted so the sum still matches: the per-line rule is what refuses.
        var rows = Plain.Rows.OrderBy(r => r.EmployeeRef).ToList();
        var others = rows.Skip(1).Sum(r => r.SalaryAmount);
        var body = Rewrite(Plain.Body, Cell(1, 2, amount));
        var parsed = decimal.TryParse(amount.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;
        var header = Rewrite(Plain.Header, Cell(1, 10, (others + parsed).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        ShouldBeInconsistent(await Send(Submit(Plain) with { Body = body, Header = header }));
    }

    [Fact]
    public async Task AQuotedCellThatTheGeneratorWouldNotQuote_IsRefused() =>
        ShouldBeInconsistent(await Send(Submit(Plain) with { Body = Rewrite(Plain.Body, Cell(1, 7, "\"RJHISARI\"")) }));

    [Theory]
    [InlineData("header.CSV", AnbConnectCsvGenerator.BodyFileName)]
    [InlineData(AnbConnectCsvGenerator.HeaderFileName, "body.csv\n")]
    [InlineData(AnbConnectCsvGenerator.BodyFileName, AnbConnectCsvGenerator.HeaderFileName)]
    public async Task FileNamesOtherThanTheGeneratorsOwn_AreRefused(string headerName, string bodyName) =>
        ShouldBeInconsistent(await Send(Submit(Plain) with
        {
            Header = Plain.Header with { FileName = headerName },
            Body = Plain.Body with { FileName = bodyName },
        }));
}
