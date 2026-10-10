using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// The rates surfaces through the REAL HTTP pipeline, so every response goes through the production
/// System.Text.Json serializer. Controller-level tests read <c>OkObjectResult.Value</c> and never
/// serialize; that is how a DTO with two properties named "unit" (p.Unit and a computed unit) shipped
/// and turned every company-rate read and write into a 500. An anonymous DTO with colliding camelCase
/// names fails only here.
/// </summary>
public class RatesHttpSerializationTests : IClassFixture<RatesHttpFixture>
{
    private readonly RatesHttpFixture _fx;
    public RatesHttpSerializationTests(RatesHttpFixture fx) => _fx = fx;

    private async Task<JsonElement> OkJson(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task CompanyRates_ListRegistryCreateAndSupersede_Serialize_WithOneUnit()
    {
        using var list = await _fx.SendAsync(HttpMethod.Get, $"/api/finance/rates/company?companyId={_fx.CompanyId}");
        var rows = await OkJson(list);
        rows.GetArrayLength().Should().BeGreaterThan(0, "the fixture seeds a rate, so the element type is serialized");
        rows[0].GetProperty("unit").GetString().Should().Be("months");

        using var registry = await _fx.SendAsync(HttpMethod.Get, "/api/finance/rates/company/registry");
        (await OkJson(registry)).GetArrayLength().Should().BeGreaterThan(0);

        using var created = await _fx.SendAsync(HttpMethod.Post, $"/api/finance/rates/company?companyId={_fx.CompanyId}",
            new { rateKey = "payparameter.proration_basis", rateValue = "CalendarActual", effectiveFrom = "2026-01-01" });
        var row = await OkJson(created);
        row.GetProperty("unit").GetString().Should().Be("enum");
        var id = row.GetProperty("id").GetGuid();

        using var superseded = await _fx.SendAsync(HttpMethod.Put, $"/api/finance/rates/company/{id}",
            new { rateKey = "payparameter.proration_basis", rateValue = "WorkingDays", effectiveFrom = "2027-03-01" });
        (await OkJson(superseded)).GetProperty("rateValue").GetString().Should().Be("WorkingDays");
    }

    [Fact]
    public async Task Statutory_CompanyGroupAndPending_Serialize()
    {
        using var company = await _fx.SendAsync(HttpMethod.Get, $"/api/finance/rates/statutory?companyId={_fx.CompanyId}");
        var rows = await OkJson(company);
        rows.EnumerateArray().Select(r => r.GetProperty("ruleKey").GetString()).Should().Contain("ot.standard_multiplier");

        using var group = await _fx.SendAsync(HttpMethod.Get, "/api/finance/rates/statutory");
        (await OkJson(group)).GetArrayLength().Should().BeGreaterThan(0);

        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/finance/rates/statutory/override", new
        {
            companyId = _fx.CompanyId, countryCode = "SAU", jurisdiction = "KSA-mainland", ruleKey = "ot.standard_multiplier",
            overrideValue = "1.75", effectiveFrom = "2027-01-01", reason = "CBA", reviewBy = "2028-01-01",
        });
        (await OkJson(created)).GetProperty("kind").GetString().Should().Be("change");

        using var pending = await _fx.SendAsync(HttpMethod.Get, "/api/finance/rates/statutory/pending");
        var queue = await OkJson(pending);
        queue.EnumerateArray().Should().Contain(q => q.GetProperty("ruleKey").GetString() == "ot.standard_multiplier"
                                                    && q.GetProperty("kind").GetString() == "change"
                                                    && q.GetProperty("isNew").GetBoolean(),
            "no override is in force for this rule, so a checker sees a NEW override, not a value change");
    }
}

public sealed class RatesHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private HttpClient _client = null!;
    private string _token = null!;
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:rates-http-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        _client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            var tenant = new Tenant { Name = "Rates HTTP", Slug = $"rates-http-{Guid.NewGuid():N}", IsActive = true };
            db.Tenants.Add(tenant);
            TenantId = tenant.Id;
            var company = new Company
            {
                TenantId = TenantId, LegalNameEn = "Rates Co", TradeName = "Rates Co", CountryCode = "SAU",
                Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
            };
            db.Companies.Add(company);
            CompanyId = company.Id;
            db.TenantSubscriptions.Add(new TenantSubscription { TenantId = TenantId, Plan = "Enterprise", Status = "Active", MaxEmployees = 1000 });
            var since = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var (key, value) in new[] { ("ot.standard_multiplier", "1.5"), ("lop.monthly_day_divisor", "30") })
                db.StatutoryRules.Add(new StatutoryRule
                {
                    TenantId = null, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = key, RuleValue = value,
                    DataType = "decimal", Description = "KSA Labour Law", EffectiveFrom = since,
                });
            // A row with an EMPTY unit and one with its own: both shapes go through RateRow.
            db.CompanyRatePolicies.Add(new CompanyRatePolicy
            {
                TenantId = TenantId, CompanyId = CompanyId, RateKey = "payparameter.arrears_max_lookback_months",
                RateCategory = "PayParameter", RateValue = "24", DataType = "decimal", Unit = "",
                EffectiveFrom = new DateOnly(2025, 1, 1), Status = CompanyPolicyStatuses.Active, Notes = "",
            });
            var user = new User
            {
                TenantId = TenantId, Email = "rates@rates-http.local", NormalizedEmail = "RATES@RATES-HTTP.LOCAL",
                FullName = "Rates Admin", PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal",
                IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
            };
            db.Users.Add(user);
            foreach (var p in new[] { "payroll.rates.read", "payroll.rates.manage", "payroll.rates.statutory_override" })
                db.Set<UserPermissionOverride>().Add(new UserPermissionOverride
                {
                    TenantId = TenantId, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "HTTP serialization regression",
                });
            await db.SaveChangesAsync();
        }
        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
            var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Include(u => u.Tenant)
                .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
                .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
                .SingleAsync(u => u.TenantId == TenantId && u.Email == "rates@rates-http.local");
            var grants = Array.Empty<EntityAccessGrant>();
            var entityScope = EntityScopeClaims.Resolve(user.IsGroupScope, grants, Array.Empty<Guid>());
            _token = tokens.CreateAccessToken(user, AuthService.GetRoles(user), AuthService.GetPermissions(user), user.Tenant!, grants, entityScope, out _);
        }
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return _client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
