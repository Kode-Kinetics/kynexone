using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// D3 (round 2) — the batch endpoints that live on <c>PayrollController</c>.
///
/// <para>THE GAP. D3's first round authorised the two endpoints on <c>BankConfirmationsController</c>
/// and stopped there, but its stated outcome is that Company A must not <i>import, inspect, reverse,
/// acknowledge or otherwise affect</i> Company B's payment batch — and nine more batch-specific routes
/// hang off <c>PayrollController</c>. <c>PayrollPaymentBatch</c>, <c>PayrollPaymentRecord</c>,
/// <c>FinanceGlEntry</c> and <c>SIFFileRecord</c> are <c>ITenantOwned</c> ONLY, so no ambient company
/// filter reached any of them.</para>
///
/// <para>Concretely, before this: <c>GET payment-batches/{id}/records</c> returned another entity's
/// per-employee amounts, WPS references, bank references and — with <c>payroll.export</c> — full
/// unmasked IBANs; <c>GET payment-batches</c> listed every legal entity's batch totals; and
/// <c>settle/reverse</c> reversed another company's net-pay settlement GL. The run-status guards were
/// no defence: they read <c>PayrollRun</c>, which IS company-filtered, so cross-company the run came
/// back null and <c>run?.Status == "Voided"</c> quietly skipped the refusal — failing OPEN.</para>
///
/// <para>All of them now derive the entity the same way the bank-confirmation endpoints do, through the
/// one shared <see cref="PaymentBatchScopeExtensions.PaymentBatchScopeErrorAsync"/>.</para>
/// </summary>
public class PaymentBatchScopeTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <param name="scopedCompany">null ⇒ an explicitly group-scoped token.</param>
    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId, Guid? scopedCompany)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Payroll Officer"),
            new("permission", "payroll.export"),
        };
        if (scopedCompany is Guid c)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { c.ToString() } })));
        else
            claims.Add(new Claim("is_group_scope", "true"));

        var httpCtx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        var rules = new StubRuleReader();
        var ctrl = new PayrollController(
            db,
            new _PbUnrestrictedScope(),
            new _PbHttpAccessor(httpCtx),
            new _PbNullNotifications(),
            new _PbPackResolver(rules),
            rules,
            new _PbNullLetterService(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    /// <summary>A run owned by <paramref name="companyId"/> (null = a legacy, pre-company-dimension run)
    /// and a payment batch hanging off it, with one payment record carrying a real IBAN.</summary>
    private static (Guid BatchId, Guid RunId) SeedBatch(ZayraDbContext db, Guid tenantId, Guid? companyId)
    {
        var run = new PayrollRun
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId,
            Year = 2026, Month = 7, Status = "Locked",
        };
        var batch = new PayrollPaymentBatch
        {
            Id = Guid.NewGuid(), TenantId = tenantId, PayrollRunId = run.Id,
            BatchNumber = "PB-2026-07", PaymentMethod = "WPS", TotalAmount = 50_000m,
            Currency = "SAR", Status = "Pending", WpsStatus = WpsStatuses.Accepted,
        };
        db.PayrollRuns.Add(run);
        db.PayrollPaymentBatches.Add(batch);
        db.PayrollPaymentRecords.Add(new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = 1,
            Amount = 50_000m, Status = "Pending", Iban = "SA0380000000608010167519",
            WpsReference = "WPS-REF-1",
        });
        db.SaveChanges();
        return (batch.Id, run.Id);
    }

    // ── Own company: allowed ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OwnCompanyBatch_PaymentRecords_AreReturned()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var (batchId, _) = SeedBatch(db, tenantId, companyA);

        var result = await MakeCtrl(db, tenantId, companyA).PaymentRecords(batchId, default);

        var rows = result.Should().BeOfType<OkObjectResult>().Subject.Value as System.Collections.IEnumerable;
        rows!.Cast<object>().Should().ContainSingle("the caller's own entity's records are theirs to read");
    }

    // ── Cross company: denied, on every route ─────────────────────────────────────────────────────

    [Fact]
    public async Task CrossCompanyBatch_PaymentRecords_AreDenied_AndNoIbanIsDisclosed()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var (batchId, _) = SeedBatch(db, tenantId, companyB);

        var result = await MakeCtrl(db, tenantId, Guid.NewGuid()).PaymentRecords(batchId, default);

        result.Should().BeOfType<ForbidResult>(
            "this is the route that returned another entity's per-employee amounts and unmasked IBANs");
    }

    [Fact]
    public async Task CrossCompanyBatch_ReverseSettlement_IsDenied_AndPostsNoContraGl()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var (batchId, runId) = SeedBatch(db, tenantId, companyB);
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = companyB, SourceModule = "Payroll", SourceEntityId = runId,
            EventType = GlEventTypes.NetSettlement, DebitAccount = "2100", CreditAccount = "1000",
            Amount = 50_000m, Currency = "SAR", EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07",
        });
        await db.SaveChangesAsync();

        var result = await MakeCtrl(db, tenantId, Guid.NewGuid())
            .ReverseSettlement(batchId, new PayrollReasonRequest("unwind"), default);

        result.Should().BeOfType<ForbidResult>();
        (await db.FinanceGlEntries.AsNoTracking().CountAsync())
            .Should().Be(1, "a denied reversal must post no contra entry");
        (await db.FinanceGlEntries.AsNoTracking().SingleAsync()).IsReversed
            .Should().BeFalse("and must not mark the original reversed");
    }

    [Fact]
    public async Task CrossCompanyBatch_WpsStatus_IsDenied_AndMutatesNothing()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var (batchId, _) = SeedBatch(db, tenantId, companyB);

        var result = await MakeCtrl(db, tenantId, Guid.NewGuid())
            .UpdateWpsStatus(batchId, new WpsStatusRequest(WpsStatuses.Paid, "ref", "notes"), default);

        result.Should().BeOfType<ForbidResult>();
        (await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.Id == batchId))
            .WpsStatus.Should().Be(WpsStatuses.Accepted, "the batch status must be untouched");
    }

    [Fact]
    public async Task CrossCompanyBatch_WpsExportHistory_IsDenied()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (batchId, _) = SeedBatch(db, tenantId, Guid.NewGuid());

        (await MakeCtrl(db, tenantId, Guid.NewGuid()).WpsExportHistory(batchId, default))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task CrossCompanyBatch_WpsFileDownload_IsDenied()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (batchId, _) = SeedBatch(db, tenantId, Guid.NewGuid());

        (await MakeCtrl(db, tenantId, Guid.NewGuid()).DownloadWpsFile(batchId, default))
            .Should().BeOfType<ForbidResult>();
    }

    // ── The list is scoped, not merely the single-batch reads ─────────────────────────────────────

    [Fact]
    public async Task ListPaymentBatches_ShowsOnlyTheCallersOwnEntities()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var (mine, _) = SeedBatch(db, tenantId, companyA);
        SeedBatch(db, tenantId, Guid.NewGuid());   // a sibling legal entity
        SeedBatch(db, tenantId, null);             // a legacy, unattributed run

        var result = await MakeCtrl(db, tenantId, companyA).ListPaymentBatches(null, default);

        var rows = (result.Should().BeOfType<OkObjectResult>().Subject.Value as System.Collections.IEnumerable)!
            .Cast<object>()
            .Select(r => JsonDocument.Parse(JsonSerializer.Serialize(r)).RootElement)
            .ToList();
        rows.Should().ContainSingle("a company-scoped caller sees neither a sibling entity's batch nor a legacy one");
        rows[0].GetProperty("Id").GetGuid().Should().Be(mine);
    }

    [Fact]
    public async Task ListPaymentBatches_ShowsEverythingToAGroupCaller()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        SeedBatch(db, tenantId, Guid.NewGuid());
        SeedBatch(db, tenantId, Guid.NewGuid());
        SeedBatch(db, tenantId, null);

        var result = await MakeCtrl(db, tenantId, null).ListPaymentBatches(null, default);

        ((result.Should().BeOfType<OkObjectResult>().Subject.Value as System.Collections.IEnumerable)!)
            .Cast<object>().Should().HaveCount(3);
    }

    // ── Legacy null-company runs are group-only ───────────────────────────────────────────────────

    [Fact]
    public async Task NullCompanyBatch_IsDeniedToASelectedCompanyCaller()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (batchId, _) = SeedBatch(db, tenantId, null);

        (await MakeCtrl(db, tenantId, Guid.NewGuid()).PaymentRecords(batchId, default))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task NullCompanyBatch_IsAllowedToAnExplicitGroupCaller()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (batchId, _) = SeedBatch(db, tenantId, null);

        (await MakeCtrl(db, tenantId, null).PaymentRecords(batchId, default))
            .Should().BeOfType<OkObjectResult>();
    }

    // ── Fail-closed on broken ownership ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ABatchWhoseRunIsMissing_FailsClosed()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var batch = new PayrollPaymentBatch
        {
            Id = Guid.NewGuid(), TenantId = tenantId, PayrollRunId = Guid.NewGuid(),
            BatchNumber = "PB-ORPHAN", PaymentMethod = "WPS", TotalAmount = 1m, Currency = "SAR",
            Status = "Pending", WpsStatus = WpsStatuses.Draft,
        };
        db.PayrollPaymentBatches.Add(batch);
        await db.SaveChangesAsync();

        var result = await MakeCtrl(db, tenantId, null).PaymentRecords(batch.Id, default);

        // Refused rather than guessed: an unresolvable entity is a data-integrity fault, not an allowance.
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ACrossTenantBatch_IsNotFound_RatherThanForbidden()
    {
        using var db = NewDb();
        var theirs = Guid.NewGuid();
        var (batchId, _) = SeedBatch(db, theirs, Guid.NewGuid());

        var result = await MakeCtrl(db, Guid.NewGuid(), null).PaymentRecords(batchId, default);

        // 404, not 403 — a 403 would confirm the batch exists in another tenant.
        result.Should().BeOfType<NotFoundResult>();
    }

    // ── The durable ratchet: no batch route may ship without the guard ────────────────────────────

    /// <summary>
    /// A source-level ratchet, because the failure mode here is a NEW endpoint added later without the
    /// check — exactly how the nine routes above came to be tenant-only in the first place.
    ///
    /// <para>The first version of this test had a FALSE PASS, found by independent review: it bounded an
    /// action's body at the next <c>[Http</c> attribute, so for the LAST action in a file the region ran
    /// to end-of-file and swallowed the private helper whose name it greps for. Deleting the real guard
    /// in <c>BankConfirmationsController</c> left it green. It now brace-matches the actual method body,
    /// and discovers controllers across the whole tree instead of trusting two hard-coded paths.</para>
    /// </summary>
    [Fact]
    public void EveryBatchSpecificRoute_CallsTheSharedCompanyGuard()
    {
        var controllers = Directory.GetFiles(
            Path.Combine(FindApiRoot(), "Controllers"), "*.cs", SearchOption.AllDirectories);
        controllers.Should().NotBeEmpty("the scan must actually find controller sources");

        var checkedRoutes = new List<string>();
        var missing = new List<string>();

        foreach (var file in controllers)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsBatchSpecificRoute(lines, i)) continue;
                var rel = Path.GetFileName(file);
                var route = lines[i].Trim();
                checkedRoutes.Add($"{rel} {route}");
                var body = MethodBodyAfter(lines, i);
                if (!body.Contains("PaymentBatchScopeErrorAsync") && !body.Contains("BatchScopeErrorAsync"))
                    missing.Add($"{rel}:{i + 1} {route}");
            }
        }

        // Guard the guard: if the scan silently stops matching (a renamed route segment, a reformatted
        // attribute), an empty result would look like success.
        checkedRoutes.Should().HaveCountGreaterThanOrEqualTo(9,
            "the known batch-specific routes must all still be discovered by this scan; found:\n"
            + string.Join('\n', checkedRoutes));

        missing.Should().BeEmpty(
            "every batch-specific endpoint must derive the legal entity from batch -> run -> "
            + "PayrollRun.CompanyId before reading or mutating anything. Missing:\n"
            + string.Join('\n', missing));
    }

    /// <summary>True when line <paramref name="i"/> begins an action whose route carries a batch id.
    /// Tolerates the template sitting on the following line (a wrapped attribute).</summary>
    private static bool IsBatchSpecificRoute(string[] lines, int i)
    {
        if (!lines[i].Contains("[Http")) return false;
        var window = lines[i] + (i + 1 < lines.Length ? lines[i + 1] : string.Empty);
        return window.Contains("payment-batches/{");
    }

    /// <summary>
    /// The action's real body, by brace matching from the first '{' after the attribute — not "up to the
    /// next attribute", which runs to EOF for the last action in a file.
    /// </summary>
    private static string MethodBodyAfter(string[] lines, int attributeLine)
    {
        var i = attributeLine;
        // Skip the attribute block and the signature to the body's opening brace. The attribute line
        // ITSELF contains braces, from the route template ("{batchId:guid}") — matching on those made the
        // second version of this scan stop dead on the attribute and report every route as unguarded.
        // The body opener is a line that is exactly "{".
        while (i < lines.Length && lines[i].Trim() != "{") i++;
        if (i >= lines.Length) return string.Empty;

        var depth = 0;
        var body = new System.Text.StringBuilder();
        for (; i < lines.Length; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') depth++;
                else if (ch == '}') depth--;
            }
            body.AppendLine(lines[i]);
            if (depth <= 0) break;
        }
        return body.ToString();
    }

    private static string FindApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api", "Controllers")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to locate the Zayra.Api source tree");
        return Path.Combine(dir!.FullName, "Zayra.Api");
    }
}

file sealed class _PbUnrestrictedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(
        System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _PbHttpAccessor : IHttpContextAccessor
{
    public _PbHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _PbNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _PbNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _PbPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _PbPackResolver(StubRuleReader rules) => _rules = rules;
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
}
