using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Compliance;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Admin;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Owner decision 2026-10-25 on real PostgreSQL: a below-minimum value persists with its acknowledgement and an
/// audit row, and the "Rules below the legal minimum" list reads it back.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class FloorWarningsPostgresTests(PostgresFixture fixture)
{
    private static ClaimsPrincipal Admin(Guid tenant) => new(new ClaimsIdentity(
    [
        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "Hana HR"), new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "security.manage"), new Claim("is_group_scope", "true"),
    ], "test"));

    private static T With<T>(T c, ClaimsPrincipal u) where T : ControllerBase
    {
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = u } };
        return c;
    }

    [Fact]
    public async Task BelowMinimumAnnualPolicy_IsStoredWithItsAcknowledgement_AndAudited_AndListed()
    {
        var tenant = Guid.NewGuid();
        Guid typeId, companyId;
        await using (var seed = fixture.CreateDb())
        {
            var company = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = $"A-{tenant:N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
            var type = new LeaveType { TenantId = tenant, CompanyId = company.Id, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsPaid = true, IsActive = true };
            seed.AddRange(company, type);
            await seed.SaveChangesAsync();
            (typeId, companyId) = (type.Id, company.Id);
        }

        CreateLeavePolicyRequest Req(IReadOnlyCollection<FloorAcknowledgement>? acks) => new(
            "Annual policy", typeId, "SA", companyId, null, null, null, null, null, null, true, 15, "Monthly", 0, 0, false, 0, 1, 0, 0, true, true, "Full", null, "Active",
            AcknowledgedWarnings: acks);

        Guid policyId;
        await using (var db = fixture.CreateDb())
        {
            var c = With(new LeavePoliciesController(db, new LeaveService(db, new ApprovalRouter(db))), Admin(tenant));
            var refused = (ObjectResult)await c.Create(Req(null), default);
            refused.StatusCode.Should().Be(409);
            (await db.LeavePolicies.IgnoreQueryFilters().AnyAsync(p => p.TenantId == tenant)).Should().BeFalse();

            var created = (CreatedResult)await c.Create(Req([new FloorAcknowledgement("leave_annual_below_minimum", "Seasonal staff agreed 15 days")]), default);
            policyId = ((LeavePolicy)created.Value!).Id;
        }

        await using (var db = fixture.CreateDb())
        {
            var stored = await db.LeavePolicies.IgnoreQueryFilters().SingleAsync(p => p.Id == policyId);
            stored.AnnualEntitlementDays.Should().Be(15m);
            var ack = FloorAckStore.Parse(stored.BelowMinimumAcksJson).Should().ContainSingle().Subject;
            (ack.ConfiguredValue, ack.StatutoryValue, ack.AcknowledgedBy).Should().Be((15m, 21m, "Hana HR"));
            ack.Citation.Should().Contain("Art. 109");

            var audit = await db.LeaveAuditLogs.IgnoreQueryFilters().SingleAsync(a => a.EntityId == policyId.ToString() && a.Action == "BelowStatutoryMinimumAcknowledged");
            (audit.PerformedByName, audit.Reason).Should().Match<(string, string)>(x => x.Item1 == "Hana HR" && x.Item2.Contains("Seasonal staff"));

            var list = With(new FloorComplianceController(db), Admin(tenant));
            var items = JsonSerializer.SerializeToElement(((OkObjectResult)await list.BelowMinimum(default)).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                .GetProperty("items").EnumerateArray().ToList();
            items.Should().ContainSingle(i => i.GetProperty("code").GetString() == "leave_annual_below_minimum" && i.GetProperty("acknowledged").GetBoolean());
        }
    }

    [Fact]
    public async Task BelowArt84EosbRate_IsStoredWithItsAcknowledgement_AndAuditedInTheSameSave()
    {
        var tenant = Guid.NewGuid();
        await using (var db = fixture.CreateDb())
        {
            var c = With(new SetupSettingsController(db, null!), Admin(tenant));
            GCCSettingRequest Req(IReadOnlyCollection<FloorAcknowledgement>? acks) => new("SA", false, null, null, false, true, 10m, 30m, 0, "Sun-Thu", "Fri,Sat", true, 60, false, 60, false, false, 2, acks);
            ((ObjectResult)await c.UpsertGCCSetting(Req(null), default)).StatusCode.Should().Be(409);
            (await c.UpsertGCCSetting(Req([new FloorAcknowledgement("eosb_tier1_below_minimum", "Casual staff policy")]), default)).Should().BeOfType<OkObjectResult>();
        }
        await using (var db = fixture.CreateDb())
        {
            var row = await db.GCCComplianceSettings.IgnoreQueryFilters().SingleAsync(g => g.TenantId == tenant);
            KsaEosbFloorChecks.IsAcknowledged(row.EosbBelowFloorAcksJson, KsaEosbFloorChecks.Tier1, 10m).Should().BeTrue();
            (await db.AdminAuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant && a.Action == "BelowStatutoryMinimumAcknowledged")).Should().Be(1);
        }
    }
}
