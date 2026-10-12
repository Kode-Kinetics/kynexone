using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Setup;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Setup;

namespace Zayra.Api.Tests;

public sealed class PolicyExtractionTests
{
    private const string Source = "All employees use web check-in. The leave policy uses monthly accrual and prorates partial months.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static PolicyExtractionResult Validate(params object[] proposals) => PolicyExtractionService.Validate(
        Guid.NewGuid(), new string('A', 64), "ollama", Source,
        JsonSerializer.Serialize(new { proposals }, Json), SetupConfigurationTests.Profile());

    [Fact]
    public void SupportedSourceBackedProposal_DoesNotApplyAnything()
    {
        var result = Validate(new { target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = Source });
        result.Proposals.Should().ContainSingle();
        result.Coverage.Single(c => c.Section == "Attendance").Status.Should().Be("Proposals to review");
        result.Coverage.Single(c => c.Section == "Benefits").Status.Should().Be("Needs review");
    }

    [Fact]
    public void ForgedQuote_UnsupportedTarget_AndConflictingProposals_AreExcluded()
    {
        Validate(new { target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = "made up" }).Proposals.Should().BeEmpty();
        Validate(new { target = "configuration", field = "statutoryRates", value = 0, sourceQuote = Source }).Proposals.Should().BeEmpty();
        Validate(new { target = "profile", field = "payCycle", value = "Weekly", sourceQuote = Source }).Proposals.Should().BeEmpty();
        Validate(new { target = "profile", field = "leaveYearBasis", value = "Anniversary", sourceQuote = Source }).Proposals.Should().BeEmpty();
        var duplicate = new { target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = Source };
        Validate(duplicate, duplicate, duplicate).Proposals.Should().BeEmpty();
    }

    [Theory]
    [InlineData("proratePartialMonths")]
    [InlineData("gradeCode")]
    [InlineData("departmentCode")]
    [InlineData("employmentType")]
    public void OmittedLeaveDecisions_CannotBecomeConstructorDefaults(string missing)
    {
        var leave = JsonSerializer.SerializeToNode(SetupConfigurationTests.Leave(), Json)!.AsObject();
        leave.Remove(missing);
        Validate(new { target = "configuration", field = "leavePolicies", value = new[] { leave }, sourceQuote = Source }).Proposals.Should().BeEmpty();
    }

    [Theory]
    [InlineData("requiresEnrollment")]
    [InlineData("gradeCodes")]
    public void OmittedBenefitDecisions_CannotBecomeConstructorDefaults(string missing)
    {
        var benefit = JsonSerializer.SerializeToNode(SetupConfigurationTests.Benefit(), Json)!.AsObject();
        benefit.Remove(missing);
        Validate(new { target = "configuration", field = "benefitPlans", value = new[] { benefit }, sourceQuote = Source }).Proposals.Should().BeEmpty();
    }

    [Fact]
    public void ExplicitCompleteLeaveConfiguration_RemainsReviewable()
    {
        Validate(new { target = "configuration", field = "leavePolicies", value = new[] { SetupConfigurationTests.Leave() }, sourceQuote = Source })
            .Proposals.Should().ContainSingle();
    }

    [Theory]
    [InlineData("profile", "leaveYearBasis", "\"Calendar\"")]
    [InlineData("configuration", "overtimeModes", "[\"NotApplicable\"]")]
    public void RealModelRegression_UnspecifiedPolicyDoesNotBecomeADefault(string target, string field, string value)
    {
        const string source = "No other configuration is specified in this excerpt.";
        var json = JsonSerializer.Serialize(new { proposals = new[] { new { target, field, value = JsonDocument.Parse(value).RootElement, sourceQuote = source } } });
        PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", source, json, SetupConfigurationTests.Profile()).Proposals.Should().BeEmpty();
    }

    private sealed class Model(string text, bool throws = false) : ILlmClient
    {
        public List<LlmRequest> Calls { get; } = [];
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            if (throws) throw new HttpRequestException("provider down");
            return Task.FromResult(new LlmResponse(true, request.Provider, request.Model, text));
        }
    }
    private sealed class Recorder : IAiCallRecorder
    {
        public List<AiCallRecord> Records { get; } = [];
        public Task RecordAsync(AiCallRecord record, CancellationToken ct) { Records.Add(record); return Task.CompletedTask; }
    }
    private static AiOptions Options(string url = "http://localhost:11434") => new("ollama", "configured-model", "", "other-key", url, "", 4096, true, false);

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"scalar\"")]
    [InlineData("not json")]
    public async Task UnusableModelOutput_IsRecordedAsDegradedWithoutInventedDefaults(string output)
    {
        var recorder = new Recorder();
        var model = new Model(output);
        var service = new PolicyExtractionService(model, Options(), recorder);
        var result = await service.ExtractAsync(new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash", Source,
            SetupConfigurationTests.Profile(), CancellationToken.None);
        result.Proposals.Should().BeEmpty();
        result.Provider.Should().Be("fallback");
        recorder.Records.Should().ContainSingle().Which.FailureReason.Should().NotBeNullOrEmpty();
        model.Calls.Should().ContainSingle().Which.Provider.Should().Be("ollama");
        model.Calls.Single().Model.Should().Be("configured-model");
        model.Calls.Single().RequireJson.Should().BeTrue();
    }

    [Fact]
    public async Task DisabledOllama_DoesNotFallBackToAnotherVendorsKey()
    {
        var model = new Model("{}");
        var result = await new PolicyExtractionService(model, Options(""), new Recorder()).ExtractAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash", Source, SetupConfigurationTests.Profile(), CancellationToken.None);
        model.Calls.Should().BeEmpty();
        result.Provider.Should().Be("fallback");
    }

    [Fact]
    public async Task OversizedHandbook_IsNotSilentlyTruncated()
    {
        var model = new Model("{}");
        var result = await new PolicyExtractionService(model, Options(), new Recorder()).ExtractAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash", new string('a', 10000), SetupConfigurationTests.Profile(), CancellationToken.None);
        model.Calls.Should().BeEmpty();
        result.Message.Should().Contain("no text was truncated");
    }

    [Fact]
    public async Task TransportFailure_LeavesSourceAndRecordsDegradation()
    {
        var recorder = new Recorder();
        var result = await new PolicyExtractionService(new Model("", true), Options(), recorder).ExtractAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash", Source, SetupConfigurationTests.Profile(), CancellationToken.None);
        result.Proposals.Should().BeEmpty();
        recorder.Records.Should().ContainSingle().Which.FailureReason.Should().NotBeNullOrEmpty();
    }
}
