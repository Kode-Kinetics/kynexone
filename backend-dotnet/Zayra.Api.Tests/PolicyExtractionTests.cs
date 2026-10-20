using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Setup;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Setup;
using Word = DocumentFormat.OpenXml.Wordprocessing;

namespace Zayra.Api.Tests;

public sealed class PolicyExtractionTests
{
    [Theory]
    [InlineData("4 Proposed configuration\n4.1 Onboarding\n")]
    [InlineData("4.1 Draft onboarding\n[4.1-R1] Onboarding checklist\n")]
    public async Task NumberedProposedAncestor_CannotBeReactivatedByChildTopic(string headings)
    {
        var model = new SequenceModel("{}");
        var result = await ExtractRecovery(model, new Recorder(), headings + RecoveryClause);
        result.OnboardingTaskDrafts.Should().BeEmpty();
        model.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task MultipleSentencesSharingSourceId_CannotTransferScalarValuesBetweenTasks()
    {
        const string clause = "HR signs the contract. IT must provide the laptop.";
        var model = new SequenceModel("{}", JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] {
            new { sourceId = "@first", taskTitle = "Contract", taskDescription = "Sign contract", category = "Document", assignedToName = "IT", isMandatory = true },
            new { sourceId = "@first", taskTitle = "Laptop", taskDescription = "Provide laptop", category = "IT", assignedToName = "HR", isMandatory = true } } }));
        var result = await ExtractRecovery(model, new Recorder(), clause);
        result.OnboardingTaskDrafts.Should().HaveCount(2).And.OnlyContain(t => t.AssignedToName == null && t.IsMandatory == null);
    }

    [Fact]
    public async Task ExplicitNonTaskOutcome_DoesNotForceObjectiveIntoAnInventedTask()
    {
        var source = "Prepare the joiner and allocate onboarding costs.\n" + RecoveryClause;
        var model = new SequenceModel("{}", JsonSerializer.Serialize(new { noTaskSourceIds = new[] { "@first" },
            onboardingTaskDrafts = new[] { new { sourceId = "@last", taskTitle = "Contract", taskDescription = "Sign contract", category = "Document" } } }));
        var result = await ExtractRecovery(model, new Recorder(), source);
        result.OnboardingTaskDrafts.Should().ContainSingle().Which.SourceQuote.Should().Be(RecoveryClause);
        result.Progress!.IsComplete.Should().BeTrue();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("@first")]
    public async Task NonTaskSourceIds_MustBeKnownAndCannotContradictReturnedTask(string noTaskId)
    {
        var model = new SequenceModel("{}", JsonSerializer.Serialize(new { noTaskSourceIds = new[] { noTaskId },
            onboardingTaskDrafts = new[] { new { sourceId = "@first", taskTitle = "Contract", taskDescription = "Sign contract", category = "Document" } } }));
        var result = await ExtractRecovery(model, new Recorder(), RecoveryClause);
        result.Progress!.NextChunkIndex.Should().Be(0);
    }

    [Fact]
    public async Task AllExplicitNonTaskOutcomes_ReturnNoFormsWithoutInventingCoverage()
    {
        var model = new SequenceModel("{}", "{\"onboardingTaskDrafts\":[],\"noTaskSourceIds\":[\"@first\"]}");
        var result = await ExtractRecovery(model, new Recorder(), "Prepare the joiner and allocate onboarding costs.");
        result.OnboardingTaskDrafts.Should().BeEmpty();
        result.Progress!.IsComplete.Should().BeTrue();
        result.Coverage.Should().NotContain(c => c.Status == "Task drafts to review");
        result.Message.Should().ContainEquivalentOf("no onboarding task drafts");
    }

    [Fact]
    public void CompleteSingleJsonFence_IsUnwrappedWithoutChangingEvidenceValidation()
    {
        var json = JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", RecoveryClause,
            "```json\n" + json + "\n```", SetupConfigurationTests.Profile());
        result.OnboardingTaskDrafts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("4 Proposed configuration\n4.1 Onboarding\n")]
    [InlineData("4 Policy requirements\n4.1 Draft onboarding\n[4.1-R1] Checklist\n")]
    public void PrimaryTask_CannotOverrideNumberedProposedAncestor(string heading)
    {
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", heading + RecoveryClause,
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } }),
            SetupConfigurationTests.Profile());
        result.OnboardingTaskDrafts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("# Onboarding\n", "# Probation\n")]
    [InlineData("7.2 Onboarding\n", "7.3 Probation and Confirmation\n[7.3-R1] Appraisal\n")]
    [InlineData("7.2\tOnboarding\n", "7.3\tProbation and Confirmation\n[7.3-R1] Appraisal\n")]
    public void PrimaryTasks_ExcludeKnownOtherWorkflowSourceButKeepReviewRequirement(string onboarding, string probation)
    {
        const string appraisal = "HR requests the appraisal two weeks before the probation ends.";
        var source = onboarding + RecoveryClause + "\n" + probation + appraisal;
        var output = JsonSerializer.Serialize(new {
            onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause), RecoveryTask(appraisal, "Request appraisal") },
            requirements = new[] { RecoveryRequirement(appraisal, capability: "employment.probation") }
        });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", source, output, SetupConfigurationTests.Profile());
        result.OnboardingTaskDrafts.Should().ContainSingle().Which.TaskTitle.Should().Be("Contract signing");
        result.Requirements.Should().ContainSingle().Which.SourceQuote.Should().Be(appraisal);
    }

    [Fact]
    public void WrongWorkflowTaskOnlyOutput_RetainsOneEvidenceBoundReadOnlyRequirement()
    {
        const string clause = "HR requests the appraisal two weeks before the probation ends.";
        const string heading = "# Probation\n";
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", heading + clause,
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(clause), RecoveryTask(clause, "Request appraisal") } }),
            SetupConfigurationTests.Profile());
        result.OnboardingTaskDrafts.Should().BeEmpty();
        var requirement = result.Requirements.Should().ContainSingle().Subject;
        requirement.SourceQuote.Should().Be(clause);
        requirement.SourceStart.Should().Be(heading.Length);
        requirement.SourceClassification.Should().Be("Policy");
        requirement.Capability.Should().Be("policy.other");
        requirement.Status.Should().Be("ReviewRequired");
    }

    [Theory]
    [InlineData("Proposed configuration\n4.1 Onboarding\n", false)]
    [InlineData("4 Proposed configuration\n4.1 Onboarding\n5 Policy requirements\n5.1 Onboarding\n", true)]
    public void NumberedAuthority_PreservesPlainProposalAndAllowsNewPolicySibling(string heading, bool approved)
    {
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", heading + RecoveryClause,
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } }),
            SetupConfigurationTests.Profile());
        result.OnboardingTaskDrafts.Should().HaveCount(approved ? 1 : 0);
    }

    [Theory]
    [InlineData("```json\n{}\n```\n```json\n{}\n```")]
    [InlineData("```json\n{\"onboardingTaskDrafts\":[\n```")]
    [InlineData("Here is your JSON:\n```json\n{}\n```")]
    public void IncompleteOrMultipleFences_AreNotSalvaged(string output)
    {
        var act = () => PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", RecoveryClause, output, SetupConfigurationTests.Profile());
        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("HR arranges pre‑employment medical.")]
    [InlineData("HR arranges: ... medical, ...")]
    public async Task OriginalSourceIds_RecoverWhenModelQuotationCannotLocateCandidate(string badQuote)
    {
        const string clause = "HR arranges pre-employment medical.";
        var model = new SequenceModel(JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(badQuote, classification: "REC") } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { new { sourceId = "@first", taskTitle = "Medical", taskDescription = "Pre-employment medical", category = "General", sourceClassification = "REC" } } }));
        var result = await ExtractRecovery(model, new Recorder(), clause);
        result.OnboardingTaskDrafts.Should().ContainSingle().Which.SourceQuote.Should().Be(clause);
    }

    [Fact]
    public async Task SourceIds_ResolveActualRepeatedOccurrenceAndKeepCompoundScalarsUnknown()
    {
        const string clause = "HR must arrange contract signing and medical if needed 7 days after joining.";
        var source = "# Proposed configuration\n" + clause + "\n# Onboarding\n" + clause;
        var model = new SequenceModel("{}", JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] {
            new { sourceId = "@first", taskTitle = "Contract signing", taskDescription = "Sign contract", category = "Document", assignedToName = "HR", dueOffsetDays = 7, isMandatory = true },
            new { sourceId = "@first", taskTitle = "Medical", taskDescription = "Medical if needed", category = "General", assignedToName = "HR", dueOffsetDays = 7, isMandatory = true } } }));
        var result = await ExtractRecovery(model, new Recorder(), source);
        result.OnboardingTaskDrafts.Should().HaveCount(2);
        result.OnboardingTaskDrafts.Should().OnlyContain(t => t.SourceStart == RecoveryHeading.Length + source.LastIndexOf(clause, StringComparison.Ordinal)
            && t.AssignedToName == null && t.DueOffsetDays == null && t.IsMandatory == null && t.Condition!.Contains("if needed"));
    }

    [Theory]
    [InlineData("# Onboarding\n", "# Probation\n")]
    [InlineData("7.2 Onboarding\n[7.2-R1] Checklist\n", "7.3 Probation\n[7.3-R1] Outcome\n")]
    public async Task OriginalTopicScope_DoesNotAuthorizeLaterProbationFromModelCapability(string heading, string nextHeading)
    {
        const string other = "HR issues termination letters.";
        var source = heading + RecoveryClause + "\n" + nextHeading + other;
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(other) } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } }));
        var result = await ExtractRecovery(model, new Recorder(), source);
        result.OnboardingTaskDrafts.Should().ContainSingle().Which.SourceQuote.Should().Be(RecoveryClause);
        model.Calls[1].UserPrompt.Should().NotContain(other);
    }

    [Fact]
    public async Task UnknownSourceId_IsRejectedEvenWhenItsModelQuoteExists()
    {
        var model = new SequenceModel("{}", JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] {
            new { sourceId = "unknown", taskTitle = "Contract", taskDescription = RecoveryClause, category = "Document", sourceQuote = RecoveryClause } } }));
        var result = await ExtractRecovery(model, new Recorder(), RecoveryClause);
        result.OnboardingTaskDrafts.Should().BeEmpty();
        result.Progress!.NextChunkIndex.Should().Be(0);
    }

    [Fact]
    public void DraftDiagnostics_ReportRejectionReasonsWithoutSourceOrArbitraryModelStrings()
    {
        const string source = "HR arranges confidential task ALPHA.\nHR issues confidential task BETA [REC].";
        const string policy = "HR arranges confidential task ALPHA.";
        var rows = new object[] { "invalid row",
            new { taskTitle = "No description", category = "Document", sourceQuote = policy },
            new { taskTitle = "Bad category", taskDescription = policy, category = "SecretCategoryName", sourceQuote = policy },
            RecoveryTask("Invented confidential quote"), RecoveryTask(policy, classification: "REC"),
            RecoveryTask("HR issues confidential task BETA [REC]."),
            RecoveryTask(policy, classification: "SecretLabel"), RecoveryTask(policy), RecoveryTask(policy) };
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", source,
            JsonSerializer.Serialize(new { onboardingTaskDrafts = rows }), SetupConfigurationTests.Profile());
        var info = result.Issues.Single(i => i.Code == "onboarding_draft_diagnostics").Message;
        info.Should().Contain("received 9").And.Contain("validated 1");
        info.Should().Contain("malformed=1").And.Contain("missingFields=1").And.Contain("category=1")
            .And.Contain("evidence=1").And.Contain("classification=3").And.Contain("duplicate=1");
        info.Should().Contain("modelClasses:").And.Contain("sourceClasses:").And.Contain("Other=1");
        info.Should().NotContain("confidential").And.NotContain("SecretCategoryName").And.NotContain("SecretLabel");
    }

    [Fact]
    public async Task RecoveryDiagnostics_ExplainNoProjectionForInvalidEvidenceOrNoAction()
    {
        const string nounClause = "Employee documentation file.";
        const string recClause = "HR arranges a suggested task [REC].";
        var model = new SequenceModel(JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] {
            RecoveryTask("A nonexistent quote"), RecoveryTask(nounClause, classification: "REC"), RecoveryTask(recClause) } }));
        var recorder = new Recorder();
        var result = await ExtractRecovery(model, recorder, nounClause + "\n" + recClause);
        var info = result.Issues.Single(i => i.Code == "onboarding_recovery_diagnostics").Message;
        info.Should().Contain("evidence=1").And.Contain("sourceUnapproved=1").And.Contain("noAction=1").And.Contain("selected=0");
        info.Should().NotContain(nounClause).And.NotContain(recClause).And.NotContain("nonexistent");
        recorder.Records.Single().PromptSummary.Should().Contain("noAction=1");
        model.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ProjectionDiagnostics_IncludeRejectionReasonInBothResponseAndUsageRecord()
    {
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(RecoveryClause) } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask("Not in source") } }));
        var recorder = new Recorder();
        var result = await ExtractRecovery(model, recorder, RecoveryClause);
        result.Issues.Single(i => i.Code == "onboarding_projection_diagnostics").Message.Should().Contain("unknownSourceId=1");
        recorder.Records.Single(r => r.Intent == "policy_onboarding_projection").PromptSummary.Should().Contain("unknownSourceId=1");
        result.Progress!.NextChunkIndex.Should().Be(0);
    }

    private const string RecoveryClause = "HR arranges contract signing.";
    private const string RecoveryOther = "IT provides system access if needed.";
    private const string RecoveryHeading = "# Onboarding\n";
    private static object RecoveryRequirement(string quote, string classification = "Policy", string capability = "onboarding.checklist")
        => new { section = "Onboarding", summary = quote, capability, sourceQuote = quote, sourceClassification = classification };
    private static object RecoveryTask(string quote, string title = "Contract signing", string classification = "Policy")
        => new { taskTitle = title, taskDescription = quote, category = "Document", sourceQuote = quote, sourceClassification = classification };
    private sealed class SequenceModel(params string[] outputs) : ILlmClient
    {
        public List<LlmRequest> Calls { get; } = [];
        public Action<int>? BeforeResponse { get; set; }
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            BeforeResponse?.Invoke(Calls.Count);
            ct.ThrowIfCancellationRequested();
            if (Calls.Count > outputs.Length) throw new InvalidOperationException("Unexpected extra provider call");
            if (outputs[Calls.Count - 1] == "transport failure") throw new HttpRequestException("Provider unavailable");
            var output = outputs[Calls.Count - 1];
            // This provider double selects IDs from the real request, never computes server IDs.
            if (Calls.Count == 2 && request.UserPrompt.StartsWith('{') && output.StartsWith('{'))
            {
                var input = JsonNode.Parse(request.UserPrompt)!;
                var json = JsonNode.Parse(output)!;
                if (input["sources"] is JsonArray sourceIds && json["noTaskSourceIds"] is JsonArray notTasks)
                    for (var i = 0; i < notTasks.Count; i++)
                    {
                        var selected = notTasks[i]?.GetValue<string>() switch { "@first" => sourceIds.First(), "@last" => sourceIds.Last(), _ => null };
                        if (selected != null) notTasks[i] = selected["sourceId"]!.GetValue<string>();
                    }
                if (input["sources"] is JsonArray sources && json["onboardingTaskDrafts"] is JsonArray tasks)
                    foreach (var task in tasks.OfType<JsonObject>())
                    {
                        var id = task["sourceId"]?.GetValue<string>();
                        var quote = task["sourceQuote"]?.GetValue<string>();
                        var span = id == "@first" ? sources.FirstOrDefault() : id == "@last" ? sources.LastOrDefault()
                            : id == null && quote != null ? sources.FirstOrDefault(s => s!["sourceQuote"]!.GetValue<string>().Contains(quote, StringComparison.Ordinal)) : null;
                        if (span != null) task["sourceId"] = span["sourceId"]!.GetValue<string>();
                    }
                output = json.ToJsonString();
            }
            return Task.FromResult(new LlmResponse(true, request.Provider, request.Model, output, 100, 80));
        }
    }
    private static Task<PolicyExtractionResult> ExtractRecovery(ILlmClient model, Recorder recorder, string source, CancellationToken ct = default)
        => new PolicyExtractionService(model, Options(), recorder, OpenPolicyContentGateForTests.Instance).ExtractAsync(new(Guid.NewGuid(), Guid.NewGuid(), "Admin"),
            Guid.NewGuid(), "hash", RecoveryHeading + source, SetupConfigurationTests.Profile(), ct);

    [Fact]
    public async Task RequirementsOnly_ActionableSourceIsRecoveredIntoExistingTaskForm()
    {
        var primary = JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(RecoveryClause) } });
        var model = new SequenceModel(primary, JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } }));
        var recorder = new Recorder();
        var result = await ExtractRecovery(model, recorder, RecoveryClause);
        var task = result.OnboardingTaskDrafts.Should().ContainSingle().Subject;
        task.SourceQuote.Should().Be(RecoveryClause);
        task.SourceStart.Should().Be(RecoveryHeading.Length);
        task.AssignedToName.Should().BeNull();
        task.DueOffsetDays.Should().BeNull();
        task.IsMandatory.Should().BeNull();
        result.Progress!.IsComplete.Should().BeTrue();
        recorder.Records.Should().HaveCount(2, "both billable gateway calls must be recorded");
        recorder.Records.Should().OnlyContain(r => !r.PromptSummary.Contains(RecoveryClause));
        model.Calls.Should().OnlyContain(r => r.RequireJson);
    }

    [Fact]
    public async Task MissingCoverage_RecoversSecondClauseWithoutDiscardingExistingTask()
    {
        var primary = JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(RecoveryClause), RecoveryRequirement(RecoveryOther) },
            onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } });
        var model = new SequenceModel(primary, JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryOther, "System access") } }));
        var source = RecoveryClause + "\n" + RecoveryOther;
        var result = await ExtractRecovery(model, new Recorder(), source);
        result.OnboardingTaskDrafts.Should().HaveCount(2);
        var recovered = result.OnboardingTaskDrafts!.Single(t => t.TaskTitle == "System access");
        (RecoveryHeading + source).Substring(recovered.SourceStart, recovered.SourceLength).Should().Be(RecoveryOther);
        recovered.Condition.Should().Contain("if needed");
        model.Calls[1].UserPrompt.Should().NotContain(RecoveryClause, "only uncovered source evidence is sent again");
    }

    [Theory]
    [InlineData("contract signing")]
    [InlineData("HR arranges contract signing and bank account support.")]
    public async Task CompoundClause_RecoveryReplacesPartialProjectionInsteadOfDuplicatingItsTask(string initialQuote)
    {
        const string clause = "HR arranges contract signing and bank account support.";
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(clause) },
            onboardingTaskDrafts = new[] { RecoveryTask(initialQuote, "  CONTRACT SIGNING ") } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask("contract signing"), RecoveryTask("bank account support", "Bank support") } }));
        var result = await ExtractRecovery(model, new Recorder(), clause);
        result.OnboardingTaskDrafts.Should().HaveCount(2);
        result.OnboardingTaskDrafts.Should().NotContain(t => t.TaskTitle == "CONTRACT SIGNING");
    }

    [Fact]
    public async Task FocusedProjectionAddingMissingTask_DoesNotDeletePriorNonoverlappingTask()
    {
        const string clause = "HR arranges contract signing and bank account support.";
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(clause) },
            onboardingTaskDrafts = new[] { RecoveryTask("contract signing") } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask("bank account support", "Bank support") } }));
        var result = await ExtractRecovery(model, new Recorder(), clause);
        result.OnboardingTaskDrafts.Should().HaveCount(2);
        result.OnboardingTaskDrafts.Should().Contain(t => t.TaskTitle == "Contract signing" && t.SourceQuote == "contract signing");
        result.OnboardingTaskDrafts.Should().Contain(t => t.TaskTitle == "Bank support" && t.SourceQuote == clause);
        result.Progress!.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task OverlappingCandidateExcerpts_AreSentOnceWithinOriginalSourceBudget()
    {
        const string clause = "HR arranges contract signing and bank account support.";
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] {
            RecoveryRequirement(clause), RecoveryRequirement("contract signing and bank account support.") } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(clause) } }));
        var result = await ExtractRecovery(model, new Recorder(), clause);
        result.OnboardingTaskDrafts.Should().ContainSingle();
        var prompt = model.Calls[1].UserPrompt;
        prompt.Split("contract signing").Should().HaveCount(2, "an overlapping source passage must appear only once");
    }

    [Theory]
    [InlineData("HR arranges: contract signing, bank account support.", "onboarding.checklist")]
    [InlineData("Employees must attend orientation if joining the field team.", "onboarding.orientation")]
    [InlineData("Direct Senior explains BU activities, duties, objectives and KPIs.", "onboarding.orientation")]
    [InlineData("Documents archived in personal file.", "onboarding.orientation")]
    public async Task GenericChecklistAndOrientationSources_AreEligibleForRecovery(string clause, string capability)
    {
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(clause, capability: capability) } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(clause) } }));
        var result = await ExtractRecovery(model, new Recorder(), clause);
        result.OnboardingTaskDrafts.Should().ContainSingle().Which.SourceQuote.Should().Be(clause);
    }

    [Theory]
    [InlineData("REC")]
    [InlineData("ASM")]
    [InlineData("TBC")]
    [InlineData("PROP")]
    public async Task FocusedSourceAuthority_IsNotRelabeledByModelGuess(string classification)
    {
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(RecoveryClause) } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause, classification: classification) } }));
        var result = await ExtractRecovery(model, new Recorder(), RecoveryClause);
        result.OnboardingTaskDrafts.Should().ContainSingle();
        result.Progress!.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task FocusedProjection_CannotPullTaskFromUnrequestedSourceScope()
    {
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(RecoveryClause) } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryOther) } }));
        var result = await ExtractRecovery(model, new Recorder(), RecoveryClause + "\n" + RecoveryOther);
        result.OnboardingTaskDrafts.Should().BeEmpty();
        result.Progress!.NextChunkIndex.Should().Be(0);
    }

    [Fact]
    public async Task ModelMisclassification_CanBeReconsideredButNotAutomaticallyApproved()
    {
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(RecoveryClause, "REC") } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } }));
        var result = await ExtractRecovery(model, new Recorder(), RecoveryClause + "\nControl: deadline [REC]");
        result.OnboardingTaskDrafts.Should().ContainSingle();
        result.Requirements.Should().ContainSingle().Which.SourceClassification.Should().Be("REC", "recovery does not rewrite the prior review record");
    }

    [Theory]
    [InlineData("HR arranges contract signing [REC].", "Policy", "onboarding.checklist")]
    [InlineData("# Proposed configuration\nHR arranges contract signing.", "Policy", "onboarding.checklist")]
    [InlineData("Task owners and deadlines are not specified.", "Policy", "onboarding.checklist")]
    [InlineData("Employees receive annual leave.", "Policy", "policy.other")]
    public async Task NonExecutableOrUnapprovedRequirements_DoNotTriggerTaskRecovery(string source, string classification, string capability)
    {
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(source, classification, capability) } }));
        var result = await ExtractRecovery(model, new Recorder(), source);
        result.OnboardingTaskDrafts.Should().BeEmpty();
        result.Progress!.IsComplete.Should().BeTrue();
        model.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("transport failure")]
    [InlineData("{\"onboardingTaskDrafts\":[{\"taskTitle\":\"Forged\",\"taskDescription\":\"Forged\",\"category\":\"Document\",\"sourceQuote\":\"not in document\"}]}")]
    public async Task FailedRecovery_KeepsExistingDraftAndExplicitRetryableSection(string recovery)
    {
        var primary = JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(RecoveryOther) }, onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } });
        var model = new SequenceModel(primary, recovery);
        var recorder = new Recorder();
        var result = await ExtractRecovery(model, recorder, RecoveryClause + "\n" + RecoveryOther);
        result.OnboardingTaskDrafts.Should().ContainSingle().Which.TaskTitle.Should().Be("Contract signing");
        result.Progress!.NextChunkIndex.Should().Be(0);
        result.Progress.IsComplete.Should().BeFalse();
        result.Message.Should().ContainEquivalentOf("retry");
        recorder.Records.Should().HaveCount(2);
        recorder.Records.Should().Contain(r => r.FailureReason != null && r.Intent == "policy_onboarding_projection");
        model.Calls.Should().HaveCount(2, "recovery is bounded to one extra attempt per section");
    }

    [Fact]
    public async Task RejectedDraft_IsDiagnosedAndCanRecoverFromItsExactSource()
    {
        var invalid = new { taskTitle = "Contract signing", taskDescription = RecoveryClause, category = "Unsupported", sourceQuote = RecoveryClause };
        var model = new SequenceModel(JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { invalid } }),
            JsonSerializer.Serialize(new { onboardingTaskDrafts = new[] { RecoveryTask(RecoveryClause) } }));
        var result = await ExtractRecovery(model, new Recorder(), RecoveryClause);
        result.OnboardingTaskDrafts.Should().ContainSingle();
        result.Issues.Should().Contain(i => i.Code == "onboarding_draft_diagnostics" && i.Message.Contains("received 1") && i.Message.Contains("validated 0"));
    }

    [Fact]
    public async Task CancellationDuringRecovery_PropagatesAndRecordsBothCalls()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new SequenceModel(JsonSerializer.Serialize(new { requirements = new[] { RecoveryRequirement(RecoveryClause) } }), "{}");
        model.BeforeResponse = count => { if (count == 2) cancellation.Cancel(); };
        var recorder = new Recorder();
        var act = () => ExtractRecovery(model, recorder, RecoveryClause, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.Records.Should().HaveCount(2);
    }

    private static JsonElement OnboardingDraftJson(string source, object[] drafts, string hash = "hash", int sourceOffset = 0, Guid? documentId = null)
    {
        var result = PolicyExtractionService.Validate(documentId ?? Guid.NewGuid(), hash, "ollama", source,
            JsonSerializer.Serialize(new { onboardingTaskDrafts = drafts }), SetupConfigurationTests.Profile(), sourceOffset);
        var json = JsonSerializer.SerializeToElement(result, Json);
        json.TryGetProperty("onboardingTaskDrafts", out var tasks).Should().BeTrue("onboarding form drafts must be a structured response channel, not only review text");
        return tasks;
    }

    [Fact]
    public void OnboardingDraft_KeepsUnknownInputsNullAndConditionalTextVisible()
    {
        const string clause = "additional medical if needed";
        var tasks = OnboardingDraftJson(clause, [new { taskTitle = "Additional medical", taskDescription = clause, category = "General",
            assignedToName = "IT", dueOffsetDays = 0, isMandatory = true, sourceQuote = clause }], sourceOffset: 1200);
        tasks.GetArrayLength().Should().Be(1);
        var task = tasks[0];
        task.GetProperty("assignedToName").ValueKind.Should().Be(JsonValueKind.Null);
        task.GetProperty("dueOffsetDays").ValueKind.Should().Be(JsonValueKind.Null);
        task.GetProperty("isMandatory").ValueKind.Should().Be(JsonValueKind.Null);
        task.GetProperty("condition").GetString().Should().Contain("if needed");
        task.GetProperty("sourceStart").GetInt32().Should().Be(1200);
        task.GetProperty("sourceLength").GetInt32().Should().Be(clause.Length);
    }

    [Fact]
    public void OnboardingDraft_IndependentTasksHaveStableSourceVersionBoundIds()
    {
        const string clause = "HR arranges contract signing and system access.";
        var id = Guid.NewGuid();
        object[] rows = [new { taskTitle = "Contract signing", taskDescription = "Arrange contract signing.", category = "Document", sourceQuote = clause },
            new { taskTitle = "System access", taskDescription = "Arrange system access.", category = "Access", sourceQuote = clause }];
        var tasks = OnboardingDraftJson(clause, rows, documentId: id);
        tasks.GetArrayLength().Should().Be(2);
        tasks[0].GetProperty("id").GetString().Should().NotBe(tasks[1].GetProperty("id").GetString());
        tasks[0].GetProperty("id").GetString().Should().Be(OnboardingDraftJson(clause, rows, documentId: id)[0].GetProperty("id").GetString());
        tasks[0].GetProperty("id").GetString().Should().NotBe(OnboardingDraftJson(clause, rows, "new-version", documentId: id)[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("HR must complete document verification 7 days after joining.", 7, 7)]
    [InlineData("HR must complete document verification 14 days before the employee start date.", -14, -14)]
    [InlineData("HR must complete document verification on the joining date.", 0, 0)]
    [InlineData("HR must complete document verification; probation lasts 90 days.", 90, null)]
    [InlineData("HR must complete document verification 7 working days after joining.", 7, null)]
    [InlineData("HR must complete document verification 7 days after joining.", 14, null)]
    public void OnboardingDraft_OnlyExplicitMatchingStartRelativeCalendarOffsetIsRetained(string clause, int proposed, int? expected)
    {
        var tasks = OnboardingDraftJson(clause, [new { taskTitle = "Document verification", taskDescription = clause, category = "Document",
            assignedToName = "HR", dueOffsetDays = proposed, isMandatory = true, sourceQuote = clause }]);
        tasks[0].GetProperty("assignedToName").GetString().Should().Be("HR");
        tasks[0].GetProperty("isMandatory").GetBoolean().Should().BeTrue();
        var offset = tasks[0].GetProperty("dueOffsetDays");
        if (expected.HasValue) offset.GetInt32().Should().Be(expected.Value);
        else offset.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("HR arranges document verification [REC].")]
    [InlineData("HR arranges document verification [ASM].")]
    [InlineData("HR arranges document verification [TBC-PO].")]
    [InlineData("HR arranges document verification [PROP].")]
    [InlineData("# Proposed configuration\nHR arranges document verification.")]
    public void OnboardingDraft_UnapprovedClausesCannotPrefillTasks(string source)
    {
        OnboardingDraftJson(source, [new { taskTitle = "Document verification", taskDescription = "Verify documents.", category = "Document",
            sourceQuote = "HR arranges document verification", sourceClassification = "Policy" }]).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void OnboardingDraft_RejectsForgedEvidenceAndUnsupportedCategory()
    {
        const string source = "HR arranges document verification.";
        OnboardingDraftJson(source, [new { taskTitle = "Document verification", taskDescription = source, category = "Payroll", sourceQuote = source },
            new { taskTitle = "Invented task", taskDescription = "Invented", category = "General", sourceQuote = "Missing quote" }])
            .GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("Iqama processing: non-Saudi employees.")]
    [InlineData("Medical examination outside Saudi Arabia.")]
    public void OnboardingDraft_ApplicabilityFragmentWithoutIfStillRetainsCondition(string clause)
    {
        var tasks = OnboardingDraftJson(clause, [new { taskTitle = "Required onboarding step", taskDescription = clause,
            category = "General", sourceQuote = clause }]);
        tasks[0].GetProperty("condition").GetString().Should().Be(clause);
    }

    [Theory]
    [InlineData("HR signs the contract; IT must grant access 7 days after joining.")]
    [InlineData("HR signs the contract. IT must grant access 7 days after joining.")]
    [InlineData("HR signs the contract and IT must grant access 7 days after joining.")]
    public void OnboardingDraft_AnotherTasksDeadlineInBroadQuoteIsNotCopied(string clause)
    {
        var tasks = OnboardingDraftJson(clause, [new { taskTitle = "Contract signing", taskDescription = "Arrange contract signing.",
            category = "Document", dueOffsetDays = 7, sourceQuote = clause }]);
        tasks[0].GetProperty("dueOffsetDays").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private const string OnboardingTasksClause = "HR arranges: contract signing, bank account support, workspace, stationery, additional medical if needed, IT equipment/extension, ID card, system access and e-mail, welcome announcement, personal file and emergency contact, mentor during probation.";
    private const string RecommendedDeadlineClause = "Checklist complete before day 1 [REC]";

    // A neighboring control-cell recommendation must not relabel the source's task-list cell.
    // The Word variant goes through the real parser, which emits paragraphs on separate lines.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedPolicyRow_ClassifiesEachQuotedCellIndependently(bool wordTable)
    {
        const string heading = "Policy 3 – Table 1: Policy Requirements and Business Rules";
        string[] cells = ["[3.1-R3] Onboarding checklist", OnboardingTasksClause, "Joining", "HR; BU; IT; Admin", "Joining report", "—", RecommendedDeadlineClause, "§3.1.6", "CD-03-03: Due dates and owners of each task not stated"];
        var source = wordTable ? ParseWordTable(heading, cells) : heading + "\n" + string.Join('\t', cells);
        var output = JsonSerializer.Serialize(new { requirements = new[] {
            new { section = "Onboarding", summary = "Prepare the stated onboarding tasks.", capability = "onboarding.checklist", sourceClassification = "Policy", sourceQuote = OnboardingTasksClause },
            new { section = "Onboarding", summary = "Recommended completion by day 1.", capability = "onboarding.checklist", sourceClassification = "Policy", sourceQuote = RecommendedDeadlineClause }
        } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", source, output, SetupConfigurationTests.Profile());
        result.Requirements.Should().HaveCount(2);
        var tasks = result.Requirements!.Single(r => r.SourceQuote == OnboardingTasksClause);
        tasks.SourceClassification.Should().Be("Policy");
        tasks.SourceStart.Should().Be(source.IndexOf(OnboardingTasksClause, StringComparison.Ordinal));
        var deadline = result.Requirements.Single(r => r.SourceQuote == RecommendedDeadlineClause);
        deadline.SourceClassification.Should().Be("REC");
        deadline.Status.Should().Be("ReviewRequired");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedCellsUnderProposedTable_CannotActivateUnannotatedProposal(bool wordTable)
    {
        const string heading = "Policy 3 – Table 4: Proposed Configuration in the Newly Developed Solution";
        const string clause = "All employees use web check-in.";
        string[] cells = [clause, "Deadline to be confirmed [TBC-PO]"];
        var source = wordTable ? ParseWordTable(heading, cells) : heading + "\n" + string.Join('\t', cells);
        var output = JsonSerializer.Serialize(new { proposals = new[] { new {
            target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = clause, sourceClassification = "Policy"
        } } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", source, output, SetupConfigurationTests.Profile());
        result.Proposals.Should().BeEmpty();
        result.Requirements.Should().ContainSingle().Which.SourceClassification.Should().Be("PROP");
    }

    [Fact]
    public void AnnotationInQuotedCellBeyondChunk_StillRequiresReview()
    {
        const string clause = "All employees use web check-in.";
        const string prefix = "Policy requirements\n";
        var fullSource = prefix + clause + " [REC]\tDifferent policy cell";
        var output = JsonSerializer.Serialize(new { proposals = new[] { new {
            target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = clause
        } } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", clause, output,
            SetupConfigurationTests.Profile(), prefix.Length, fullSource);
        result.Proposals.Should().BeEmpty();
        result.Requirements.Should().ContainSingle().Which.SourceClassification.Should().Be("REC");
    }

    private static string ParseWordTable(string heading, string[] cells)
    {
        using var input = new MemoryStream();
        using (var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(input,
                   DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Word.Document(new Word.Body(
                new Word.Paragraph(new Word.Run(new Word.Text(heading))),
                new Word.Table(new Word.TableRow(cells.Select(cell =>
                    new Word.TableCell(new Word.Paragraph(new Word.Run(new Word.Text(cell)))))))));
            main.Document.Save();
        }
        input.Position = 0;
        return PolicyTextParser.Extract(input, "policy.docx", "application/octet-stream").Text;
    }

    [Theory]
    [InlineData("# Proposed configuration\n")]
    [InlineData("# Proposed configuration\n## Attendance\n")]
    [InlineData("Policy 3 – Table 4: Proposed Configuration in the Newly Developed Solution\n")]
    [InlineData("Policy 3 – Table 4: Proposed Configuration in the Newly Developed Solution\r\n")]
    public void ProposedHeading_CannotBePromotedToApprovedPolicyByModel(string heading)
    {
        const string clause = "All employees use web check-in.";
        var source = heading + clause;
        var output = JsonSerializer.Serialize(new { proposals = new[] { new {
            target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = clause, sourceClassification = "Policy"
        } } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", source, output, SetupConfigurationTests.Profile());
        result.Proposals.Should().BeEmpty("source proposal headings remain unapproved even if the model mislabels them");
        result.Requirements.Should().ContainSingle().Which.SourceClassification.Should().Be("PROP");
    }

    [Fact]
    public void ProposedHeadingBeforeChunk_RemainsUnapprovedWithAbsoluteEvidence()
    {
        const string clause = "All employees use web check-in.";
        var prefix = "# Proposed configuration\n" + new string('x', 9000) + "\n";
        var output = JsonSerializer.Serialize(new { proposals = new[] { new {
            target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = clause
        } } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", clause, output,
            SetupConfigurationTests.Profile(), prefix.Length, prefix + clause);
        result.Proposals.Should().BeEmpty();
        var requirement = result.Requirements.Should().ContainSingle().Subject;
        requirement.SourceClassification.Should().Be("PROP");
        requirement.SourceStart.Should().Be(prefix.Length);
    }

    [Fact]
    public void SubsequentPolicySection_IsNotContaminatedByEarlierProposedHeading()
    {
        const string clause = "All employees use web check-in.";
        const string source = "# Proposed configuration\nConsider biometric attendance.\n# Policy requirements\nAll employees use web check-in.";
        var output = JsonSerializer.Serialize(new { proposals = new[] { new {
            target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = clause
        } } });
        PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", source, output, SetupConfigurationTests.Profile())
            .Proposals.Should().ContainSingle();
    }

    [Fact]
    public void RequirementsUseAbsoluteOffsets_RejectForgedEvidence_AndKeepUnknownsUnknown()
    {
        const string clause = "Checklist task owners and due dates are not stated [TBC-PO].";
        var output = JsonSerializer.Serialize(new { requirements = new[] {
            new { section = "Onboarding", summary = "Confirm task owners and due dates.", capability = "onboarding.checklist", sourceQuote = clause, sourceClassification = "Policy", missingFields = new[] { "taskOwner", "dueDate" } },
            new { section = "Onboarding", summary = "Create a made-up task.", capability = "onboarding.checklist", sourceQuote = "not in source", sourceClassification = "Policy", missingFields = Array.Empty<string>() }
        } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", clause, output, SetupConfigurationTests.Profile(), 6000);
        var requirement = result.Requirements.Should().ContainSingle().Subject;
        requirement.SourceStart.Should().Be(6000);
        requirement.SourceClassification.Should().Be("TBC");
        requirement.Status.Should().Be("ReviewRequired");
        requirement.MissingFields.Should().Equal("taskOwner", "dueDate");
        result.Proposals.Should().BeEmpty();
    }

    // These regressions exercise the real extraction validator. Provider JSON is the external
    // boundary fixture; no checklist/workflow writer is mocked or invoked by a review result.
    [Fact]
    public void OperationalClause_IsRetainedForReviewWhenSetupCannotExecuteIt()
    {
        const string clause = "HR requests appraisal from Direct Manager two weeks before 90 days; monthly appraisals.";
        var output = JsonSerializer.Serialize(new { requirements = new[] { new {
            section = "Probation", summary = "Request appraisal two weeks before day 90 and monthly appraisals.",
            capability = "employment.probation", status = "Applied", sourceClassification = "Policy", sourceQuote = clause,
            missingFields = new[] { "reviewOwner" }
        } } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", clause, output, SetupConfigurationTests.Profile());
        var json = JsonSerializer.SerializeToElement(result, Json);
        json.TryGetProperty("requirements", out var requirements).Should().BeTrue("operational rules must not disappear because setup cannot execute them");
        requirements.GetArrayLength().Should().Be(1);
        requirements[0].GetProperty("sourceQuote").GetString().Should().Be(clause);
        requirements[0].GetProperty("status").GetString().Should().NotBe("Applied");
        result.Proposals.Should().BeEmpty();
    }

    [Fact]
    public void IrrelevantUnquotedMissingTopics_DoNotCreateReviewNoise()
    {
        const string clause = "HR delivers orientation and the employee acknowledges the explained policies.";
        var output = JsonSerializer.Serialize(new { issues = new[] { new { section = "Overtime", kind = "missing", message = "Overtime multipliers were not stated." } } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", clause, output, SetupConfigurationTests.Profile());
        result.Issues.Should().BeEmpty("absence of unrelated topics in a chapter is not a policy defect");
    }

    [Fact]
    public void SupportedButAnnotatedSuggestion_RemainsUnapprovedRequirement()
    {
        const string clause = "Web check-in is recommended for all employees [REC].";
        var output = JsonSerializer.Serialize(new { proposals = new[] { new { target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = clause } } });
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", clause, output, SetupConfigurationTests.Profile());
        result.Proposals.Should().BeEmpty("a recommendation is not approved policy even when its destination exists");
        var requirement = JsonSerializer.SerializeToElement(result, Json).GetProperty("requirements")[0];
        requirement.GetProperty("sourceClassification").GetString().Should().Be("REC");
        requirement.GetProperty("status").GetString().Should().Be("ReviewRequired");
    }

    [Fact]
    public void GenuineConflict_HasVerifiedSourceAndStableVersionBoundIdentity()
    {
        const string clause = "Probation is 90 days; confirmation after two months requires clarification.";
        var documentId = Guid.NewGuid();
        var output = JsonSerializer.Serialize(new { issues = new[] { new {
            section = "Probation", kind = "review", message = "Reconcile confirmation timing with probation duration.",
            code = "conflicting_rules", sourceQuote = clause
        } } });
        JsonElement Issue(string hash) => JsonSerializer.SerializeToElement(PolicyExtractionService.Validate(
            documentId, hash, "ollama", clause, output, SetupConfigurationTests.Profile()), Json).GetProperty("issues")[0];
        var first = Issue("version-a");
        first.TryGetProperty("sourceQuote", out var quote).Should().BeTrue();
        quote.GetString().Should().Be(clause);
        first.GetProperty("sourceLength").GetInt32().Should().Be(clause.Length);
        first.GetProperty("id").GetString().Should().Be(Issue("version-a").GetProperty("id").GetString());
        first.GetProperty("id").GetString().Should().NotBe(Issue("version-b").GetProperty("id").GetString());
    }

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

    [Fact]
    public void ExplicitCompanyWideBenefitScope_AcceptsEmptyGradeEligibility()
    {
        const string clause = "Medical benefit is available to all employees company-wide.";
        PolicyExtractionResult Extract(object value) => PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", clause,
            JsonSerializer.Serialize(new { proposals = new[] { new { target = "configuration", field = "benefitPlans", value, sourceQuote = clause } } }, Json),
            SetupConfigurationTests.Profile());
        Extract(new[] { SetupConfigurationTests.Benefit(null) }).Proposals.Should().ContainSingle();
        var partial = Extract(new[] { new { name = "Medical", gradeCodes = Array.Empty<string>() } });
        partial.PartialProposals.Should().ContainSingle();
        partial.PartialProposals![0].MissingFields.Should().NotContain("benefitPlans[0].gradeCodes");
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
        var service = new PolicyExtractionService(model, Options(), recorder, OpenPolicyContentGateForTests.Instance);
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
        var result = await new PolicyExtractionService(model, Options(""), new Recorder(), OpenPolicyContentGateForTests.Instance).ExtractAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash", Source, SetupConfigurationTests.Profile(), CancellationToken.None);
        model.Calls.Should().BeEmpty();
        result.Provider.Should().Be("fallback");
    }

    [Fact]
    public async Task LargeHandbook_IsProcessedInBoundedResumableSections()
    {
        var model = new Model("{}");
        var service = new PolicyExtractionService(model, Options(), new Recorder(), OpenPolicyContentGateForTests.Instance);
        var source = new string('a', 250_000);
        var coveredThrough = 0;
        var index = 0;
        while (true)
        {
            var result = await service.ExtractAsync(new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash",
                source, SetupConfigurationTests.Profile(), CancellationToken.None, index);
            var progress = result.Progress!;
            progress.ChunkIndex.Should().Be(index);
            progress.SourceStart.Should().BeLessThanOrEqualTo(coveredThrough);
            progress.SourceLength.Should().BeLessThanOrEqualTo((4096 - 2500) * 3);
            progress.TotalCharacters.Should().Be(source.Length);
            coveredThrough = progress.SourceStart + progress.SourceLength;
            if (progress.IsComplete) { progress.NextChunkIndex.Should().BeNull(); break; }
            progress.NextChunkIndex.Should().Be(index + 1);
            index = progress.NextChunkIndex!.Value;
        }
        coveredThrough.Should().Be(source.Length);
        model.Calls.Should().HaveCount(index + 1);
    }

    [Fact]
    public async Task LaterSection_SourceOffsetsStillReferToWholeDocument()
    {
        var model = new Model(JsonSerializer.Serialize(new { proposals = new[] {
            new { target = "configuration", field = "attendanceMethods", value = new[] { "WebCheckIn" }, sourceQuote = Source }
        } }));
        var source = new string('a', 6000) + Source;
        var service = new PolicyExtractionService(model, Options(), new Recorder(), OpenPolicyContentGateForTests.Instance);
        var result = await service.ExtractAsync(new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash",
            source, SetupConfigurationTests.Profile(), CancellationToken.None, 1);
        result.Proposals.Should().ContainSingle().Which.SourceStart.Should().Be(6000);
        var proposal = result.Proposals.Single();
        source.Substring(proposal.SourceStart, proposal.SourceLength).Should().Be(Source);
        result.Progress!.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void ChunkBoundariesPreserveUnicodeAndHaveNoGaps()
    {
        var source = string.Concat(Enumerable.Repeat("\ud83d\udcd8 HR handbook\n", 1000));
        var ranges = PolicyExtractionService.ChunkRanges(source, 256);
        var covered = 0;
        foreach (var (start, length) in ranges)
        {
            start.Should().BeLessThanOrEqualTo(covered);
            char.IsLowSurrogate(source[start]).Should().BeFalse();
            char.IsHighSurrogate(source[start + length - 1]).Should().BeFalse();
            covered = start + length;
        }
        covered.Should().Be(source.Length);
    }

    [Theory]
    [InlineData("Annual leave is 22 working days.")]
    [InlineData("Annual leave is 22 working days; accrual is not specified.")]
    public void IncompleteEntitlement_IsRetainedWithoutInventingMissingDecisions(string clause)
    {
        var result = PolicyExtractionService.Validate(Guid.NewGuid(), "hash", "ollama", clause,
            JsonSerializer.Serialize(new { proposals = new[] { new { target = "configuration", field = "leavePolicies",
                value = new[] { new { annualEntitlementDays = 22 } }, sourceQuote = clause } } }), SetupConfigurationTests.Profile());
        result.Proposals.Should().BeEmpty();
        var partial = result.PartialProposals.Should().ContainSingle().Subject;
        partial.Value[0].GetProperty("annualEntitlementDays").GetInt32().Should().Be(22);
        partial.Value[0].TryGetProperty("proratePartialMonths", out _).Should().BeFalse();
        partial.MissingFields.Should().Contain("leavePolicies[0].proratePartialMonths");
        partial.SourceLength.Should().Be(clause.Length);
    }

    [Theory]
    [InlineData("{\"unknownRule\":true}")]
    [InlineData("{\"annualEntitlementDays\":\"22\"}")]
    [InlineData("{\"proratePartialMonths\":null}")]
    public void MalformedPartialValues_AreNotRetained(string row)
    {
        var result = Validate(new { target = "configuration", field = "leavePolicies",
            value = new[] { JsonDocument.Parse(row).RootElement }, sourceQuote = Source });
        result.Proposals.Should().BeEmpty();
        result.PartialProposals.Should().BeEmpty();
    }

    [Fact]
    public async Task CanceledSection_DoesNotStartProviderWork()
    {
        var model = new Model("{}");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var act = () => new PolicyExtractionService(model, Options(), new Recorder(), OpenPolicyContentGateForTests.Instance).ExtractAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash", Source,
            SetupConfigurationTests.Profile(), canceled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        model.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TransportFailure_LeavesSourceAndRecordsDegradation()
    {
        var recorder = new Recorder();
        var result = await new PolicyExtractionService(new Model("", true), Options(), recorder, OpenPolicyContentGateForTests.Instance).ExtractAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), "Admin"), Guid.NewGuid(), "hash", Source, SetupConfigurationTests.Profile(), CancellationToken.None);
        result.Proposals.Should().BeEmpty();
        result.Progress!.NextChunkIndex.Should().Be(0);
        result.Progress.IsComplete.Should().BeFalse();
        recorder.Records.Should().ContainSingle().Which.FailureReason.Should().NotBeNullOrEmpty();
    }
}
