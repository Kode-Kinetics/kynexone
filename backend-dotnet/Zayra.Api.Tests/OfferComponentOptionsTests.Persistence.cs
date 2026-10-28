using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public partial class OfferComponentOptionsTests
{
    private ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private async Task<ZayraDbContext> SeedCompany()
    {
        var db = Db(); db.Companies.Add(new Company { Id = _company, TenantId = _tenant, DefaultCurrency = "KWD" });
        await db.SaveChangesAsync(); return db;
    }
    private static OfferComponentInput Basic() => OfferCompensationTests.Cash("BASIC", 1000) with { LabelEn = "Basic salary" };
    private Task<OfferCompensationSnapshot> Validate(ZayraDbContext db, params OfferComponentInput[] inputs) =>
        OfferComponentOptions.CalculateAsync(db, _tenant, _company, _start, inputs, null, null, null, null, default);

    [Fact]
    public async Task UnseededCompanyHasServerCanonicalBasicWithoutInventingCatalogueRows()
    {
        await using var db = await SeedCompany();
        var snapshot = await Validate(db, Basic());
        snapshot.Currency.Should().Be("KWD"); snapshot.BasicSalary.Should().Be(1000);
        snapshot.Components.Single().MappingSource.Should().Be("CanonicalBasic");
        snapshot.Components.Single().StructureField.Should().Be("BasicSalary");
        (await db.PayComponents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CustomBasicSpoofAndUnavailableOrCrossCompanyReferencesAreRejected()
    {
        await using var db = await SeedCompany();
        var foreign = Definition("SPECIAL", Guid.NewGuid()); db.PayComponents.Add(foreign); await db.SaveChangesAsync();
        foreach (var item in new[] { Basic() with { LabelEn = "Discretionary bonus" }, Basic() with { Kind = "Benefit", Amount = null, Frequency = null },
            Basic() with { Included = false, ExclusionReason = "Skip" }, Basic() with { PayComponentId = foreign.Id }, Basic() with { PayComponentId = Guid.NewGuid() } })
            await FluentActions.Awaiting(() => Validate(db, item)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => OfferComponentOptions.CalculateAsync(db, _tenant, null, _start, [Basic()], null, null, null, null, default)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CatalogueAmountsAreExplicitAndDefinitionProvenanceIsSnapshotted()
    {
        await using var db = await SeedCompany();
        var definition = Definition("BASE_PAY"); definition.CalcMethod = "StructureField"; definition.StructureField = "BasicSalary";
        definition.NameEn = "Contract basic"; definition.IsTaxable = true; definition.EffectiveFrom = new(2026, 12, 1);
        db.PayComponents.Add(definition); await db.SaveChangesAsync();
        var input = OfferCompensationTests.Cash("NOT_BASIC", 1250) with { PayComponentId = definition.Id };
        var snapshot = await Validate(db, input);
        snapshot.BasicSalary.Should().Be(1250); snapshot.MonthlyCash.Should().Be(1250);
        var row = snapshot.Components.Single(); row.Code.Should().Be("BASIC"); row.PayComponentId.Should().Be(definition.Id);
        row.DefinitionCode.Should().Be("BASE_PAY"); row.DefinitionLabelEn.Should().Be("Contract basic");
        row.EffectiveFrom.Should().Be(new DateOnly(2026, 12, 1)); row.IsTaxable.Should().BeTrue();
        var frozen = JsonSerializer.Serialize(snapshot);
        definition.NameEn = "Changed"; definition.Value = 5000; await db.SaveChangesAsync();
        JsonSerializer.Serialize(snapshot).Should().Be(frozen);
        await FluentActions.Awaiting(() => Validate(db, input with { Amount = null })).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CompositeAndIndividualMappingsCannotDoubleProjectSalary()
    {
        await using var db = await SeedCompany();
        var composite = Definition("OTHER"); composite.CalcMethod = "StructureField"; composite.StructureField = "OtherAllowancesComposite";
        var food = Definition("FOOD"); food.CalcMethod = "StructureField"; food.StructureField = "FoodAllowance";
        db.PayComponents.AddRange(composite, food); await db.SaveChangesAsync();
        await FluentActions.Awaiting(() => Validate(db, Basic(), OfferCompensationTests.Cash("OTHER", 100) with { PayComponentId = composite.Id },
            OfferCompensationTests.Cash("FOOD", 20) with { PayComponentId = food.Id })).Should().ThrowAsync<ArgumentException>();
        var custom = await Validate(db, Basic(), OfferCompensationTests.Cash("HOUSING", 200));
        custom.MonthlyCash.Should().Be(1200); custom.HousingAllowance.Should().Be(0, "a display code is not a payroll mapping");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothCreatePathsUseTheSameAuthoritativeComponentsAndRejectContradictoryLegacyAmounts(bool useLegacy)
    {
        await using var db = await SeedCompany();
        var dep = new Department { TenantId = _tenant, Code = "HR", NameEn = "People", IsActive = true };
        var des = new Designation { TenantId = _tenant, TitleEn = "HR Lead", Code = "HRL", IsActive = true };
        var opening = new JobOpening { TenantId = _tenant, Title = "HR Lead", DepartmentId = dep.Id, DepartmentName = "People", DesignationId = des.Id };
        var candidate = new Candidate { TenantId = _tenant, CompanyId = _company, FirstName = "Synthetic", LastName = "QA", Email = "synthetic@example.invalid" };
        var app = new JobApplication { TenantId = _tenant, CompanyId = _company, JobOpeningId = opening.Id, CandidateId = candidate.Id, CandidateEmail = candidate.Email, CandidateName = "Synthetic QA", JobTitle = "HR Lead", Stage = "Offer" };
        db.Candidates.Add(candidate); db.Departments.Add(dep); db.Designations.Add(des); db.JobOpenings.Add(opening); db.JobApplications.Add(app); await db.SaveChangesAsync();
        var http = Http("Admin");
        var offers = new OffersController(db, null!, new RecruitmentService(db), OfferWorkflowTestSupport.Lifecycle(db)) { ControllerContext = new() { HttpContext = http } };
        var applications = new ApplicationsController(db, new RecruitmentService(db), null!, OfferWorkflowTestSupport.Lifecycle(db)) { ControllerContext = new() { HttpContext = http } };
        var components = new[] { Basic(), OfferCompensationTests.Cash("JOINING", 500, "OneTime") };
        var created = await offers.Create(new(app.Id, "HR Lead", "People", _start, useLegacy ? 1000 : null, null, null, null, 3, null, null, Components: useLegacy ? null : components, ExpectedApplicationVersion: 1), default);
        var first = (created as CreatedAtActionResult)?.Value as OfferLetter;
        first.Should().NotBeNull(); first!.GrossSalary.Should().Be(1000);
        (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == app.Id)).OfferedSalary.Should().Be(1000);
        var generated = await applications.GenerateOffer(app.Id, new("People", _start, useLegacy ? 1000 : null, null, null, null, 3, Components: useLegacy ? null : components, ExpectedApplicationVersion: 2), default);
        var second = ((CreatedResult)generated).Value as OfferLetter;
        second!.CompensationSnapshotJson.Should().Be(first.CompensationSnapshotJson);
        (await offers.Create(new(app.Id, "HR Lead", "People", _start, 999, null, null, null, 3, null, null, Components: components, ExpectedApplicationVersion: 3), default))
            .Should().BeOfType<UnprocessableEntityObjectResult>();
        (await applications.GenerateOffer(app.Id, new("People", _start, 999, null, null, null, 3, Components: components, ExpectedApplicationVersion: 3), default))
            .Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    private DefaultHttpContext Http(string role) => new() { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
        new Claim("tenant_id", _tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role) }, "test")) };
    private sealed class Scope(RequestEntityScope scope) : IRequestEntityScopeResolver
    {
        public RequestEntityScope Resolve() => scope;
        public RequestEntityScope ResolveFor(ClaimsPrincipal? user, string? selectedCompanyHeader) => scope;
    }
    private OfferPolicyController PolicyController(ZayraDbContext db, string role = "Admin", Guid? selected = null, bool allowed = true) =>
        new(db, new Scope(new(_tenant, false, allowed ? [_company] : [], selected ?? _company, true, false,
            ScopeResolutionSource.NoHttpContext, ScopeDenialReason.None, false))) { ControllerContext = new() { HttpContext = Http(role) } };

    [Fact]
    public async Task AdminPolicyUsesSelectedCompanyExpectedVersionAndAuditWithoutRewritingSnapshots()
    {
        await using var db = await SeedCompany(); var controller = PolicyController(db);
        var original = await Validate(db, Basic());
        var savedOffer = new OfferLetter { TenantId = _tenant, CompanyId = _company, StartDate = _start };
        OfferCompensation.ApplySnapshot(savedOffer, original); db.OfferLetters.Add(savedOffer); await db.SaveChangesAsync();
        var savedJson = savedOffer.CompensationSnapshotJson;
        var updated = (OkObjectResult)await controller.Put(new(3, ["BASIC"], 0), default);
        ((OfferPolicyDto)updated.Value!).ExpectedVersion.Should().Be(1);
        original.Precision.Should().Be(2); original.PolicyVersion.Should().Be(0);
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == savedOffer.Id)).CompensationSnapshotJson.Should().Be(savedJson);
        var future = await Validate(db, Basic(), OfferCompensationTests.Cash("EXTRA", 0.001m));
        future.Precision.Should().Be(3); future.PolicyVersion.Should().Be(1);
        (await controller.Put(new(0, ["BASIC"], 0), default)).Should().BeOfType<ConflictObjectResult>();
        (await db.RecruitmentAuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PolicyCannotBeReadOrChangedByNonAdminOrOutsideSelectedCompany()
    {
        await using var db = await SeedCompany();
        foreach (var controller in new[] { PolicyController(db, "Recruiter"), PolicyController(db, allowed: false), PolicyController(db, selected: Guid.NewGuid()) })
        {
            (await controller.Get(default)).Should().BeOfType<ForbidResult>();
            (await controller.Put(new(3, [], 0), default)).Should().BeOfType<ForbidResult>();
        }
        (await db.RecruitmentAuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AdditionalRequiredCatalogueComponentsCannotBeOmittedOrReplacedByCustomCode()
    {
        await using var db = await SeedCompany(); var housing = Definition("HOUSING"); housing.CalcMethod = "StructureField"; housing.StructureField = "HousingAllowance";
        db.PayComponents.Add(housing); await db.SaveChangesAsync();
        (await PolicyController(db).Put(new(3, ["HOUSING"], 0), default)).Should().BeOfType<OkObjectResult>();
        await FluentActions.Awaiting(() => Validate(db, Basic())).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Validate(db, Basic(), OfferCompensationTests.Cash("HOUSING", 200))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Validate(db, Basic(), OfferCompensationTests.Cash("HOUSING", 200.001m) with { PayComponentId = housing.Id })).Should().ThrowAsync<ArgumentException>();
        var valid = await Validate(db, Basic(), OfferCompensationTests.Cash("HOUSING", 200) with { PayComponentId = housing.Id });
        valid.HousingAllowance.Should().Be(200); valid.MonthlyCash.Should().Be(1200);
    }

    [Fact]
    public async Task ComponentOptionsUseRequestedStartDateAndExposeNoSharedAmounts()
    {
        await using var db = await SeedCompany();
        var future = Definition("FUTURE", _company); future.EffectiveFrom = _start;
        db.PayComponents.Add(future); await db.SaveChangesAsync();
        var controller = PolicyController(db, "Recruiter");
        var before = (OfferComponentOptionsDto)((OkObjectResult)await controller.Options(_start.AddDays(-1), default)).Value!;
        before.Options.Should().BeEmpty(); before.CanonicalBasic.Code.Should().Be("BASIC");
        var at = (OfferComponentOptionsDto)((OkObjectResult)await controller.Options(_start, default)).Value!;
        at.Options.Should().ContainSingle().Which.Id.Should().Be(future.Id);
        // No shared amount (the definition's Value 999) leaks as a JSON number. A substring check also matched random
        // GUIDs that happen to contain "999", which failed this test about one run in fifty.
        JsonSerializer.Serialize(at).Should().NotMatchRegex(@"[:\[,]\s*999(\.0+)?\s*[,}\]]");
        (await controller.Options(null, default)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RequiredMappedSubtotalMustFitEmployeeSalaryCapacity()
    {
        await using var db = await SeedCompany(); var housing = Definition("HOUSING"); housing.CalcMethod = "StructureField"; housing.StructureField = "HousingAllowance";
        db.PayComponents.Add(housing); await db.SaveChangesAsync();
        (await PolicyController(db).Put(new(2, ["HOUSING"], 0), default)).Should().BeOfType<OkObjectResult>();
        await FluentActions.Awaiting(() => Validate(db, Basic() with { Amount = 9_999_999_000m },
            OfferCompensationTests.Cash("HOUSING", 1000) with { PayComponentId = housing.Id })).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task OtherAllowanceUsesTheActualSalaryWriterFieldAndCannotOverlapComposite()
    {
        await using var db = await SeedCompany();
        var other = Definition("OTHER"); other.CalcMethod = "StructureField"; other.StructureField = "OtherAllowance";
        db.PayComponents.Add(other); await db.SaveChangesAsync();
        var snapshot = await Validate(db, Basic(), OfferCompensationTests.Cash("OTHER", 20) with { PayComponentId = other.Id });
        snapshot.OtherAllowances.Should().Be(20);
        snapshot.Components.Single(c => c.Code == "OTHER").StructureField.Should().Be("OtherAllowance");
        var composite = Definition("COMPOSITE"); composite.CalcMethod = "StructureField"; composite.StructureField = "OtherAllowancesComposite";
        db.PayComponents.Add(composite); await db.SaveChangesAsync();
        await FluentActions.Awaiting(() => Validate(db, Basic(), OfferCompensationTests.Cash("OTHER", 20) with { PayComponentId = other.Id },
            OfferCompensationTests.Cash("COMPOSITE", 100) with { PayComponentId = composite.Id })).Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(false, "Future")]
    [InlineData(true, "Future")]
    [InlineData(false, "Expired")]
    [InlineData(true, "Expired")]
    [InlineData(false, "InactiveOverride")]
    [InlineData(true, "InactiveOverride")]
    [InlineData(false, "Eligible")]
    [InlineData(true, "Eligible")]
    [InlineData(false, "DifferentMapping")]
    [InlineData(true, "DifferentMapping")]
    [InlineData(false, "Fixed")]
    [InlineData(true, "Fixed")]
    public async Task LegacyAdaptersResolveRequiredHousingAtStartDateAndRetainDefinitionProvenance(bool generate, string state)
    {
        await using var db = await SeedCompany();
        var housing = Definition("HOUSING", _company);
        housing.CalcMethod = "StructureField"; housing.StructureField = "HousingAllowance";
        housing.NameEn = "Company contractual housing"; housing.IsTaxable = true;
        housing.EffectiveFrom = _start.AddDays(-1); housing.EffectiveTo = _start.AddMonths(1);
        if (state == "Future") housing.EffectiveFrom = _start.AddDays(1);
        if (state == "Expired") housing.EffectiveTo = _start.AddDays(-1);
        if (state == "DifferentMapping") housing.StructureField = "TransportAllowance";
        if (state == "Fixed") { housing.CalcMethod = "Fixed"; housing.StructureField = null; }
        if (state is "InactiveOverride" or "Eligible")
        {
            var tenantDefault = Definition("HOUSING"); tenantDefault.CalcMethod = "StructureField"; tenantDefault.StructureField = "HousingAllowance";
            db.PayComponents.Add(tenantDefault);
        }
        if (state == "InactiveOverride") housing.IsActive = false;
        db.PayComponents.Add(housing); await db.SaveChangesAsync();
        (await PolicyController(db).Put(new(2, ["HOUSING"], 0), default)).Should().BeOfType<OkObjectResult>();

        var designation = new Designation { TenantId = _tenant, TitleEn = "HR Lead", Code = "HRL", IsActive = true };
        var opening = new JobOpening { TenantId = _tenant, Title = "HR Lead", DesignationId = designation.Id };
        var app = new JobApplication { TenantId = _tenant, CompanyId = _company, JobOpeningId = opening.Id, CandidateName = "Synthetic QA", JobTitle = "HR Lead", Stage = "Offer" };
        db.Designations.Add(designation); db.JobOpenings.Add(opening); db.JobApplications.Add(app); await db.SaveChangesAsync();
        var http = Http("Admin");
        IActionResult result = generate
            ? await new ApplicationsController(db, new RecruitmentService(db), null!) { ControllerContext = new() { HttpContext = http } }
                .GenerateOffer(app.Id, new(null, _start, 1000, 200, 50, 25, 3), default)
            : await new OffersController(db, null!, new RecruitmentService(db)) { ControllerContext = new() { HttpContext = http } }
                .Create(new(app.Id, "HR Lead", null, _start, 1000, 200, 50, 25, 3, null, null), default);
        if (state != "Eligible")
        {
            result.Should().BeOfType<UnprocessableEntityObjectResult>();
            (await db.OfferLetters.CountAsync()).Should().Be(0);
            app.OfferedSalary.Should().BeNull();
            return;
        }
        var offer = ((ObjectResult)result).Value.Should().BeOfType<OfferLetter>().Subject;
        offer.GrossSalary.Should().Be(1275); offer.HousingAllowance.Should().Be(200);
        var snapshot = JsonSerializer.Deserialize<OfferCompensationSnapshot>(offer.CompensationSnapshotJson!, OfferCompensation.Json)!;
        var row = snapshot.Components.Single(c => c.Code == "HOUSING");
        row.Required.Should().BeTrue(); row.PayComponentId.Should().Be(housing.Id);
        row.MappingSource.Should().Be("PayComponentDefinition"); row.StructureField.Should().Be("HousingAllowance");
        row.DefinitionLabelEn.Should().Be("Company contractual housing"); row.DefinitionCode.Should().Be("HOUSING");
        row.EffectiveFrom.Should().Be(_start.AddDays(-1)); row.EffectiveTo.Should().Be(_start.AddMonths(1));
        row.Classification.Should().Be("Earning"); row.IsTaxable.Should().BeTrue();
        snapshot.Components.Single(c => c.Code == "TRANSPORT").MappingSource.Should().Be("LegacyFourField");
    }

    [Theory]
    [InlineData(100, false, false)]
    [InlineData(99, false, true)]
    [InlineData(99, true, true)]
    public async Task PolicyComponentLimitCountsNormalizedFinalSetIncludingCanonicalBasic(int nonBasicCount, bool repeatBasic, bool accepted)
    {
        await using var db = await SeedCompany();
        var definitions = Enumerable.Range(1, nonBasicCount).Select(i => Definition($"EXTRA_{i}")).ToArray();
        db.PayComponents.AddRange(definitions); await db.SaveChangesAsync();
        var codes = definitions.Select(d => d.Code).ToList();
        if (repeatBasic) codes.AddRange(["basic", " BASIC "]);
        var result = await PolicyController(db).Put(new(2, codes, 0), default);
        if (!accepted)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            (await db.OfferCompanyPolicies.CountAsync()).Should().Be(0);
            (await db.RecruitmentAuditLogs.CountAsync()).Should().Be(0);
            return;
        }
        var policy = ((OkObjectResult)result).Value.Should().BeOfType<OfferPolicyDto>().Subject;
        policy.RequiredComponentCodes.Should().HaveCount(100).And.Contain("BASIC");
        var inputs = new[] { Basic() }.Concat(definitions.Select(d => OfferCompensationTests.Cash(d.Code, 1) with { PayComponentId = d.Id })).ToArray();
        var snapshot = await Validate(db, inputs);
        snapshot.Components.Should().HaveCount(100); snapshot.MonthlyCash.Should().Be(1099);
    }
}
