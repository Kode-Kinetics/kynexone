using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Shifts;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Shifts;

namespace Zayra.Api.Tests;

/// <summary>
/// The roster planner calls a model and parses the reply as JSON, so it carried the same two
/// defects that made the setup assistant fall back to a template on every request against the
/// production reasoning model: no JSON constraint on the request, and a 4000-token budget a
/// reasoning model can spend before the answer starts. It also had no timeout, and — worst for
/// anyone trying to notice — recorded nothing at all, so a permanently degraded planner was
/// indistinguishable from a working one in every log and usage total.
///
/// These tests pin the corrected behaviour: the request is constrained and budgeted, each of the
/// five degrade paths names itself, exactly one usage record is written on every path including
/// "no provider configured", and no employee name reaches the record or the provider.
/// </summary>
public class RosterPlannerServiceTests
{
    // ── Harness ─────────────────────────────────────────────────────────────

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

    private sealed class RecordingRecorder : IAiCallRecorder
    {
        public List<AiCallRecord> Records { get; } = new();
        public Task RecordAsync(AiCallRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private static AiOptions Options(string provider = "ollama", string baseUrl = "https://ollama.com")
        => new(provider, "test-model", string.Empty, string.Empty, baseUrl, "key", 4096, true, false);

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DayShiftId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid NightShiftId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string EmployeeName = "Fatima Al-Otaibi";
    private const string SecondEmployeeName = "Yousef Al-Harbi";

    private static readonly DateOnly From = new(2026, 8, 17); // Monday
    private static readonly DateOnly To = new(2026, 8, 19);

    private static RosterPlanInput Input(int minRestHours = 8, int maxConsecutiveDays = 6)
        => new(
            From, To,
            new List<RosterPlanEmployee>
            {
                new(1, EmployeeName, "Female", "Operations"),
                new(2, SecondEmployeeName, "Male", "Operations"),
            },
            new List<RosterPlanShift>
            {
                new(DayShiftId, "DAY", "Day", new TimeOnly(8, 0), new TimeOnly(16, 0), "#2F6BFF"),
                new(NightShiftId, "NIGHT", "Night", new TimeOnly(20, 0), new TimeOnly(4, 0), "#123456"),
            },
            new RosterPlanPolicy(
                new List<GenderShiftRule>(),
                new List<string>(),
                new List<DemandTarget>(),
                new List<DemandTarget>(),
                minRestHours, maxConsecutiveDays),
            new HashSet<DateOnly>(),
            new HashSet<DateOnly>(),
            new RosterPlanCaller(TenantId, UserId, "HR Manager"));

    private static (RosterPlannerService Service, StubLlm Llm, RecordingRecorder Recorder) Build(
        Func<LlmRequest, LlmResponse> reply, AiOptions? options = null)
    {
        var llm = new StubLlm(reply);
        var recorder = new RecordingRecorder();
        var service = new RosterPlannerService(llm, options ?? Options(), recorder,
            NullLogger<RosterPlannerService>.Instance);
        return (service, llm, recorder);
    }

    private static LlmResponse Reply(string text) => new(true, "ollama", "test-model", text, 120, 340);

    /// <summary>What LlmClient now returns for a reasoning model that spent the whole budget thinking.</summary>
    private static LlmResponse EmptyCompletion() => new(false, "ollama", "test-model", string.Empty, 120, 8000,
        Error: "model produced no content: the 8000-token budget was consumed before the answer began (6122 chars of reasoning emitted)");

    private const string UsablePlan =
        """{"assignments":[{"employeeId":1,"date":"2026-08-17","shiftCode":"NIGHT"},{"employeeId":2,"date":"2026-08-17","shiftCode":"DAY"}]}""";

    // ── Contract A: the request is constrained and budgeted ─────────────────

    [Fact]
    public async Task Plan_AsksTheProviderForJson()
    {
        // The reply is parsed as JSON. Without this a reasoning model may fence or preface the
        // object, which is exactly what defeated the setup assistant's brace-span parsing.
        var (service, llm, _) = Build(_ => Reply(UsablePlan));

        await service.PlanAsync(Input(), CancellationToken.None);

        llm.LastRequest!.RequireJson.Should().BeTrue();
    }

    [Fact]
    public async Task Plan_LeavesRoomForReasoningTokens()
    {
        var (service, llm, _) = Build(_ => Reply(UsablePlan));

        await service.PlanAsync(Input(), CancellationToken.None);

        llm.LastRequest!.MaxOutputTokens.Should().BeGreaterThan(4000,
            "a reasoning model spends part of the budget before the first token of the roster");
    }

    [Fact]
    public async Task Plan_AsksForAJsonObject_NotABareArray()
    {
        // json_object (OpenAI) and format:"json" (Ollama) constrain the model to an OBJECT, so a
        // prompt demanding a top-level array cannot be honoured under Contract A.
        var (service, llm, _) = Build(_ => Reply(UsablePlan));

        await service.PlanAsync(Input(), CancellationToken.None);

        llm.LastRequest!.SystemPrompt.Should().Contain("assignments");
        llm.LastRequest!.SystemPrompt.Should().Contain("JSON object");
    }

    // ── The model's plan is used when it is usable ──────────────────────────

    [Fact]
    public async Task Plan_UsesTheModelsAssignments_WhenTheReplyIsUsable()
    {
        var (service, _, _) = Build(_ => Reply(UsablePlan));

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        result.Engine.Should().Be("ollama+guardrails");
        result.Assignments.Should().Contain(a =>
            a.EmployeeId == 1 && a.Date == From && a.ShiftCode == "NIGHT" && a.Reason == "AI suggestion");
        result.Warnings.Should().NotContain(w => w.Contains("rules engine"));
    }

    [Fact]
    public async Task Plan_AlsoAcceptsABareJsonArray()
    {
        // Backwards compatibility: a provider without a JSON mode still answers with the array.
        var (service, _, _) = Build(_ => Reply(
            """[{"employeeId":1,"date":"2026-08-17","shiftCode":"NIGHT"}]"""));

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        result.Engine.Should().Be("ollama+guardrails");
        result.Assignments.Should().Contain(a => a.EmployeeId == 1 && a.Date == From && a.ShiftCode == "NIGHT");
    }

    [Fact]
    public async Task Plan_AcceptsAFencedObject()
    {
        var (service, _, _) = Build(_ => Reply("Here you go:\n```json\n" + UsablePlan + "\n```"));

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        result.Engine.Should().Be("ollama+guardrails");
    }

    // ── Contract B: the five outcomes stay distinguishable ──────────────────

    [Fact]
    public async Task Plan_EmptyCompletion_DegradesCleanlyAndNamesTheBudget()
    {
        var (service, _, _) = Build(_ => EmptyCompletion());

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        result.Engine.Should().Be("deterministic (ollama returned no usable plan)");
        result.Warnings.Should().Contain(w => w.Contains("token budget was consumed"));
        // The plan itself must still be produced — degrading is not failing.
        result.Assignments.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Plan_UnparseableReply_SaysTheReplyCouldNotBeRead()
    {
        var (service, _, _) = Build(_ => Reply("Sure! I have rostered everyone fairly this week."));

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        result.Engine.Should().Be("deterministic (ollama reply unreadable)");
        result.Warnings.Should().Contain(w => w.Contains("could not be read as roster assignments"));
        result.Assignments.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Plan_NoProviderConfigured_NeverCallsTheModelAndSaysSo()
    {
        var (service, llm, _) = Build(_ => Reply(UsablePlan), Options(provider: "fallback", baseUrl: string.Empty));

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        llm.Calls.Should().Be(0);
        result.Engine.Should().Be("deterministic (no AI provider configured)");
        result.Warnings.Should().NotContain(w => w.Contains("unavailable"));
        result.Assignments.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Plan_TreatsOllamaWithoutABaseUrlAsNotConfigured()
    {
        // AI_PROVIDER=ollama with no OLLAMA_BASE_URL makes LlmClient target
        // http://localhost:11434, which on a hosted deploy hangs until the connect attempt dies.
        var (service, llm, recorder) = Build(_ => Reply(UsablePlan), Options(provider: "ollama", baseUrl: string.Empty));

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        llm.Calls.Should().Be(0);
        result.Engine.Should().Be("deterministic (no AI provider configured)");
        recorder.Records.Should().ContainSingle().Which.Request.Should().BeNull();
    }

    [Fact]
    public async Task Plan_Timeout_IsReportedAsATimeout()
    {
        var (service, _, recorder) = Build(_ => throw new OperationCanceledException());

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        result.Engine.Should().Be("deterministic (ollama timed out)");
        result.Warnings.Should().Contain(w => w.Contains("did not respond within"));
        recorder.Records.Should().ContainSingle().Which.FailureReason.Should().Contain("timed out");
    }

    [Fact]
    public async Task Plan_TransportFailure_IsReportedAsUnreachable()
    {
        var (service, _, _) = Build(_ => throw new HttpRequestException("Connection refused (localhost:11434)"));

        var result = await service.PlanAsync(Input(), CancellationToken.None);

        result.Engine.Should().Be("deterministic (ollama unreachable)");
        result.Warnings.Should().Contain(w => w.Contains("could not be reached"));
    }

    [Fact]
    public async Task Plan_DegradeReasons_AreAllDifferentFromEachOther()
    {
        // The whole point: "AI provider unavailable" covered all of these and sent two days of
        // debugging at the infrastructure when the fault was in the request.
        var engines = new List<string>();

        foreach (var (reply, options) in new (Func<LlmRequest, LlmResponse>, AiOptions?)[]
        {
            (_ => EmptyCompletion(), null),
            (_ => Reply("no json here"), null),
            (_ => throw new OperationCanceledException(), null),
            (_ => throw new HttpRequestException("boom"), null),
            (_ => Reply(UsablePlan), Options(provider: "fallback", baseUrl: string.Empty)),
        })
        {
            var (service, _, _) = Build(reply, options);
            engines.Add((await service.PlanAsync(Input(), CancellationToken.None)).Engine);
        }

        engines.Should().OnlyHaveUniqueItems();
        engines.Should().NotContain(e => e.Contains("unavailable"));
    }

    [Fact]
    public async Task Plan_PropagatesCallerCancellation_RatherThanPretendingItPlanned()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (service, _, recorder) = Build(_ => throw new OperationCanceledException(cts.Token));

        var act = async () => await service.PlanAsync(Input(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.Records.Should().ContainSingle().Which.FailureReason.Should().Contain("cancelled by the caller");
    }

    // ── Contract C: exactly one record on every path ────────────────────────

    [Fact]
    public async Task Plan_RecordsTheCall_OnSuccess()
    {
        var (service, _, recorder) = Build(_ => Reply(UsablePlan));

        await service.PlanAsync(Input(), CancellationToken.None);

        var record = recorder.Records.Should().ContainSingle().Subject;
        record.TenantId.Should().Be(TenantId);
        record.UserId.Should().Be(UserId);
        record.UserRole.Should().Be("HR Manager");
        record.Module.Should().Be("roster");
        record.Intent.Should().Be("shift_roster_plan");
        record.Request.Should().NotBeNull();
        record.Response!.Success.Should().BeTrue();
        record.FailureReason.Should().BeNull();
    }

    public static IEnumerable<object[]> FailurePaths() => new List<object[]>
    {
        new object[] { "empty-completion" },
        new object[] { "unparseable" },
        new object[] { "timeout" },
        new object[] { "transport" },
        new object[] { "not-configured" },
    };

    [Theory]
    [MemberData(nameof(FailurePaths))]
    public async Task Plan_RecordsExactlyOneCall_OnEveryFailurePath(string path)
    {
        Func<LlmRequest, LlmResponse> reply = path switch
        {
            "empty-completion" => _ => EmptyCompletion(),
            "unparseable" => _ => Reply("I could not do that."),
            "timeout" => _ => throw new OperationCanceledException(),
            "transport" => _ => throw new HttpRequestException("boom"),
            _ => _ => Reply(UsablePlan),
        };
        var options = path == "not-configured" ? Options(provider: "fallback", baseUrl: string.Empty) : null;
        var (service, _, recorder) = Build(reply, options);

        await service.PlanAsync(Input(), CancellationToken.None);

        var record = recorder.Records.Should().ContainSingle().Subject;
        record.TenantId.Should().Be(TenantId);
        record.FailureReason.Should().NotBeNullOrWhiteSpace();
        // No provider call was made, so there is no request to attribute tokens to.
        if (path == "not-configured") record.Request.Should().BeNull();
        else record.Request.Should().NotBeNull();

        // "unparseable" is the one failure where the provider answered and billed tokens: the
        // record must still show a provider success, with FailureReason explaining the degrade.
        // Collapsing it into a fallback would hide real spend.
        if (path == "unparseable") record.Response!.Success.Should().BeTrue();
        else (record.Response is null || !record.Response.Success).Should().BeTrue();
    }

    // ── No PII in the record, and none sent to the provider ─────────────────

    [Fact]
    public async Task PromptSummary_CarriesCountsOnly_NeverEmployeeNames()
    {
        var (service, _, recorder) = Build(_ => Reply(UsablePlan));

        await service.PlanAsync(Input(), CancellationToken.None);

        var summary = recorder.Records.Should().ContainSingle().Subject.PromptSummary;
        summary.Should().NotContain(EmployeeName);
        summary.Should().NotContain("Fatima");
        summary.Should().NotContain(SecondEmployeeName);
        summary.Should().NotContain("Operations");
        summary.Should().Contain("2 employee(s)");
        summary.Should().Contain("3 day(s)");
    }

    [Fact]
    public async Task PromptSummary_CarriesNoNames_OnTheNotConfiguredPathEither()
    {
        var (service, _, recorder) = Build(_ => Reply(UsablePlan), Options(provider: "fallback", baseUrl: string.Empty));

        await service.PlanAsync(Input(), CancellationToken.None);

        recorder.Records.Should().ContainSingle().Which.PromptSummary.Should().NotContain("Fatima");
    }

    [Fact]
    public async Task Plan_SendsNoEmployeeNamesToTheProvider()
    {
        // The model answers with employeeId only; a full staff roster of names was leaving the
        // tenant for no gain.
        var (service, llm, _) = Build(_ => Reply(UsablePlan));

        await service.PlanAsync(Input(), CancellationToken.None);

        llm.LastRequest!.UserPrompt.Should().NotContain(EmployeeName);
        llm.LastRequest!.UserPrompt.Should().NotContain(SecondEmployeeName);
        llm.LastRequest!.UserPrompt.Should().Contain("DAY");
    }
}
