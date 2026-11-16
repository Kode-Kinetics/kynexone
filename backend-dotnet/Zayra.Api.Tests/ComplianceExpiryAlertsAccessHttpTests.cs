using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Expiry alerts and the compliance dashboard carry colleagues' visa, passport, work-permit and contract expiry dates, so they are
/// as restricted as the module's own registers: the compliance read permission, and the caller's employee data scope. A plain
/// employee login is refused.
/// </summary>
public sealed class ComplianceExpiryAlertsAccessHttpTests(SchedulingScopeFixture f) : IClassFixture<SchedulingScopeFixture>
{
    [Theory]
    [InlineData("/api/compliance/reports/expiry-alerts")]
    [InlineData("/api/compliance/reports/dashboard")]
    public async Task AnEmployeeLogin_IsRefused(string path)
    {
        using var r = await f.Send(HttpMethod.Get, path, f.Token("Ess"));
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden, await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("ComplianceGroup")]
    [InlineData("Admin")]
    public async Task AComplianceReader_IsAdmitted_AndSeesEveryEmployeeInTheirCompanies(string persona)
    {
        var alerts = await f.Json(HttpMethod.Get, "/api/compliance/reports/expiry-alerts", f.Token(persona));
        alerts.GetProperty("total").GetInt32().Should().Be(3);
        (await f.Json(HttpMethod.Get, "/api/compliance/reports/dashboard", f.Token(persona))).GetProperty("visasExpiring30").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task AOneCompanyComplianceReader_SeesOnlyThatCompanysAlertsAndCounts()
    {
        var alerts = await f.Json(HttpMethod.Get, "/api/compliance/reports/expiry-alerts", f.Token("ComplianceA"));
        alerts.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("employeeName").GetString())
            .Should().BeEquivalentTo(["In scope", "Out of scope"]);
        (await f.Json(HttpMethod.Get, "/api/compliance/reports/dashboard", f.Token("ComplianceA"))).GetProperty("visasExpiring30").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ATeamScopedReader_SeesOnlyTheEmployeesInTheirScope()
    {
        var alerts = await f.Json(HttpMethod.Get, "/api/compliance/reports/expiry-alerts", f.Token("TeamReader"));
        alerts.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("employeeName").GetString())
            .Should().BeEquivalentTo(["In scope"], "only the reader's reporting line, not the whole company or group");
        var dashboard = await f.Json(HttpMethod.Get, "/api/compliance/reports/dashboard", f.Token("TeamReader"));
        dashboard.GetProperty("visasExpiring30").GetInt32().Should().Be(1);
        dashboard.GetProperty("pendingRenewals").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("Ess")]
    [InlineData("HrGroup")]
    [InlineData("ComplianceGroup")]
    [InlineData("Admin")]
    public async Task TheAlertsAndDashboard_AdmitExactlyWhomTheVisaRegisterAdmits(string persona)
    {
        var token = persona == "HrGroup" ? f.HrGroup : f.Token(persona);
        var register = await f.Status(HttpMethod.Get, "/api/compliance/visa-tracking", token);
        (await f.Status(HttpMethod.Get, "/api/compliance/reports/expiry-alerts", token)).Should().Be(register, persona);
        (await f.Status(HttpMethod.Get, "/api/compliance/reports/dashboard", token)).Should().Be(register, persona);
    }
}
