using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>Final batch CD46 through the real pipeline: a draft resumed and autosaved by a caller who cannot see pay or ID
/// values must never blank them.</summary>
public class HireFinalBatchHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private const string Iban = "SA0380000000608010167519";
    private readonly HireIntegrityHttpFixture _fx;
    public HireFinalBatchHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    [Fact]
    public async Task DraftAutosave_ByACallerWithoutSensitive_LeavesStoredPayAndIdentityUnchanged()
    {
        int id;
        using (var s = _fx.Host.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var e = new Employee { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, EmployeeCode = $"FB-{Guid.NewGuid():N}"[..14], FullName = "Resumed Person",
                EnglishName = "Resumed Person", Status = "Draft", JoiningDate = DateTime.UtcNow.Date, Salary = 7000m, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5) };
            db.Employees.Add(e); await db.SaveChangesAsync();
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = _fx.TenantId, EmployeeId = e.Id, Iban = Iban, BankName = "Test Bank", SalaryCurrency = "SAR" });
            db.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord { TenantId = _fx.TenantId, EmployeeId = e.Id, CountryCode = "SA", FieldKey = "iqama_number",
                FieldLabel = "Iqama", FieldValue = "2000000006", IsSensitive = true });
            await db.SaveChangesAsync();
            id = e.Id;
        }
        var token = await _fx.NewUserAsync("cd46c", "employees.write", "employees.read");   // no employees.sensitive
        DateTime basis;
        using (var s = _fx.Host.Services.CreateScope())
        {
            var e = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
            basis = e.UpdatedAtUtc ?? e.CreatedAtUtc;
        }
        // What the resumed form holds: the pay and ID fields are empty because the GET masked them.
        using var r = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new
        {
            baseUpdatedAtUtc = basis,
            request = new Dictionary<string, object?>
            {
                ["englishName"] = "Resumed Person Renamed",
                ["companyId"] = _fx.CompanyId,
                ["payrollProfile"] = new { iban = "", bankName = "", paymentMethod = "" },
                ["complianceRecords"] = new[] { new { countryCode = "SA", fieldKey = "iqama_number", fieldLabel = "Iqama", fieldValue = "", isSensitive = true, isRequired = false } },
            },
        }, token);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

        using var check = _fx.Host.Services.CreateScope();
        var cdb = check.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await cdb.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id)).EnglishName.Should().Be("Resumed Person Renamed");
        (await cdb.EmployeePayrollProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.EmployeeId == id)).Iban.Should().Be(Iban);
        (await cdb.EmployeeComplianceRecords.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.EmployeeId == id)).FieldValue.Should().Be("2000000006");
        (await cdb.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id)).Salary.Should().Be(7000m);
    }
}
