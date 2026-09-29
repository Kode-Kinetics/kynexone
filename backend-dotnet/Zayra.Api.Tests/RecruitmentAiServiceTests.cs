using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Tests;

/// <summary>
/// Recruitment's three model calls used to ask a REASONING model for JSON without switching the
/// provider into JSON mode, with a 2000-token budget that the model could spend entirely on
/// chain-of-thought, and recorded nothing whatsoever — no usage row, no cost record. That is the
/// same combination that left the setup assistant serving a built-in template on every request
/// while reporting "AI provider unavailable", invisibly, for an unknown length of time.
///
/// <para>These tests pin: the JSON constraint is sent, the budget leaves room for reasoning, each
/// of the four degrade reasons stays distinguishable, exactly one record is written on every path
/// including "no provider configured", and no candidate text reaches the record.</para>
/// </summary>
public class RecruitmentAiServiceTests
{
    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class StubLlm : ILlmClient
    {
        private readonly Func<LlmRequest, LlmResponse> _reply;
        public List<LlmRequest> Requests { get; } = new();
        public LlmRequest? LastRequest => Requests.LastOrDefault();
        public StubLlm(Func<LlmRequest, LlmResponse> reply) => _reply = reply;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_reply(request));
        }
    }

    private sealed class ThrowingLlm : ILlmClient
    {
        private readonly Func<Exception> _boom;
        public List<LlmRequest> Requests { get; } = new();
        public ThrowingLlm(Func<Exception> boom) => _boom = boom;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            throw _boom();
        }
    }

    private sealed class RecordingRecorder : IAiCallRecorder
    {
        public List<AiCallRecord> Records { get; } = new();
        public AiCallRecord Only => Records.Should().ContainSingle().Subject;
        public Task RecordAsync(AiCallRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    /// <summary>A recorder whose write fails. Bookkeeping must not fail the recruiter's request.</summary>
    private sealed class BrokenRecorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken cancellationToken)
            => throw new InvalidOperationException("audit table unavailable");
    }

    private static AiOptions Options(string provider = "ollama", string baseUrl = "https://ollama.com")
        => new(provider, "test-model", string.Empty, string.Empty, baseUrl, "key", 4096, true, false);

    private static RecruitmentAiService Service(ILlmClient llm, IAiCallRecorder recorder, AiOptions? options = null)
        => new(llm, recorder, options ?? Options(), NullLogger<RecruitmentAiService>.Instance);

    private static readonly RecruitmentAiCaller Caller =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "HR Manager");

    private static StubLlm EmptyCompletionLlm()
        // Exactly what the frozen client now returns when a reasoning model burns its budget
        // before the answer starts: Success:false, with the cause named.
        => new(r => new LlmResponse(false, "ollama", "test-model", string.Empty,
            Error: $"model produced no content: the {r.MaxOutputTokens}-token budget was consumed before the answer began (9123 chars of reasoning emitted)"));

    private static JobDescriptionRequest JobRequest()
        => new("Registered Nurse", "Nursing", "Staff Nurse", "FullTime", "Mid", "SA", "night rotation");

    private static readonly Guid CandidateA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CandidateB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static ScreeningInput Screening()
        => new("Senior Data Engineer", "Build the warehouse.", "python sql airflow",
            new List<CandidateForScreening>
            {
                new(CandidateA, "Fatima Al-Otaibi", "Data Engineer", 6m, "Bachelor", "python,sql"),
                new(CandidateB, "Bilal Haddad", "Analyst", 2m, "Diploma", "excel"),
            });

    // ── Contract A: the JSON constraint is actually sent ────────────────────

    [Fact]
    public async Task JobDescription_AsksTheProviderForJson()
    {
        var llm = EmptyCompletionLlm();
        await Service(llm, new RecordingRecorder()).GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        llm.LastRequest!.RequireJson.Should().BeTrue(
            "a reasoning model is otherwise free to fence or preface the object, which defeats brace-span parsing");
    }

    [Fact]
    public async Task Screening_AsksTheProviderForJson()
    {
        var llm = EmptyCompletionLlm();
        await Service(llm, new RecordingRecorder()).ScreenAsync(Caller, Screening(), CancellationToken.None);

        llm.LastRequest!.RequireJson.Should().BeTrue();
        // JSON mode returns an OBJECT on OpenAI, so the prompt must not demand a bare array.
        llm.LastRequest!.SystemPrompt.Should().Contain("\"candidates\"");
    }

    [Fact]
    public async Task InterviewQuestions_AsksTheProviderForJson()
    {
        var llm = EmptyCompletionLlm();
        await Service(llm, new RecordingRecorder()).GenerateInterviewQuestionsAsync(
            Caller, new InterviewQuestionsRequest("Site Engineer", "Senior", null), CancellationToken.None);

        llm.LastRequest!.RequireJson.Should().BeTrue();
    }

    // ── Contract D: the budget leaves room for reasoning ────────────────────

    [Fact]
    public async Task EveryCall_LeavesRoomForReasoningTokens()
    {
        var llm = EmptyCompletionLlm();
        var service = Service(llm, new RecordingRecorder());

        await service.GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);
        await service.GenerateInterviewQuestionsAsync(Caller, new InterviewQuestionsRequest("QA Lead", null, null), CancellationToken.None);
        await service.ScreenAsync(Caller, Screening(), CancellationToken.None);

        // The old value was 2000, which a reasoning model can spend entirely on `thinking`.
        llm.Requests.Should().OnlyContain(r => r.MaxOutputTokens >= 4000);
    }

    [Fact]
    public async Task Screening_BudgetGrowsWithCandidateCount_AndStaysCapped()
    {
        var llm = EmptyCompletionLlm();
        var many = Enumerable.Range(0, 200)
            .Select(i => new CandidateForScreening(Guid.NewGuid(), $"Candidate {i}", "Engineer", 3m, "Bachelor", "python"))
            .ToList();

        await Service(llm, new RecordingRecorder()).ScreenAsync(Caller, Screening(), CancellationToken.None);
        var small = llm.LastRequest!.MaxOutputTokens;

        await Service(llm, new RecordingRecorder()).ScreenAsync(
            Caller, new ScreeningInput("Role", "", "python", many), CancellationToken.None);
        var large = llm.LastRequest!.MaxOutputTokens;

        large.Should().BeGreaterThan(small, "screening emits one object per candidate");
        large.Should().BeLessThanOrEqualTo(16000, "an opening with 200 applicants must not ask for an unbounded generation");
    }

    // ── Contract B: the four degrade reasons stay distinguishable ───────────

    [Fact]
    public async Task NoProviderConfigured_DegradesAndSaysSo_WithoutCallingAnything()
    {
        var llm = EmptyCompletionLlm();
        var service = Service(llm, new RecordingRecorder(), Options(provider: "fallback", baseUrl: string.Empty));

        var result = await service.GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        llm.Requests.Should().BeEmpty();
        result.Engine.Should().Be("template (AI not configured)");
        result.Summary.Should().NotBeEmpty("the deterministic path still answers");
    }

    [Fact]
    public async Task EmptyCompletion_DegradesCleanly_AndBlamesTheProviderNotTheConfiguration()
    {
        var result = await Service(EmptyCompletionLlm(), new RecordingRecorder())
            .GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        result.Engine.Should().Be("template (AI returned nothing usable)");
        result.Engine.Should().NotContain("not configured");
        result.Responsibilities.Should().NotBeEmpty();
    }

    [Fact]
    public async Task UnreadableReply_DegradesCleanly()
    {
        var prose = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", "Sure! Here is a great job description."));
        var result = await Service(prose, new RecordingRecorder())
            .GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        result.Engine.Should().Be("template (AI reply unreadable)");
    }

    [Fact]
    public async Task BrokenJson_DegradesCleanly_RatherThanThrowing()
    {
        var broken = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", "{\"summary\": \"half a sen"));
        var result = await Service(broken, new RecordingRecorder())
            .GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        result.Engine.Should().Be("template (AI reply unreadable)");
    }

    [Fact]
    public async Task WronglyTypedFields_DegradeCleanly_RatherThanThrowing()
    {
        // Valid JSON, wrong shapes. JsonElement.GetString() throws on a number.
        var odd = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model",
            "{\"summary\": 42, \"responsibilities\": \"not a list\", \"requirements\": null}"));
        var result = await Service(odd, new RecordingRecorder())
            .GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        result.Engine.Should().StartWith("template");
        result.Requirements.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Timeout_IsReportedAsATimeout_NotAsAnUnavailableProvider()
    {
        // HttpClient's 120s timeout surfaces as a cancellation with OUR token un-cancelled.
        var llm = new ThrowingLlm(() => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 120 seconds elapsing."));
        var recorder = new RecordingRecorder();

        var result = await Service(llm, recorder).GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        result.Engine.Should().Be("template (AI timed out)");
        recorder.Only.FailureReason.Should().Contain("timeout");
    }

    [Fact]
    public async Task CallerCancellation_Propagates_AndIsNotRecordedAsADegradedAnswer()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var llm = new ThrowingLlm(() => new OperationCanceledException(cts.Token));
        var recorder = new RecordingRecorder();

        var act = () => Service(llm, recorder).GenerateJobDescriptionAsync(Caller, JobRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreeningNotes_NameWhichOfTheFourProblemsHappened()
    {
        var notConfigured = await Service(EmptyCompletionLlm(), new RecordingRecorder(),
            Options(provider: "fallback", baseUrl: string.Empty)).ScreenAsync(Caller, Screening(), CancellationToken.None);
        var answeredBadly = await Service(EmptyCompletionLlm(), new RecordingRecorder())
            .ScreenAsync(Caller, Screening(), CancellationToken.None);
        var unreadable = await Service(new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", "no json here")),
            new RecordingRecorder()).ScreenAsync(Caller, Screening(), CancellationToken.None);

        notConfigured.Notes.Should().Contain(n => n.Contains("No AI provider is configured"));
        answeredBadly.Notes.Should().Contain(n => n.Contains("did not return usable scores"));
        answeredBadly.Notes.Should().NotContain(n => n.Contains("No AI provider is configured"));
        unreadable.Notes.Should().Contain(n => n.Contains("could not be read as candidate scores"));

        // Whatever went wrong, every candidate still comes back ranked and nobody is auto-rejected.
        foreach (var r in new[] { notConfigured, answeredBadly, unreadable })
        {
            r.Engine.Should().Be("heuristic");
            r.Ranked.Should().HaveCount(2);
            r.Notes.Should().Contain(n => n.Contains("Advisory only"));
        }
    }

    // ── Contract C: exactly one record per call, on every path ──────────────

    [Fact]
    public async Task Record_IsWritten_WhenTheModelAnswers()
    {
        var llm = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model",
            "{\"summary\":\"A nurse.\",\"responsibilities\":[\"Care\"],\"requirements\":[\"License\"]}",
            InputTokens: 120, OutputTokens: 80));
        var recorder = new RecordingRecorder();

        var result = await Service(llm, recorder).GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        result.Engine.Should().Be("ollama+guardrails");
        var record = recorder.Only;
        record.Module.Should().Be("recruitment");
        record.Intent.Should().Be("job_description");
        record.TenantId.Should().Be(Caller.TenantId);
        record.UserId.Should().Be(Caller.UserId);
        record.UserRole.Should().Be("HR Manager");
        record.FailureReason.Should().BeNull();
        record.Request.Should().NotBeNull();
        record.Response!.OutputTokens.Should().Be(80, "the cost record has to carry what was actually spent");
    }

    [Fact]
    public async Task Record_IsWritten_WhenNoProviderIsConfigured_EvenThoughNoCallWasMade()
    {
        // The one degrade that leaves no provider-side trace: without this row nothing anywhere
        // shows that the feature never used AI.
        var recorder = new RecordingRecorder();
        await Service(EmptyCompletionLlm(), recorder, Options(provider: "fallback", baseUrl: string.Empty))
            .GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        var record = recorder.Only;
        record.Request.Should().BeNull();
        record.Response.Should().BeNull();
        record.FailureReason.Should().Contain("No AI provider is configured");
    }

    [Fact]
    public async Task Record_IsWritten_WhenTheProviderAnswersEmpty()
    {
        var recorder = new RecordingRecorder();
        await Service(EmptyCompletionLlm(), recorder).GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        var record = recorder.Only;
        record.Response!.Success.Should().BeFalse();
        // The reason must survive to the row, including that the budget went on reasoning.
        record.FailureReason.Should().Contain("budget was consumed before the answer began");
    }

    [Fact]
    public async Task Record_IsWritten_WhenTheReplyCannotBeParsed()
    {
        var recorder = new RecordingRecorder();
        await Service(new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", "{ not json }")), recorder)
            .GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        recorder.Only.FailureReason.Should().Contain("could not be read as a job description");
    }

    [Fact]
    public async Task Record_IsWritten_WhenTheCallThrows()
    {
        var recorder = new RecordingRecorder();
        await Service(new ThrowingLlm(() => new HttpRequestException("connection reset")), recorder)
            .GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        var record = recorder.Only;
        record.Request.Should().NotBeNull("a call was attempted");
        record.Response.Should().BeNull("it never came back");
        record.FailureReason.Should().Contain("connection reset");
    }

    [Fact]
    public async Task EveryEntryPoint_RecordsExactlyOnce()
    {
        var recorder = new RecordingRecorder();
        var service = Service(EmptyCompletionLlm(), recorder);

        await service.GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);
        await service.ScreenAsync(Caller, Screening(), CancellationToken.None);
        await service.GenerateInterviewQuestionsAsync(Caller, new InterviewQuestionsRequest("QA Lead", null, null), CancellationToken.None);

        recorder.Records.Should().HaveCount(3);
        recorder.Records.Select(r => r.Intent).Should()
            .BeEquivalentTo(new[] { "job_description", "candidate_screening", "interview_questions" });
    }

    [Fact]
    public async Task ScreeningWithNoCandidates_MakesNoModelCall_AndRecordsNothing()
    {
        var llm = EmptyCompletionLlm();
        var recorder = new RecordingRecorder();

        var result = await Service(llm, recorder).ScreenAsync(
            Caller, new ScreeningInput("Role", "", "", new List<CandidateForScreening>()), CancellationToken.None);

        result.Ranked.Should().BeEmpty();
        llm.Requests.Should().BeEmpty();
        recorder.Records.Should().BeEmpty("no model call was made, so there is nothing to bill");
    }

    [Fact]
    public async Task ARecorderThatFails_DoesNotFailTheRecruitersRequest()
    {
        var result = await Service(EmptyCompletionLlm(), new BrokenRecorder())
            .GenerateJobDescriptionAsync(Caller, JobRequest(), CancellationToken.None);

        result.Summary.Should().NotBeEmpty();
    }

    // ── Candidate text must not leak ────────────────────────────────────────

    [Fact]
    public async Task PromptSummary_CarriesNoCandidateOrRoleText()
    {
        var recorder = new RecordingRecorder();
        await Service(EmptyCompletionLlm(), recorder).ScreenAsync(Caller, Screening(), CancellationToken.None);

        var summary = recorder.Only.PromptSummary;
        summary.Should().NotContain("Fatima");
        summary.Should().NotContain("Al-Otaibi");
        summary.Should().NotContain("Bilal");
        summary.Should().NotContain("Data Engineer");
        summary.Should().NotContain("python");
        summary.Should().Contain("2 candidate(s)", "counts are what an auditor needs");
    }

    [Fact]
    public async Task PromptSummary_CarriesNoFreeTextFromTheJobDescriptionForm()
    {
        var recorder = new RecordingRecorder();
        await Service(EmptyCompletionLlm(), recorder).GenerateJobDescriptionAsync(
            Caller, JobRequest() with { Notes = "call Dr. Khalid on 0555123456" }, CancellationToken.None);

        recorder.Only.PromptSummary.Should().NotContain("Khalid");
        recorder.Only.PromptSummary.Should().NotContain("0555123456");
    }

    [Fact]
    public async Task ScreeningPrompt_DoesNotSendCandidateNamesToTheProvider()
    {
        var llm = EmptyCompletionLlm();
        await Service(llm, new RecordingRecorder()).ScreenAsync(Caller, Screening(), CancellationToken.None);

        var prompt = llm.LastRequest!.UserPrompt;
        prompt.Should().NotContain("Fatima");
        prompt.Should().NotContain("Bilal");
        prompt.Should().Contain("ref=c1", "the model scores against an opaque reference instead");
    }

    // ── The happy path still works ──────────────────────────────────────────

    [Fact]
    public async Task Screening_AppliesModelScores_AndFillsAnyCandidateTheModelSkipped()
    {
        var llm = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model",
            "{\"candidates\":[{\"ref\":\"c1\",\"score\":91,\"recommendation\":\"Shortlist\",\"rationale\":\"Strong pipeline experience.\"}]}"));
        var recorder = new RecordingRecorder();

        var result = await Service(llm, recorder).ScreenAsync(Caller, Screening(), CancellationToken.None);

        result.Engine.Should().Be("ollama+guardrails");
        result.Ranked.Should().HaveCount(2);
        var top = result.Ranked.First();
        top.CandidateId.Should().Be(CandidateA);
        top.Name.Should().Be("Fatima Al-Otaibi", "the name is resolved locally, never round-tripped through the model");
        top.Score.Should().Be(91);
        result.Notes.Should().Contain(n => n.Contains("1 of 2 candidates were not returned"));
        recorder.Only.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task Screening_StillAcceptsABareArrayAndRawGuids()
    {
        // Backwards compatible with the previous prompt shape, so a model that ignores the
        // wrapper does not cost the recruiter their screening.
        var llm = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model",
            $"[{{\"candidateId\":\"{CandidateB}\",\"score\":\"77\",\"recommendation\":\"maybe\",\"rationale\":\"Some overlap.\"}}]"));

        var result = await Service(llm, new RecordingRecorder()).ScreenAsync(Caller, Screening(), CancellationToken.None);

        var scored = result.Ranked.Single(r => r.CandidateId == CandidateB);
        scored.Score.Should().Be(77);
        scored.Recommendation.Should().Be("Maybe");
    }

    [Fact]
    public async Task InterviewQuestions_AreUsedWhenTheModelAnswers()
    {
        var llm = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model",
            "```json\n{\"categories\":[{\"category\":\"Technical\",\"questions\":[\"Explain indexing.\"]}]}\n```"));
        var recorder = new RecordingRecorder();

        var result = await Service(llm, recorder).GenerateInterviewQuestionsAsync(
            Caller, new InterviewQuestionsRequest("DBA", "Senior", null), CancellationToken.None);

        result.Engine.Should().Be("ollama+guardrails");
        result.Categories.Should().ContainSingle().Which.Category.Should().Be("Technical");
        recorder.Only.FailureReason.Should().BeNull();
    }
}
