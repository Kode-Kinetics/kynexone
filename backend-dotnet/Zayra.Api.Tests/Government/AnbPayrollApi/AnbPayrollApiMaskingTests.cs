using System.Net;
using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// No IBAN, national ID, client secret, client id or access token in any log line, exception message,
/// outcome message or ToString, whatever the bank echoes back.
/// </summary>
public class AnbPayrollApiMaskingTests
{
    private static readonly Batch Data = BuildBatch(3);

    private static string[] Forbidden() => new[]
    {
        ClientSecret, ClientId, AccessToken,
        Uri.EscapeDataString(ClientSecret),
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(ClientId) + ":" + Uri.EscapeDataString(ClientSecret))),
        Data.Rows[0].AccountNumber!, Data.Rows[0].EmployeeNationalId!, Data.Rows[0].EmployeeName!,
        Data.Rows[1].AccountNumber!, Data.Rows[1].EmployeeNationalId!,
    };

    private static void AssertClean(string text, string what)
    {
        foreach (var secret in Forbidden())
            text.Should().NotContain(secret, $"{what} must not carry personal data or a secret");
    }

    private static string Echo =>
        $"Row 2: account {Data.Rows[0].AccountNumber} for {Data.Rows[0].EmployeeNationalId} ({Data.Rows[0].EmployeeName}) " +
        $"rejected; client {ClientId} secret {ClientSecret} token {AccessToken}; also {Data.Rows[1].AccountNumber} {Data.Rows[1].EmployeeNationalId}";

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    [InlineData(500)]
    [InlineData(200)]
    public async Task SubmitOutcomes_AndLogs_AreClean_WhenTheBankEchoesEverything(int status)
    {
        var handler = new StubAnbHandler(_ => Json((HttpStatusCode)status, new { message = Echo, error = Echo, detail = Echo }));
        var client = Client(handler, out var log);

        var outcome = await client.SubmitAsync(Submit(Data), CancellationToken.None);

        AssertClean(outcome.ToString(), "the outcome");
        AssertClean(log.All, "the log");
        if (status is 400 or 422)
        {
            outcome.Message.Should().NotBeNull();
            outcome.Message!.Length.Should().BeLessThanOrEqualTo(AnbPayrollApiClient.MaxMessageLength);
            outcome.Message.Should().Contain("rejected", "the masked bank message is still useful");
        }
    }

    [Fact]
    public async Task TokenFailures_LogTheOAuthErrorCodeOnly()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException(),
            _ => Json(HttpStatusCode.Unauthorized, new { error = "invalid_client", error_description = Echo }));
        var outcome = await Client(handler, out var log).SubmitAsync(Submit(Data), CancellationToken.None);

        AssertClean(outcome.ToString(), "the outcome");
        AssertClean(log.All, "the log");
        log.All.Should().Contain("invalid_client");
    }

    [Fact]
    public async Task TransportExceptions_DoNotLeakThroughLogsOrOutcomes()
    {
        var handler = new StubAnbHandler((_, _) => throw new HttpRequestException(Echo, new IOException(Echo)));
        var outcome = await Client(handler, out var log).SubmitAsync(Submit(Data), CancellationToken.None);

        outcome.Kind.Should().Be(AnbSubmitOutcomeKind.Unknown);
        AssertClean(outcome.ToString(), "the outcome");
        AssertClean(log.All, "the log");
    }

    [Fact]
    public async Task PollErrors_CarryNoBodyText()
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.BadRequest, new { message = Echo }));
        var client = Client(handler, out var log);
        var act = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<AnbPayrollApiException>()).Which;

        AssertClean(ex.ToString(), "the exception");
        AssertClean(log.All, "the log");
    }

    [Fact]
    public async Task ASuccessfulPoll_LogsNoLineData()
    {
        var lines = Data.Rows.Select(r => Line(r, "PAID", "P" + r.EmployeeRef, Echo)).ToList();
        var handler = new StubAnbHandler(PagedStatus(lines));
        var client = Client(handler, out var log);

        var snapshot = await client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);

        AssertClean(log.All, "the log");
        AssertClean(snapshot.Lines[0].ToString(), "a line's ToString");
        AssertClean(string.Join(" ", snapshot.Lines), "the lines' ToString");
    }

    [Fact]
    public void ToStrings_OfTypesHoldingSecretsOrPersonalData_AreClean()
    {
        AssertClean(new AnbClientCredential(ClientId, ClientSecret, "v1").ToString(), "the credential");
        AssertClean(Data.Submitted[0].ToString(), "a submitted line");
        AssertClean(Data.Body.ToString(), "an artifact");
        AssertClean(Submit(Data).ToString(), "a submit request");
    }

    [Fact]
    public void RefusalMessages_NameSettingsNotValues()
    {
        var config = Config();
        config.BaseUrl = "http://" + ClientSecret + ".test/api";
        var act = () => Client(new StubAnbHandler(_ => throw new InvalidOperationException()), out _, config);
        AssertClean(act.Should().Throw<AnbPayrollApiRefusedException>().Which.ToString(), "the refusal");
    }
}
