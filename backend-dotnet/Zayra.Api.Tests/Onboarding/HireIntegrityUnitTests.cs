using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>In-process tests of the create-time integrity logic (no host, no Postgres).</summary>
public class HireIntegrityUnitTests
{
    // ── Rate limit ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void TenantUserRateLimit_ChainsUserThenTenant()
    {
        using var limiter = new TenantUserRateLimiter();   // duplicate-check: 20 per user, 120 per tenant
        var tenant = Guid.NewGuid().ToString();
        var user = Guid.NewGuid().ToString();
        for (var i = 0; i < 20; i++) limiter.TryAcquire("duplicate-check", tenant, user, out _).Should().BeTrue($"call {i + 1}");
        limiter.TryAcquire("duplicate-check", tenant, user, out var retry).Should().BeFalse("the 21st call in the window");
        retry.Should().BeGreaterThan(0);

        // Other users of the same tenant are independent of that user's bucket (shared office address, G29) ...
        for (var u = 0; u < 5; u++)
        {
            var other = Guid.NewGuid().ToString();
            for (var i = 0; i < 20; i++) limiter.TryAcquire("duplicate-check", tenant, other, out _).Should().BeTrue();
        }
        // ... until the tenant ceiling: 20 + 5*20 = 120 permits are gone.
        limiter.TryAcquire("duplicate-check", tenant, Guid.NewGuid().ToString(), out _).Should().BeFalse("the tenant ceiling");
        // A different tenant is untouched.
        limiter.TryAcquire("duplicate-check", Guid.NewGuid().ToString(), user, out _).Should().BeTrue();
    }

    [Fact]
    public void TenantUserRateLimit_EachPolicyHasItsOwnWindow()
    {
        using var limiter = new TenantUserRateLimiter();
        var tenant = Guid.NewGuid().ToString();
        var user = Guid.NewGuid().ToString();
        for (var i = 0; i < 20; i++) limiter.TryAcquire("duplicate-check", tenant, user, out _);
        limiter.TryAcquire("derive-work-email", tenant, user, out _).Should().BeTrue();
        limiter.TryAcquire("no-such-policy", tenant, user, out _).Should().BeFalse("an unknown name is a wiring mistake and fails closed");
    }

    // ── Identity lock keys ───────────────────────────────────────────────────────────────────────
    private static DuplicateProbe Probe(string? name, DateOnly? dob, params (string Field, string Value)[] ids) =>
        new(name, null, dob, null, null, ids.ToDictionary(x => x.Field, x => x.Value, StringComparer.OrdinalIgnoreCase), null);

    [Fact]
    public void IdentityLock_SameIdentityFormatVariants_ShareAKey()
    {
        var tenant = Guid.NewGuid();
        var a = EmployeeIdentityLock.KeysFor(tenant, Probe("A", null, ("iqama_number", "2123456789")));
        var b = EmployeeIdentityLock.KeysFor(tenant, Probe("B", null, ("IQAMA_NUMBER", " 2123 456-789 ")));
        a.Should().Equal(b, "two requests for the same person must contend on the same lock");
    }

    [Fact]
    public void IdentityLock_TenantsDoNotShareKeys_AndKeysAreSorted()
    {
        var p = Probe("A", null, ("iqama_number", "2123456789"), ("passport_number", "P1234567"));
        var t1 = EmployeeIdentityLock.KeysFor(Guid.NewGuid(), p);
        var t2 = EmployeeIdentityLock.KeysFor(Guid.NewGuid(), p);
        t1.Intersect(t2).Should().BeEmpty();
        t1.Should().BeInAscendingOrder("a fixed lock order is what prevents two creates deadlocking");
        t1.Should().HaveCount(2);
    }

