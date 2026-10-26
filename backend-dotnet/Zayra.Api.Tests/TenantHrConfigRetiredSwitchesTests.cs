using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Tests;

/// <summary>
/// Eleven TenantHrConfig switches were never read by anything (approval routing, import behaviour, hierarchy and payroll
/// flags). They are retired from the API: GET no longer returns them and PUT neither needs nor applies them. The columns
/// are no longer mapped by the entity (they stay in the database, with defaults, until a later release drops them). An old client that still sends them is ignored, not refused.
/// </summary>
public class TenantHrConfigRetiredSwitchesTests
{
    private static readonly string[] Retired =
    {
        "useDeptHeadApproval", "useHrFinalApproval", "useSupervisorBeforeManager", "allowDottedLineApproval",
        "autoCreateDeptOnImport", "autoCreateDesignationOnImport", "requireImportPreviewBeforeCommit",
        "allowCrossDeptManager", "allowCrossLocationManager", "requireCostCenterForPayroll", "requireGradeForApprovalPolicy",
    };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TenantHrConfigController Controller(ZayraDbContext db, Guid tenantId) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("permission", "organization.write"), new Claim("permission", "organization.establishment.write"),
                    new Claim("is_group_scope", "true"),
                }, "test"))
            }
        }
    };

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Web);

    [Fact]
    public async Task Get_DoesNotExposeTheRetiredSwitches_ButKeepsEstablishmentAndEngagementSettings()
    {
        await using var db = CreateDb();
        var json = Body(await Controller(db, Guid.NewGuid()).Get(CancellationToken.None));

        foreach (var name in Retired) json.TryGetProperty(name, out _).Should().BeFalse($"{name} is retired");
        json.GetProperty("establishmentEnforcementMode").GetString().Should().Be("Enforced");
        json.GetProperty("engagementMinCohortSize").GetInt32().Should().Be(5);
        json.GetProperty("engagementFreeTextRetentionMonths").GetInt32().Should().Be(24);
    }

    [Fact]
    public async Task Put_WithoutTheRetiredFields_Succeeds_AndLeavesEstablishmentAndEngagementUnchanged()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantHrConfigs.Add(new TenantHrConfig
        {
            TenantId = tenantId, EstablishmentEnforcementMode = "Advisory", EngagementMinCohortSize = 8, EngagementFreeTextRetentionMonths = 12,
        });
        await db.SaveChangesAsync();

        var request = JsonSerializer.Deserialize<TenantHrConfigRequest>("{}", Web)!;
        var json = Body(await Controller(db, tenantId).Upsert(request, CancellationToken.None));

        json.GetProperty("establishmentEnforcementMode").GetString().Should().Be("Advisory");
        json.GetProperty("engagementMinCohortSize").GetInt32().Should().Be(8);
        json.GetProperty("engagementFreeTextRetentionMonths").GetInt32().Should().Be(12);
        foreach (var name in Retired) json.TryGetProperty(name, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Put_FromAnOldClientStillSendingTheRetiredFields_IsIgnoredNotRefused_AndChangesNoStoredSwitch()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var old = """
            { "useDeptHeadApproval": false, "useHrFinalApproval": false, "useSupervisorBeforeManager": true, "allowDottedLineApproval": true,
              "autoCreateDeptOnImport": true, "autoCreateDesignationOnImport": true, "requireImportPreviewBeforeCommit": false,
              "allowCrossDeptManager": false, "allowCrossLocationManager": false, "requireCostCenterForPayroll": true,
              "requireGradeForApprovalPolicy": true, "establishmentEnforcementMode": "Advisory",
              "establishmentModeChangeReason": "pilot" }
            """;
        var request = JsonSerializer.Deserialize<TenantHrConfigRequest>(old, Web)!;

        var json = Body(await Controller(db, tenantId).Upsert(request, CancellationToken.None));

        json.GetProperty("establishmentEnforcementMode").GetString().Should().Be("Advisory");
        // Nothing reads or writes the retired columns any more: the entity no longer maps them at all.
        typeof(TenantHrConfig).GetProperties().Select(p => p.Name).Should().NotContain(
            ["UseDeptHeadApproval", "UseHrFinalApproval", "AutoCreateDeptOnImport", "RequireImportPreviewBeforeCommit", "RequireCostCenterForPayroll"]);
        (await db.TenantHrConfigs.AsNoTracking().SingleAsync()).EstablishmentEnforcementMode.Should().Be("Advisory");
    }
}
