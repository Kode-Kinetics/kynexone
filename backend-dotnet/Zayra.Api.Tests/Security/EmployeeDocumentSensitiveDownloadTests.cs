using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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

/// <summary>PEO-06 — identity scans (passport, iqama) are raw bytes of protected facts: employees.sensitive only.</summary>
public sealed class EmployeeDocumentSensitiveDownloadTests
{
    [Theory]
    [InlineData("Passport", true)]
    [InlineData("Iqama", true)]
    [InlineData("Medical report", false)]
    public async Task DownloadDocument_PassportScan_RequiresSensitivePermission(string documentType, bool protectedType)
    {
        var tenantId = Guid.NewGuid();
        await using var db = PeopleAuditFixtures.CreateDb();
        var employee = PeopleAuditFixtures.SeedEmployee(db, tenantId, "E1", "Subject");
        var document = new EmployeeDocument
        {
            TenantId = tenantId, EmployeeId = employee.Id, DocumentType = documentType, DocumentCategory = "General",
            FileName = "scan.pdf", ContentType = "application/pdf", StorageUrl = $"{tenantId:N}/scan.pdf",
        };
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync();

        var withoutSensitive = PeopleAuditFixtures.CreateController(db, tenantId, "HR Officer", ["employees.read", "employees.documents"]);
        var refused = await withoutSensitive.DownloadDocument(document.Id, CancellationToken.None);
        if (protectedType) refused.Should().BeOfType<ForbidResult>();
        else refused.Should().NotBeOfType<ForbidResult>();

        var withSensitive = PeopleAuditFixtures.CreateController(db, tenantId, "HR Manager", ["employees.read", "employees.documents", "employees.sensitive"]);
        (await withSensitive.DownloadDocument(document.Id, CancellationToken.None)).Should().NotBeOfType<ForbidResult>();
    }
}
