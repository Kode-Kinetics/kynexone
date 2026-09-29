using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;

namespace Zayra.Api.Tests;

/// <summary>
/// Locks the downloadable organization-structure template to the validators: the
/// starter package the controller hands out must round-trip cleanly. We download it,
/// split it back into per-section CSV exactly the way the frontend splitOrgPackage does
/// (by <c># section</c> headers), feed it to Preview(), and assert there are no blocking
/// errors — proving the example rows are mutually consistent and every cross-section
/// reference resolves inside the package. Self-contained: in-memory SQLite, group scope
/// (companies + grades are group-scope-gated).
/// </summary>
public sealed class OrganizationStructureDependencyRegressionTests
{
    // Mirrors the frontend splitOrgPackage regex (AiSetupAssistant.tsx): a section marker
    // line is a lone '#' followed by the section name in letters.
    private static readonly Regex SectionMarker = new(@"^#\s*([A-Za-z]+)\s*$", RegexOptions.Compiled);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParentInsidePackage_ResolvesRegardlessOfFileOrder(bool parentFirst)
    {
        var parent = "OPS,Operations,\n";
        var child = "FIN,Finance,OPS\n";
        var result = await PreviewAsync(r => r with { DepartmentsCsv =
            "Code,NameEn,ParentDepartmentCode\n" + (parentFirst ? parent + child : child + parent) });
        result.HasBlockingErrors.Should().BeFalse(string.Join(" | ", result.Rows.SelectMany(x => x.Errors)));
    }

    [Fact]
    public async Task MissingParent_IsStillBlocked()
    {
        var result = await PreviewAsync(r => r with { DepartmentsCsv =
            "Code,NameEn,ParentDepartmentCode\nOPS,Operations,MISSING\n" });
        result.HasBlockingErrors.Should().BeTrue();
        result.Rows.SelectMany(x => x.Errors).Should().Contain(x => x.Contains("MISSING"));
    }

    [Theory]
    [InlineData("OPS,Operations,OPS\n")]
    [InlineData("OPS,Operations,FIN\nFIN,Finance,OPS\n")]
    public async Task CircularParents_AreBlocked(string rows)
    {
        var result = await PreviewAsync(r => r with { DepartmentsCsv =
            "Code,NameEn,ParentDepartmentCode\n" + rows });
        result.Rows.SelectMany(x => x.Errors).Should().Contain(x => x.Contains("cycle"));
    }

    [Fact]
    public async Task DuplicateDepartment_ReturnsValidation_NotUnhandledException()
    {
        var result = await PreviewAsync(r => r with { DepartmentsCsv =
            "Code,NameEn,ParentDepartmentCode\nOPS,Operations,FIN\nops,Duplicate,FIN\nFIN,Finance,\n" });
        result.HasBlockingErrors.Should().BeTrue();
        result.Rows.SelectMany(x => x.Errors).Should().Contain(x => x.Contains("Duplicate"));
    }

    [Fact]
    public async Task SameComponentAcrossDifferentGrades_IsAccepted()
    {
        var result = await PreviewAsync(r => r with {
            GradesCsv = "Code,Name,MinSalary,MidSalary,MaxSalary,Currency\nG1,Grade 1,8000,10000,12000,SAR\nG2,Grade 2,12000,15000,18000,SAR\n",
            GradePayComponentsCsv = "GradeCode,ComponentCode,ComponentName,CalculationType,Amount\nG1,BASIC,Basic Salary,Fixed,8000\nG2,BASIC,Basic Salary,Fixed,12000\n" });
        result.HasBlockingErrors.Should().BeFalse(string.Join(" | ", result.Rows.SelectMany(x => x.Errors)));
    }

    [Fact]
    public async Task SameComponentTwiceWithinOneGrade_IsBlockedIgnoringCase()
    {
        var result = await PreviewAsync(r => r with { GradePayComponentsCsv =
            "GradeCode,ComponentCode,ComponentName,CalculationType,Amount\nG1,BASIC,Basic Salary,Fixed,8000\ng1,basic,Duplicate,Fixed,8000\n" });
        result.HasBlockingErrors.Should().BeTrue();
        result.Rows.SelectMany(x => x.Errors).Should().Contain(x => x.Contains("Duplicate"));
    }

    private static async Task<OrganizationStructureImportResult> PreviewAsync(
        Func<OrganizationStructureImportRequest, OrganizationStructureImportRequest> change)
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var tenant = await SeedTenantAsync(harness.Db);
        var controller = CreateController(harness.Db, GroupHr(tenant));
        var template = (FileContentResult)controller.Template();
        var request = change(SplitPackage(Encoding.UTF8.GetString(template.FileContents)));
        var response = await controller.Preview(request, CancellationToken.None);
        return ((OkObjectResult)response.Result!).Value.Should().BeOfType<OrganizationStructureImportResult>().Subject;
    }

    /// <summary>
    /// Re-implements the frontend splitOrgPackage: walk the package line by line, switch the
    /// active section on a '# name' marker, and accumulate every following line into that
    /// section's buffer. Lowercased section name maps to the matching CSV property.
    /// </summary>
    private static OrganizationStructureImportRequest SplitPackage(string package)
    {
        var buffers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var line in package.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var match = SectionMarker.Match(line.Trim());
            if (match.Success)
            {
                current = match.Groups[1].Value.ToLowerInvariant();
                if (!buffers.ContainsKey(current)) buffers[current] = new List<string>();
                continue;
            }
            if (current is not null) buffers[current].Add(line);
        }

        string? Csv(string section) =>
            buffers.TryGetValue(section, out var lines) && string.Join("\n", lines).Trim() is { Length: > 0 } csv
                ? csv
                : null;

        return new OrganizationStructureImportRequest(
            CompaniesCsv: Csv("companies"),
            BranchesCsv: Csv("branches"),
            CostCentersCsv: Csv("costcenters"),
            DepartmentsCsv: Csv("departments"),
            GradesCsv: Csv("grades"),
            GradePayComponentsCsv: Csv("gradepaycomponents"),
            DesignationsCsv: Csv("designations"),
            PositionsCsv: Csv("positions"));
    }

    private static OrganizationStructureImportController CreateController(ZayraDbContext db, ClaimsPrincipal principal)
    {
        var controller = new OrganizationStructureImportController(db, new AuditService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static ClaimsPrincipal GroupHr(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Template Tenant", Slug = $"template-{tenantId:N}" });
        await db.SaveChangesAsync();
        return tenantId;
    }

    private sealed class SqliteHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ZayraDbContext Db { get; }

        private SqliteHarness(SqliteConnection connection, ZayraDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<SqliteHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ZayraDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ZayraDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new SqliteHarness(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
