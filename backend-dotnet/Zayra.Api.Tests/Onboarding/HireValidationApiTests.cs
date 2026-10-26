using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// HTTP-level pins for the hire input contract (400/409/422 problem shapes, never a 500). They need the full
/// pipeline (WebApplicationFactory) and the guard wired into the create service, so they run on the train after
/// WP-B's hooks land; they are not runnable in the sandbox.
/// </summary>
public class HireValidationApiTests : IClassFixture<EmployeeDraftCreateHttpFixture>
{
    private readonly EmployeeDraftCreateHttpFixture _fx;
    public HireValidationApiTests(EmployeeDraftCreateHttpFixture fixture) => _fx = fixture;

    private static Dictionary<string, object?> Body(Dictionary<string, object?>? extra = null)
    {
        var d = new Dictionary<string, object?> { ["englishName"] = "Api Hire " + Guid.NewGuid().ToString("N")[..6] };
        if (extra is not null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> Post(Dictionary<string, object?> body)
    {
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", body);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static IEnumerable<object[]> OverlongFields() =>
    [
        ["englishName", 151], ["arabicName", 181], ["preferredName", 121], ["gender", 41], ["nationality", 81], ["maritalStatus", 61],
        ["mobileNumber", 61], ["profilePhotoUrl", 501], ["jobTitle", 181], ["employmentType", 81], ["contractType", 81],
        ["workLocation", 121], ["payrollGroup", 81], ["shiftPolicyCode", 81], ["leavePolicyCode", 81], ["attendancePolicyCode", 81],
        ["personalEmail", 260], ["workEmail", 260], ["employeeCode", 51], ["nationalityCode", 3],
        ["payrollProfile.iban", 35], ["payrollProfile.bankName", 121], ["payrollProfile.accountNumber", 35], ["payrollProfile.molId", 41],
        ["salaryBreakdown.salaryStructureCode", 61], ["complianceRecords.fieldValue", 201],
    ];

    [Theory]
    [MemberData(nameof(OverlongFields))]
    public async Task Create_Overlong_AnyField_Returns400_NeverStatus500(string field, int length)
    {
        var value = field.Contains("mail", StringComparison.OrdinalIgnoreCase)
            ? new string('a', length - 8) + "@ex.com" : new string('x', length);
        Dictionary<string, object?> body = field switch
        {
            var f when f.StartsWith("payrollProfile.") => Body(new() { ["payrollProfile"] = new Dictionary<string, object?> { [f["payrollProfile.".Length..]] = value } }),
            var f when f.StartsWith("salaryBreakdown.") => Body(new() { ["salaryBreakdown"] = new Dictionary<string, object?> { [f["salaryBreakdown.".Length..]] = value } }),
            "complianceRecords.fieldValue" => Body(new() { ["complianceRecords"] = new[] { new { countryCode = "SA", fieldKey = "passport_number", fieldLabel = "Passport", fieldValue = value, isSensitive = false, isRequired = false } } }),
            "employeeCode" => Body(new() { ["employeeCode"] = value, ["manualEmployeeCode"] = true }),
            _ => Body(new() { [field] = value }),
        };
        var (status, json) = await Post(body);
        ((int)status).Should().BeLessThan(500, $"{field} of {length} characters must be refused, not crash");
        status.Should().Be(HttpStatusCode.BadRequest, json.ToString());
    }

    [Fact]
    public async Task Create_ForeignTenantCompany_Returns422()
    {
        Guid foreign;
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var other = new Tenant { Name = "Other", Slug = "other-" + Guid.NewGuid().ToString("N"), IsActive = true };
            db.Tenants.Add(other);
            var c = new Company { TenantId = other.Id, LegalNameEn = "Foreign Co", TradeName = "Foreign", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
            db.Companies.Add(c); await db.SaveChangesAsync(); foreign = c.Id;
        }
        var before = await _fx.EmployeeCountAsync();
        var (status, json) = await Post(Body(new() { ["companyId"] = foreign }));
        status.Should().Be(HttpStatusCode.UnprocessableEntity, json.ToString());
        json.GetProperty("code").GetString().Should().Be("invalid_company");
        (await _fx.EmployeeCountAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData("GB82WEST12345698765432")]       // G07: non-SA IBAN for a Saudi company
    [InlineData("SA03800000006080101675")]       // G07: 22 characters
    [InlineData("SA0480000000608010167519")]     // G07: bad mod-97
    public async Task Create_InvalidSaIban_ForSaCompany_Returns400(string iban)
    {
        var (status, json) = await Post(Body(new() { ["companyId"] = _fx.CompanyId, ["payrollProfile"] = new { iban } }));
        status.Should().Be(HttpStatusCode.BadRequest, json.ToString());
        json.GetProperty("errors").GetProperty("payrollProfile.iban")[0].GetString().Should().Be("iban_invalid");
    }

    [Fact]
    public async Task Create_BadSaudiIdChecksum_Returns400_SameNumberAccepted_WhenValid()
    {
        object Records(string id) => new[] { new { countryCode = "SA", fieldKey = "id_number", fieldLabel = "National ID", fieldValue = id, isSensitive = true, isRequired = true } };
        var bad = await Post(Body(new() { ["companyId"] = _fx.CompanyId, ["nationality"] = "Saudi", ["complianceRecords"] = Records("1000000009") }));
        bad.Status.Should().Be(HttpStatusCode.BadRequest, bad.Json.ToString());
        var ok = await Post(Body(new() { ["companyId"] = _fx.CompanyId, ["nationality"] = "Saudi", ["complianceRecords"] = Records(TestIds.ValidNationalId(41)) }));
        ok.Status.Should().Be(HttpStatusCode.Created, ok.Json.ToString());
    }

    [Fact]
    public async Task Create_Probation200d_409Warning_StoredUnclamped_AfterAck()   // G33
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = start.AddDays(200);
        Dictionary<string, object?> Probation(object? ack = null) => Body(new()
        {
            ["companyId"] = _fx.CompanyId, ["englishName"] = "Probation Long", ["probationStartDate"] = start.ToString("yyyy-MM-dd"),
            ["probationEndDate"] = end.ToString("yyyy-MM-dd"), ["acknowledgedWarnings"] = ack,
        });
        var first = await Post(Probation());
        first.Status.Should().Be(HttpStatusCode.Conflict, first.Json.ToString());
        first.Json.GetProperty("code").GetString().Should().Be("warnings_require_acknowledgement");
        first.Json.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).Should().Contain("probation_over_limit");

        var second = await Post(Probation(new[] { new { code = "probation_over_limit", reason = "Executive hire, 200 day probation agreed in contract" } }));
        second.Status.Should().Be(HttpStatusCode.Created, second.Json.ToString());
        var id = second.Json.GetProperty("id").GetInt32();
        using var scope = _fx.Host.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
        saved.ProbationEndDate.Should().Be(end, "the stored value is never clamped");
    }

    [Fact]
    public async Task Create_SalaryOutsideGradeBand_409Warning_thenReplayWithAcknowledgementSaves()   // G08
    {
        Guid gradeId;
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var g = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "G-BAND", Name = "Band 5", MinSalary = 5000, MaxSalary = 9000, IsActive = true };
            db.Grades.Add(g); await db.SaveChangesAsync(); gradeId = g.Id;
        }
        Dictionary<string, object?> Req(object? ack = null) => Body(new()
        {
            ["companyId"] = _fx.CompanyId, ["gradeId"] = gradeId, ["englishName"] = "Band Breaker",
            ["salaryBreakdown"] = new { basicSalary = 20000, housingAllowance = 0, transportAllowance = 0 }, ["acknowledgedWarnings"] = ack,
        });
        var first = await Post(Req());
        first.Status.Should().Be(HttpStatusCode.Conflict, first.Json.ToString());
        first.Json.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).Should().Contain("grade_band_out_of_range");
        var second = await Post(Req(new[] { new { code = "grade_band_out_of_range", reason = "Approved exception by the CFO, see ticket" } }));
        second.Status.Should().Be(HttpStatusCode.Created, second.Json.ToString());
    }

    [Fact]
    public async Task Create_DesignationGradeMismatch_422()   // G02
    {
        Guid desig, otherGrade;
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var g1 = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "G-A", Name = "A", IsActive = true };
            var g2 = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "G-B", Name = "B", IsActive = true };
            var d = new Designation { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "D-A", TitleEn = "Analyst", GradeId = g1.Id, IsActive = true };
            db.AddRange(g1, g2, d); await db.SaveChangesAsync(); desig = d.Id; otherGrade = g2.Id;
        }
        var (status, json) = await Post(Body(new() { ["companyId"] = _fx.CompanyId, ["designationId"] = desig, ["gradeId"] = otherGrade }));
        status.Should().Be(HttpStatusCode.UnprocessableEntity, json.ToString());
        json.GetProperty("field").GetString().Should().Be("gradeId");
    }

    public static IEnumerable<object[]> DraftStringProperties() =>
        typeof(EmployeeDraftRequest).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(DraftStringProperties))]
    public async Task DraftSubmit_OverlongAnyStringProperty_400(string property)   // G17
    {
        var camel = char.ToLowerInvariant(property[0]) + property[1..];
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees/drafts",
            new Dictionary<string, object?> { ["englishName"] = "Draft Long", [camel] = new string('z', 1000) });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Activate_BlankNationality_Refused_DraftSaved()   // G24
    {
        var (status, json) = await Post(Body(new() { ["companyId"] = _fx.CompanyId, ["englishName"] = "No Nationality" }));
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        var id = json.GetProperty("id").GetInt32();
        using var activate = await _fx.SendAsync(HttpMethod.Patch, $"/api/employees/{id}/status",
            new { status = "Active", effectiveDate = "2026-01-01", reason = "Nationality is required" });
        activate.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await activate.Content.ReadAsStringAsync());
        using var readiness = await _fx.SendAsync(HttpMethod.Get, $"/api/employees/{id}/readiness");
        (await readiness.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("blocking").EnumerateArray()
            .Select(i => i.GetProperty("key").GetString()).Should().Contain("Nationality");
    }

    [Fact]
    public async Task FieldCatalog_ForeignCompany_Denied()   // N4
    {
        Guid foreign;
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var other = new Tenant { Name = "Other2", Slug = "other2-" + Guid.NewGuid().ToString("N"), IsActive = true };
            db.Tenants.Add(other);
            var c = new Company { TenantId = other.Id, LegalNameEn = "Foreign Co 2", TradeName = "Foreign2", CountryCode = "AE", IsActive = true, DefaultCurrency = "AED" };
            db.Companies.Add(c); await db.SaveChangesAsync(); foreign = c.Id;
        }
        using var r = await _fx.SendAsync(HttpMethod.Get, $"/api/employees/field-catalog?companyId={foreign}");
        r.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NationalityCatalog_IsServed()
    {
        using var r = await _fx.SendAsync(HttpMethod.Get, "/api/reference/nationalities");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().Should().BeGreaterThan(240);
    }
}
