using FluentAssertions;
using Zayra.Api.Infrastructure.Setup;

namespace Zayra.Api.Tests;

public sealed class LocalOnboardingPolicyMapperTests
{
    private static readonly Guid DocumentId = Guid.Parse("3bdba593-c2a2-47c8-a276-ae570386420d");

    [Fact]
    public void ConditionalCommaList_IsLeftIntactForManualReviewRatherThanLosingItsQualifier()
    {
        const string clause = "HR arranges: contract signing, if the employee works remotely, a laptop, a phone.";
        var result = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", "# Onboarding\n" + clause);
        result.OnboardingTaskDrafts.Should().BeEmpty();
        result.Issues.Should().Contain(issue => issue.Kind == "warning" && issue.SourceQuote == clause);
    }

    [Fact]
    public void EnumeratedChecklist_RetainsConditionsWithoutAssigningAnArrangerAsTaskOwner()
    {
        const string text = "2 Onboarding\nHR arranges: contract signing, workspace, additional medical if needed, laptop, mentor during probation.\nPolicy 4 – Table 4: Proposed Configuration\n4.1 Onboarding\nHR must collect identity documents.";
        var tasks = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", text).OnboardingTaskDrafts!;
        tasks.Select(task => task.TaskTitle).Should().Equal("contract signing", "workspace", "additional medical if needed", "laptop");
        tasks.Should().OnlyContain(task => task.AssignedToName == null && task.DueOffsetDays == null && task.IsMandatory == null);
        tasks[2].Condition.Should().Be("additional medical if needed");
        foreach (var task in tasks)
            text.Substring(task.SourceStart, task.SourceLength).Should().Be(task.SourceQuote);
    }

    [Fact]
    public void FlattenedPdfSentences_PreserveOnboardingContextAndStopAtOtherWorkflowHeading()
    {
        const string text = "Onboarding. HR signs the contract. IT issues a laptop. Probation. HR requests an appraisal.";
        LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", text).OnboardingTaskDrafts!
            .Select(task => task.TaskTitle).Should().Equal("HR signs the contract.", "IT issues a laptop.");
    }

    [Fact]
    public void Prepare_MapsSourceBackedTasksWithoutInventingUnknownValues()
    {
        const string text = "# Onboarding\r\n  • HR signs the employment contract.\r\n- Provide a laptop.\r\n";
        var result = LocalOnboardingPolicyMapper.Prepare(DocumentId, "hash-a", text);
        result.Provider.Should().Be("local-rules");
        result.DocumentId.Should().Be(DocumentId);
        result.SourceHash.Should().Be("hash-a");
        result.Proposals.Should().BeEmpty();
        var tasks = result.OnboardingTaskDrafts!;
        tasks.Should().HaveCount(2);
        tasks[0].TaskTitle.Should().Be("HR signs the employment contract.");
        tasks[0].TaskDescription.Should().Be(tasks[0].SourceQuote);
        tasks[0].AssignedToName.Should().Be("HR");
        tasks[0].Category.Should().Be("Document");
        tasks[0].DueOffsetDays.Should().BeNull();
        tasks[0].IsMandatory.Should().BeNull();
        tasks[1].AssignedToName.Should().BeNull();
        tasks[1].Category.Should().Be("IT");
        foreach (var task in tasks)
            text.Substring(task.SourceStart, task.SourceLength).Should().Be(task.SourceQuote);
        result.Progress!.IsComplete.Should().BeTrue();
        result.Progress.NextChunkIndex.Should().BeNull();
        result.Progress.TotalCharacters.Should().Be(text.Length);
    }

    [Fact]
    public void NumberedBullets_AreActionsRatherThanNewSections()
    {
        const string text = "7 Policy requirements\n7.2 Onboarding\n[7.2-R1] Checklist\n1. HR collects identity documents.\n2) IT issues a laptop.\n7.3 Probation\nHR requests an appraisal.\n";
        var tasks = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", text).OnboardingTaskDrafts!;
        tasks.Select(task => task.SourceQuote).Should().Equal("HR collects identity documents.", "IT issues a laptop.");
    }

    [Theory]
    [InlineData("# Proposed configuration\n## Onboarding\n")]
    [InlineData("4 Proposed configuration\n4.1 Onboarding\n")]
    [InlineData("Proposed configuration\nOnboarding\n")]
    [InlineData("# Draft onboarding\n")]
    [InlineData("Proposed onboarding configuration\nOnboarding\n")]
    [InlineData("DRAFT\nOnboarding\n")]
    public void ProposedAncestors_CannotBecomeApprovedByAnOnboardingChild(string headings)
    {
        LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", headings + "HR must sign the contract.")
            .OnboardingTaskDrafts.Should().BeEmpty();
    }

    [Fact]
    public void NegatedChecklistItems_AreNotCreated()
    {
        var result = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", "# Onboarding\nHR arranges: no laptop, contract signing.");
        result.OnboardingTaskDrafts.Should().ContainSingle().Which.TaskTitle.Should().Be("contract signing.");
    }

