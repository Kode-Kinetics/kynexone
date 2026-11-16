using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// The seeded HR Manager reads the compliance registers, the expiry alerts and the compliance dashboard through the REAL pipeline
/// (real JWT built from the seeded role, real role and permission gates, real company filters). A one-company HR Manager sees
/// their own company's records only; a group HR Manager sees every company; HR Officer is still refused; and the read key opens
/// no write.
/// </summary>
public sealed class HrManagerComplianceReadHttpTests(SchedulingScopeFixture f) : IClassFixture<SchedulingScopeFixture>
{
    private static string[] Names(JsonElement array, string property) =>
        array.EnumerateArray().Select(a => a.GetProperty(property).GetString()!).ToArray();

    [Fact]
    public async Task AOneCompanyHrManager_ReadsAlertsAndDashboard_ForTheirOwnCompanyOnly()
    {
        var alerts = await f.Json(HttpMethod.Get, "/api/compliance/reports/expiry-alerts", f.HrA);
        Names(alerts.GetProperty("alerts"), "employeeName").Should().BeEquivalentTo(["In scope", "Out of scope"], "never the other company's employee");
        (await f.Json(HttpMethod.Get, "/api/compliance/reports/dashboard", f.HrA)).GetProperty("visasExpiring30").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task AOneCompanyHrManager_ReadsTheVisaRegister_ForTheirOwnCompanyOnly()
    {
        var register = await f.Json(HttpMethod.Get, "/api/compliance/visa-tracking", f.HrA);
        Names(register.GetProperty("items"), "employeeName").Should().BeEquivalentTo(["In scope", "Out of scope"]);
    }

    [Theory]
    [InlineData("/api/compliance/visa-tracking")]
    [InlineData("/api/compliance/passports")]
    [InlineData("/api/compliance/work-permits")]
    [InlineData("/api/compliance/renewals")]
    [InlineData("/api/compliance/contracts")]
    [InlineData("/api/compliance/reports/dashboard")]
    [InlineData("/api/compliance/reports/expiry-alerts")]
    public async Task AnHrManager_IsAdmittedToEachComplianceRead(string path)
    {
        (await f.Status(HttpMethod.Get, path, f.HrA)).Should().Be(HttpStatusCode.OK, path);
        (await f.Status(HttpMethod.Get, path, f.HrGroup)).Should().Be(HttpStatusCode.OK, path);
    }

    [Fact]
    public async Task AGroupHrManager_SeesEveryCompanysAlerts()
    {
        (await f.Json(HttpMethod.Get, "/api/compliance/reports/expiry-alerts", f.HrGroup)).GetProperty("total").GetInt32().Should().Be(3);
    }

    [Theory]
    [InlineData("/api/compliance/visa-tracking")]
    [InlineData("/api/compliance/passports")]
    [InlineData("/api/compliance/work-permits")]
    [InlineData("/api/compliance/renewals")]
    [InlineData("/api/compliance/contracts")]
    [InlineData("/api/compliance/reports/dashboard")]
    [InlineData("/api/compliance/reports/expiry-alerts")]
    public async Task AnHrOfficer_IsStillRefused(string path)
    {
        (await f.Status(HttpMethod.Get, path, f.Token("HrOfficerA"))).Should().Be(HttpStatusCode.Forbidden, path);
    }

    [Fact]
    public async Task TheReadKey_OpensNoWrite()
    {
        (await f.Status(HttpMethod.Post, "/api/compliance/visa-tracking", f.HrA, new { })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, "/api/compliance/passports", f.HrA, new { })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, "/api/compliance/renewals", f.HrA, new { })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, "/api/compliance/contracts", f.HrA, new { })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Patch, $"/api/compliance/renewals/{f.RenewalA}/status", f.HrA, new { status = "Completed" })).Should().Be(HttpStatusCode.Forbidden);
    }
}
