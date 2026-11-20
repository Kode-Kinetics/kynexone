using System.Net;
using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
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

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        AssertClean(outcome.ToString(), "the outcome");
        AssertClean(log.All, "the log");
    }

    [Fact]
    public async Task PollErrors_CarryNoBodyText()
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.BadRequest, new { message = Echo }));
        var client = Client(handler, out var log);
        var act = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<BankChannelException>()).Which;

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
        AssertClean(Credential().ToString(), "the credential");
        AssertClean(Data.Submitted[0].ToString(), "a submitted line");
        AssertClean(Data.Body.ToString(), "an artifact");
        AssertClean(Submit(Data).ToString(), "a submit request");
    }

    [Fact]
    public async Task RefusalMessages_NameSettingsNotValues()
    {
        var config = Config();
        config[GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox")] = "http://" + ClientSecret + ".test/api";
        var client = Client(new StubAnbHandler(_ => throw new InvalidOperationException()), out var log, config);
        var outcome = await client.SubmitAsync(Submit(Data), CancellationToken.None);
        outcome.Code.Should().Be(BankChannelCodes.HostNotAllowed);
        AssertClean(outcome.ToString(), "the refusal");
        AssertClean(log.All, "the log");

        config[AnbPayrollApiClient.ScopeKey] = "payroll " + ClientSecret + "\u0001";
        var build = () => Client(new StubAnbHandler(_ => throw new InvalidOperationException()), out _, config);
        AssertClean(build.Should().Throw<BankChannelRefusedException>().Which.ToString(), "the refusal");
    }

    [Fact]
    public void AStatusLine_ABatchBinding_AndTheSubmitPreparation_PrintNoWageTokenOrSecret()
    {
        // Security P3-3.
        var line = new AnbStatusLine(Data.Rows[0].EmployeeNationalId!, Data.Rows[0].AccountNumber!, 12345.67m, "RJHISARI",
            Data.Rows[0].EmployeeName, "P1", "PAID", "ok", "TRX1", null, null);
        line.ToString().Should().NotContain("12345").And.Contain("PAID");
        AssertClean(line.ToString(), "a status line");
        Binding(Data).ToString().Should().NotContain(Data.Total.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.NotContain("total");

        // The client's private submit preparation holds the token and the credential: its ToString prints neither.
        var prepared = typeof(AnbPayrollApiClient).GetNestedType("Prepared", System.Reflection.BindingFlags.NonPublic)!;
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(prepared);
        void Set(string property, object value) =>
            prepared.GetField($"<{property}>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(instance, value);
        Set("Token", AccessToken);
        Set("Credential", Credential());
        Set("Redact", new List<string> { ClientSecret, ClientId, AccessToken });
        Set("HeaderSha", Data.Header.ExpectedSha256);
        Set("BodySha", Data.Body.ExpectedSha256);
        var text = instance.ToString()!;
        AssertClean(text, "the submit preparation");
        text.Should().Contain(Data.Body.ExpectedSha256);
    }
}
