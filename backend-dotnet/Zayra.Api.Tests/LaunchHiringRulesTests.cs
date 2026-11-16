using FluentAssertions;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Launch-hiring rules that need no database: what an opening inherits from its requisition, and when an application may move to Offer.</summary>
public sealed class LaunchHiringRulesTests
{
    private static ManpowerRequisition Requisition() => new()
    {
        DepartmentId = Guid.NewGuid(), DepartmentName = "Finance", DesignationId = Guid.NewGuid(), DesignationTitle = "Accountant",
        GradeId = Guid.NewGuid(), HeadCount = 2, EmploymentType = "Contract", RequiredSkills = "IFRS\nExcel", BudgetFrom = 9000, BudgetTo = 11000,
    };

    private static CreateOpeningRequest Request(Guid requisitionId, string requirements = "", decimal? from = null, decimal? to = null,
        string dept = "", string title = "", Guid? departmentId = null, Guid? designationId = null, Guid? gradeId = null) =>
        new(requisitionId, "Accountant", dept, title, "Full-Time", 1, "", requirements, "", from, to, "Riyadh", "",
            DepartmentId: departmentId, DesignationId: designationId, GradeId: gradeId);

    [Fact]
    public void AnOpeningInheritsEverythingItsRequestLeavesBlank()
    {
        var r = Requisition();
        var terms = OpeningTerms.Inherit(Request(Guid.NewGuid()), r);
        terms.DepartmentId.Should().Be(r.DepartmentId);
        terms.DesignationId.Should().Be(r.DesignationId);
        terms.GradeId.Should().Be(r.GradeId);
        terms.DepartmentName.Should().Be("Finance");
        terms.DesignationTitle.Should().Be("Accountant");
        terms.SalaryFrom.Should().Be(9000);
        terms.SalaryTo.Should().Be(11000);
        terms.Requirements.Should().Be("IFRS\nExcel");
    }

    [Fact]
    public void WhatTheRequestStatesWinsOverTheRequisition()
    {
        var r = Requisition();
        var dept = Guid.NewGuid(); var grade = Guid.NewGuid();
        var terms = OpeningTerms.Inherit(Request(Guid.NewGuid(), "Own requirements", 7000, 8000, "Sales", "Rep", dept, null, grade), r);
        terms.DepartmentId.Should().Be(dept);
        terms.GradeId.Should().Be(grade);
        terms.DesignationId.Should().Be(r.DesignationId, "an unstated field is still inherited");
        terms.DepartmentName.Should().Be("Sales");
        terms.DesignationTitle.Should().Be("Rep");
        terms.SalaryFrom.Should().Be(7000);
        terms.SalaryTo.Should().Be(8000);
        terms.Requirements.Should().Be("Own requirements");
    }

    [Fact]
    public void AnOpeningWithNoRequisitionKeepsItsRequestAsIs()
    {
        var terms = OpeningTerms.Inherit(Request(Guid.Empty, "x", 1, 2, "Ops", "Officer"), null);
        terms.DepartmentId.Should().BeNull();
        terms.GradeId.Should().BeNull();
        terms.SalaryFrom.Should().Be(1);
        terms.Requirements.Should().Be("x");
    }

    [Theory]
    [InlineData("Screening", false, false, null, OfferStageGateOutcome.NotApplicable)]
    [InlineData("Offer", true, false, null, OfferStageGateOutcome.Allowed)]
    [InlineData("Offer", false, true, null, OfferStageGateOutcome.Allowed)]
    [InlineData("Offer", false, false, null, OfferStageGateOutcome.NeedsEvaluation)]
    [InlineData("Offer", false, false, "   ", OfferStageGateOutcome.NeedsEvaluation)]
    [InlineData("Offer", false, false, "abc", OfferStageGateOutcome.NeedsEvaluation)]
    [InlineData("Offer", false, false, "Senior referral, interviewed outside the system", OfferStageGateOutcome.OverriddenWithReason)]
    public void MovingToOfferNeedsAnInterviewOrAssessmentUnlessAReasonIsGiven(string toStage, bool interview, bool assessment, string? reason, OfferStageGateOutcome expected) =>
        OfferStageGate.Evaluate(toStage, interview, assessment, reason).Should().Be(expected);

    [Fact]
    public void AReasonIsNotNeededWhenAnEvaluationExists_AndIsNotRecorded()
    {
        OfferStageGate.Evaluate("Offer", true, false, "unneeded reason text").Should().Be(OfferStageGateOutcome.Allowed);
    }

    [Fact]
    public void AReasonLongerThanTheStoredLimitIsRefused() =>
        OfferStageGate.Evaluate("Offer", false, false, new string('x', 1001)).Should().Be(OfferStageGateOutcome.NeedsEvaluation);
}
