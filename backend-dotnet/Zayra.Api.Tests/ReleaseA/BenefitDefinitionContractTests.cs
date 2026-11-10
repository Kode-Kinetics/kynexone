using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The shape of the benefit definition contract (feature <c>benefit_definition_v1</c>): what a request may carry, what the matrix returns, which
/// route edits a definition and who may call it, and the columns and CHECKs the definition lives in. Behaviour is proved in the service tests.
/// </summary>
public class BenefitDefinitionContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TheOldThreeFieldBody_StillBinds_AndCarriesNoDefinitionField()
    {
        var req = JsonSerializer.Deserialize<CreateBenefitRequest>("""{"nameEn":"Parking","nameAr":"موقف","kind":"Contract"}""", Web)!;

        (req.NameEn, req.NameAr, req.Kind).Should().Be(("Parking", "موقف", "Contract"));
        req.CarriesDefinitionFields.Should().BeFalse();
        req.Unrecognised.Should().BeNull();
        JsonSerializer.Deserialize<CreateBenefitRequest>("""{"nameEn":"Parking"}""", Web)!.Kind
            .Should().BeNull("an absent kind is the default (Contract), told apart from a stated one so a contradiction with 'delivery' can be refused");
    }

    [Fact]
    public void TheDefinitionFields_Bind_AndAnUnknownFieldIsCaptured_NotDropped()
    {
        var company = Guid.NewGuid();
        var request = Guid.NewGuid();
        var json = $$"""
            {"nameEn":"Parking","code":"PARK","delivery":"Coverage","frequency":null,"category":"Facilities","policyClass":"Discretionary",
             "effectiveFrom":"2026-11-01","effectiveTo":"2027-10-31","isActive":true,"requiresEnrolment":false,
             "scope":{"companyIds":["{{company}}"]},"requestId":"{{request}}","financeConfirmedAtUtc":"2026-01-01T00:00:00Z","tenantId":"{{Guid.NewGuid()}}"}
            """;
        var req = JsonSerializer.Deserialize<CreateBenefitRequest>(json, Web)!;

        (req.Code, req.Delivery, req.Category, req.PolicyClass, req.IsActive, req.RequiresEnrolment, req.RequestId)
            .Should().Be(("PARK", "Coverage", "Facilities", "Discretionary", true, false, request));
        (req.EffectiveFrom, req.EffectiveTo).Should().Be((new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31)));
        req.Scope!.CompanyIds.Should().Equal(company);
        req.CarriesDefinitionFields.Should().BeTrue();
        req.Unrecognised!.Keys.Should().BeEquivalentTo("financeConfirmedAtUtc", "tenantId");
    }

    [Fact]
    public void TheUpdateRequest_BindsEveryEditableField_AndCapturesTheRest()
    {
        var req = JsonSerializer.Deserialize<UpdateBenefitRequest>(
            """{"expectedVersion":"123","nameEn":"Parking","nameAr":"موقف","category":"Facilities","policyClass":"Contractual","isActive":false,"sourceBenefitPlanId":"x"}""", Web)!;

        (req.ExpectedVersion, req.NameEn, req.Category, req.PolicyClass, req.IsActive).Should().Be(("123", "Parking", "Facilities", "Contractual", false));
        req.Unrecognised!.Keys.Should().Equal("sourceBenefitPlanId");
    }

    [Fact]
    public void TheEditRoute_IsAPutOnComponentsCode_WithTheSamePermissionAsCreate()
    {
        var edit = typeof(EntitlementMatrixController).GetMethod(nameof(EntitlementMatrixController.UpdateBenefit))!;
        var create = typeof(EntitlementMatrixController).GetMethod(nameof(EntitlementMatrixController.AddBenefit))!;

        edit.GetCustomAttribute<HttpPutAttribute>()!.Template.Should().Be("components/{code}");
        edit.GetCustomAttribute<HasPermissionAttribute>()!.Permissions.Should().Equal(create.GetCustomAttribute<HasPermissionAttribute>()!.Permissions)
            .And.Equal("entitlements.manage");
    }

    [Fact]
    public void TheMatrixComponent_CarriesADefinition_AndTheMatrixAdvertisesItsCapabilities()
    {
        typeof(MatrixComponentDto).GetProperty(nameof(MatrixComponentDto.Definition))!.PropertyType.Should().Be(typeof(BenefitDefinitionDto));
        typeof(EntitlementMatrixDto).GetProperty(nameof(EntitlementMatrixDto.Capabilities))!.PropertyType.Should().Be(typeof(MatrixCapabilitiesDto));
        JsonSerializer.Serialize(new MatrixCapabilitiesDto(true), Web).Should().Be("""{"benefitDefinition":true}""");
        typeof(BenefitDefinitionDto).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "Delivery", "Category", "PolicyClass", "EffectiveFrom", "EffectiveTo", "IsActive", "RequiresEnrolment", "OfferedCompanyIds", "Version", "SourcePlanId", "CodeLocked");
    }

    [Fact]
    public void TheFeatureKey_IsOptIn_AndOwnsNoRoute_AndHasAConsoleSwitch()
    {
        FeatureKeys.BenefitDefinitionV1.Should().Be("benefit_definition_v1");
        OptInFeatures.IsOptIn(FeatureKeys.BenefitDefinitionV1).Should().BeTrue("an absent row means OFF");
        OptInFeatures.RoutePrefixes[FeatureKeys.BenefitDefinitionV1].Should().BeEmpty("the routes belong to release_a");
        Zayra.Api.Controllers.PlatformController.FeatureFlagCatalog.Should().Contain(f => f.Key == FeatureKeys.BenefitDefinitionV1);
    }

    [Fact]
    public void ThePayComponentModel_HasTheDefinitionColumns_TheChecks_AndAUniqueSourcePlanIndex()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var entity = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(db).Model.FindEntityType(typeof(PayComponent))!;
        var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table("pay_components");

        entity.FindProperty(nameof(PayComponent.BenefitCategory))!.GetColumnName(table).Should().Be("benefit_category");
        entity.FindProperty(nameof(PayComponent.BenefitCategory))!.GetMaxLength().Should().Be(120);
        entity.FindProperty(nameof(PayComponent.PolicyClass))!.GetColumnName(table).Should().Be("policy_class");
        entity.FindProperty(nameof(PayComponent.PolicyClass))!.GetMaxLength().Should().Be(16);
        entity.FindProperty(nameof(PayComponent.RequiresEnrolment))!.IsNullable.Should().BeTrue();
        entity.FindProperty(nameof(PayComponent.SourceBenefitPlanId))!.GetColumnName(table).Should().Be("source_benefit_plan_id");
        foreach (var p in new[] { nameof(PayComponent.BenefitCategory), nameof(PayComponent.PolicyClass), nameof(PayComponent.RequiresEnrolment), nameof(PayComponent.SourceBenefitPlanId) })
            entity.FindProperty(p)!.IsNullable.Should().BeTrue($"{p} is additive and nullable: every existing row stays valid");
        entity.GetCheckConstraints().Select(c => c.Name).Should().Contain(
            ["ck_pay_components__policy_class", "ck_pay_components__benefit_fields_custom_only", "ck_pay_components__requires_enrolment_unsupported", "ck_pay_components__benefit_category_not_blank"]);
        entity.GetCheckConstraints().Single(c => c.Name == "ck_pay_components__policy_class").Sql.Should().Contain("'Contractual','Discretionary'").And.NotContain("Mandatory");
        entity.GetForeignKeys().Should().ContainSingle(f => f.Properties.Single().Name == nameof(PayComponent.SourceBenefitPlanId) && f.DeleteBehavior == DeleteBehavior.Restrict);
        entity.GetIndexes().Should().ContainSingle(i => i.IsUnique && i.GetFilter() == "source_benefit_plan_id IS NOT NULL");
    }
}
