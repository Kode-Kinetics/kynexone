using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>The read-only report of existing Retired / Deceased people: whether each still has an open login, a WPS footprint or a separation record. HR Manager and Admin only; it changes nothing.</summary>
public sealed class EmployeeSeparatedReportHttpTests(EmployeeDirectoryHttpFixture f) : IClassFixture<EmployeeDirectoryHttpFixture>
{
    private static string NewTag() => "R" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod method, string url, string token, object? body = null)
    {
        using var r = await f.Send(method, url, token, body);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    // ═════════════ 7. Read-only report of existing Retired / Deceased people ═════════════

    private async Task<(int WithLogin, int WithWps, int WithSeparation, int Plain, int OtherCompany, string Tag)> SeedSeparatedPeopleAsync()
    {
        var tag = NewTag();
        var login = (await f.SeedAsync(tag, 1, status: "Retired", suffix: "LOGIN")).Single();
        var wps = (await f.SeedAsync(tag, 1, status: "Deceased", suffix: "WPS")).Single();
        var sep = (await f.SeedAsync(tag, 1, status: "retired", suffix: "SEP")).Single();   // a legacy lower-case row
        var plain = (await f.SeedAsync(tag, 1, status: "Retired", suffix: "PLAIN")).Single();
        var other = (await f.SeedAsync(tag, 1, status: "Retired", suffix: "FAR", companyId: f.OtherCompanyId)).Single();
        await f.SeedAsync(tag, 1, status: "Active", suffix: "ACTIVE");
        await f.WithDbAsync(async db =>
        {
            var user = new User
            {
                TenantId = f.TenantId, Email = $"{tag}@report.test", NormalizedEmail = $"{tag}@REPORT.TEST", FullName = "Retired Login", PasswordHash = "x",
                Status = "Active", AccessMode = AccessModes.EssOnly, IsActive = true, IsEmailConfirmed = true,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = f.TenantId, EmployeeId = login.Id, UserId = user.Id, AccessMode = AccessModes.EssOnly, Status = "Active" });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = f.TenantId, EmployeeId = wps.Id, WpsEligible = true });
            db.EmployeeOffboardings.Add(new EmployeeOffboarding
            {
                TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = sep.Id, EmployeeName = sep.FullName, EmployeeCode = sep.EmployeeCode,
                SeparationType = "Retirement", Status = "InProgress", LastWorkingDay = new DateOnly(2026, 10, 1), NoticeDate = new DateOnly(2026, 10, 1),
            });
            await db.SaveChangesAsync();
            return 0;
        });
        return (login.Id, wps.Id, sep.Id, plain.Id, other.Id, tag);
    }

    [Fact]
    public async Task SeparatedReport_ListsRetiredAndDeceased_WithLoginWpsAndSeparationFlags_CaseInsensitively()
    {
        var (withLogin, withWps, withSep, plain, other, tag) = await SeedSeparatedPeopleAsync();

        var (status, body) = await Call(HttpMethod.Get, "/api/employees/separated-status-report?pageSize=100", f.Token("HR Manager"));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var mine = body.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("employeeCode").GetString()!.StartsWith(tag)).ToList();
        mine.Select(i => i.GetProperty("employeeId").GetInt32()).Should().BeEquivalentTo(new[] { withLogin, withWps, withSep, plain, other }, "Active people are not listed; a lower-case 'retired' is");
        JsonElement Row(int id) => mine.Single(i => i.GetProperty("employeeId").GetInt32() == id);
        Row(withLogin).GetProperty("hasOpenLogin").GetBoolean().Should().BeTrue();
        Row(withLogin).GetProperty("hasWpsFootprint").GetBoolean().Should().BeFalse();
        Row(withLogin).GetProperty("hasSeparationRecord").GetBoolean().Should().BeFalse();
        Row(withWps).GetProperty("hasWpsFootprint").GetBoolean().Should().BeTrue();
        Row(withWps).GetProperty("status").GetString().Should().Be("Deceased");
        Row(withSep).GetProperty("hasSeparationRecord").GetBoolean().Should().BeTrue();
        Row(withSep).GetProperty("separationType").GetString().Should().Be("Retirement");
        Row(plain).GetProperty("hasOpenLogin").GetBoolean().Should().BeFalse();
        Row(plain).GetProperty("hasWpsFootprint").GetBoolean().Should().BeFalse();
        Row(plain).GetProperty("hasSeparationRecord").GetBoolean().Should().BeFalse();
        body.GetProperty("summary").GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task SeparatedReport_ChangesNothing()
    {
        var (withLogin, _, _, _, _, tag) = await SeedSeparatedPeopleAsync();
        var before = await f.WithDbAsync(async db => (await db.AuditLogs.IgnoreQueryFilters().CountAsync(), await db.Users.IgnoreQueryFilters().CountAsync(u => u.IsActive)));

        (await Call(HttpMethod.Get, "/api/employees/separated-status-report", f.Token("Admin"))).Status.Should().Be(HttpStatusCode.OK);

        var after = await f.WithDbAsync(async db => (await db.AuditLogs.IgnoreQueryFilters().CountAsync(), await db.Users.IgnoreQueryFilters().CountAsync(u => u.IsActive)));
        after.Should().Be(before, "a report is read-only: no audit rows, no login switched off");
        (await f.StatusOf(withLogin)).Should().Be("Retired");
        tag.Should().NotBeNull();
    }

    [Fact]
    public async Task SeparatedReport_IsScopedToTheCallersCompanies()
    {
        var (withLogin, withWps, withSep, plain, other, tag) = await SeedSeparatedPeopleAsync();

        var (status, body) = await Call(HttpMethod.Get, "/api/employees/separated-status-report?pageSize=100", f.ScopedToken("HR Manager"));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var ids = body.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("employeeCode").GetString()!.StartsWith(tag)).Select(i => i.GetProperty("employeeId").GetInt32()).ToList();
        ids.Should().BeEquivalentTo(new[] { withLogin, withWps, withSep, plain });
        ids.Should().NotContain(other, "a person of another company is outside this caller's companies");
    }

    [Theory]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("HR Manager", HttpStatusCode.OK)]
    [InlineData("HR Officer", HttpStatusCode.Forbidden)]
    [InlineData("Auditor", HttpStatusCode.Forbidden)]
    [InlineData("Employee", HttpStatusCode.Forbidden)]
    public async Task SeparatedReport_IsForHrManagerAndAdminOnly(string role, HttpStatusCode expected)
    {
        var (status, _) = await Call(HttpMethod.Get, "/api/employees/separated-status-report", f.Token(role));
        status.Should().Be(expected, role);
    }
}
