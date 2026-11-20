using System.Net;
using System.Net.Http.Headers;
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
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// Wave 1 P1 (final gate Fable P1-2 = Security P2-1) through the REAL HTTP pipeline: marking a Returned or Rejected salary as
/// paid again, with or without an exit, is the same decision as an exit. It needs payroll.wps_status.update on top of
/// payroll.export, is refused on the caller's own record, takes a reason of at most 500 characters, and masks that reason
/// before it reaches the sealed audit row. The own-record refusal covers EVERY row the import would apply, not only the exits.
/// No role's grants change: synthetic custom roles hold exactly the keys named. SQLite stands in for Postgres.
/// </summary>
public sealed class BankReversalPermissionHttpTests(BankReversalHttpFixture f) : IClassFixture<BankReversalHttpFixture>
{
    private const string Iban = "SA0380000000608010167519";
    private string Path(bool reversal, string? reason) =>
        $"/api/payroll/payment-batches/{f.BatchId}/bank-confirmations?allowReturnReversal={reversal.ToString().ToLowerInvariant()}"
        + (reason is null ? "" : $"&returnReversalReason={Uri.EscapeDataString(reason)}");

    private async Task<(HttpStatusCode Status, string Body)> Post(string user, string wpsReference, bool reversal = false, string? reason = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            fileName = $"letter-{Guid.NewGuid():N}.json",
            rows = new[] { new { wpsReference, amount = 100m, status = "PAID", valueDate = "2026-07-30", bankReference = Guid.NewGuid().ToString("N") } },
        });
        var request = new HttpRequestMessage(HttpMethod.Post, Path(reversal, reason))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(user));
        using var response = await f.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The record's status and how many bank lines and audit rows exist for the batch: what a refusal must leave alone.</summary>
    private async Task<(string Status, int Lines, int Audits)> State(string wpsReference)
    {
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var record = await db.PayrollPaymentRecords.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.TenantId == f.TenantId && r.WpsReference == wpsReference);
        var lines = await db.BankPaymentConfirmations.IgnoreQueryFilters().CountAsync(c => c.TenantId == f.TenantId && c.PaymentRecordId == record.Id);
        var audits = await db.PayrollAuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == f.TenantId && a.Action == "payroll.bank_confirmation.imported");
        return (record.Status!, lines, audits);
    }

    [Fact]
    public async Task AReversal_WithNoExits_ByImportOnly_IsRefused_AndNothingChanges()
    {
        var before = await State("W-PERM");
        var (status, body) = await Post("ImportOnly", "W-PERM", reversal: true, reason: "Statement shows the credit");
        status.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("exit_needs_payment_status_permission");
        (await State("W-PERM")).Should().Be(before).And.Be((PaymentRecordStatuses.Returned, 0, before.Audits));
    }

    [Fact]
    public async Task AReversal_ByTheRecordsOwnEmployee_IsRefused_AndNothingChanges()
    {
        var before = await State("W-OWN");
        var (status, body) = await Post("Self", "W-OWN", reversal: true, reason: "Statement shows the credit");
        status.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("exit_on_own_payment_record");
        (await State("W-OWN")).Should().Be(before).And.Be((PaymentRecordStatuses.Returned, 0, before.Audits));
    }

    [Fact]
    public async Task APlainPaidLine_OnTheCallersOwnRecord_IsRefused_NotOnlyExits()
    {
        var before = await State("W-OWNPLAIN");
        var (status, body) = await Post("Self", "W-OWNPLAIN");
        status.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("exit_on_own_payment_record");
        (await State("W-OWNPLAIN")).Should().Be(before).And.Be((PaymentRecordStatuses.Pending, 0, before.Audits));
    }

    [Fact]
    public async Task AFailedToPaidLine_WithoutTheFlag_ByImportOnly_IsRefused_AndNothingChanges()
    {
        var before = await State("W-NOFLAG");
        var (status, body) = await Post("ImportOnly", "W-NOFLAG");
        status.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("exit_needs_payment_status_permission");
        (await State("W-NOFLAG")).Should().Be(before).And.Be((PaymentRecordStatuses.Returned, 0, before.Audits));
    }

    [Fact]
    public async Task AReversal_ByAKeyHolderWhoIsNotTheSubject_StillSucceeds()
    {
        var (status, body) = await Post("ImportAndStatus", "W-OK", reversal: true, reason: "Statement shows the credit");
        status.Should().Be(HttpStatusCode.OK, body);
        (await State("W-OK")).Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    /// <summary>A PENDING line that carries reason DUPL plus the named exit: the shape that resolves an open duplicate hold.</summary>
    private async Task<(HttpStatusCode Status, string Body)> PostDuplicateExit(string user, string wpsReference, string exitKind)
    {
        var payload = JsonSerializer.Serialize(new
        {
            fileName = $"letter-{Guid.NewGuid():N}.json",
            rows = new[] { new { wpsReference, amount = 100m, status = "PENDING", reasonCode = "DUPL", valueDate = "2026-07-30", bankReference = Guid.NewGuid().ToString("N") } },
            exits = new[] { new { kind = exitKind, employeeReference = wpsReference, reason = "test-statement checked", bankReference = "test-stmt-1" } },
        });
        var request = new HttpRequestMessage(HttpMethod.Post, Path(false, null))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(user));
        using var response = await f.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("W-DUPL-SELF-ORIG", BankLineExitKinds.ConfirmOriginalPaymentReceived)]
    [InlineData("W-DUPL-SELF-NOTPAID", BankLineExitKinds.ConfirmDuplicateNotPaid)]
    public async Task ADuplicateExit_OnAPendingRow_ForTheCallersOwnRecord_IsRefused_AndNothingChanges(string wps, string exitKind)
    {
        var before = await State(wps);
        var (status, body) = await PostDuplicateExit("Self", wps, exitKind);
        status.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("exit_on_own_payment_record");
        (await State(wps)).Should().Be(before).And.Be((PaymentRecordStatuses.Pending, 1, before.Audits));
    }

    [Theory]
    [InlineData("W-DUPL-OTHER-ORIG", BankLineExitKinds.ConfirmOriginalPaymentReceived, "Paid")]
    [InlineData("W-DUPL-OTHER-NOTPAID", BankLineExitKinds.ConfirmDuplicateNotPaid, "Rejected")]
    public async Task ADuplicateExit_OnAPendingRow_ByAKeyHolderWhoIsNotTheSubject_StillSucceeds(string wps, string exitKind, string expected)
    {
        var (status, body) = await PostDuplicateExit("ImportAndStatus", wps, exitKind);
        status.Should().Be(HttpStatusCode.OK, body);
        (await State(wps)).Status.Should().Be(expected);
    }

    [Fact]
    public async Task AReasonOverFiveHundredCharacters_IsRefused400_AndNothingChanges()
    {
        var before = await State("W-LONG");
        var (status, body) = await Post("ImportAndStatus", "W-LONG", reversal: true, reason: new string('r', 501));
        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("return_reversal_reason_too_long");
        (await State("W-LONG")).Should().Be(before).And.Be((PaymentRecordStatuses.Returned, 0, before.Audits));
    }

    [Fact]
    public async Task AReasonOfExactlyFiveHundredCharacters_IsAccepted()
    {
        var (status, body) = await Post("ImportAndStatus", "W-EDGE", reversal: true, reason: new string('r', 500));
        status.Should().Be(HttpStatusCode.OK, body);
    }

    [Fact]
    public async Task AnIbanShapedReason_IsStoredMasked()
    {
        var (status, body) = await Post("ImportAndStatus", "W-MASK", reversal: true, reason: $"Statement for {Iban} shows the credit");
        status.Should().Be(HttpStatusCode.OK, body);
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var metadata = (await db.PayrollAuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == f.TenantId && a.Action == "payroll.bank_confirmation.imported").ToListAsync())
            .Select(a => a.MetadataJson!).Single(m => m.Contains("Statement for"));
        metadata.Should().NotContain(Iban).And.Contain("shows the credit");
    }
}

