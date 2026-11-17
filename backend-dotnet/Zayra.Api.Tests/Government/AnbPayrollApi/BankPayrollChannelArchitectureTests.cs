using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Tests.Security;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// The ANB client sits behind the bank-neutral <see cref="IBankPayrollChannel"/>. Nothing outside the Anb*
/// files may name an ANB client type, and nothing in the app (composition root, service-collection
/// extensions, controllers, jobs) registers or builds the channel yet: it ships switched off.
/// </summary>
public class BankPayrollChannelArchitectureTests
{
    private const string ApiNamespace = "Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api";

    /// <summary>Every type the ANB client folder declares (Anb*, IAnb*). The generator's older types live in
    /// another namespace and are not in this list.</summary>
    private static IReadOnlyList<string> AnbClientTypeNames() =>
        typeof(AnbPayrollApiClient).Assembly.GetTypes()
            .Where(t => t.Namespace == ApiNamespace && !t.IsNested && !t.Name.StartsWith('<'))
            .Select(t => t.Name.Split('`')[0])
            .Distinct()
            .ToList();

    [Fact]
    public void TheTypeListIsReal()
    {
        AnbClientTypeNames().Should().Contain(new[]
        {
            nameof(AnbPayrollApiClient), nameof(AnbPayrollChannel), nameof(IAnbCredentialSource), nameof(IAnbEndpointConfig),
            nameof(AnbHttpHandlerFactory), nameof(AnbPayrollStatusMapper),
        });
        AnbClientTypeNames().Should().OnlyContain(n => n.StartsWith("Anb", StringComparison.Ordinal) || n.StartsWith("IAnb", StringComparison.Ordinal),
            "every ANB-specific type is named Anb*, so the rule below is enforceable by name");
    }

    [Fact]
    public void NoFileOutsideTheAnbFilesNamesAnAnbClientType()
    {
        var names = AnbClientTypeNames();
        var pattern = new Regex(@"\b(" + string.Join("|", names.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
        var offenders = SourceScan.Files(SourceScan.ResolveApiRoot(), "*.cs")
            .Where(f => !Path.GetFileName(f.Relative).StartsWith("Anb", StringComparison.Ordinal))
            .SelectMany(f => pattern.Matches(f.Code).Select(m => $"{f.Relative}: {m.Value}"))
            .Distinct()
            .ToList();
        offenders.Should().BeEmpty("callers depend on IBankPayrollChannel and the neutral types only");
    }

    [Fact]
    public void NothingInTheAppRegistersOrBuildsTheChannel()
    {
        // Scans the WHOLE API source (Program.cs, every *ServiceCollectionExtensions, controllers, jobs), not
        // just the composition root: only the channel's own folders may mention these names.
        var names = new[] { nameof(IBankPayrollChannel), nameof(AnbPayrollChannel), nameof(AnbPayrollApiClient),
            nameof(IAnbCredentialSource), nameof(IAnbEndpointConfig), nameof(AnbHttpHandlerFactory), nameof(AnbPayrollApiOptions) };
        var pattern = new Regex(@"\b(" + string.Join("|", names) + @")\b", RegexOptions.CultureInvariant);
        var files = SourceScan.Files(SourceScan.ResolveApiRoot(), "*.cs").ToList();
        files.Count.Should().BeGreaterThan(500, "the scan must be reading the real API source");

        var offenders = files
            .Where(f => !f.Relative.StartsWith("Infrastructure/Payroll/SaudiBankExports/Api/", StringComparison.Ordinal)
                        && !f.Relative.StartsWith("Infrastructure/Payroll/SaudiBankExports/Channels/", StringComparison.Ordinal))
            .Where(f => pattern.IsMatch(f.Code))
            .Select(f => f.Relative)
            .ToList();
        offenders.Should().BeEmpty("the bank channel ships switched off; the integration wave wires it behind the live gate");

        var config = Directory.EnumerateFiles(SourceScan.ResolveApiRoot(), "appsettings*.json", SearchOption.TopDirectoryOnly).ToList();
        config.Should().NotBeEmpty();
        config.Where(f => File.ReadAllText(f).Contains("PayrollApi", StringComparison.Ordinal)).Should().BeEmpty("no default ANB endpoint is configured");
    }

    [Fact]
    public void TheAnbChannelIsNeutral_AndNotLive()
    {
        IBankPayrollChannel channel = new AnbPayrollChannel(Client(new StubAnbHandler(_ => throw new InvalidOperationException()), out _));
        channel.BankId.Should().Be("anb");
        channel.IsLiveIntegration.Should().BeFalse();
    }

    [Fact]
    public async Task TheChannelSendsTheTwoFrozenFiles_ByTheGeneratorsOwnNames()
    {
        var batch = BuildBatch(3);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));
        IBankPayrollChannel channel = new AnbPayrollChannel(Client(handler, out _));

        var outcome = await channel.SubmitAsync(new BankSubmitRequest(TestScope, new[] { batch.Body, batch.Header }, new BankSubmitOptions()),
            CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        var parts = await AnbPayrollApiSubmitTests.ReadMultipart(handler.Posts.Single());
        parts["header"].Should().Equal(batch.Header.Content);
        parts["body"].Should().Equal(batch.Body.Content);
    }

    [Theory]
    [InlineData("head.csv", AnbConnectCsvGenerator.BodyFileName)]
    [InlineData(AnbConnectCsvGenerator.HeaderFileName, AnbConnectCsvGenerator.HeaderFileName)]
    public async Task TheChannelRefusesFilesNotNamedByTheGenerator(string headerName, string bodyName)
    {
        var batch = BuildBatch(2);
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        IBankPayrollChannel channel = new AnbPayrollChannel(Client(handler, out _));

        var outcome = await channel.SubmitAsync(new BankSubmitRequest(TestScope,
            new[] { batch.Header with { FileName = headerName }, batch.Body with { FileName = bodyName } }, new BankSubmitOptions()),
            CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.ArtifactInconsistent);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TheChannelMapsTheStatusOntoTheBoundBatch()
    {
        var batch = BuildBatch(3);
        var handler = new StubAnbHandler(PagedStatus(batch.Rows.Select(r => Line(r, "PAID", "P" + r.EmployeeRef)).ToList(), "COMPLETED"));
        IBankPayrollChannel channel = new AnbPayrollChannel(Client(handler, out _));

        var snapshot = await channel.GetStatusAsync(TestScope, PayrollId, Binding(batch), CancellationToken.None);

        snapshot.BankPayrollId.Should().Be(PayrollId);
        snapshot.PayrollPhase.Should().Be(BankPayrollPhase.Completed);
        snapshot.Rows.Select(r => r.WpsReference).Should().Equal(WpsReference(1), WpsReference(2), WpsReference(3));
        snapshot.Complete.Should().BeTrue();
    }
}
