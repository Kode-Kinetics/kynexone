using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// The batch-approver setting through the REAL HTTP pipeline (routing, JWT, role gates, permission filters, model binding,
/// JSON casing): the wire names the frontend reads and sends, an older client's body-less approve still binds, self-approval
/// with a reason, the role matrix on the approve and policy endpoints, and tenant isolation of the policy that relaxes it.
/// SQLite stands in for Postgres; LoanBatchApproverModeWorkflowPostgresTests covers the locks and the CHECK.
/// </summary>
public sealed class LoanBatchApproverModeHttpTests(LoanBatchApproverHttpFixture f) : IClassFixture<LoanBatchApproverHttpFixture>
{
    private const string Policies = "/api/finance/loans/policies";
    private const string Batches = "/api/finance/loans/payment-batches";

    private static object Policy(Guid companyId, Guid typeId, string mode, decimal cap = 0) => new
    {
        companyId, loanTypeId = typeId, policyName = "Batch approver", maxAmount = 5000, maxInstallments = 12,
        allowedEmploymentStatuses = new[] { "Active" }, allowedRepaymentMethods = new[] { "BankTransfer" },
        batchApproverMode = mode, selfApprovalMaxAmount = cap,
    };

    /// <summary>A fresh loan type with an approved loan, so each test owns its policy chain and its batch.</summary>
    private async Task<(Guid TypeId, Guid LoanId)> ApprovedLoan(decimal amount = 100m)
    {
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var type = new LoanType { TenantId = f.TenantId, Code = "T" + Guid.NewGuid().ToString("N")[..8], NameEn = "Type", MaxAmount = 10_000m, MaxInstallments = 12, RequiresApproval = true, IsActive = true };
        db.LoanTypes.Add(type);
        var loan = new EmployeeLoan
        {
            TenantId = f.TenantId, CompanyId = f.CompanyA, EmployeeIntId = f.EmployeeIntId, EmployeeId = f.EmployeePublicId, EmployeeName = "Borrower",
            LoanNumber = "LN-" + Guid.NewGuid().ToString("N")[..10], Status = "Approved", LoanTypeId = type.Id, LoanTypeName = "Type",
            RepaymentMethod = "BankTransfer", RepaymentFrequency = "Monthly", Currency = "SAR", RequestedAmount = amount, ApprovedAmount = amount,
            RequestedInstallments = 2, ApprovedInstallments = 2, InstallmentAmount = amount / 2,
        };
        db.EmployeeLoans.Add(loan);
        await db.SaveChangesAsync();
        return (type.Id, loan.Id);
    }