public sealed class BankReversalHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid BatchId { get; private set; }
    public string Token(string key) => _tokens[key];

    private int _selfEmployeeId;

    private static readonly (string Key, string[] Permissions)[] Users =
    {
        ("ImportOnly", new[] { "payroll.export" }),
        ("ImportAndStatus", new[] { "payroll.export", "payroll.wps_status.update" }),
        ("Self", new[] { "payroll.export", "payroll.wps_status.update" }),
    };

    /// <summary>One record per test, so no test depends on another's changes. Status Returned unless stated.</summary>
    /// <summary>EmployeeId 0 stands for the employee the "Self" login belongs to.</summary>
    private static readonly (string Wps, int EmployeeId, string Status)[] Records =
    {
        ("W-PERM", 9101, PaymentRecordStatuses.Returned),
        ("W-OWN", 0, PaymentRecordStatuses.Returned),
        ("W-OWNPLAIN", 0, PaymentRecordStatuses.Pending),
        ("W-NOFLAG", 9103, PaymentRecordStatuses.Returned),
        ("W-OK", 9104, PaymentRecordStatuses.Returned),
        ("W-LONG", 9105, PaymentRecordStatuses.Returned),
        ("W-EDGE", 9106, PaymentRecordStatuses.Returned),
        ("W-MASK", 9107, PaymentRecordStatuses.Returned),
        ("W-DUPL-SELF-ORIG", 0, PaymentRecordStatuses.Pending),
        ("W-DUPL-SELF-NOTPAID", 0, PaymentRecordStatuses.Pending),
        ("W-DUPL-OTHER-ORIG", 9110, PaymentRecordStatuses.Pending),
        ("W-DUPL-OTHER-NOTPAID", 9111, PaymentRecordStatuses.Pending),
    };

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:bank-reversal-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Reversal", Slug = $"rv-{TenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyId, TenantId = TenantId, LegalNameEn = "Reversal Co", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true });
        var run = new PayrollRun { TenantId = TenantId, CompanyId = CompanyId, Year = 2026, Month = 7, Status = "Locked" };
        var batch = new PayrollPaymentBatch { TenantId = TenantId, PayrollRunId = run.Id, BatchNumber = "PB-REV-1", Currency = "SAR", TotalAmount = 100m * Records.Length };
        var selfEmployee = new Employee
        {
            TenantId = TenantId, CompanyId = CompanyId, EmployeeCode = "E-SELF", FullName = "Self", Status = EmployeeStatuses.Active,
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.AddRange(run, batch, selfEmployee);
        await db.SaveChangesAsync();
        _selfEmployeeId = selfEmployee.Id;
        foreach (var (wps, employeeId, status) in Records)
            db.PayrollPaymentRecords.Add(new PayrollPaymentRecord
            {
                TenantId = TenantId, PaymentBatchId = batch.Id, EmployeeId = employeeId == 0 ? _selfEmployeeId : employeeId, Amount = 100m, Status = status,
                Iban = "SA0380000000608010167519", WpsReference = wps,
            });
        await db.SaveChangesAsync();
        BatchId = batch.Id;
        // The duplicate-hold records already carry the bank's earlier DUPL line, held for a person (an open hold an exit may resolve).
        foreach (var record in await db.PayrollPaymentRecords.IgnoreQueryFilters().Where(r => r.TenantId == TenantId && r.WpsReference!.StartsWith("W-DUPL-")).ToListAsync())
            db.BankPaymentConfirmations.Add(new BankPaymentConfirmation
            {
                TenantId = TenantId, PaymentBatchId = batch.Id, PaymentRecordId = record.Id, EmployeeId = record.EmployeeId,
                Outcome = BankConfirmationOutcomes.Rejected, RawOutcome = "RJCT", PreviousStatus = PaymentRecordStatuses.Pending,
                Applied = false, HoldReason = BankOutcomeHolds.DuplicateRejection, ConfirmedAmount = 100m, RecordAmount = 100m,
                ReasonCode = "DUPL", MatchedBy = "wps_reference", ImportBatchId = Guid.NewGuid(), SourceFileName = "test-earlier.json",
                SourceFileHash = $"test-hash-{record.Id:N}", ParserKey = "generic-csv", ImportedByName = "test-officer",
                ImportedAtUtc = DateTime.UtcNow.AddDays(-1),
            });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);
        foreach (var (key, permissions) in Users) await AddUser(db, key, permissions);
        var self = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.TenantId == TenantId && u.FullName == "Self");
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = TenantId, EmployeeId = _selfEmployeeId, UserId = self.Id, AccessMode = AccessModes.FullPortal, Status = "Active", RequiresPasswordSetup = false });
        await db.SaveChangesAsync();
        foreach (var (key, _) in Users) await IssueToken(scope, key);
    }

    /// <summary>A synthetic custom role holding exactly <paramref name="permissions"/> (no seeded role is changed).</summary>
    private async Task AddUser(ZayraDbContext db, string key, string[] permissions)
    {
        var role = new Role { TenantId = TenantId, Name = $"Synthetic {key}", NormalizedName = $"SYNTHETIC {key}".ToUpperInvariant(), AuthorityLevel = 50 };
        db.Roles.Add(role);
        foreach (var p in permissions)
            db.Set<RolePermission>().Add(new RolePermission { RoleId = role.Id, PermissionId = (await db.Permissions.SingleAsync(x => x.Key == p)).Id });
        var email = $"{key}-{TenantId:N}@reversal.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
    }

    private async Task IssueToken(IServiceScope scope, string key)
    {
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{key}-{TenantId:N}@reversal.local";
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
            EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