    [Fact]
    public void ApprovedSibling_AfterProposedSectionRestoresEligibility()
    {
        const string text = "4 Proposed configuration\n4.1 Onboarding\nHR signs the contract.\n5 Policy requirements\n5.1 Onboarding\nHR signs the contract.";
        var task = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", text).OnboardingTaskDrafts.Should().ContainSingle().Subject;
        task.SourceStart.Should().Be(text.LastIndexOf("HR signs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[REC-1] HR must sign the contract.")]
    [InlineData("[TBC] IT must issue a laptop.")]
    [InlineData("Gap: HR must collect the passport.")]
    [InlineData("Proposed: IT should issue a laptop.")]
    [InlineData("Not approved: HR must sign the contract.")]
    [InlineData("Recommended: HR must sign the contract.")]
    [InlineData("Recommendations: HR must sign the contract.")]
    [InlineData("Proposal: HR must sign the contract.")]
    [InlineData("HR must not issue the access badge.")]
    [InlineData("HR calculates probation compensation.")]
    [InlineData("HR must calculate annual leave entitlements.")]
    [InlineData("HR prepares onboarding costs and reimbursement calculations.")]
    public void NonPolicyOrOtherWorkflowClauses_AreNeverTasks(string clause)
    {
        LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", "# Onboarding\n" + clause)
            .OnboardingTaskDrafts.Should().BeEmpty();
    }

    [Fact]
    public void OtherWorkflowHeadings_EndOnboardingScope()
    {
        const string text = "# Onboarding\nHR signs the contract.\n# Annual leave\nHR collects documents.\n# Probation\nIT issues a laptop.\n# Payroll\nHR completes the checklist.";
        LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", text).OnboardingTaskDrafts.Should().ContainSingle();
    }

    [Fact]
    public void ExplicitOffsetsAndObligations_AreBoundToTheirOwnClause()
    {
        const string text = "# Onboarding\nHR must verify identity documents 2 calendar days before the joining date.\nEmployees attend orientation on the start date; attendance is optional.\nIT provides a laptop within 3 working days.\nHR signs the contract. IT must issue the laptop.";
        var tasks = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", text).OnboardingTaskDrafts!;
        tasks.Should().HaveCount(5);
        tasks[0].DueOffsetDays.Should().Be(-2);
        tasks[0].IsMandatory.Should().BeTrue();
        tasks[1].IsMandatory.Should().BeFalse();
        tasks[2].DueOffsetDays.Should().BeNull();
        tasks[2].IsMandatory.Should().BeNull();
        tasks[3].AssignedToName.Should().Be("HR");
        tasks[3].IsMandatory.Should().BeNull();
        tasks[4].AssignedToName.Should().Be("IT");
        tasks[4].IsMandatory.Should().BeTrue();
    }

    [Fact]
    public void ConditionsAndAmbiguousCompoundOwners_AreRetainedForHumanReview()
    {
        const string condition = "If the employee works remotely, IT provides a laptop.";
        const string compound = "HR and IT must prepare documents and issue a laptop.";
        var tasks = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", "# Onboarding\n" + condition + "\n" + compound).OnboardingTaskDrafts!;
        tasks[0].Condition.Should().Be(condition);
        tasks[0].AssignedToName.Should().BeNull();
        tasks[1].AssignedToName.Should().BeNull();
        tasks[1].Category.Should().Be("General");
        tasks[1].IsMandatory.Should().BeNull();
    }

    [Fact]
    public void UnheadedGenericActions_AreNotAssumedToBeOnboarding()
    {
        var result = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", "HR signs a contract.\nIT issues a laptop to a new hire.");
        result.OnboardingTaskDrafts.Should().ContainSingle().Which.SourceQuote.Should().Be("IT issues a laptop to a new hire.");
    }

    [Fact]
    public void RepeatedCalls_AreStableAndSourceVersionChangesTaskIdentity()
    {
        const string text = "# Orientation\nAttend safety training.";
        var first = LocalOnboardingPolicyMapper.Prepare(DocumentId, "a", text).OnboardingTaskDrafts!.Single();
        var again = LocalOnboardingPolicyMapper.Prepare(DocumentId, "a", text).OnboardingTaskDrafts!.Single();
        var revised = LocalOnboardingPolicyMapper.Prepare(DocumentId, "b", text).OnboardingTaskDrafts!.Single();
        first.Should().Be(again);
        first.Id.Should().NotBe(revised.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("An overview of company values.")]
    [InlineData("# Onboarding\nOur goal is a welcoming workplace.")]
    public void UnrecognizedText_DoesNotInventTasksOrClaimPolicyCoverage(string text)
    {
        var result = LocalOnboardingPolicyMapper.Prepare(DocumentId, "h", text);
        result.OnboardingTaskDrafts.Should().BeEmpty();
        result.Coverage.Should().NotContain(item => item.Status == "Task drafts to review");
        result.Issues.Should().Contain(item => item.Kind == "info");
    }
}
