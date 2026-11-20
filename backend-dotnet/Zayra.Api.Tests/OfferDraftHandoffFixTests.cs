using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// An offer may hold a department by its code (the placement stores the code when the name is shared, so activation finds the same
/// record). People read the department's name. The accepted offer's hire draft and the workspace now show the name, the hire still
/// activates against the same department, and a frozen original is never rewritten by any of it. Real Postgres.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferDraftHandoffFixTests(PostgresFixture fixture)
{
    private async Task<OfferLifecycleSeed> SeedWithDepartmentCodeAsync(string storedDepartment, params (string Code, string Name)[] departments)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        foreach (var (code, name) in departments)
            db.Departments.Add(new Department { TenantId = seed.Actor.TenantId, CompanyId = seed.Actor.CompanyId, Code = code, NameEn = name, NameAr = name, IsActive = true });
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        offer.OfferedDepartment = storedDepartment;
        await db.SaveChangesAsync();
        return seed;
    }

    [Fact]
    public async Task The_hire_draft_of_an_accepted_offer_carries_the_department_name_and_still_activates_against_the_same_department()
    {
        var seed = await SeedWithDepartmentCodeAsync("ALM-DAIRY-KSA-OPS", ("ALM-DAIRY-KSA-OPS", "Operations"));
        await using var db = fixture.CreateDb();
        var result = await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Recruiter");
        var draft = await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == result.OnboardingDraftId);
        draft.Department.Should().Be("Operations");
        var viaName = await EmployeeOrgFieldResolver.ResolveDepartmentAsync(db, seed.Actor.TenantId, draft.Department, default, draft.AcceptedOfferCompanyId);
        var viaCode = await EmployeeOrgFieldResolver.ResolveDepartmentAsync(db, seed.Actor.TenantId, "ALM-DAIRY-KSA-OPS", default, draft.AcceptedOfferCompanyId);
        viaName.Id.Should().Be(viaCode.Id);
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).OfferedDepartment.Should().Be("ALM-DAIRY-KSA-OPS",
            "the offer keeps what it stored; only what is shown changes");
    }

    [Fact]
    public async Task The_hire_draft_keeps_the_code_when_the_name_would_resolve_to_a_different_department()
    {
        var seed = await SeedWithDepartmentCodeAsync("OPS-B", ("OPS-A", "Operations"), ("OPS-B", "Operations"));
        await using var db = fixture.CreateDb();
        var result = await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Recruiter");
        var draft = await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == result.OnboardingDraftId);
        var intended = await EmployeeOrgFieldResolver.ResolveDepartmentAsync(db, seed.Actor.TenantId, "OPS-B", default, draft.AcceptedOfferCompanyId);
        var resolved = await EmployeeOrgFieldResolver.ResolveDepartmentAsync(db, seed.Actor.TenantId, draft.Department, default, draft.AcceptedOfferCompanyId);
        resolved.Id.Should().Be(intended.Id, "the draft must activate against the department the offer named");
    }

    [Fact]
    public async Task The_workspace_shows_the_department_name_while_the_stored_offer_is_unchanged()
    {
        var seed = await SeedWithDepartmentCodeAsync("ALM-DAIRY-KSA-OPS", ("ALM-DAIRY-KSA-OPS", "Operations"));
        await using var db = fixture.CreateDb();
        var workspace = await new OfferHistoryQueryService(db, new(db, OfferLifecycleServiceTests.Config(false))).WorkspaceAsync(seed.Actor, seed.OfferId);
        workspace!.Offer.OfferedDepartment.Should().Be("Operations");
        await using var verify = fixture.CreateDb();
        (await verify.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).OfferedDepartment.Should().Be("ALM-DAIRY-KSA-OPS");
    }

    [Fact]
    public async Task A_frozen_original_keeps_its_old_text_while_a_newly_generated_letter_states_the_recorded_probation()
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        // A draft written before the fix: the design snapshot carries the old fixed sentence, whatever the offer records.
        await using (var db = fixture.CreateDb())
        {
            (await db.Companies.SingleAsync(c => c.Id == seed.Actor.CompanyId)).CountryCode = "SA";
            var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
            offer.ProbationMonths = 6;
            var current = await OfferLetterDesign.SnapshotAsync(db, offer, OfferLetterDesign.KsaDefault(false), null, null, default);
            var heading = current.Design.Blocks.FindIndex(b => b.Kind == "heading" && b.Text == "Probation");
            current.Design.Blocks[heading + 1].Text = OfferLetterClauses.LegacyProbationEn;
            offer.DesignSnapshotJson = OfferLetterDesign.SerializeSnapshot(current);
            await db.SaveChangesAsync();
        }
        var storage = new OfferMemoryStorage();
        await using var work = fixture.CreateDb();
        var artifacts = new OfferArtifactService(work, storage, OfferLifecycleServiceTests.Config());
        var frozenVersion = (await work.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).Version;
        await artifacts.FreezeAsync(seed.Actor, seed.OfferId, frozenVersion);
        var original = await artifacts.OriginalAsync(seed.Actor, seed.OfferId);
        var snapshotBefore = (await work.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).DesignSnapshotJson;
        Text(original).Should().Contain("probation period of 90 days", "an issued original is immutable by design");

        // The same terms generated again (a new revision, a regenerated preview) take the fix.
        var offerNow = await work.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId);
        var fresh = await OfferLetterDesign.SnapshotAsync(work, offerNow, OfferLetterDesign.KsaDefault(false), null, null, default);
        var freshDesign = fresh.Design; freshDesign.Header = ""; freshDesign.Footer = "";
        freshDesign.Blocks.RemoveAll(b => b.Kind == "compensation");
        Text(OfferLetterDesign.Render(fresh)).Should().Contain("probation period of 180 days").And.NotContain("90 days");

        // And nothing rewrote the frozen original or its design.
        work.ChangeTracker.Clear();
        (await artifacts.OriginalAsync(seed.Actor, seed.OfferId)).Should().Equal(original);
        Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant()
            .Should().Be((await work.OfferArtifacts.AsNoTracking().SingleAsync(a => a.OfferId == seed.OfferId)).Sha256);
        (await work.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).DesignSnapshotJson.Should().Be(snapshotBefore);
    }

    private static string Text(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return Regex.Replace(string.Join(" ", document.GetPages().Select(p => p.Text)), @"\s+", " ");
    }
}
