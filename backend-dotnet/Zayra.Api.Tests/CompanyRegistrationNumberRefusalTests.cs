using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A duplicate registration number is refused with a field-addressed payload
/// ({ error, field, message }) so the company form can show it under the Registration Number input.
/// </summary>
public class CompanyRegistrationNumberRefusalTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DefaultHttpContext GroupAdmin(Guid tenantId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"),
            new("is_group_scope", "true"),
        }, "test")),
    };

    private static CompanyRequest Request(string registrationNumber) => new(
        LegalNameEn: "Second Co", LegalNameAr: null, TradeName: null, CountryCode: "SA", Jurisdiction: null,
        RegistrationNumber: registrationNumber, TaxNumber: null, WpsEmployerId: null, GosiEmployerId: null,
        QiwaEstablishmentId: null, DefaultCurrency: "SAR");

    [Fact]
    public async Task DuplicateRegistrationNumber_IsRefused_OnTheRegistrationNumberField()
    {
        await using var db = Db();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "Group", Slug = $"group-{tenantId:N}", AccountType = Zayra.Api.Domain.Entities.TenantAccountTypes.Group });
        db.Companies.Add(new Company { TenantId = tenantId, LegalNameEn = "First Co", RegistrationNumber = "CR-100", IsActive = true });
        await db.SaveChangesAsync();
        var controller = new OrganizationController(new OrganizationSetupService(db, new AuditService(db)), db)
        { ControllerContext = new ControllerContext { HttpContext = GroupAdmin(tenantId) } };

        var result = (await controller.CreateCompany(Request("CR-100"), CancellationToken.None)).Result;

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("\"error\":\"registration_number_taken\"")
            .And.Contain("\"field\":\"registrationNumber\"")
            .And.Contain("already exists in this tenant");
        (await db.Companies.CountAsync(c => c.TenantId == tenantId)).Should().Be(1);
    }

    [Fact]
    public void DuplicateException_KeepsItsMessage_ForImportersThatCatchInvalidOperation()
    {
        InvalidOperationException ex = new CompanyRegistrationNumberTakenException();
        ex.Message.Should().Be("Company registration number already exists in this tenant.");
    }
}
