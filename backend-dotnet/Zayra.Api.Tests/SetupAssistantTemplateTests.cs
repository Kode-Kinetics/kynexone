using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Setup;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Setup;

namespace Zayra.Api.Tests;

/// <summary>
/// The setup assistant's fallback used to be a fixed Saudi template that ignored industry, company
/// size and the free-text note, stamped the tenant's currency onto SAR figures, and appended a
/// Saudi GOSI deduction for every country on earth. It reported all of that as "AI provider
/// unavailable" even when the provider was up. These tests pin the corrected behaviour.
/// </summary>
public class SetupAssistantTemplateTests
{
    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class StubLlm : ILlmClient
    {
        private readonly Func<LlmRequest, LlmResponse> _reply;
        public LlmRequest? LastRequest { get; private set; }
        public StubLlm(Func<LlmRequest, LlmResponse> reply) => _reply = reply;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_reply(request));
        }
    }

    private static AiOptions Options(string provider = "ollama", string baseUrl = "https://ollama.com")
        => new(provider, "test-model", string.Empty, string.Empty, baseUrl, "key", 4096, true, false);

    private static SetupAssistantService Service(ILlmClient llm, AiOptions? options = null,
        IAiCallRecorder? recorder = null, ISetupStatutoryDefaults? statutory = null)
        => new(llm, options ?? Options(), recorder ?? new NoOpRecorder(),
               statutory ?? new StubStatutory(), NullLogger<SetupAssistantService>.Instance);

    /// <summary>
    /// The platform statutory rules the assistant reads for entitlements and overtime rates. The
    /// default is EMPTY on purpose: most tests here are about the template's own tailoring, and an
    /// empty rule set is also the real behaviour for every country the seeder does not cover.
    /// </summary>
    private sealed class StubStatutory : ISetupStatutoryDefaults
    {
        private readonly Dictionary<string, string> _rules;
        public StubStatutory(params (string Key, string Value)[] rules)
            => _rules = rules.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyDictionary<string, string>> LoadAsync(string iso3, Guid tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(_rules);
    }

    /// <summary>The KSA subset this assistant actually reads, with the seeder's own values.</summary>
    private static StubStatutory KsaRules() => new(
        ("leave.annual_base_days", "21"),
        ("leave.sick_band1_days", "30"),
        ("ot.standard_multiplier", "1.5"),
        ("ot.restday_multiplier", "2.0"),
        ("ot.holiday_multiplier", "2.0"),
        ("ot.standard_monthly_hours", "240"));

    /// <summary>An LLM that always fails, so every test below exercises the deterministic template.</summary>
    private static StubLlm FailingLlm()
        => new(_ => new LlmResponse(false, "ollama", "test-model", string.Empty, Error: "boom"));

    /// <summary>An LLM that never answers inside the budget. The service's timeout guard is
    /// "cancelled, but not by the caller's token", which is exactly this.</summary>
    private static StubLlm TimingOutLlm()
        => new(_ => throw new OperationCanceledException());

    private sealed class NoOpRecorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Captures what was written, through the real <see cref="AiCallRecorder"/>, so the
    /// assertions are about the row that would reach the database rather than about the call site's
    /// intentions.</summary>
    private sealed class CapturingAudit : IAiAuditService
    {
        public List<AiAuditEntry> Entries { get; } = new();
        public Task LogAsync(AiAuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static (SetupAssistantService Service, CapturingAudit Audit) Recording(
        ILlmClient llm, AiOptions? options = null)
    {
        var opts = options ?? Options();
        var audit = new CapturingAudit();
        var recorder = new AiCallRecorder(audit, new AiRedactionService(), opts, NullLogger<AiCallRecorder>.Instance);
        return (Service(llm, opts, recorder), audit);
    }

    private static readonly SetupRequester Caller =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "HR Manager");

    private static CompanyProfile Profile(
        string country = "SA", string industry = "General", string size = "51-200",
        string currency = "SAR", string? notes = null, SetupSections? sections = null,
        string? workPattern = null, string? weekendPattern = null, string? leaveYearBasis = null,
        int probationMonths = 0, int noticePeriodDays = 0, string? workforceMix = null,
        string? overtimeHandling = null, string? attendanceCapture = null, string? payCycle = null,
        string? timeZone = null, string? defaultLanguage = null)
        => new(country, industry, size, currency, notes, "Evostel", null,
               "Functional", "GradeBased", "HRFinal", true, true, true,
               sections ?? new SetupSections(Org: true, Leave: true, Shifts: true, Payroll: true, Entity: true, Governance: true),
               workPattern, weekendPattern, leaveYearBasis, probationMonths, noticePeriodDays,
               workforceMix, overtimeHandling, attendanceCapture, payCycle, timeZone, defaultLanguage);

    private static SetupSections AllSections(
        bool org = true, bool leave = true, bool shifts = true, bool payroll = true, bool entity = true,
        bool governance = true, bool leavePolicies = true, bool holidays = true, bool attendance = true,
        bool localization = true)
        => new(org, leave, shifts, payroll, entity, governance, leavePolicies, holidays, attendance, localization);

    private static Task<SetupPreviewResult> Generate(
        CompanyProfile p, ILlmClient? llm = null, ISetupStatutoryDefaults? statutory = null)
        => Service(llm ?? FailingLlm(), statutory: statutory).GenerateAsync(Caller, p, CancellationToken.None);

    // ── Industry tailoring ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Healthcare", "NURS", "NURSE")]
    [InlineData("Construction", "HSE", "SITE_ENG")]
    [InlineData("Logistics", "WHSE", "DRIVER")]
    [InlineData("Manufacturing", "PROD", "QA_INSP")]
    [InlineData("Technology", "ENG", "SW_ENG")]
    public async Task Template_AddsIndustrySpecificOrgStructure(string industry, string dept, string designation)
    {
        var result = await Generate(Profile(industry: industry));

        result.Draft.Departments.Select(x => x.Code).Should().Contain(dept);
        result.Draft.Designations.Select(x => x.Code).Should().Contain(designation);
        // The core four survive alongside the industry pack.
        result.Draft.Departments.Select(x => x.Code).Should().Contain(new[] { "HR", "FIN", "IT", "ADMIN" });
    }

    [Fact]
    public async Task Template_ForDifferentIndustries_DoesNotReturnIdenticalDrafts()
    {
        // The exact defect: every industry produced byte-identical output.
        var hospital = await Generate(Profile(industry: "Healthcare"));
        var software = await Generate(Profile(industry: "Technology"));

        hospital.Draft.Departments.Select(x => x.Code)
            .Should().NotBeEquivalentTo(software.Draft.Departments.Select(x => x.Code));
    }

    // ── Company size ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1-50", 3)]
    [InlineData("51-200", 5)]
    [InlineData("200+", 7)]
    [InlineData("2000", 7)]
    public async Task Template_GradeLadderDepthFollowsHeadcount(string size, int expectedGrades)
    {
        var result = await Generate(Profile(size: size));
        result.Draft.Grades.Should().HaveCount(expectedGrades);
    }

    [Fact]
    public async Task Template_EveryDesignationPointsAtAGradeThatExists()
    {
        // Roles resolve to a ladder position, so a 3-rung ladder must not leave "G4" dangling.
        foreach (var size in new[] { "1-50", "51-200", "200+" })
        {
            var result = await Generate(Profile(size: size));
            var codes = result.Draft.Grades.Select(g => g.Code).ToHashSet();
            result.Draft.Designations.Should().OnlyContain(d => codes.Contains(d.GradeCode));
        }
    }

    // ── Statutory deductions are country-scoped ─────────────────────────────

    [Fact]
    public async Task Template_AddsGosiForSaudiOnly()
    {
        var saudi = await Generate(Profile(country: "SA"));
        saudi.Draft.PayComponents.Select(x => x.Code).Should().Contain("GOSI_DED");
    }

    [Theory]
    [InlineData("AE", "GPSSA_DED")]
    public async Task Template_UsesTheLocalContributionComponent(string country, string expected)
    {
        var result = await Generate(Profile(country: country, currency: "AED"));
        result.Draft.PayComponents.Select(x => x.Code).Should().Contain(expected);
        result.Draft.PayComponents.Select(x => x.Code).Should().NotContain("GOSI_DED");
    }

    [Theory]
    [InlineData("KW")]
    [InlineData("OM")]
    public async Task Template_NeverLeaksSaudiGosiIntoOtherCountries(string country)
    {
        var result = await Generate(Profile(country: country));

        result.Draft.PayComponents.Select(x => x.Code).Should().NotContain("GOSI_DED");
        result.Notes.Should().Contain(n => n.Contains("No statutory payroll deduction"));
    }

    // ── Currency ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Template_SarBandsAreUnchanged()
    {
        // Regression guard: the KSA output that shipped must not move.
        var result = await Generate(Profile(currency: "SAR"));
        var entry = result.Draft.Grades.First();

        entry.MinSalary.Should().Be(3000m);
        entry.MaxSalary.Should().Be(6000m);
        result.Draft.Grades.Last().MaxSalary.Should().Be(50000m);
    }

    [Fact]
    public async Task Template_ConvertsBandsForPeggedCurrencies()
    {
        var result = await Generate(Profile(country: "US", currency: "USD"));
        var entry = result.Draft.Grades.First();

        // 3000 SAR at the 3.75 peg is 800 USD -- not "USD 3000".
        entry.MinSalary.Should().Be(800m);
        entry.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task Template_LeavesBandsEmptyAndFlagsThemForUnpeggedCurrencies()
    {
        var result = await Generate(Profile(country: "KW", currency: "KWD"));

        result.Draft.Grades.Should().OnlyContain(g => g.MinSalary == 0 && g.MidSalary == 0 && g.MaxSalary == 0);
        result.Notes.Should().Contain(n => n.Contains("KWD") && n.Contains("left at zero"));
    }

    // ── Shifts follow stated operations ─────────────────────────────────────

    [Fact]
    public async Task Template_OfficeBusinessGetsOneDayShift()
    {
        var result = await Generate(Profile(industry: "Technology"));
        result.Draft.Shifts.Should().ContainSingle().Which.Code.Should().Be("DAY");
    }

    [Theory]
    [InlineData("we run 24/7 operations with field crews")]
    [InlineData("Round-the-clock cover is required")]
    public async Task Template_FreeTextNoteCanTurnOnShiftRotation(string note)
    {
        var result = await Generate(Profile(industry: "Technology", notes: note));

        result.Draft.Shifts.Select(x => x.Code).Should().BeEquivalentTo("MORNING", "EVENING", "NIGHT");
        result.Notes.Should().NotContain(n => n.Contains("free-text note was not applied"));
    }

    [Fact]
    public async Task Template_SaysSoWhenItCouldNotUseTheFreeTextNote()
    {
        // Silently discarding the note is what made the form look like it did something.
        var result = await Generate(Profile(industry: "Technology", notes: "we hire mostly part-time students"));
        result.Notes.Should().Contain(n => n.Contains("free-text note was not applied"));
    }

    // ── The banner tells the truth about why AI was not used ────────────────

    [Fact]
    public async Task NeverSilentlySwitchesVendor_WhenTheConfiguredProviderIsUnusable()
    {
        // AI_PROVIDER=ollama, no base URL, and a stray ANTHROPIC_API_KEY in the environment.
        // This used to resolve to "anthropic" and post the tenant's company profile to a vendor
        // nobody selected — while the published privacy policy names Ollama. Degrade instead.
        var options = new AiOptions("ollama", "test-model",
            AnthropicApiKey: "sk-ant-stray-key", OpenAIApiKey: "sk-stray-key",
            OllamaBaseUrl: string.Empty, OllamaApiKey: string.Empty,
            MaxContextTokens: 4096, RequireHumanReview: true, LogPrompts: false);
        var llm = FailingLlm();

        var result = await Service(llm, options).GenerateAsync(Caller, Profile(), CancellationToken.None);

        llm.LastRequest.Should().BeNull("no provider call may be made at all");
        result.Notes.Should().Contain(n => n.Contains("No AI provider is configured"));
        result.Engine.Should().StartWith("deterministic template");
    }

    [Fact]
    public async Task Notes_SayNotConfigured_WhenNoProviderIsSet()
    {
        var service = Service(FailingLlm(), Options(provider: "fallback", baseUrl: string.Empty));
        var result = await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        result.Notes.Should().Contain(n => n.Contains("No AI provider is configured"));
        result.Notes.Should().NotContain(n => n.Contains("unavailable"));
    }

    [Fact]
    public async Task Notes_SayTheCallFailed_WhenTheProviderIsConfiguredButAnswersBadly()
    {
        // The live case: AI_PROVIDER=ollama, key valid, chat working -- and this path still fell
        // back. Reporting that as "provider unavailable" sent debugging to the wrong place.
        var result = await Generate(Profile());

        result.Notes.Should().Contain(n => n.Contains("did not return a usable draft"));
        result.Notes.Should().NotContain(n => n.Contains("No AI provider is configured"));
    }

    [Fact]
    public async Task Notes_SayTheReplyWasUnreadable_WhenTheModelReturnsNonJson()
    {
        var llm = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", "Here is your configuration!"));
        var result = await Generate(Profile(), llm);

        result.Notes.Should().Contain(n => n.Contains("could not be read as valid configuration"));
    }

    [Fact]
    public async Task Preview_AsksTheProviderForJson()
    {
        // Without this the model may fence or preface the object, which defeats brace-span parsing.
        var llm = FailingLlm();
        await Generate(Profile(), llm);

        llm.LastRequest!.RequireJson.Should().BeTrue();
        llm.LastRequest!.MaxOutputTokens.Should().BeGreaterThan(4000,
            "a reasoning model spends part of the budget before the JSON starts");
    }

    [Fact]
    public async Task Engine_NamesWhatTheTemplateKeyedOff()
    {
        var result = await Generate(Profile(industry: "Healthcare", size: "1-50"));

        result.Engine.Should().Contain("deterministic template");
        result.Engine.Should().Contain("Healthcare");
        result.Engine.Should().Contain("small-org ladder");
    }

    [Fact]
    public async Task Engine_ReportsTheProvider_WhenTheModelSucceeds()
    {
        var llm = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model",
            """{"departments":[{"code":"OPS","nameEn":"Operations"}],"grades":[{"code":"G1","name":"Grade 1","level":1,"minSalary":1,"midSalary":2,"maxSalary":3,"currency":"SAR"}]}"""));
        var result = await Generate(Profile(), llm);

        result.Engine.Should().Be("ollama+guardrails");
        result.Notes.Should().NotContain(n => n.Contains("starter template"));
        result.Draft.Departments.Should().ContainSingle().Which.Code.Should().Be("OPS");
    }

    [Fact]
    public async Task Notes_SayTheProviderTimedOut_WhenItDoesNotAnswerInBudget()
    {
        var result = await Generate(Profile(), TimingOutLlm());

        result.Notes.Should().Contain(n => n.Contains("did not respond within 75s"));
        result.Notes.Should().NotContain(n => n.Contains("could not be reached"));
    }

    // ── Usage recording ─────────────────────────────────────────────────────
    //
    // The reason nobody could say how long this feature had been degraded: it called ILlmClient
    // directly and wrote nothing. Every path below must leave exactly one row.

    private const string SuccessJson =
        """{"departments":[{"code":"OPS","nameEn":"Operations"}],"grades":[{"code":"G1","name":"Grade 1","level":1,"minSalary":1,"midSalary":2,"maxSalary":3,"currency":"SAR"}]}""";

    private static StubLlm AnsweringLlm()
        => new(_ => new LlmResponse(true, "ollama", "deepseek-v4-pro:cloud", SuccessJson, InputTokens: 1200, OutputTokens: 340));

    [Fact]
    public async Task Record_IsWritten_WhenTheModelAnswers()
    {
        var (service, audit) = Recording(AnsweringLlm());

        await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.TenantId.Should().Be(Caller.TenantId);
        entry.UserId.Should().Be(Caller.UserId);
        entry.UserRole.Should().Be("HR Manager");
        entry.Module.Should().Be("setup");
        entry.IntentClassified.Should().Be("starter_configuration");
        entry.ResponseStatus.Should().Be("provider_success");
        entry.Provider.Should().Be("ollama");
        entry.Model.Should().Be("deepseek-v4-pro:cloud");
        entry.PromptTokens.Should().Be(1200);
        entry.CompletionTokens.Should().Be(340);
        entry.TokensUsed.Should().Be(1540);
        entry.WasBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Record_IsWritten_WhenNoProviderIsConfigured()
    {
        // The call that never happens is still a decision to serve a template, and until now it
        // was the one that left no trace at all.
        var (service, audit) = Recording(FailingLlm(), Options(provider: "fallback", baseUrl: string.Empty));

        await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("fallback");
        entry.Provider.Should().Be("fallback");
        entry.Model.Should().BeEmpty("no request was ever built");
        entry.TokensUsed.Should().Be(0);
        entry.Response.Should().Contain("No AI provider is configured");
    }

    [Fact]
    public async Task Record_IsWritten_WhenTheCallFails()
    {
        var (service, audit) = Recording(FailingLlm());

        await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("fallback");
        entry.TokensUsed.Should().Be(0);
        entry.Model.Should().Be("test-model", "the attempted model is still worth knowing");
        entry.Response.Should().Contain("did not return a usable draft");
    }

    [Fact]
    public async Task Record_IsWritten_WhenTheProviderTimesOut()
    {
        var (service, audit) = Recording(TimingOutLlm());

        await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("fallback");
        entry.TokensUsed.Should().Be(0);
        entry.Response.Should().Contain("did not respond within 75s");
    }

    [Fact]
    public async Task Record_IsWrittenAsFallback_WhenTheReplyCannotBeParsed()
    {
        // Two facts that must both survive: the user did NOT get an AI answer (status is
        // fallback), and the provider DID charge for the attempt (tokens are billed). The first
        // draft of the recorder forced a choice between them; it no longer does.
        var llm = new StubLlm(_ => new LlmResponse(true, "ollama", "test-model", "Here is your configuration!",
            InputTokens: 900, OutputTokens: 60));
        var (service, audit) = Recording(llm);

        await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ResponseStatus.Should().Be("fallback");           // the user got the template
        entry.Provider.Should().Be("ollama");                    // but ollama is who replied
        entry.TokensUsed.Should().Be(960);                       // and 900+60 was really spent
        entry.Response.Should().Contain("could not be read as valid configuration");
    }

    [Fact]
    public async Task Record_SummarisesTheRequestWithoutTheFreeTextNote()
    {
        // The note box is where a user pastes anything at all; it is a prompt input, never a log
        // value. Same for the legal entity name.
        const string secret = "our CFO Mariam Al-Otaibi earns 48000 and banks at IBAN SA03";
        var profile = Profile(industry: "Healthcare", size: "1-50", country: "SA", currency: "SAR", notes: secret);
        var (service, audit) = Recording(AnsweringLlm());

        await service.GenerateAsync(Caller, profile, CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        foreach (var field in new[] { entry.PromptSummary, entry.Query, entry.Response, entry.PromptHash })
        {
            field.Should().NotContain("Mariam");
            field.Should().NotContain("48000");
            field.Should().NotContain("IBAN");
            field.Should().NotContain("Evostel", "the legal entity name is not needed to describe the request");
        }

        // It still has to be useful: what was asked for, not who asked.
        entry.PromptSummary.Should().Contain("Healthcare").And.Contain("small-org ladder").And.Contain("SAU");
        entry.PromptSummary.Should().Contain("sections: org,entity,leave,shifts,payroll,governance");
        entry.PromptSummary.Length.Should().BeLessThan(200);
    }

    [Fact]
    public async Task Record_KeepsFreeTextOutOfTheRow_EvenWhenPromptLoggingIsOn()
    {
        // AI_LOG_PROMPTS is honoured for the chat assistant. It must not be able to turn the
        // company profile into a log row here, so the summary is the only thing this path has.
        var options = new AiOptions("ollama", "test-model", string.Empty, string.Empty, "https://ollama.com", "key", 4096, true, LogPrompts: true);
        var (service, audit) = Recording(AnsweringLlm(), options);

        await service.GenerateAsync(Caller, Profile(notes: "night shift for the Jeddah plant, staff list attached"), CancellationToken.None);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Query.Should().NotContain("Jeddah");
        entry.Query.Should().Be(entry.PromptSummary);
    }

    [Fact]
    public async Task Record_IsWrittenOncePerPreview()
    {
        var (service, audit) = Recording(AnsweringLlm());

        await service.GenerateAsync(Caller, Profile(), CancellationToken.None);
        await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        audit.Entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Preview_StillReturnsADraft_WhenRecordingFails()
    {
        // Bookkeeping must never cost the tenant their draft.
        var service = Service(AnsweringLlm(), recorder: new ThrowingRecorder());

        var result = await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        result.Engine.Should().Be("ollama+guardrails");
        result.Draft.Departments.Should().ContainSingle().Which.Code.Should().Be("OPS");
    }

    [Fact]
    public async Task Preview_StillReturnsTheTemplate_WhenRecordingFailsOnTheFallbackPath()
    {
        var service = Service(FailingLlm(), recorder: new ThrowingRecorder());

        var result = await service.GenerateAsync(Caller, Profile(), CancellationToken.None);

        result.Notes.Should().Contain(n => n.Contains("did not return a usable draft"));
        result.Draft.Departments.Should().NotBeEmpty();
    }

    private sealed class ThrowingRecorder : IAiCallRecorder
    {
        public Task RecordAsync(AiCallRecord record, CancellationToken ct)
            => throw new InvalidOperationException("audit table unavailable");
    }

    // ── Operating choices → configuration ───────────────────────────────────
    //
    // The form used to collect thirteen values and the draft used to fill a fixed set of columns
    // that overlapped them only partly. These pin the new ones to the columns they land in.

    [Theory]
    [InlineData("SingleDayShift", 1)]
    [InlineData("TwoShifts", 2)]
    [InlineData("ContinuousThreeShifts", 3)]
    [InlineData("FieldRoster", 2)]
    public async Task WorkPattern_DecidesTheShiftSet(string pattern, int expected)
    {
        var result = await Generate(Profile(workPattern: pattern));
        result.Draft.Shifts.Should().HaveCount(expected);
    }

    [Fact]
    public async Task WorkPattern_OverridesTheIndustryGuess()
    {
        // Healthcare is a continuous-operations pack, so it used to get three shifts whatever the
        // company said. A clinic that closes at night is not a hospital that does not.
        var inferred = await Generate(Profile(industry: "Healthcare"));
        var stated = await Generate(Profile(industry: "Healthcare", workPattern: "SingleDayShift"));

        inferred.Draft.Shifts.Should().HaveCount(3);
        stated.Draft.Shifts.Should().ContainSingle().Which.Code.Should().Be("DAY");
    }

    [Theory]
    [InlineData("Fri-Sat", "Sun-Thu", "Sunday")]
    [InlineData("Sat-Sun", "Mon-Fri", "Monday")]
    [InlineData("Fri", "Sat-Thu", "Saturday")]
    [InlineData("Sun", "Mon-Sat", "Monday")]
    public async Task WeekendPattern_BecomesTheWorkingWeek(string weekend, string workWeek, string startDay)
    {
        var result = await Generate(Profile(weekendPattern: weekend));
        result.Draft.WorkingWeek!.WorkWeek.Should().Be(workWeek);
        result.Draft.WorkingWeek.WeekStartDay.Should().Be(startDay);
    }

    [Fact]
    public async Task WeekendPattern_Unset_FallsBackToTheCountry()
    {
        var saudi = await Generate(Profile(country: "SA"));
        var uae = await Generate(Profile(country: "AE", currency: "AED"));

        saudi.Draft.WorkingWeek!.WorkWeek.Should().Be("Sun-Thu");
        uae.Draft.WorkingWeek!.WorkWeek.Should().Be("Mon-Fri");
    }

    // ── Localization ────────────────────────────────────────────────────────

    [Fact]
    public async Task Localization_UsesTheCountry_NotTheEntityDefaults()
    {
        // TenantLocalizationSetting is constructed America/New_York + MM/DD/YYYY, and the setup
        // wrote only the work week and currency over it. Every Gulf tenant kept a New York clock.
        var result = await Generate(Profile(country: "SA"));

        var loc = result.Draft.Localization!;
        loc.DefaultTimezone.Should().Be("Asia/Riyadh");
        loc.DateFormat.Should().Be("DD/MM/YYYY");
        loc.HijriDatesEnabled.Should().BeTrue();
        loc.RtlEnabled.Should().BeTrue();
        loc.CalendarSystem.Should().Be("Gregorian");
    }

    [Fact]
    public async Task Localization_HonoursAnExplicitTimezoneAndLanguage()
    {
        var result = await Generate(Profile(country: "AE", currency: "AED", timeZone: "Asia/Dubai", defaultLanguage: "ar"));
        result.Draft.Localization!.DefaultTimezone.Should().Be("Asia/Dubai");
        result.Draft.Localization.DefaultLanguage.Should().Be("ar");
    }

    // ── Leave entitlement ───────────────────────────────────────────────────

    [Fact]
    public async Task LeavePolicies_TakeTheirDaysFromThePlatformStatutoryRules()
    {
        var result = await Generate(Profile(country: "SA"), statutory: KsaRules());

        var annual = result.Draft.LeavePolicies.Single(x => x.LeaveTypeCode == "ANNUAL");
        annual.AnnualEntitlementDays.Should().Be(21m);   // leave.annual_base_days
        annual.AccrualMethod.Should().Be("Monthly");
        annual.PayrollImpact.Should().Be("Full");

        result.Draft.LeavePolicies.Single(x => x.LeaveTypeCode == "SICK")
            .AnnualEntitlementDays.Should().Be(30m);      // leave.sick_band1_days, the full-pay band
    }

    [Fact]
    public async Task LeavePolicies_CoverEveryDraftedLeaveType()
    {
        // A leave type with no policy grants nobody anything, which is what applying this draft
        // used to produce for all of them.
        var result = await Generate(Profile(country: "SA"), statutory: KsaRules());

        result.Draft.LeavePolicies.Select(x => x.LeaveTypeCode)
            .Should().BeEquivalentTo(result.Draft.LeaveTypes.Select(x => x.Code));
    }

    [Fact]
    public async Task LeavePolicies_WithNoStatutoryFigure_AreZeroAndSaySo()
    {
        // The unpegged-currency treatment, applied to entitlements: no invented number.
        var result = await Generate(Profile(country: "GB", currency: "GBP"));

        result.Draft.LeavePolicies.Single(x => x.LeaveTypeCode == "ANNUAL")
            .AnnualEntitlementDays.Should().Be(0m);
        result.Notes.Should().Contain(n => n.Contains("0 days") && n.Contains("Annual Leave"));
    }

    [Fact]
    public async Task LeavePolicies_NeverCarryUnpaidLeaveAsPaid()
    {
        var result = await Generate(Profile(country: "SA"), statutory: KsaRules());

        var unpaid = result.Draft.LeavePolicies.Single(x => x.LeaveTypeCode == "UNPAID");
        unpaid.PayrollImpact.Should().Be("Unpaid");
        unpaid.AnnualEntitlementDays.Should().Be(0m);
    }

    [Fact]
    public async Task LeavePolicies_AreSkippedWhenLeaveTypesAre()
    {
        var result = await Generate(Profile(sections: AllSections(leave: false)));

        result.Draft.LeaveTypes.Should().BeEmpty();
        result.Draft.LeavePolicies.Should().BeEmpty();
        result.Notes.Should().Contain(n => n.Contains("Leave entitlements were skipped"));
    }

    // ── Overtime ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Overtime_MultipliersComeFromTheStatutoryRules()
    {
        var result = await Generate(Profile(country: "SA"), statutory: KsaRules());

        var policy = result.Draft.OvertimePolicy!;
        policy.Multipliers.Should().BeEquivalentTo(new[]
        {
            new DraftOvertimeMultiplier("RegularDay", 1.5m),
            new DraftOvertimeMultiplier("Weekend", 2.0m),
            new DraftOvertimeMultiplier("PublicHoliday", 2.0m),
        });
        policy.StandardMonthlyHours.Should().Be(240);
    }

    [Fact]
    public async Task Overtime_WithNoStatutoryRate_DraftsNoMultiplierAndSaysWhy()
    {
        // A drafted 1.5 for a jurisdiction nobody has checked would be approved and then paid.
        var result = await Generate(Profile(country: "GB", currency: "GBP"));

        result.Draft.OvertimePolicy!.Multipliers.Should().BeEmpty();
        result.Notes.Should().Contain(n => n.Contains("Overtime multipliers were left empty"));
    }

    [Fact]
    public async Task Overtime_NotApplicable_DraftsNoPolicyAtAll()
    {
        var result = await Generate(Profile(overtimeHandling: "NotApplicable"), statutory: KsaRules());

        result.Draft.OvertimePolicy.Should().BeNull();
        result.Notes.Should().Contain(n => n.Contains("No overtime policy was drafted"));
        result.Draft.AttendancePolicy!.RequiresOvertimeApproval.Should().BeFalse();
    }

    [Fact]
    public async Task Overtime_CompensatoryOff_TurnsOnCompOffConversion()
    {
        var result = await Generate(Profile(overtimeHandling: "CompensatoryOff"), statutory: KsaRules());
        result.Draft.OvertimePolicy!.AllowCompOffConversion.Should().BeTrue();
    }

    // ── Attendance ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("BiometricDevice", 5, "NearestMinute")]
    [InlineData("MobileGeofence", 10, "Nearest15")]
    [InlineData("WebCheckIn", 15, "Nearest15")]
    [InlineData("Manual", 30, "Nearest15")]
    public async Task AttendanceCapture_SetsGraceAndRoundingItCanHonour(string capture, int grace, string rounding)
    {
        var result = await Generate(Profile(attendanceCapture: capture));

        var policy = result.Draft.AttendancePolicy!;
        policy.GraceMinutes.Should().Be(grace);
        policy.RoundingRule.Should().Be(rounding);
    }

    [Fact]
    public async Task AttendanceRounding_IsOnlyEverOneOfTheTwoTheEngineEvaluates()
    {
        // A third value would be stored, displayed on the policy screen, and ignored at run time.
        foreach (var capture in new[] { "BiometricDevice", "MobileGeofence", "WebCheckIn", "Manual", "nonsense" })
        {
            var result = await Generate(Profile(attendanceCapture: capture));
            result.Draft.AttendancePolicy!.RoundingRule.Should().BeOneOf("NearestMinute", "Nearest15");
        }
    }

    [Fact]
    public async Task FieldRoster_GetsALongerDayAndLongerBreak()
    {
        var result = await Generate(Profile(workPattern: "FieldRoster"));

        result.Draft.AttendancePolicy!.StandardWorkMinutes.Should().Be(540);
        result.Draft.AttendancePolicy.BreakMinutes.Should().Be(90);
    }

    // ── Public holidays ─────────────────────────────────────────────────────

    [Fact]
    public async Task Holidays_DraftOnlyTheFixedDatesAndNameTheOnesTheyCannot()
    {
        var result = await Generate(Profile(country: "SA"));

        var names = result.Draft.HolidayCalendar!.Holidays.Select(x => x.NameEn).ToList();
        names.Should().Contain("Founding Day").And.Contain("Saudi National Day");
        // Wrong by one day is enough to misprice every overtime hour worked over the holiday.
        names.Should().NotContain(n => n.Contains("Eid"));
        result.Notes.Should().Contain(n => n.Contains("Eid al-Fitr") && n.Contains("lunar"));
    }

    [Fact]
    public async Task Holidays_EveryDraftedDateIsRealAndInTheCalendarYear()
    {
        foreach (var country in new[] { "SA", "AE", "QA", "KW", "BH", "OM" })
        {
            var result = await Generate(Profile(country: country, currency: "USD"));
            var calendar = result.Draft.HolidayCalendar!;
            foreach (var h in calendar.Holidays)
            {
                DateOnly.TryParseExact(h.Date, "yyyy-MM-dd", out var date).Should().BeTrue($"{country} {h.NameEn}");
                date.Year.Should().Be(calendar.CalendarYear);
            }
        }
    }

    [Fact]
    public async Task Holidays_ForACountryWithNoList_AreOmittedNotInvented()
    {
        var result = await Generate(Profile(country: "GB", currency: "GBP"));

        result.Draft.HolidayCalendar.Should().BeNull();
        result.Notes.Should().Contain(n => n.Contains("No public holiday calendar was drafted"));
    }

    // ── Pay cycle & workforce mix ───────────────────────────────────────────

    [Fact]
    public async Task PayCycle_TravelsWithEveryPayComponent()
    {
        // The ladder is monthly; paying a month's salary weekly is the failure this prevents.
        var monthly = await Generate(Profile(payCycle: "Monthly"));
        var weekly = await Generate(Profile(payCycle: "Weekly"));

        weekly.Draft.GradePayComponents.Should().OnlyContain(x => x.Frequency == "Weekly");
        var monthlyBasic = monthly.Draft.GradePayComponents.First(x => x.ComponentCode == "BASIC").Amount;
        var weeklyBasic = weekly.Draft.GradePayComponents.First(x => x.ComponentCode == "BASIC").Amount;
        weeklyBasic.Should().BeLessThan(monthlyBasic);
        weekly.Notes.Should().Contain(n => n.Contains("per-run amount"));
    }

    [Theory]
    [InlineData("MostlyExpat", true)]
    [InlineData("Mixed", true)]
    [InlineData("MostlyNational", false)]
    public async Task WorkforceMix_DecidesTheExpatriateComponents(string mix, bool expectTicket)
    {
        var result = await Generate(Profile(workforceMix: mix));
        result.Draft.PayComponents.Any(x => x.Code == "AIR_TICKET").Should().Be(expectTicket);
    }

    // ── Employment terms → tenant rules ─────────────────────────────────────

    [Fact]
    public async Task ProbationAndNotice_BecomeRulesOnlyWhenChosen()
    {
        var chosen = await Generate(Profile(probationMonths: 3, noticePeriodDays: 30, leaveYearBasis: "JoiningDate"));
        var untouched = await Generate(Profile());

        chosen.Draft.StatutoryRules.Should().Contain(r => r.RuleKey == "employment.probation_months" && r.RuleValue == "3");
        chosen.Draft.StatutoryRules.Should().Contain(r => r.RuleKey == "employment.notice_period_days" && r.RuleValue == "30");
        chosen.Draft.StatutoryRules.Should().Contain(r => r.RuleKey == "leave.year_basis" && r.RuleValue == "JoiningDate");

        // An untouched field must not become a rule that says "0 days' notice".
        untouched.Draft.StatutoryRules.Should().NotContain(r => r.RuleKey.StartsWith("employment."));
    }

    // ── Section toggles ─────────────────────────────────────────────────────

    [Fact]
    public async Task TurningOffTheNewSections_LeavesThemEmpty()
    {
        var result = await Generate(Profile(
            sections: AllSections(leavePolicies: false, holidays: false, attendance: false, localization: false)));

        result.Draft.LeavePolicies.Should().BeEmpty();
        result.Draft.HolidayCalendar.Should().BeNull();
        result.Draft.AttendancePolicy.Should().BeNull();
        result.Draft.OvertimePolicy.Should().BeNull();
        result.Draft.Localization.Should().BeNull();
        // ...and the rest of the draft is unaffected.
        result.Draft.Departments.Should().NotBeEmpty();
    }

}
