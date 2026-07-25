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
public sealed class OrganizationStructureTemplateRoundTripTests
{
    // Mirrors the frontend splitOrgPackage regex (AiSetupAssistant.tsx): a section marker
    // line is a lone '#' followed by the section name in letters.
    private static readonly Regex SectionMarker = new(@"^#\s*([A-Za-z]+)\s*$", RegexOptions.Compiled);

    [Fact]
    public async Task Template_RoundTrips_ThroughPreview_WithNoBlockingErrors()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var tenantId = await SeedTenantAsync(harness.Db);
        // Group scope: companies and grades in the template can only be imported by a
        // group-level HR/Admin caller (AddScopeRows gates them for company-scoped users).
        var controller = CreateController(harness.Db, GroupHr(tenantId));

        // 1. Download the starter template package.
        var template = controller.Template();
        var file = template.Should().BeOfType<FileContentResult>().Subject;
        var package = Encoding.UTF8.GetString(file.FileContents);

        // 2. Split it back into a request the same way the frontend does.
        var request = SplitPackage(package);

        // 3. Validate the round-tripped package.
        var preview = await controller.Preview(request, CancellationToken.None);
        var ok = preview.Result.Should().BeOfType<OkObjectResult>().Subject;
        var result = ok.Value.Should().BeOfType<OrganizationStructureImportResult>().Subject;

        // 4. The template is self-consistent and must process cleanly.
        result.HasBlockingErrors.Should().BeFalse(
            "the downloadable template must re-upload with no blocking errors; " +
            string.Join(" | ", result.Rows.SelectMany(r => r.Errors)));
        result.Errors.Should().Be(0);
        result.Warnings.Should().Be(0, "a pristine tenant + consistent example rows should raise no warnings");

        // 5. Every one of the eight sections parsed at least one example row (drift guard:
        //    a dropped section header or an empty sample would silently shrink the package).
        result.Received.Should().Be(8);
        request.CompaniesCsv.Should().NotBeNullOrWhiteSpace();
        request.BranchesCsv.Should().NotBeNullOrWhiteSpace();
        request.CostCentersCsv.Should().NotBeNullOrWhiteSpace();
        request.DepartmentsCsv.Should().NotBeNullOrWhiteSpace();
        request.GradesCsv.Should().NotBeNullOrWhiteSpace();
        request.GradePayComponentsCsv.Should().NotBeNullOrWhiteSpace();
        request.DesignationsCsv.Should().NotBeNullOrWhiteSpace();
        request.PositionsCsv.Should().NotBeNullOrWhiteSpace();
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