    [Fact]
    public void IdentityLock_NoIdentity_FallsBackToNameAndDob()
    {
        var tenant = Guid.NewGuid();
        var dob = new DateOnly(1990, 5, 1);
        var a = EmployeeIdentityLock.KeysFor(tenant, Probe("Ali  Hassan", dob));
        var b = EmployeeIdentityLock.KeysFor(tenant, Probe("ali hassan", dob));
        var c = EmployeeIdentityLock.KeysFor(tenant, Probe("ali hassan", dob.AddDays(1)));
        a.Should().ContainSingle().And.Equal(b);
        a.Should().NotEqual(c);
        EmployeeIdentityLock.KeysFor(tenant, Probe(null, null)).Should().BeEmpty();
    }

    // ── Duplicate override policy ────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(false, null, false, DuplicateOverridePolicy.Verdict.Allowed)]
    [InlineData(true, "a proper reason", false, DuplicateOverridePolicy.Verdict.NotPermitted)]
    [InlineData(true, "too short", true, DuplicateOverridePolicy.Verdict.ReasonRequired)]
    [InlineData(true, null, true, DuplicateOverridePolicy.Verdict.ReasonRequired)]
    [InlineData(true, "Same person, second legal entity", true, DuplicateOverridePolicy.Verdict.Allowed)]
    public void Override_RequiresPermissionAndReason(bool ack, string? reason, bool permitted, DuplicateOverridePolicy.Verdict expected) =>
        DuplicateOverridePolicy.Check(ack, reason, permitted).Should().Be(expected);

