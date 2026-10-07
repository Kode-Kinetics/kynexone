using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Opt-in measurement (not part of the normal suite): one 5,000-row attendanceDaily section through the migration
/// commit, timed. Used to check that the per-row failure bookkeeping is not quadratic in the tracked entities.
/// <code>KYNEX_MIGRATION_TIMING_PROBE=1 dotnet test --filter MigrationImportSectionTimingProbe</code>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class MigrationImportSectionTimingProbe
{
    private readonly PostgresFixture _fx;
    public MigrationImportSectionTimingProbe(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task FiveThousandAttendanceRows_Timed()
    {
        if (Environment.GetEnvironmentVariable("KYNEX_MIGRATION_TIMING_PROBE") != "1") return;
        int employees = 50, days = int.TryParse(Environment.GetEnvironmentVariable("KYNEX_MIGRATION_TIMING_DAYS"), out var dd) ? dd : 100;
        Guid tenant;
        await using (var seed = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company { TenantId = tenant, LegalNameEn = "Timing Co", CountryCode = "SA", Jurisdiction = "SA", RegistrationNumber = $"T-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true };
            seed.Companies.Add(company);
            for (var e = 1; e <= employees; e++)
                seed.Employees.Add(new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = $"TM{e:D3}", FullName = $"Timing {e}", Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            await seed.SaveChangesAsync();
        }
        var csv = new StringBuilder("EmployeeCode,WorkDate,FirstInUtc,LastOutUtc,TotalWorkedMinutes,BreakMinutes,LateMinutes,EarlyExitMinutes,OvertimeMinutes,UndertimeMinutes,MissingPunch,Status,WorkMode\n");
        var start = new DateOnly(2025, 1, 1);
        for (var e = 1; e <= employees; e++)
            for (var d = 0; d < days; d++)
            {
                var day = start.AddDays(d).ToString("yyyy-MM-dd");
                csv.Append($"TM{e:D3},{day},{day}T08:00:00Z,{day}T17:00:00Z,480,60,0,0,0,0,false,Present,Work from site\n");
            }

        await using var db = _fx.CreateDb();
        var controller = new MigrationImportController(db, new Pbkdf2PasswordHasher(1_000), new AuditService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin"),
                new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
            }, "test")) } },
        };
        var sw = Stopwatch.StartNew();
        var result = await controller.Commit(new MigrationPackageRequest($"timing-{Guid.NewGuid():N}",
            new Dictionary<string, string> { ["attendanceDaily"] = csv.ToString() }), CancellationToken.None);
        sw.Stop();
        var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var line = $"attendanceDaily rows={employees * days} created={dto.CreatedRows} errors={dto.ErrorRows} seconds={sw.Elapsed.TotalSeconds:F1}";
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("KYNEX_MIGRATION_TIMING_OUT") is { Length: > 0 } outFile)
            await File.AppendAllTextAsync(outFile, line + Environment.NewLine);
    }
}
