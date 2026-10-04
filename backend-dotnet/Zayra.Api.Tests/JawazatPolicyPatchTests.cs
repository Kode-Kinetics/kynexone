using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Jawazat;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public sealed class JawazatPolicyPatchTests
{
    [Fact]
    public async Task NarrowPatch_PreservesReadinessAndLifecycleChangesMadeAfterEditorOpened()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var tenant = Guid.NewGuid();
        var company = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using (var seed = new ZayraDbContext(options))
        {
            seed.CompanyComplianceProfiles.Add(new CompanyComplianceProfile { Id = id, TenantId = tenant, CompanyId = company, CountryCode = "SA", EffectiveFrom = new(2026, 1, 1), RequiredFieldsJson = "[]" });
            await seed.SaveChangesAsync();
        }
        // Another administrator changes readiness and lifecycle after the editor fetched its copy.
        await using (var changed = new ZayraDbContext(options))
        {
            var profile = await changed.CompanyComplianceProfiles.SingleAsync();
            profile.RequiredFieldsJson = "[{\"field\":\"IqamaNumber\",\"failClosed\":true}]";
            profile.Status = "Draft";
            profile.EffectiveFrom = new(2026, 5, 1);
            profile.EffectiveTo = new(2026, 12, 31);
            profile.Notes = "Keep the other administrator's note";
            await changed.SaveChangesAsync();
        }
        await using var db = new ZayraDbContext(options);
        var policyJson = JawazatJson.Serialize(new JawazatPolicy(RuleVersion: "revised-jawazat-only"));
        var controller = Controller(db, tenant, company, "Compliance Officer");
        Assert.IsType<OkObjectResult>(await controller.UpdateJawazatPolicy(id, new UpdateJawazatPolicyRequest(policyJson), default));
        var saved = await db.CompanyComplianceProfiles.SingleAsync();
        Assert.Equal("revised-jawazat-only", JawazatPolicyRules.Parse(saved.JawazatPolicyJson).RuleVersion);
        Assert.Equal("[{\"field\":\"IqamaNumber\",\"failClosed\":true}]", saved.RequiredFieldsJson);
        Assert.Equal("Draft", saved.Status);
        Assert.Equal(new DateOnly(2026, 5, 1), saved.EffectiveFrom);
        Assert.Equal(new DateOnly(2026, 12, 31), saved.EffectiveTo);
        Assert.Equal("Keep the other administrator's note", saved.Notes);
        Assert.NotNull(saved.UpdatedBy);
        Assert.NotNull(saved.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("patch")]
    public async Task PolicyWrites_ValidateExplicitReviewThenStampAuthenticatedReviewerAndServerTime(string path)
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        db.Companies.Add(new Company { Id = company, TenantId = tenant, CountryCode = "SA", LegalNameEn = "Saudi Company" });
        var profile = new CompanyComplianceProfile { TenantId = tenant, CompanyId = company, CountryCode = "SA" };
        if (path != "create") db.CompanyComplianceProfiles.Add(profile);
        await db.SaveChangesAsync();
        var controller = Controller(db, tenant, company, "Compliance Officer");
        var userId = controller.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var json = JawazatJson.Serialize(new JawazatPolicy(1, true, true, 90, 90, "review-v2", "Another reviewer", DateTime.UtcNow.AddDays(-7)));
        var request = new CompanyComplianceProfileRequest(company, "SA", null, null, DateOnly.FromDateTime(DateTime.UtcNow), null, "Active", "[]", "Policy", json);
        var started = DateTime.UtcNow;
        var result = path switch
        {
            "create" => await controller.Create(request, default),
            "update" => await controller.Update(profile.Id, request, default),
            _ => await controller.UpdateJawazatPolicy(profile.Id, new UpdateJawazatPolicyRequest(json), default)
        };
        Assert.IsType<OkObjectResult>(result);
        var saved = JawazatPolicyRules.Parse((await db.CompanyComplianceProfiles.SingleAsync()).JawazatPolicyJson);
        Assert.Equal(userId, saved.ReviewedBy);
        Assert.InRange(saved.ReviewedAtUtc!.Value, started, DateTime.UtcNow);
        Assert.Equal("review-v2", saved.RuleVersion);
    }

    [Fact]
    public async Task EnabledPolicyWithoutExplicitReview_IsRejectedBeforeStamping()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        var profile = new CompanyComplianceProfile { TenantId = tenant, CompanyId = company, CountryCode = "SA" };
        db.CompanyComplianceProfiles.Add(profile); await db.SaveChangesAsync();
        var json = JawazatJson.Serialize(new JawazatPolicy(EmployerAssistedEnabled: true));
        Assert.IsType<BadRequestObjectResult>(await Controller(db, tenant, company, "Compliance Officer")
            .UpdateJawazatPolicy(profile.Id, new UpdateJawazatPolicyRequest(json), default));
        Assert.Null(profile.JawazatPolicyJson);
    }

    [Theory]
    [InlineData("AE", "Compliance Officer", "{}", 400)]
    [InlineData("SA", "HR Manager", "{}", 403)]
    [InlineData("SA", "Compliance Officer", "{\"schemaVersion\":99}", 400)]
    public async Task Patch_EnforcesCountryActorAndTypedPolicy(string country, string role, string json, int expectedStatus)
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        var profile = new CompanyComplianceProfile { TenantId = tenant, CompanyId = company, CountryCode = country };
        db.CompanyComplianceProfiles.Add(profile); await db.SaveChangesAsync();
        var result = await Controller(db, tenant, company, role).UpdateJawazatPolicy(profile.Id, new UpdateJawazatPolicyRequest(json), default);
        if (expectedStatus == 403) Assert.IsType<ForbidResult>(result);
        else Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(profile.JawazatPolicyJson);
    }

    private static CompanyComplianceProfilesController Controller(ZayraDbContext db, Guid tenant, Guid company, string role) => new(db)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenant.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role),
                new Claim("entity_scope", JawazatJson.Serialize(new { v = 2, m = "companies", c = new[] { company } }))], "test"))
        } }
    };
}