    [Fact]
    public void DuplicateAudit_OmitsOutOfScopeIds_AndCarriesNoIdentityValue()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var matches = new[]
        {
            new DuplicateMatch(11, "E-11", "Visible Person", null, visible, "Active", "strong", ["iqama_number"]),
            new DuplicateMatch(22, "E-22", "Hidden Person", null, hidden, "Active", "strong", ["iqama_number"]),
        };
        var json = DuplicateOverridePolicy.AuditPayload(matches, c => c == visible, true, "  same person, other entity  ");
        json.Should().Contain("11").And.NotContain("22").And.Contain("\"outOfScopeMatchCount\":1");
        json.Should().NotContain("Hidden").And.NotContain("E-22").And.Contain("same person, other entity");
    }

    // ── Draft discard rules ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void DraftDiscard_Rules()
    {
        var me = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var ok = DraftDiscardRules.Evaluate("Draft", me, me, now.AddDays(-2), now, false);
        ok.Should().Be(DraftDiscardRules.Refusal.None);
        DraftDiscardRules.Evaluate("Active", me, me, now, now, false).Should().Be(DraftDiscardRules.Refusal.NotDraft);
        DraftDiscardRules.Evaluate("Draft", Guid.NewGuid(), me, now, now, false).Should().Be(DraftDiscardRules.Refusal.NotCreator);
        DraftDiscardRules.Evaluate("Draft", null, me, now, now, false).Should().Be(DraftDiscardRules.Refusal.NotCreator);
        DraftDiscardRules.Evaluate("Draft", me, me, now.AddDays(-8), now, false).Should().Be(DraftDiscardRules.Refusal.TooOld);
        DraftDiscardRules.Evaluate("Draft", me, me, now, now, true).Should().Be(DraftDiscardRules.Refusal.HasActivity);
    }

    [Fact]
    public void DraftDiscard_Apply_SetsTheSoftDeleteFieldSet()
    {
        var me = Guid.NewGuid();
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var e = new Employee { Status = "Draft" };
        DraftDiscardService.ApplySoftDelete(e, now, me);
        e.IsDeleted.Should().BeTrue();
        e.DeletedAtUtc.Should().Be(now);
        e.DeletedBy.Should().Be(me);
        e.Status.Should().Be("Inactive");
        e.PrivacyStatus.Should().Be("RetainedForStatutoryAudit");
        e.RetentionUntilUtc.Should().Be(now.AddYears(7));
    }

    [Fact]
    public void DraftDiscard_FieldSet_MatchesEmployeesControllerDelete()
    {
        // The controller's private SoftDeleteEmployeeAsync is the reference; a discard must set the same fields.
        var text = File.ReadAllText(Path.Combine(FindApiRoot(), "Controllers", "EmployeesController.cs"));
        var start = text.IndexOf("private async Task SoftDeleteEmployeeAsync", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var body = text.Substring(start, 900);
        body.Should().Contain("employee.IsDeleted = true;").And.Contain("employee.DeletedAtUtc = deletedAt;")
            .And.Contain("employee.DeletedBy = context.UserId;").And.Contain("employee.Status = \"Inactive\";")
            .And.Contain("employee.PrivacyStatus = \"RetainedForStatutoryAudit\";").And.Contain("employee.RetentionUntilUtc = deletedAt.AddYears(7);");
    }

    // ── Compensation approval gate ───────────────────────────────────────────────────────────────
    [Fact]
    public void CompensationApprove_RefusedToCreator_Subject_AndPayrollMaker()
    {
        var creator = Guid.NewGuid(); var subject = Guid.NewGuid(); var maker = Guid.NewGuid(); var finance = Guid.NewGuid();
        CompensationApprovalGate.RefusalFor(creator, creator, subject, [maker]).Should().Be("creator");
        CompensationApprovalGate.RefusalFor(maker, creator, subject, [maker]).Should().Be("creator");
        CompensationApprovalGate.RefusalFor(subject, creator, subject, [maker]).Should().Be("subject");
        CompensationApprovalGate.RefusalFor(finance, creator, subject, [maker]).Should().BeNull();
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData(0, "", "", false)]
    [InlineData(5000, null, null, true)]
    [InlineData(null, "SA0380000000608010167519", null, true)]
    [InlineData(null, null, "123456", true)]
    public void CompensationGate_OnlyOpensWhenThereIsSomethingToApprove(int? salary, string? iban, string? account, bool expected) =>
        CompensationApprovalGate.CarriesCompensation(salary, iban, account).Should().Be(expected);

    // The idempotency fingerprint now covers the whole request: see HireSecurityHardeningTests.RequestFingerprint_*.

    // ── Foreign-tenant company (second write layer) ──────────────────────────────────────────────
    [Fact]
    public async Task MembershipGuard_RejectsACompanyOfAnotherTenant_AcceptsOwn()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var mine = Guid.NewGuid(); var theirs = Guid.NewGuid();
        var own = new Company { TenantId = mine, LegalNameEn = "Mine", CountryCode = "SA", IsActive = true };
        var foreign = new Company { TenantId = theirs, LegalNameEn = "Theirs", CountryCode = "SA", IsActive = true };
        db.Companies.AddRange(own, foreign);
        await db.SaveChangesAsync();

        await TenantCompanyMembershipGuard.AssertAsync(db, own.Id, mine, default);
        var act = () => TenantCompanyMembershipGuard.AssertAsync(db, foreign.Id, mine, default);
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("company_scope_denied*");
        var missing = () => TenantCompanyMembershipGuard.AssertAsync(db, Guid.NewGuid(), mine, default);
        await missing.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── G23: no catch-all in the new files ───────────────────────────────────────────────────────
    [Fact]
    public void NewOnboardingFiles_HaveNoInvalidOperationCatchAll()
    {
        var root = FindApiRoot();
        var files = Directory.GetFiles(Path.Combine(root, "Infrastructure", "Employees", "Onboarding"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "Controllers", "Employees"), "*.cs"))
            .Append(Path.Combine(root, "Infrastructure", "Http", "TenantUserRateLimit.cs"));
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            text.Should().NotContain("catch (InvalidOperationException", f);
            text.Should().NotContain("throw new InvalidOperationException", f);
        }
    }

    private static string FindApiRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Zayra.Api", "Infrastructure"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("backend-dotnet not found"), "Zayra.Api");
    }
}