    private async Task<JsonElement> Json(HttpResponseMessage r, HttpStatusCode expected)
    {
        var body = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<Guid> DraftBatch(string makerKey, Guid loanId)
    {
        using var r = await f.Send(HttpMethod.Post, Batches, f.Token(makerKey), new { loanIds = new[] { loanId } });
        return (await Json(r, HttpStatusCode.OK)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task NoPolicy_WireFlagIsFalse_CreatorIsRefused_AndABodylessApproveByAnotherUserStillBinds()
    {
        var (_, loanId) = await ApprovedLoan();
        var batchId = await DraftBatch("Finance", loanId);

        using (var get = await f.Send(HttpMethod.Get, $"{Batches}/{batchId}", f.Token("Finance")))
            (await Json(get, HttpStatusCode.OK)).GetProperty("selfApprovalAllowed").GetBoolean().Should().BeFalse();
        using (var self = await f.SendRaw(HttpMethod.Patch, $"{Batches}/{batchId}/approve", f.Token("Finance"), """{"reason":"I am the only one here"}"""))
            self.StatusCode.Should().Be(HttpStatusCode.BadRequest, await self.Content.ReadAsStringAsync());

        // An older web build or another client sends no body at all: it must still reach the action.
        var bodyless = new HttpRequestMessage(HttpMethod.Patch, $"{Batches}/{batchId}/approve");
        bodyless.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token("Finance 2"));
        using (var r = await f.Client.SendAsync(bodyless))
            (await Json(r, HttpStatusCode.OK)).GetProperty("status").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task SingleOperator_AdminPublishes_WireFlagIsTrue_AndTheCreatorSelfApprovesWithTheReasonInTheBody()
    {
        var (typeId, loanId) = await ApprovedLoan();
        using (var p = await f.Send(HttpMethod.Post, Policies, f.Token("Admin"), Policy(f.CompanyA, typeId, "SingleOperator", 500)))
        {
            var saved = await Json(p, HttpStatusCode.OK);
            saved.GetProperty("batchApproverMode").GetString().Should().Be("SingleOperator");
            saved.GetProperty("selfApprovalMaxAmount").GetDecimal().Should().Be(500m);
        }
        using (var list = await f.Send(HttpMethod.Get, $"{Policies}?companyId={f.CompanyA}&loanTypeId={typeId}", f.Token("HR Manager")))
            (await Json(list, HttpStatusCode.OK))[0].GetProperty("batchApproverMode").GetString().Should().Be("SingleOperator", "the policy form reads it back to prefill");

        var batchId = await DraftBatch("Finance", loanId);
        using (var get = await f.Send(HttpMethod.Get, $"{Batches}/{batchId}", f.Token("Finance")))
            (await Json(get, HttpStatusCode.OK)).GetProperty("selfApprovalAllowed").GetBoolean().Should().BeTrue();
        using (var missing = await f.SendRaw(HttpMethod.Patch, $"{Batches}/{batchId}/approve", f.Token("Finance"), "{}"))
            missing.StatusCode.Should().Be(HttpStatusCode.BadRequest, "self-approval needs a reason");
        using (var ok = await f.SendRaw(HttpMethod.Patch, $"{Batches}/{batchId}/approve", f.Token("Finance"), """{"reason":"Sole finance officer this month"}"""))
            (await Json(ok, HttpStatusCode.OK)).GetProperty("status").GetString().Should().Be("Approved");

        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.LoanAuditLogs.IgnoreQueryFilters().CountAsync(x => x.LoanId == loanId && x.Action == "PaymentBatchSelfApproved")).Should().Be(1);
    }

    [Theory]
    [InlineData("HR Manager", HttpStatusCode.Forbidden)]
    [InlineData("Finance", HttpStatusCode.Forbidden)]
    [InlineData("Employee", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task OnlyAnAdminCanRelaxTheSetting(string role, HttpStatusCode expected)
    {
        var (typeId, _) = await ApprovedLoan();
        using var r = await f.Send(HttpMethod.Post, Policies, f.Token(role), Policy(f.CompanyA, typeId, "SingleOperator"));
        r.StatusCode.Should().Be(expected, role + " " + await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("Employee")]
    public async Task RolesWithoutFinanceApprovalCannotApproveABatch(string role)
    {
        var (typeId, loanId) = await ApprovedLoan();
        using (var p = await f.Send(HttpMethod.Post, Policies, f.Token("Admin"), Policy(f.CompanyA, typeId, "SingleOperator")))
            p.StatusCode.Should().Be(HttpStatusCode.OK);
        var batchId = await DraftBatch("Finance", loanId);
        using var r = await f.SendRaw(HttpMethod.Patch, $"{Batches}/{batchId}/approve", f.Token(role), """{"reason":"I am not finance at all"}""");
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
    }

    [Fact]
    public async Task AnotherTenantsSingleOperatorPolicyDoesNotRelaxThisTenantsBatch()
    {
        var (typeId, loanId) = await ApprovedLoan();
        using (var scope = f.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            db.LoanPolicies.Add(new LoanPolicy { TenantId = f.ForeignTenantId, CompanyId = f.CompanyA, LoanTypeId = typeId, PolicyName = "Foreign", IsActive = true, BatchApproverMode = "SingleOperator" });
            await db.SaveChangesAsync();
        }
        var batchId = await DraftBatch("Finance", loanId);
        using var r = await f.SendRaw(HttpMethod.Patch, $"{Batches}/{batchId}/approve", f.Token("Finance"), """{"reason":"Another tenant said so"}""");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"batchApproverMode":"Nobody"}""")]
    [InlineData("""{"batchApproverMode":"SeparateApprover","selfApprovalMaxAmount":10}""")]
    [InlineData("""{"batchApproverMode":"SingleOperator","selfApprovalMaxAmount":-5}""")]
    public async Task InvalidSettingsAreRejectedOnTheWire(string fragment)
    {
        var (typeId, _) = await ApprovedLoan();
        var body = JsonSerializer.SerializeToNode(Policy(f.CompanyA, typeId, "SeparateApprover"))!.AsObject();
        foreach (var (k, v) in JsonDocument.Parse(fragment).RootElement.EnumerateObject().Select(p => (p.Name, p.Value)))
            body[k] = System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText());
        using var r = await f.SendRaw(HttpMethod.Post, Policies, f.Token("Admin"), body.ToJsonString());
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
    }
}

public sealed class LoanBatchApproverHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    private readonly Dictionary<string, (string Email, EntityAccessGrant[] Grants)> _meta = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid ForeignTenantId { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public int EmployeeIntId { get; private set; }
    public Guid EmployeePublicId { get; private set; }
    public string Token(string key) => _tokens[key];

    private static readonly (string Key, string Role)[] Users =
        { ("Admin", "Admin"), ("HR Manager", "HR Manager"), ("Finance", "Finance"), ("Finance 2", "Finance"), ("Employee", "Employee") };

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:loan-approver-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.AddRange(new Tenant { Id = TenantId, Name = "Loans", Slug = $"la-{TenantId:N}", IsActive = true },
            new Tenant { Id = ForeignTenantId, Name = "Foreign", Slug = $"laf-{ForeignTenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyA, TenantId = TenantId, LegalNameEn = "Loan Co", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true });
        var employee = new Employee
        {
            TenantId = TenantId, CompanyId = CompanyA, EmployeeCode = "LA-1", FullName = "Borrower", EnglishName = "Borrower", Status = EmployeeStatuses.Active,
            BankName = "Bank", BankIban = "SA4420000001234567891234", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        EmployeeIntId = employee.Id; EmployeePublicId = employee.PublicId;
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);
        foreach (var (key, role) in Users) await AddUser(db, key, role);
        foreach (var (key, _) in Users) await IssueToken(scope, key);
    }

    private async Task AddUser(ZayraDbContext db, string key, string role)
    {
        var email = $"{key.Replace(' ', '-')}-{TenantId:N}@la.local";
        var user = new User { TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        await db.SaveChangesAsync();
        _meta[key] = (email, Array.Empty<EntityAccessGrant>());
    }

    private async Task IssueToken(IServiceScope scope, string key)
    {
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var m = _meta[key];
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == m.Email);
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, m.Grants, EntityScopeClaims.Resolve(true, m.Grants, Array.Empty<Guid>()), out _);
    }

    public Task<HttpResponseMessage> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendRaw(HttpMethod method, string path, string bearer, string json)
    {
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
