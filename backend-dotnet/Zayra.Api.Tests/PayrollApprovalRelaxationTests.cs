using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Policy;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// PAY-05 — payroll.allow_skip_review / payroll.allow_same_approver_multiple_levels. Both default off (today's
/// two-level behaviour for HR / Payroll Managers); when on, only that one action is relaxed and the
/// approval row and audit entry record it.
/// </summary>
public class PayrollApprovalRelaxationTests
{
    private sealed class Policy(bool skip, bool same) : IPolicySettings
    {
        public Task<bool> GetBoolAsync(Guid t, string key, CancellationToken ct = default) =>
            Task.FromResult(key == PolicyRegistry.PayrollAllowSkipReview ? skip : key == PolicyRegistry.PayrollAllowSameApproverMultipleLevels && same);
        public Task<bool> GetBoolAsync(string key, CancellationToken ct = default) => GetBoolAsync(Guid.Empty, key, ct);
        public Task<string> GetStringAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEnumAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TEnum> GetEnumAsync<TEnum>(string key, CancellationToken ct = default) where TEnum : struct, Enum => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetCsvAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetStringAsync(Guid t, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEnumAsync(Guid t, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TEnum> GetEnumAsync<TEnum>(Guid t, string key, CancellationToken ct = default) where TEnum : struct, Enum => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetCsvAsync(Guid t, string key, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static PayrollController Ctrl(ZayraDbContext db, Guid tenantId, Guid userId, string role, IPolicySettings? policy)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Role, role),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(db, new _RelUnrestrictedScope(), new _RelHttpAccessor(httpCtx), new _RelNullNotifications(),
            new _RelNullPackResolver(), new StubRuleReader(), new _RelNullLetterService(), new _RelNullDocStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4), policySettings: policy);
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static async Task<PayrollRun> SeedProcessed(ZayraDbContext db, Guid tenantId, Guid processor)
    {
        var run = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 6, Status = "Processed", ProcessedByUserId = processor, CreatedByUserId = processor };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private static Task<IActionResult> Approve(PayrollController c, Guid runId) =>
        c.Approve(runId, new PayrollDecisionRequest("ok"), CancellationToken.None);

    [Fact]
    public async Task Default_a_payroll_manager_only_advances_to_finance_review()
    {
        var db = CreateDb(); var t = Guid.NewGuid(); var run = await SeedProcessed(db, t, Guid.NewGuid());
        (await Approve(Ctrl(db, t, Guid.NewGuid(), "Payroll Manager", new Policy(false, false)), run.Id)).Should().BeOfType<OkObjectResult>();
        (await db.PayrollRuns.FindAsync(run.Id))!.Status.Should().Be("PendingFinanceReview");
    }

    [Fact]
    public async Task Default_the_same_manager_cannot_give_the_second_level()
    {
        var db = CreateDb(); var t = Guid.NewGuid(); var me = Guid.NewGuid(); var run = await SeedProcessed(db, t, Guid.NewGuid());
        await Approve(Ctrl(db, t, me, "Payroll Manager", new Policy(false, false)), run.Id);
        db.ChangeTracker.Clear();
        (await Approve(Ctrl(db, t, me, "Payroll Manager", new Policy(false, false)), run.Id)).Should().BeOfType<BadRequestObjectResult>();
        (await db.PayrollRuns.FindAsync(run.Id))!.Status.Should().Be("PendingFinanceReview");
    }

    [Fact]
    public async Task Skip_review_on_lets_the_payroll_manager_finalise_and_records_it()
    {
        var db = CreateDb(); var t = Guid.NewGuid(); var me = Guid.NewGuid(); var run = await SeedProcessed(db, t, Guid.NewGuid());
        (await Approve(Ctrl(db, t, me, "Payroll Manager", new Policy(true, false)), run.Id)).Should().BeOfType<OkObjectResult>();
        (await db.PayrollRuns.FindAsync(run.Id))!.Status.Should().Be("Approved");
        (await db.PayrollApprovals.SingleAsync()).Notes.Should().Contain("allow_skip_review");
        (await db.PayrollAuditLogs.SingleAsync(a => a.Action == "payroll.run.approved")).MetadataJson.Should().Contain("allow_skip_review");
    }

    [Fact]
    public async Task Same_approver_on_lets_the_level_one_approver_give_the_second_level_and_records_it()
    {
        var db = CreateDb(); var t = Guid.NewGuid(); var me = Guid.NewGuid(); var run = await SeedProcessed(db, t, Guid.NewGuid());
        var policy = new Policy(false, true);
        await Approve(Ctrl(db, t, me, "Payroll Manager", policy), run.Id);
        db.ChangeTracker.Clear();
        (await Approve(Ctrl(db, t, me, "Payroll Manager", policy), run.Id)).Should().BeOfType<OkObjectResult>();
        (await db.PayrollRuns.FindAsync(run.Id))!.Status.Should().Be("Approved");
        (await db.PayrollApprovals.Where(a => a.ApprovalLevel == "FinanceReview").SingleAsync()).Notes.Should().Contain("allow_same_approver_multiple_levels");
    }

    [Fact]
    public async Task Same_approver_on_does_not_let_a_different_payroll_manager_finalise_nor_skip_review()
    {
        var db = CreateDb(); var t = Guid.NewGuid(); var run = await SeedProcessed(db, t, Guid.NewGuid());
        var policy = new Policy(false, true);
        // Skip is its own setting: with only same-approver on, level one still stops at finance review.
        await Approve(Ctrl(db, t, Guid.NewGuid(), "Payroll Manager", policy), run.Id);
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.FindAsync(run.Id))!.Status.Should().Be("PendingFinanceReview");
        (await Approve(Ctrl(db, t, Guid.NewGuid(), "Payroll Manager", policy), run.Id)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Skip_review_on_does_not_waive_the_same_approver_rule_and_never_waives_maker_checker()
    {
        var db = CreateDb(); var t = Guid.NewGuid(); var processor = Guid.NewGuid(); var run = await SeedProcessed(db, t, processor);
        (await Approve(Ctrl(db, t, processor, "Payroll Manager", new Policy(true, true)), run.Id))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }
}

file sealed class _RelUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
}

file sealed class _RelHttpAccessor : IHttpContextAccessor
{
    public _RelHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _RelNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _RelNullPackResolver : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new DefaultCountryPackDescriptor();
}

file sealed class _RelNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _RelNullDocStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, Microsoft.AspNetCore.Http.IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
