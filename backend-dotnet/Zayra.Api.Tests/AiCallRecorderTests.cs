using FluentAssertions;
using Zayra.Api.Application.AI;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Tests;

/// <summary>
/// The recorder is the gateway AGENTS.md requires: one place where every model call produces a
/// usage/cost record. Four of five call sites used to record nothing, which is why a setup
/// assistant that had been falling back on every single request left no trace anyone could find.
/// These tests pin the properties that make such a record trustworthy.
/// </summary>
public class AiCallRecorderTests
{
    private sealed class CapturingAudit : IAiAuditService
    {
        public List<AiAuditEntry> Entries { get; } = new();
        public bool Throw { get; set; }
        public Task LogAsync(AiAuditEntry entry, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException("audit store unavailable");
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static readonly Guid Tenant = Guid.NewGuid();

    private static (AiCallRecorder Recorder, CapturingAudit Audit) Build()
    {
        var audit = new CapturingAudit();
        var options = new AiOptions("ollama", "deepseek-v4-pro:cloud", "", "", "https://ollama.com", "k", 4096, true, false);
        return (new AiCallRecorder(audit, new AiRedactionService(), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiCallRecorder>.Instance), audit);
    }

    private static AiCallRecord Record(LlmRequest? request, LlmResponse? response, string? failure = null, string summary = "starter configuration for SAU / Technology / 51-200")
        => new(Tenant, Guid.NewGuid(), "Admin", "setup", "starter_configuration", summary, request, response, 1234, failure);

    private static LlmRequest AnyRequest() => new("ollama", "deepseek-v4-pro:cloud", "s", "u", 8000, RequireJson: true);

    [Fact]
    public async Task SuccessfulCall_RecordsProviderModelAndBilledTokens()
    {
        var (recorder, audit) = Build();
        var response = new LlmResponse(true, "ollama", "deepseek-v4-pro:cloud", "{}", InputTokens: 900, OutputTokens: 120);

        await recorder.RecordAsync(Record(AnyRequest(), response), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("provider_success");
        entry.Provider.Should().Be("ollama");
        entry.Model.Should().Be("deepseek-v4-pro:cloud");
        entry.PromptTokens.Should().Be(900);
        entry.CompletionTokens.Should().Be(120);
        entry.TokensUsed.Should().Be(1020);
        entry.ResponseTimeMs.Should().Be(1234);
        entry.Module.Should().Be("setup");
    }

    [Fact]
    public async Task FailedCall_IsRecordedAsFallback_ButStillBillsWhatTheProviderSpent()
    {
        // The prompt was sent and evaluated before the model produced nothing. That input spend
        // is real; zeroing it because the call failed understates the tenant's bill.
        var (recorder, audit) = Build();
        var response = new LlmResponse(false, "ollama", "deepseek-v4-pro:cloud", "", InputTokens: 900, OutputTokens: 0,
            Error: "model produced no content: the 8000-token budget was consumed");

        await recorder.RecordAsync(Record(AnyRequest(), response), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("fallback");
        entry.Provider.Should().Be("fallback");
        entry.PromptTokens.Should().Be(900);
        entry.TokensUsed.Should().Be(900);
        entry.Response.Should().Contain("8000-token budget");
    }

    [Fact]
    public async Task ProviderAnsweredButCallerCouldNotUseIt_CountsAsFallbackAndStillBills()
    {
        // The case three call sites each resolved differently. The provider really answered and
        // really charged; the user really got the deterministic path. Both facts must survive.
        var (recorder, audit) = Build();
        var response = new LlmResponse(true, "ollama", "deepseek-v4-pro:cloud",
            "Here is your configuration!", InputTokens: 1200, OutputTokens: 340);

        await recorder.RecordAsync(
            Record(AnyRequest(), response, failure: "replied, but the draft could not be read as valid configuration"),
            CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("fallback");          // the user did not get an AI answer
        entry.Provider.Should().Be("ollama");                   // but ollama is who replied
        entry.TokensUsed.Should().Be(1540);                     // and the spend is not lost
        entry.Response.Should().Contain("could not be read");
    }

    [Fact]
    public async Task CallThatNeverHappened_IsStillRecorded()
    {
        // "No provider configured" must leave a row too, otherwise a workspace with AI switched
        // off is indistinguishable from one where AI is broken.
        var (recorder, audit) = Build();

        await recorder.RecordAsync(Record(null, null, failure: "No AI provider is configured."), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("fallback");
        entry.Response.Should().Contain("No AI provider is configured");
        entry.TokensUsed.Should().Be(0);
    }

    [Fact]
    public async Task SuccessWithEmptyText_IsNotCountedAsSuccess()
    {
        var (recorder, audit) = Build();
        var response = new LlmResponse(true, "ollama", "m", "   ", InputTokens: 5, OutputTokens: 5);

        await recorder.RecordAsync(Record(AnyRequest(), response), CancellationToken.None);

        audit.Entries.Single().ResponseStatus.Should().Be("fallback");
    }

    [Fact]
    public async Task RawPromptNeverReachesTheLog()
    {
        // Recruitment prompts carry CV text and setup prompts carry company profiles. Neither may
        // land in a log row, and AI_LOG_PROMPTS must not be able to turn them on for these
        // modules -- AiAuditService swaps Query for PromptSummary, so both must be the summary.
        var (recorder, audit) = Build();
        var request = new LlmRequest("ollama", "m", "SYSTEM PROMPT SECRET", "USER PROMPT SECRET", 8000, RequireJson: true);

        await recorder.RecordAsync(Record(request, new LlmResponse(true, "ollama", "m", "{}")), CancellationToken.None);

        var entry = audit.Entries.Single();
        entry.Query.Should().NotContain("SECRET");
        entry.PromptSummary.Should().NotContain("SECRET");
        entry.Query.Should().Be(entry.PromptSummary);
    }

    [Fact]
    public async Task ResponseBodyIsSummarised_NotStored()
    {
        // The completion may contain candidate or employee data.
        var (recorder, audit) = Build();
        var response = new LlmResponse(true, "ollama", "m", "CANDIDATE NAME AND SALARY DETAIL");

        await recorder.RecordAsync(Record(AnyRequest(), response), CancellationToken.None);

        audit.Entries.Single().Response.Should().NotContain("CANDIDATE");
        audit.Entries.Single().Response.Should().Contain("chars");
    }

    [Fact]
    public async Task PromptHashIsStableForTheSameCall_AndDiffersAcrossModules()
    {
        var (recorder, audit) = Build();
        var ok = new LlmResponse(true, "ollama", "m", "{}");

        await recorder.RecordAsync(Record(AnyRequest(), ok), CancellationToken.None);
        await recorder.RecordAsync(Record(AnyRequest(), ok), CancellationToken.None);
        await recorder.RecordAsync(Record(AnyRequest(), ok) with { Module = "recruitment" }, CancellationToken.None);

        audit.Entries[0].PromptHash.Should().Be(audit.Entries[1].PromptHash);
        audit.Entries[2].PromptHash.Should().NotBe(audit.Entries[0].PromptHash);
    }

    [Fact]
    public async Task RecordingFailure_NeverPropagatesToTheCaller()
    {
        // The model call already happened. Losing its audit row must not also lose the user's
        // result -- and guaranteeing that here means no call site has to remember a try/catch.
        var (recorder, audit) = Build();
        audit.Throw = true;

        var act = async () => await recorder.RecordAsync(
            Record(AnyRequest(), new LlmResponse(true, "ollama", "m", "{}")), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
