using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>PEO-08 — bounded request size on import and a bounded export, each with a clear refusal.</summary>
public sealed class EmployeeImportExportLimitsTests
{
    [Theory]
    [InlineData(nameof(EmployeesController.Import))]
    [InlineData(nameof(EmployeesController.ImportPreview))]
    public void ImportEndpoints_CarryARequestSizeLimit_OfAtMostFiveMegabytes(string action)
    {
        var limit = typeof(EmployeesController).GetMethod(action)!
            .GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: true)
            .Cast<IRequestSizeLimitMetadata>().SingleOrDefault();

        limit.Should().NotBeNull($"{action} reads the whole file into memory");
        limit!.MaxRequestBodySize.Should().BeLessThanOrEqualTo(5L * 1024 * 1024);
    }

    [Fact]
    public void ImportRowCap_Exists()
    {
        EmployeesController.MaxImportRows.Should().BeLessThanOrEqualTo(5_000);
    }

    [Fact]
    public async Task Export_AboveTheRowCap_IsRefusedWithAClearMessage_NotBuilt()
    {
        var tenantId = Guid.NewGuid();
        await using var db = PeopleAuditFixtures.CreateDb();
        var rows = Enumerable.Range(0, EmployeesController.MaxExportRows + 1).Select(i => new Employee
        {
            TenantId = tenantId, EmployeeCode = $"E{i}", FullName = $"Person {i}", Status = "Active",
        });
        db.Employees.AddRange(rows);
        await db.SaveChangesAsync();

        var controller = PeopleAuditFixtures.CreateController(db, tenantId, "HR Manager", ["employees.write", "employees.read"]);
        var result = await controller.Export(CancellationToken.None);

        var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain("export_too_many_rows");
    }

    [Fact]
    public async Task Export_AtTheRowCap_StillWorks()
    {
        var tenantId = Guid.NewGuid();
        await using var db = PeopleAuditFixtures.CreateDb();
        PeopleAuditFixtures.SeedEmployee(db, tenantId, "E1", "One");
        var controller = PeopleAuditFixtures.CreateController(db, tenantId, "HR Manager", ["employees.write", "employees.read"]);

        (await controller.Export(CancellationToken.None)).Should().BeOfType<FileContentResult>();
    }
}
