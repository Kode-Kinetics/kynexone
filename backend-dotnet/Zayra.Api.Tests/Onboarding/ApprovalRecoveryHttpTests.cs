using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

public class ApprovalRecoveryHttpTests
{
    private static Guid UserId(string token) => Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Subject);

    [Fact]
    public async Task Readiness_lists_the_same_pay_and_task_blockers_as_activation()
    {
        await using var fixture = new Fixture();
        var fx = await fixture.Start();
        var maker = await fx.NewUserAsync("maker", "employees.write", "employees.read", "employees.approve");
        using var created = await fx.SendAsync(HttpMethod.Post, "/api/employees", new {
            englishName = "Readiness Recovery", companyId = fx.CompanyId,
            nationality = "Indian",
            complianceRecords = new[] { new { countryCode = "SA", fieldKey = "iqama_number", fieldLabel = "Iqama",
                fieldValue = TestIds.ValidIqama(12), expiryDate = DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd"), isSensitive = true, isRequired = true } },
            salaryBreakdown = new { basicSalary = 8000m }
        }, maker);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var taskId = Guid.NewGuid();
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var publicId = await db.Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.PublicId).SingleAsync();
            db.OnboardingTasks.Add(new OnboardingTask { Id = taskId, TenantId = fx.TenantId, EmployeeId = publicId,
                TaskTitle = "Review signed contract", Status = "Pending", BlocksActivation = true });
            await db.SaveChangesAsync();
        }
        using var readiness = await fx.SendAsync(HttpMethod.Get, $"/api/employees/{id}/readiness", null, maker);
        readiness.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("blocking").EnumerateArray().Select(x => x.GetProperty("key").GetString())
            .Should().Contain("pay:compensationUnapproved").And.Contain("onboarding:task:" + taskId);
        using var activation = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/activate",
            new { status = "Active", effectiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), reason = "Ready" }, maker);
        activation.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await activation.Content.ReadAsStringAsync());
        var refused = await activation.Content.ReadFromJsonAsync<JsonElement>();
        refused.GetProperty("blocking").EnumerateArray().Select(x => x.GetProperty("key").GetString())
            .Should().BeEquivalentTo(body.GetProperty("blocking").EnumerateArray().Select(x => x.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task New_company_requires_an_independent_usable_reviewer_and_detects_lost_access()
    {
        await using var fixture = new Fixture();
        var fx = await fixture.Start();
        var maker = await fx.NewScopedUserAsync("maker", fx.CompanyId, "security.manage", "payroll.approve", "employees.write");
        async Task<JsonElement> Setup()
        {
            using var response = await fx.SendAsync(HttpMethod.Get, $"/api/pay-approvals/setup?companyId={fx.CompanyId}", null, maker);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        (await Setup()).GetProperty("independentReviewerCount").GetInt32().Should().Be(0);
        (await Setup()).GetProperty("readyForNewHires").GetBoolean().Should().BeFalse();
        var reviewer = await fx.NewScopedUserAsync("reviewer", fx.CompanyId, "payroll.approve");
        (await Setup()).GetProperty("independentReviewerCount").GetInt32().Should().Be(1);
        (await Setup()).GetProperty("readyForNewHires").GetBoolean().Should().BeTrue();
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == UserId(reviewer));
            user.AccessMode = "NoLogin";
            await db.SaveChangesAsync();
        }
        (await Setup()).GetProperty("independentReviewerCount").GetInt32().Should().Be(0);
        (await Setup()).GetProperty("readyForNewHires").GetBoolean().Should().BeFalse();
        using var foreign = await fx.SendAsync(HttpMethod.Get, $"/api/pay-approvals/setup?companyId={fx.OtherCompanyId}", null, maker);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("locked")]
    [InlineData("denied")]
    [InlineData("other-company")]
    [InlineData("draft-employee")]
    [InlineData("sso-enforced")]
    public async Task Reviewer_setup_does_not_count_unusable_or_out_of_scope_accounts(string condition)
    {
        await using var fixture = new Fixture();
        var fx = await fixture.Start();
        var admin = await fx.NewScopedUserAsync("admin", fx.CompanyId, "security.manage", "employees.write");
        var reviewer = await fx.NewScopedUserAsync("reviewer", condition == "other-company" ? fx.OtherCompanyId : fx.CompanyId, "payroll.approve");
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var user = await db.Users.IgnoreQueryFilters().Include(u => u.PermissionOverrides).SingleAsync(u => u.Id == UserId(reviewer));
            if (condition == "password") user.MustChangePassword = true;
            if (condition == "locked") { user.IsLocked = true; user.LockoutEnd = DateTime.UtcNow.AddHours(1); }
            if (condition == "denied") user.PermissionOverrides.Single(p => p.PermissionKey == "payroll.approve").Effect = "Deny";
            if (condition == "sso-enforced")
            {
                user.IdentityProvider = "Entra";
                db.TenantIdentityProviderSettings.Add(new TenantIdentityProviderSetting {
                    TenantId = fx.TenantId, EnforceSsoLogin = true, AllowedDomainsCsv = ""
                });
            }
            if (condition == "draft-employee")
            {
                var e = new Employee { TenantId = fx.TenantId, CompanyId = fx.CompanyId, FullName = "Pending reviewer",
                    EmployeeCode = "PENDING", Status = "Draft", JoiningDate = DateTime.UtcNow };
                db.Employees.Add(e); await db.SaveChangesAsync();
                db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = fx.TenantId, EmployeeId = e.Id,
                    UserId = user.Id, IsPrimary = true, AccessMode = "FullPortal", Status = "Active", RequiresPasswordSetup = false });
            }
            await db.SaveChangesAsync();
        }
        using var response = await fx.SendAsync(HttpMethod.Get, $"/api/pay-approvals/setup?companyId={fx.CompanyId}", null, admin);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("independentReviewerCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Activation_preserves_an_authorized_checksum_exception_in_the_same_transaction()
    {
        await using var fixture = new Fixture();
        var fx = await fixture.Start();
        var maker = await fx.NewUserAsync("maker", "employees.write", "employees.read", "employees.approve", "employees.override_validation");
        using var created = await fx.SendAsync(HttpMethod.Post, "/api/employees", new {
            englishName = "Checksum exception", companyId = fx.CompanyId, nationality = "Indian",
            complianceRecords = new[] { new { countryCode = "SA", fieldKey = "iqama_number", fieldLabel = "Iqama",
                fieldValue = TestIds.ValidIqama(13), isSensitive = true, isRequired = true } }
        }, maker);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var record = await db.EmployeeComplianceRecords.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == id && x.FieldKey == "iqama_number");
            record.FieldValue = "2000000009";
            (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id)).IqamaNumber = record.FieldValue;
            await db.SaveChangesAsync();
        }
        using var response = await fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/activate", new {
            status = "Active", effectiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), reason = "Identity verified",
            acknowledgedWarnings = new[] { new { code = "id_checksum_invalid", reason = "Verified against original identity evidence" } }
        }, maker);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var verify = fx.Host.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await verifyDb.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == fx.TenantId && a.EntityId == id.ToString()
            && a.Action == "employee.warning_overridden")).Should().BeTrue();
    }

    [Fact]
    public async Task Creator_queue_reports_packages_needing_someone_else_without_disclosing_pay()
    {
        await using var fixture = new Fixture();
        var fx = await fixture.Start();
        var maker = await fx.NewUserAsync("maker", "employees.write", "employees.read", "payroll.approve", "security.manage");
        using var created = await fx.SendAsync(HttpMethod.Post, "/api/employees", new {
            englishName = "Queue Recovery", companyId = fx.CompanyId,
            salaryBreakdown = new { basicSalary = 8123m }
        }, maker);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        using var queue = await fx.SendAsync(HttpMethod.Get, "/api/pay-approvals", null, maker);
        var body = await queue.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("requiresOtherReviewerCount").GetInt32().Should().Be(1);
        (await queue.Content.ReadAsStringAsync()).Should().NotContain("8123");
    }

    [Fact]
    public async Task Pay_approval_refresh_preserves_advisory_import_gaps_and_editor_attribution()
    {
        await using var fixture = new Fixture();
        var fx = await fixture.Start();
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            (await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == fx.CompanyId)).CountryCode = "US";
            db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile { TenantId = fx.TenantId, CompanyId = fx.CompanyId,
                CountryCode = "US", EffectiveFrom = new DateOnly(2020, 1, 1), Status = "Active",
                RequiredFieldsJson = """[{"key":"SalaryStructure","category":"payroll","failClosed":true,"gate":"activate"}]""" });
            await db.SaveChangesAsync();
        }
        var maker = await fx.NewUserAsync("maker", "employees.write", "employees.read");
        var reviewer = await fx.NewUserAsync("reviewer", "payroll.approve");
        using var created = await fx.SendAsync(HttpMethod.Post, "/api/employees", new {
            englishName = "Imported Recovery", companyId = fx.CompanyId, salaryBreakdown = new { basicSalary = 8000m }
        }, maker);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Guid? editor;
        using (var scope = fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            editor = (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id)).UpdatedBy;
            db.EmployeeImportGaps.Add(new EmployeeImportGap { TenantId = fx.TenantId, CompanyId = fx.CompanyId,
                EmployeeId = id, GapType = "org:department", GapCategory = "org", Detail = "Department missing" });
            await db.SaveChangesAsync();
        }
        using var detail = await fx.SendAsync(HttpMethod.Get, $"/api/pay-approvals/{id}", null, reviewer);
        detail.StatusCode.Should().Be(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync());
        var fingerprint = (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fingerprint").GetString();
        using var decision = await fx.SendAsync(HttpMethod.Post, $"/api/pay-approvals/{id}/decision",
            new { fingerprint, decision = "approve", reason = "Checked signed offer and bank evidence" }, reviewer);
        decision.StatusCode.Should().Be(HttpStatusCode.OK, await decision.Content.ReadAsStringAsync());
        using var verify = fx.Host.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await verifyDb.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
        employee.ReadinessState.Should().Be("NeedsAttention");
        employee.ActivationBlockersCount.Should().Be(0);
        employee.UpdatedBy.Should().Be(editor, "refreshing a derived badge is not editing the employee");
        (await verifyDb.EmployeeImportGaps.IgnoreQueryFilters().SingleAsync(g => g.EmployeeId == id && g.GapType == "org:department"))
            .ResolvedAtUtc.Should().BeNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HireIntegrityHttpFixture _fx = new();
        public async Task<HireIntegrityHttpFixture> Start() { await _fx.InitializeAsync(); return _fx; }
        public async ValueTask DisposeAsync() => await _fx.DisposeAsync();
    }
}
