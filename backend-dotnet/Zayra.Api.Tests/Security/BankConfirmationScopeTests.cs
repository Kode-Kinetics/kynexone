using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// D3 — every batch endpoint must authorise the caller against the batch's legal entity.
///
/// <para>THE DEFECT. <c>BankConfirmationsController</c> checked the tenant and a permission and stopped.
/// <c>PayrollPaymentBatch</c>, <c>PayrollPaymentRecord</c> and <c>BankPaymentConfirmation</c> are
/// <c>ITenantOwned</c> only, so no ambient company filter caught it either. A Company-A payroll officer
/// could import a bank response against a Company-B batch — flipping that company's per-employee payment
/// statuses to Paid or Returned — and read its totals, per-employee amounts and bank references.</para>
///
/// <para>The company is derived from <c>batch → payroll run → PayrollRun.CompanyId</c>, never from the
/// request, because <c>PayrollRun</c> is the only <c>ICompanyScopedOperational</c> carrier on this path.</para>
/// </summary>
public class BankConfirmationScopeTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static BankConfirmationsController MakeCtrl(
        ZayraDbContext db, Guid tenantId, Guid? scopedCompany, string? body = null)
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

        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (body is not null)
        {
            httpCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            httpCtx.Request.ContentType = "text/csv";
        }

        return new BankConfirmationsController(db, new BankConfirmationService(db, [new GenericCsvBankConfirmationParser()]))
        {
            ControllerContext = new ControllerContext { HttpContext = httpCtx },
        };
    }

    /// <summary>Seeds a run (the company carrier) and a batch that belongs to it.</summary>
    private static Guid SeedBatch(ZayraDbContext db, Guid tenantId, Guid? companyId)
    {
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 7, Status = "Locked",
        };
        db.PayrollRuns.Add(run);
        var batch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id };
        db.PayrollPaymentBatches.Add(batch);
        db.SaveChanges();
        return batch.Id;
    }

    // ── Positive ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_ForOwnCompanyBatch_IsAllowed()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var batchId = SeedBatch(db, tenantId, mine);

        var result = await MakeCtrl(db, tenantId, scopedCompany: mine).History(batchId, default);

        result.Should().NotBeOfType<ForbidResult>("a caller may always work with their own entity's batch");
        result.Should().NotBeOfType<NotFoundResult>();
    }

    // ── Negative: cross-company ───────────────────────────────────────────────

    [Fact]
    public async Task History_ForAnotherCompanysBatch_IsDenied()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var batchId = SeedBatch(db, tenantId, theirs);

        var result = await MakeCtrl(db, tenantId, scopedCompany: Guid.NewGuid()).History(batchId, default);

        result.Should().BeOfType<ForbidResult>(
            "reading another entity's batch discloses its totals, per-employee amounts and bank references");
    }

    [Fact]
    public async Task Import_ForAnotherCompanysBatch_IsDenied_AndMutatesNothing()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var batchId = SeedBatch(db, tenantId, theirs);

        var ctrl = MakeCtrl(db, tenantId, scopedCompany: Guid.NewGuid(), body: "employeeCode,amount,status\nE1,100,Paid\n");
        var result = await ctrl.Import(batchId);

        result.Should().BeOfType<ForbidResult>(
            "importing a bank response against another entity's batch would flip that company's payment statuses");
        db.BankPaymentConfirmations.Should().BeEmpty("a denied import must mutate nothing");
    }

    [Fact]
    public async Task Import_IsRefusedBeforeTheUploadedPayloadIsEvenRead()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var batchId = SeedBatch(db, tenantId, theirs);

        var ctrl = MakeCtrl(db, tenantId, scopedCompany: Guid.NewGuid(), body: "employeeCode,amount,status\nE1,100,Paid\n");
        var result = await ctrl.Import(batchId);

        result.Should().BeOfType<ForbidResult>();
        // If authorization had run after ReadPayloadAsync, the body would be drained. An unauthorised
        // caller must not get their file consumed, parsed or matched.
        ctrl.HttpContext.Request.Body.Position.Should().Be(0,
            "authorization must happen BEFORE the uploaded body is read");
    }

    // ── Legacy null-company runs are group-only ───────────────────────────────

    [Fact]
    public async Task NullCompanyBatch_IsDeniedToACompanyScopedCaller()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var batchId = SeedBatch(db, tenantId, companyId: null);

        var result = await MakeCtrl(db, tenantId, scopedCompany: Guid.NewGuid()).History(batchId, default);

        result.Should().BeOfType<ForbidResult>(
            "an unattributed legacy run belongs to no entity the caller can claim — same rule as D2");
    }

    [Fact]
    public async Task NullCompanyBatch_IsAllowedToAnExplicitGroupCaller()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var batchId = SeedBatch(db, tenantId, companyId: null);

        var result = await MakeCtrl(db, tenantId, scopedCompany: null).History(batchId, default);

        result.Should().NotBeOfType<ForbidResult>(
            "a group caller must still be able to reconcile legacy pre-company-dimension runs");
    }

    // ── Fail closed on broken ownership ───────────────────────────────────────

    [Fact]
    public async Task BatchWhoseRunIsMissing_FailsClosed()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        // A batch pointing at a run that does not exist: the legal entity cannot be established.
        db.PayrollPaymentBatches.Add(new PayrollPaymentBatch
        {
            TenantId = tenantId, PayrollRunId = Guid.NewGuid(),
        });
        db.SaveChanges();
        var batchId = db.PayrollPaymentBatches.Single().Id;

        var result = await MakeCtrl(db, tenantId, scopedCompany: null).History(batchId, default);

        result.Should().BeOfType<NotFoundObjectResult>(
            "inconsistent ownership must be refused, never guessed into an authorisation");
    }

    [Fact]
    public async Task BatchFromAnotherTenant_IsNotFound_NotForbidden()
    {
        using var db = NewDb();
        var mine = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var batchId = SeedBatch(db, otherTenant, Guid.NewGuid());

        var result = await MakeCtrl(db, mine, scopedCompany: null).History(batchId, default);

        result.Should().BeOfType<NotFoundResult>(
            "a cross-tenant probe must not disclose that the batch exists at all");
    }
}
