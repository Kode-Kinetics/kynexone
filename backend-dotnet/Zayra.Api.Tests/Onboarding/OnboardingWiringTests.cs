using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>Train wiring: one registration path for the whole onboarding backend (I-3).</summary>
public class OnboardingWiringTests
{
    [Fact]
    public void AddOnboardingServices_RegistersValidationOnce()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        OnboardingServiceRegistration.AddOnboardingServices(services);

        services.Count(d => d.ServiceType == typeof(IEmployeeInputGuard)).Should().Be(1);
        using var sp = services.BuildServiceProvider();
        var mvc = sp.GetRequiredService<IOptions<MvcOptions>>().Value;
        mvc.Filters.OfType<TypeFilterAttribute>().Count(f => f.ImplementationType == typeof(EmployeeProblemFilter))
            .Should().Be(1, "the problem filter must be registered exactly once");
        mvc.ModelMetadataDetailsProviders.OfType<DtoBoundsMetadataProvider>().Should().HaveCount(1);
    }

    // ── guard hooks in the service (I-3) ─────────────────────────────────────────────────────────

    private sealed class CountingGuard : IEmployeeInputGuard
    {
        public int Create, Update;
        public Task<EmployeeCreateRequest> EnforceCreateAsync(Guid t, EmployeeCreateRequest r, RequestContext c, CancellationToken ct) { Create++; return Task.FromResult(r); }
        public Task<EmployeeCreateRequest> EnforceUpdateAsync(Guid t, int id, EmployeeCreateRequest r, RequestContext c, CancellationToken ct) { Update++; return Task.FromResult(r); }
        public Task<EmployeeCreateRequest> EnforceDraftAsync(Guid t, int? id, EmployeeCreateRequest r, RequestContext c, CancellationToken ct) => Task.FromResult(r);
    }

    private sealed class NoDocs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => Task.FromResult(new StoredDocument("f", "t", "u", "p"));
        public string ResolvePath(string storageUrl) => "/tmp";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private static ZayraDbContext Db() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, Company Company)> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Wire", Slug = $"t-{tenant:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        var c = new Company { TenantId = tenant, LegalNameEn = "Main", TradeName = "Main", CountryCode = "SA", Jurisdiction = "t",
            RegistrationNumber = "RC-" + Guid.NewGuid().ToString("N"), DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return (tenant, c);
    }

    private static EmployeeManagementService Svc(ZayraDbContext db, IEmployeeInputGuard? guard) =>
        new(db, new AuditService(db), new NoDocs(), TestNotifications.For(db), inputGuard: guard);

    private static RequestContext Ctx(Guid tenant, params string[] perms) => new("127.0.0.1", "test", Guid.NewGuid(), tenant, ["HR Manager"], perms);

    [Fact]
    public async Task Service_CreateAndUpdate_EnforceTheGuardExactlyOnce()
    {
        await using var db = Db();
        var (t, c) = await Seed(db);
        var guard = new CountingGuard();
        var svc = Svc(db, guard);
        var req = EmployeeInputValidatorTests.Req(name: "Wire One", nationality: "Indian") with { CompanyId = c.Id, DepartmentId = null, DesignationId = null };

        var created = await svc.CreateAsync(t, req, Ctx(t), default);
        guard.Create.Should().Be(1);
        guard.Update.Should().Be(0);

        await svc.UpdateAsync(t, created.Id, req with { EnglishName = "Wire One Edited" }, Ctx(t), default);
        guard.Create.Should().Be(1);
        guard.Update.Should().Be(1);
    }

    [Fact]
    public async Task Guard_DraftThenUpdateWithTheReturnedRequest_IsNotValidatedTwice()
    {
        await using var db = Db();
        var (t, c) = await Seed(db);
        var guard = new EmployeeInputGuard(db);
        var draft = await guard.EnforceDraftAsync(t, null, EmployeeInputValidatorTests.Req() with { CompanyId = c.Id }, Ctx(t), default);
        var again = await guard.EnforceUpdateAsync(t, 1, draft, Ctx(t), default);
        again.Should().BeSameAs(draft);
    }

    [Fact]
    public async Task WarningOverride_IsAuditedWithCodeAndReason_NeverTheValue()
    {
        await using var db = Db();
        var (t, c) = await Seed(db);
        var svc = Svc(db, new EmployeeInputGuard(db));
        var joining = DateTime.UtcNow.Date.AddDays(-45);
        var req = EmployeeInputValidatorTests.Req(name: "Override Hire", nationality: "Indian", joining: joining) with { CompanyId = c.Id };
        await Assert.ThrowsAsync<EmployeeWarningsRequireAckException>(() => svc.CreateAsync(t, req, Ctx(t), default));

        var reason = "Signed contract on file since last month";
        var created = await svc.CreateAsync(t, req with
        {
            AcknowledgedWarnings = [new AcknowledgedWarning(EmployeeWarningCodes.JoiningBackdated, reason), new AcknowledgedWarning("not_a_real_warning", "Should not be written down")],
        }, Ctx(t), default);

        var rows = await db.AuditLogs.IgnoreQueryFilters().Where(a => a.Action == "employee.warning_overridden").ToListAsync();
        var row = rows.Should().ContainSingle("only the acknowledgement that was actually consumed is audited").Subject;
        row.EntityId.Should().Be(created.Id.ToString());
        row.Metadata.Should().Contain(EmployeeWarningCodes.JoiningBackdated).And.Contain(reason);
        row.Metadata.Should().NotContain(joining.ToString("yyyy-MM-dd"));
    }
}
