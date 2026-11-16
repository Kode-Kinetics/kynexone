using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The plan's employee limit applies to the normal status change too, not only to create, import and unmerge: a change INTO a status the
/// plan counts (<see cref="EmployeeSeatLimit.CountedStatuses"/>) from one it does not is refused with 402 and both languages once the
/// limit is reached. A change between two counted statuses adds nobody and is never refused. Real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeStatusChangePlanLimitPostgresTests
{
    private readonly PostgresFixture _fx;
    public EmployeeStatusChangePlanLimitPostgresTests(PostgresFixture fx) => _fx = fx;

    private sealed record Seed(Guid TenantId, int EmployedId, int DraftId);

    private async Task<Seed> SeedAsync(int maxEmployees)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Limit PG", CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"L-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        Employee Row(string code, string status) => new()
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = code, FullName = $"Limit {code}", EnglishName = $"Limit {code}",
            Status = status, ReadinessState = "Ready", JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc),
        };
        var employed = Row("LIM-1", "Active");
        var draft = Row("LIM-2", "Draft");
        db.Employees.AddRange(employed, draft);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, Plan = "Starter", Status = "Active", MaxEmployees = maxEmployees });
        await db.SaveChangesAsync();
        return new Seed(tenantId, employed.Id, draft.Id);
    }

    private async Task<ObjectResult?> ChangeAsync(Seed s, int employeeId, string status)
    {
        await using var db = _fx.CreateDb();
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db),
            new LimitHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new LimitLetters());
        var claims = new List<Claim>
        {
            new("tenant_id", s.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.read"), new("permission", "employees.write"), new("is_group_scope", "true"),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        var service = new EmployeeManagementService(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));
        var result = await controller.ChangeStatus(employeeId, new EmployeeStatusChangeRequest(status, new DateOnly(2026, 11, 1), "limit test"), service, CancellationToken.None);
        return result.Result as ObjectResult;
    }

    private async Task<string> StatusAsync(int id)
    {
        await using var db = _fx.CreateDb();
        return await db.Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.Status).SingleAsync();
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Probation")]
    [InlineData("Confirmed")]
    [InlineData("On leave")]
    [InlineData("Notice period")]
    public async Task ChangeStatus_IntoACountedStatus_IsRefused402_WhenThePlanLimitIsReached(string status)
    {
        var s = await SeedAsync(maxEmployees: 1);   // one employed person already fills the plan

        var result = await ChangeAsync(s, s.DraftId, status);

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement;
        body.GetProperty("error").GetString().Should().Be("employee_limit_reached");
        body.GetProperty("maxAllowed").GetInt32().Should().Be(1);
        body.GetProperty("message").GetString().Should().Contain("1");
        body.GetProperty("messageAr").GetString().Should().MatchRegex("[\\u0600-\\u06FF]");
        (await StatusAsync(s.DraftId)).Should().Be("Draft", "a refused change writes nothing");
    }

    [Fact]
    public async Task ChangeStatus_IntoACountedStatus_Works_WhenThePlanStillHasRoom()
    {
        var s = await SeedAsync(maxEmployees: 2);

        var result = await ChangeAsync(s, s.DraftId, "Probation");

        result!.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await StatusAsync(s.DraftId)).Should().Be("Probation");
    }

    [Fact]
    public async Task ChangeStatus_BetweenTwoCountedStatuses_AddsNobody_AndIsNeverRefused()
    {
        var s = await SeedAsync(maxEmployees: 1);

        var result = await ChangeAsync(s, s.EmployedId, "Probation");

        result!.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await StatusAsync(s.EmployedId)).Should().Be("Probation");
    }

    [Fact]
    public async Task ChangeStatus_IntoAStatusThePlanDoesNotCount_IsNeverRefused()
    {
        var s = await SeedAsync(maxEmployees: 1);

        var result = await ChangeAsync(s, s.DraftId, "Inactive");

        result!.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ChangeStatus_WithNoPlanCap_IsNeverRefused()
    {
        var s = await SeedAsync(maxEmployees: 0);   // 0 = no cap

        var result = await ChangeAsync(s, s.DraftId, "Probation");

        result!.StatusCode.Should().Be(StatusCodes.Status200OK);
    }
}

file sealed class LimitHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class LimitLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
