using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>An accepted offer carries its contract (type, start, end), the opening's grade and the matching hire template's defaults to the hire draft. Real Postgres.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class HiringContractAndDefaultsPostgresTests(PostgresFixture fixture) : HiringPostgresTestBase(fixture)
{
    // ── Contract type and term carry from the offer ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFixedTermOffer_CarriesItsTypeStartAndEndDateToTheHireDraft()
    {
        var s = await SeedAsync(o => { o.ContractType = "Fixed-term"; o.ContractTermMonths = 12; });
        var result = await AcceptAsync(s);
        result.Outcome.Should().Be(OfferAcceptanceOutcome.Accepted, result.Message);

        var draft = await DraftOfAsync(result.OnboardingDraftId!.Value);
        draft.ContractType.Should().Be("Fixed-term");
        draft.ContractStartDate.Should().Be(new DateOnly(2027, 3, 1));
        draft.ContractEndDate.Should().Be(new DateOnly(2028, 2, 29));
    }

    [Fact]
    public async Task APermanentOffer_CarriesPermanentAndNoEndDate()
    {
        var s = await SeedAsync(o => o.ContractType = "Permanent");
        var draft = await DraftOfAsync((await AcceptAsync(s)).OnboardingDraftId!.Value);
        draft.ContractType.Should().Be("Permanent");
        draft.ContractEndDate.Should().BeNull();
    }

    [Fact]
    public async Task AnOfferWrittenBeforeTheTermWasRecorded_StillHiresAsPermanent()
    {
        var s = await SeedAsync();
        var draft = await DraftOfAsync((await AcceptAsync(s)).OnboardingDraftId!.Value);
        draft.ContractType.Should().Be("Permanent");
        draft.ContractEndDate.Should().BeNull();
    }

    // ── The opening's grade and the hire template reach the draft ──────────────────────────────────────────────────────

    [Fact]
    public async Task TheOpeningsGradeAndAMatchingHireTemplatesDefaultsReachTheDraft()
    {
        var company = Guid.NewGuid();
        var grade = new Grade { TenantId = Guid.Empty, CompanyId = company, Code = "G5", Name = "Grade 5", IsActive = true };
        var s = await SeedAsync(companyId: company, opening: o => { o.GradeId = grade.Id; o.EmploymentType = "Full-Time"; },
            extra: (db, tenant) =>
            {
                grade.TenantId = tenant;
                db.Grades.Add(grade);
                db.HireTemplates.Add(new HireTemplate
                {
                    TenantId = tenant, CompanyId = company, Name = "Grade 5 hires", NameKey = "grade 5 hires", MatchGradeId = grade.Id,
                    MatchKey = HireTemplate.Key(null, grade.Id, null),
                    Defaults = """{"employmentType":"Part-Time","workLocation":"Jeddah branch"}""",
                });
            });

        var draft = await DraftOfAsync((await AcceptAsync(s)).OnboardingDraftId!.Value);

        draft.Grade.Should().Be("G5");
        draft.WorkLocation.Should().Be("Jeddah branch");
        draft.EmploymentType.Should().Be("PART_TIME", "the template's default wins over the opening's type, stored as the reference-list code");
    }

    [Fact]
    public async Task WithNoMatchingTemplate_TheOpeningsEmploymentTypeCarries_AndNoGradeIsInvented()
    {
        var s = await SeedAsync(opening: o => o.EmploymentType = "Full-Time");
        var draft = await DraftOfAsync((await AcceptAsync(s)).OnboardingDraftId!.Value);
        draft.EmploymentType.Should().Be("FULL_TIME");
        draft.Grade.Should().BeEmpty();
        draft.WorkLocation.Should().BeEmpty();
    }

}
