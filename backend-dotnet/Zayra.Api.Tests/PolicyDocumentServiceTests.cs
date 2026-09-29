using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Tests;

/// <summary>
/// Policy Q&amp;A used to report every outcome identically: it stamped the CONFIGURED provider onto
/// the request, treated an empty completion as an answer, replaced any failure with the single
/// sentence "Unable to generate an answer at this time", and wrote no usage record at all — so a
/// permanently degraded assistant was indistinguishable from a working one, both on screen and in
/// the audit trail. These tests pin the corrected behaviour.
/// </summary>
public class PolicyDocumentServiceTests
{
    // ── Harness ─────────────────────────────────────────────────────────────

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string UserRole = "HR Officer";

    private const string PolicyText =
        "Annual leave entitlement is 30 calendar days per year for all confirmed staff.";
    private const string DocumentName = "Leave Policy 2025.pdf";
    private const string Question = "What is the annual leave entitlement?";

    private sealed class StubLlm : ILlmClient
    {
        private readonly Func<LlmRequest, LlmResponse> _reply;
        public LlmRequest? LastRequest { get; private set; }
        public int Calls { get; private set; }

        public StubLlm(Func<LlmRequest, LlmResponse> reply) => _reply = reply;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            LastRequest = request;
            Calls++;
            return Task.FromResult(_reply(request));
        }
    }

    /// <summary>An LLM that must never be reached (no provider configured).</summary>
    private static StubLlm ForbiddenLlm()
        => new(_ => throw new InvalidOperationException("No model call may be made without a configured provider."));

    /// <summary>The live production failure: provider up, reasoning budget consumed, no content.</summary>
    private static StubLlm ExhaustedBudgetLlm()
        => new(_ => new LlmResponse(false, "ollama", "test-model", string.Empty,
            Error: "model produced no content: the 6000-token budget was consumed before the answer began (4812 chars of reasoning emitted)"));

    private static StubLlm AnsweringLlm(string answer = "Confirmed staff receive 30 calendar days of annual leave (Leave Policy 2025.pdf). Advisory only.")
        => new(_ => new LlmResponse(true, "ollama", "test-model", answer, InputTokens: 120, OutputTokens: 40));

    /// <summary>Cancelled, but not by the caller's token — i.e. the service's own budget fired.</summary>
    private static StubLlm TimingOutLlm()
        => new(_ => throw new OperationCanceledException());

    /// <summary>Captures what the REAL <see cref="AiCallRecorder"/> would write to the database.</summary>
    private sealed class CapturingAudit : IAiAuditService
    {
        public List<AiAuditEntry> Entries { get; } = new();
        public Task LogAsync(AiAuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRecorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct)
            => throw new InvalidOperationException("audit table unavailable");
    }

    private static AiOptions Options(string provider = "ollama", string ollamaBaseUrl = "https://ollama.com")
        => new(provider, "test-model", string.Empty, string.Empty, ollamaBaseUrl, "key", 4096, true, false);

    private static ZayraDbContext MakeDb()
        => new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ZayraDbContext> SeededDb(string content = PolicyText, string status = "Ready")
    {
        var db = MakeDb();
        var doc = new PolicyDocument
        {
            TenantId = TenantId,
            FileName = DocumentName,
            OriginalName = DocumentName,
            MimeType = "application/pdf",
            FileSizeBytes = 1024,
            Status = status,
            ChunkCount = 1
        };
        db.PolicyDocuments.Add(doc);
        db.DocumentChunks.Add(new DocumentChunk
        {
            TenantId = TenantId,
            DocumentId = doc.Id,
            Document = doc,
            ChunkIndex = 0,
            Content = content,
            TokenCount = content.Length / 4
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static (PolicyDocumentService Service, CapturingAudit Audit) Service(
        ZayraDbContext db, ILlmClient llm, AiOptions? options = null)
    {
        var opts = options ?? Options();
        var audit = new CapturingAudit();
        var recorder = new AiCallRecorder(audit, new AiRedactionService(), opts, NullLogger<AiCallRecorder>.Instance);
        return (new PolicyDocumentService(db, llm, opts, recorder, NullLogger<PolicyDocumentService>.Instance), audit);
    }

    private static Task<PolicyAskResponse> Ask(PolicyDocumentService service, string question = Question)
        => service.AskAsync(TenantId, UserId, UserRole, question, CancellationToken.None);

    // ── (a) The surfaced provider is the one that actually answered ──────────

    [Fact]
    public async Task Ask_ReportsTheProviderThatAnswered_OnTheSuccessPath()
    {
        using var db = await SeededDb();
        var (service, _) = Service(db, AnsweringLlm());

        var result = await Ask(service);

        result.Provider.Should().Be("ollama");
        result.Model.Should().Be("test-model");
        result.DegradedReason.Should().BeNull();
        result.Answer.Should().Contain("30 calendar days");
        result.Sources.Should().ContainSingle().Which.Should().Be(DocumentName);
        result.IsGrounded.Should().BeTrue();
    }

    [Fact]
    public async Task Ask_ReportsFallback_NotTheConfiguredProvider_WhenTheModelDidNotAnswer()
    {
        // The defect this pins: Provider was taken from AI_PROVIDER, so an answer written by
        // deterministic code was attributed to "ollama".
        using var db = await SeededDb();
        var (service, _) = Service(db, ExhaustedBudgetLlm(), Options(provider: "ollama"));

        var result = await Ask(service);

        result.Provider.Should().Be("fallback");
        result.Model.Should().BeNull();
    }

    [Fact]
    public async Task Ask_HandsBackThePolicyTextItself_WhenTheModelDoesNotAnswer()
    {
        // Degrading to "Unable to generate an answer at this time" threw away five retrieved
        // excerpts the user was entitled to read.
        using var db = await SeededDb();
        var (service, _) = Service(db, ExhaustedBudgetLlm());

        var result = await Ask(service);

        result.Answer.Should().Contain(PolicyText);
        result.Answer.Should().Contain(DocumentName);
        result.Answer.Should().Contain("not an AI-written answer");
        result.Answer.Should().NotContain("Unable to generate an answer at this time");
        result.Sources.Should().ContainSingle();
    }

    // ── (b) The four degrade reasons are distinguishable ─────────────────────

    [Fact]
    public async Task Ask_SaysNoProviderIsConfigured_AndNeverCallsTheModel()
    {
        using var db = await SeededDb();
        var llm = ForbiddenLlm();
        var (service, audit) = Service(db, llm, Options(provider: "fallback", ollamaBaseUrl: string.Empty));

        var result = await Ask(service);

        llm.Calls.Should().Be(0);
        result.Provider.Should().Be("fallback");
        result.DegradedReason.Should().Contain("No AI assistant is configured");
        audit.Entries.Should().ContainSingle()
            .Which.Response.Should().Contain("no AI provider configured");
    }

    [Fact]
    public async Task Ask_SaysTheProviderWasConfiguredButDidNotAnswer()
    {
        // AI_PROVIDER=ollama, key valid, chat working — and this path still degraded. Calling that
        // "AI provider unavailable" is what sent two days of debugging to the wrong place.
        using var db = await SeededDb();
        var (service, _) = Service(db, ExhaustedBudgetLlm());

        var result = await Ask(service);

        result.DegradedReason.Should().Contain("could not produce an answer");
        result.DegradedReason.Should().NotContain("No AI assistant is configured");
        result.DegradedReason.Should().NotContain("did not respond within");
    }

    [Fact]
    public async Task Ask_SaysTheReplyHadNoUsableText_WhenTheClientReportsSuccessWithBlankText()
    {
        using var db = await SeededDb();
        var (service, _) = Service(db, new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", "   ")));

        var result = await Ask(service);

        result.DegradedReason.Should().Contain("without any usable text");
        result.Provider.Should().Be("fallback");
        result.Answer.Should().Contain(PolicyText);
    }

    [Fact]
    public async Task Ask_SaysItTimedOut_WhenTheModelDoesNotAnswerInTime()
    {
        using var db = await SeededDb();
        var (service, audit) = Service(db, TimingOutLlm());

        var result = await Ask(service);

        result.DegradedReason.Should().Contain("did not respond within");
        audit.Entries.Should().ContainSingle()
            .Which.Response.Should().Contain("did not respond within");
    }

    [Fact]
    public async Task Ask_DegradeReasons_AreAllDistinctFromEachOther()
    {
        async Task<string> Reason(ILlmClient llm, AiOptions options)
        {
            using var db = await SeededDb();
            var (service, _) = Service(db, llm, options);
            return (await Ask(service)).DegradedReason ?? string.Empty;
        }

        var reasons = new[]
        {
            await Reason(ForbiddenLlm(), Options(provider: "fallback", ollamaBaseUrl: string.Empty)),
            await Reason(ExhaustedBudgetLlm(), Options()),
            await Reason(new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", "  ")), Options()),
            await Reason(TimingOutLlm(), Options())
        };

        reasons.Should().OnlyHaveUniqueItems().And.NotContain(string.Empty);
    }

    [Fact]
    public async Task Ask_TellsTheUserWhenNoDocumentMatched_AndDoesNotClaimAnAiAnswer()
    {
        using var db = await SeededDb(content: "Company dress code is business casual.");
        var (service, audit) = Service(db, ForbiddenLlm());

        var result = await Ask(service, "What is the overtime multiplier?");

        result.IsGrounded.Should().BeFalse();
        result.Provider.Should().Be("fallback");
        result.Sources.Should().BeEmpty();
        // Still recorded: a tenant whose documents never match must not look like a working one.
        audit.Entries.Should().ContainSingle()
            .Which.Response.Should().Contain("no policy excerpt matched");
    }

    // ── (c) Contract A: this reply is prose, never parsed ───────────────────

    [Fact]
    public async Task Ask_DoesNotForceJson_BecauseTheAnswerIsProseShownToAHuman()
    {
        using var db = await SeededDb();
        var llm = AnsweringLlm();
        var (service, _) = Service(db, llm);

        await Ask(service);

        llm.LastRequest!.RequireJson.Should().BeFalse(
            "the answer is rendered to the user verbatim and is never parsed as JSON");
    }

    // ── (d) Reasoning-model budget ──────────────────────────────────────────

    [Fact]
    public async Task Ask_LeavesRoomForAReasoningModelToFinishAnAnswer()
    {
        using var db = await SeededDb();
        var llm = AnsweringLlm();
        var (service, _) = Service(db, llm);

        await Ask(service);

        llm.LastRequest!.MaxOutputTokens.Should().BeGreaterThan(4000,
            "1024 could be consumed by the reasoning pass before a token of the answer is emitted");
    }

    [Fact]
    public async Task Ask_SendsTheResolvedProviderAndModel_NotAnEmptyModelName()
    {
        using var db = await SeededDb();
        var llm = AnsweringLlm();
        var (service, _) = Service(db, llm);

        await Ask(service);

        llm.LastRequest!.Provider.Should().Be("ollama");
        llm.LastRequest!.Model.Should().Be("test-model");
    }

    // ── (Contract C) Exactly one usage record, on every path ────────────────

    [Fact]
    public async Task Ask_RecordsTheCallOnce_OnTheSuccessPath()
    {
        using var db = await SeededDb();
        var (service, audit) = Service(db, AnsweringLlm());

        await Ask(service);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.TenantId.Should().Be(TenantId);
        entry.UserId.Should().Be(UserId);
        entry.UserRole.Should().Be(UserRole);
        entry.Module.Should().Be("policy");
        entry.IntentClassified.Should().Be("policy_question");
        entry.ResponseStatus.Should().Be("provider_success");
        entry.Provider.Should().Be("ollama");
        entry.TokensUsed.Should().Be(160);
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("timeout")]
    [InlineData("empty")]
    [InlineData("unconfigured")]
    [InlineData("no-match")]
    public async Task Ask_RecordsExactlyOneFallbackRow_OnEveryFailurePath(string scenario)
    {
        var (llm, options) = scenario switch
        {
            "budget" => ((ILlmClient)ExhaustedBudgetLlm(), Options()),
            "timeout" => ((ILlmClient)TimingOutLlm(), Options()),
            "empty" => ((ILlmClient)new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", string.Empty)), Options()),
            "unconfigured" => ((ILlmClient)ForbiddenLlm(), Options(provider: "fallback", ollamaBaseUrl: string.Empty)),
            _ => ((ILlmClient)ForbiddenLlm(), Options(provider: "fallback", ollamaBaseUrl: string.Empty))
        };

        using var db = await SeededDb();
        var (service, audit) = Service(db, llm, options);

        await Ask(service, scenario == "no-match" ? "zzzz unrelated topic" : Question);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("fallback");
        entry.Provider.Should().Be("fallback");
        entry.TokensUsed.Should().Be(0);
        entry.Response.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Ask_StillAnswers_WhenRecordingItself_Fails()
    {
        // Bookkeeping must never cost the user their answer.
        using var db = await SeededDb();
        var opts = Options();
        var service = new PolicyDocumentService(db, AnsweringLlm(), opts, new ThrowingRecorder(),
            NullLogger<PolicyDocumentService>.Instance);

        var result = await Ask(service);

        result.Provider.Should().Be("ollama");
        result.Answer.Should().Contain("30 calendar days");
    }

    // ── (Contract C) Nothing sensitive reaches the record ───────────────────

    [Fact]
    public async Task Record_CarriesNoPolicyContent_NoDocumentName_AndNotTheQuestionItself()
    {
        const string sensitiveQuestion =
            "Can Fatima Al-Otaibi take unpaid medical leave for her chemotherapy treatment?";
        const string sensitivePolicy =
            "Medical leave: employees on chemotherapy receive 60 days at full salary of SAR 42,000.";

        using var db = await SeededDb(content: sensitivePolicy);
        var (service, audit) = Service(db, AnsweringLlm("Advisory answer."));

        await service.AskAsync(TenantId, UserId, UserRole, sensitiveQuestion, CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        foreach (var field in new[] { entry.PromptSummary, entry.Query, entry.Response })
        {
            field.Should().NotContain("Fatima");
            field.Should().NotContain("chemotherapy");
            field.Should().NotContain("42,000");
            field.Should().NotContain(DocumentName);
            field.Should().NotContain(sensitiveQuestion);
        }

        entry.PromptSummary.Should().Contain("policy question");
        entry.PromptSummary.Should().Contain("excerpt(s)");
    }

    [Fact]
    public async Task Ask_DoesNotShowRawProviderErrorsToTheUser()
    {
        // Any holder of ai.query can reach this endpoint; provider internals belong in the audit
        // row and the log, not in the answer bubble.
        using var db = await SeededDb();
        var (service, audit) = Service(db, new StubLlm(_ => new LlmResponse(false, "ollama", "test-model",
            string.Empty, Error: "401 {\"error\":\"invalid bearer token for https://ollama.com/api/chat\"}")));

        var result = await Ask(service);

        result.Answer.Should().NotContain("invalid bearer token");
        result.DegradedReason.Should().NotContain("401");
        // ...but the real reason is recorded, which is what was missing entirely before.
        audit.Entries.Should().ContainSingle()
            .Which.Response.Should().Contain("invalid bearer token");
    }

    // ── Back-compat overload ────────────────────────────────────────────────

    [Fact]
    public async Task Ask_WithoutCallerIdentity_StillRecords_AsSystem()
    {
        using var db = await SeededDb();
        var (service, audit) = Service(db, AnsweringLlm());

        await service.AskAsync(TenantId, Question, CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.UserId.Should().BeNull();
        entry.UserRole.Should().Be("System");
    }
}
