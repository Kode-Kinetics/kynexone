using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A3 item 5 (contract 1.1 and 2.9): the structure-import PREVIEW applies the cost-centre code format to a new code, so the
/// error is on the row before anyone presses commit (it used to surface only when the commit applied the row, after the preview had said OK).
/// </summary>
public class CostCentreStructureImportPreviewTests
{
    private const string Header = "CompanyLegalName,Code,Name,IsActive\n";

    private static OrganizationStructureImportController Controller(ZayraDbContext db, Guid tenant) =>
        new(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("sub", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("permission", "organization.costcenter.manage"),
                        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                    }, "Test"))
                }
            }
        };

    private static async Task<(ZayraDbContext Db, Guid Tenant)> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Alpha Trading", RegistrationNumber = "A-1", IsActive = true };
        db.AddRange(company, new CostCenter { TenantId = tenant, CompanyId = company.Id, Code = "CC1", Name = "Ops" });
        await db.SaveChangesAsync();
        return (db, tenant);
    }

    private static async Task<OrganizationStructureImportResult> Preview(ZayraDbContext db, Guid tenant, string rows)
    {
        var result = await Controller(db, tenant).Preview(new OrganizationStructureImportRequest(null, null, Header + rows, null, null, null, null), default);
        return (OrganizationStructureImportResult)((OkObjectResult)result.Result!).Value!;
    }

    [Theory]
    [InlineData("bad code!")]
    [InlineData("-LEADING")]
    [InlineData("THIS-CODE-IS-FAR-TOO-LONG-FOR-A-COST-CENTRE")]
    public async Task T008_ANewCodeOutsideTheFormat_IsAnErrorOnItsRow_AtPreviewTime(string code)
    {
        var (db, tenant) = await Seed();
        var preview = await Preview(db, tenant, $"Alpha Trading,{code},Bad,true\nAlpha Trading,GOOD-1,Good,true\n");

        preview.HasBlockingErrors.Should().BeTrue();
        var bad = preview.Rows.Single(r => r.RowNumber == 2);
        bad.Status.Should().Be(ImportRowStatus.Error);
        bad.Errors.Should().Contain(e => e.Contains("Cost center code") && e.Contains("starts with a letter or digit"));
        preview.Rows.Single(r => r.RowNumber == 3).Status.Should().Be(ImportRowStatus.Ok, "a valid code is untouched");
    }

    [Fact]
    public async Task T008_AnExistingCode_IsNotRevalidatedByThePreview_ItIsAnUpdate()
    {
        var (db, tenant) = await Seed();
        var preview = await Preview(db, tenant, "Alpha Trading,CC1,Operations,true\n");
        preview.HasBlockingErrors.Should().BeFalse();
        preview.Rows.Single().Warnings.Should().Contain(w => w.Contains("already exists"));
    }

    [Fact]
    public async Task T008_ThePreviewVerdict_MatchesTheCommit_ForABadCode()
    {
        var (db, tenant) = await Seed();
        var controller = Controller(db, tenant);
        var request = new OrganizationStructureImportRequest(null, null, Header + "Alpha Trading,bad code!,Bad,true\n", null, null, null, null);
        var preview = (OrganizationStructureImportResult)((OkObjectResult)(await controller.Preview(request, default)).Result!).Value!;
        preview.HasBlockingErrors.Should().BeTrue();
        (await controller.Commit(request, default)).Result.Should().NotBeOfType<OkObjectResult>();
        (await db.CostCenters.CountAsync(c => c.Code == "BAD CODE!")).Should().Be(0);
    }
}
