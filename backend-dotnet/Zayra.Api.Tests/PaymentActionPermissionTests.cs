using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests;

/// <summary>
/// M2 — narrow finance permission keys for the payment actions, replacing the reverted <c>payroll.export</c> widening.
///
/// <para><b>Why four keys.</b> <c>payroll.export</c> also opens the bank file's IBAN lines, the bulk payslip bundle, the register export and
/// the bank-confirmation screens, so giving it to the finance roles (the reverted change) was far wider than "pay the batch". Each key
/// here opens exactly one family of payment actions; <c>payroll.export</c> keeps working for every action it opened before, so nobody loses
/// anything and Admin is untouched.</para>
/// <list type="bullet">
/// <item><c>payroll.payment_batch.manage</c>: create the payment batch for a locked run, validate the wage file, generate and download
/// the bank or WPS file, read its export history.</item>
/// <item><c>payroll.wps_status.update</c>: record the file's lifecycle (submitted, accepted, rejected) and attach the bank evidence.</item>
/// <item><c>payroll.payment.settle</c>: post the net-pay settlement journal, record a payment made outside the bank file, and reverse either.</item>
/// <item><c>payroll.remit.manage</c>: remit a statutory or loan liability and reverse a remittance.</item>
/// </list>
///
/// <para><b>Who prepares, who pays (final design).</b> The Payroll Manager prepares the bank file: ONLY <c>payroll.payment_batch.manage</c>. The
/// Finance Approver pays and confirms: <c>payroll.wps_status.update</c>, <c>payroll.payment.settle</c>, <c>payroll.remit.manage</c>, and NOT the batch
/// key, so no single built-in role both prepares and pays. HR, the payroll officer, the loan operator and employees hold none of the four. At runtime a
/// caller who holds only the settle key cannot settle a batch they created or generated the file of (maker-checker).</para>
/// </summary>
public class PaymentActionPermissionTests
{
    private const string Batch = "payroll.payment_batch.manage";
    private const string WpsStatus = "payroll.wps_status.update";
    private const string Settle = "payroll.payment.settle";
    private const string Remit = "payroll.remit.manage";
    private static readonly string[] AllFour = { Batch, WpsStatus, Settle, Remit };

    // (action, the one narrow key that opens it)
    public static IEnumerable<object[]> Actions() => new[]
    {
        new object[] { "CreatePaymentBatch", Batch },
        new object[] { "WpsValidation", Batch },
        new object[] { "GenerateWps", Batch },
        new object[] { "DownloadWpsFile", Batch },
        new object[] { "WpsExportHistory", Batch },
        new object[] { "UpdateWpsStatus", WpsStatus },
        new object[] { "UploadWpsEvidence", WpsStatus },
        new object[] { "ListWpsEvidence", WpsStatus },
        new object[] { "SettlePaymentBatch", Settle },
        new object[] { "ReverseSettlement", Settle },
        new object[] { "RecordOutsidePayment", Settle },
        new object[] { "ReverseOutsidePayment", Settle },
        new object[] { "RemitStatutory", Remit },
        new object[] { "ReverseRemittance", Remit },
    };

    private static Task<IActionResult> Call(PayrollController c, string action) => action switch
    {
        "CreatePaymentBatch" => c.CreatePaymentBatch(Guid.NewGuid(), new PayrollPaymentBatchRequest(null, null), default),
        "WpsValidation" => c.WpsValidation(Guid.NewGuid(), default),
        "GenerateWps" => c.GenerateWps(Guid.NewGuid(), false, false, default),
        "DownloadWpsFile" => c.DownloadWpsFile(Guid.NewGuid(), default),
        "WpsExportHistory" => c.WpsExportHistory(Guid.NewGuid(), default),
        "UpdateWpsStatus" => c.UpdateWpsStatus(Guid.NewGuid(), new WpsStatusRequest("Generated", null), default),
        "UploadWpsEvidence" => c.UploadWpsEvidence(Guid.NewGuid(), new WpsEvidenceUploadForm(), default),
        "ListWpsEvidence" => c.ListWpsEvidence(Guid.NewGuid(), default),
        "SettlePaymentBatch" => c.SettlePaymentBatch(Guid.NewGuid(), new SettlePaymentBatchRequest(), default),
        "ReverseSettlement" => c.ReverseSettlement(Guid.NewGuid(), new PayrollReasonRequest("x"), default),
        "RecordOutsidePayment" => c.RecordOutsidePayment(Guid.NewGuid(), new OutsidePaymentRequest(1, "Cash", null), default),
        "ReverseOutsidePayment" => c.ReverseOutsidePayment(Guid.NewGuid(), 1, new PayrollReasonRequest("x"), default),
        "RemitStatutory" => c.RemitStatutory(Guid.NewGuid(), new RemitStatutoryRequest(), default),
        "ReverseRemittance" => c.ReverseRemittance(Guid.NewGuid(), new RemitReverseRequest(), default),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task TheNarrowKey_OpensItsOwnAction(string action, string key)
    {
        await using var k = await GlPostingKit.CreateAsync();
        (await Call(k.Payroll(permissions: key), action)).Should().NotBeOfType<ForbidResult>($"{key} opens {action}");
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task PayrollExport_StillOpensEveryAction_SoNobodyLosesAnything(string action, string _)
    {
        await using var k = await GlPostingKit.CreateAsync();
        (await Call(k.Payroll(permissions: "payroll.export"), action)).Should().NotBeOfType<ForbidResult>();
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task AKeyForAnotherFamily_OpensNothingHere(string action, string key)
    {
        await using var k = await GlPostingKit.CreateAsync();
        var others = AllFour.Where(x => x != key).ToArray();
        (await Call(k.Payroll(permissions: others), action)).Should().BeOfType<ForbidResult>(
            $"holding every payment key except {key} must not open {action}");
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task WithNoPaymentKey_EveryActionIsRefused(string action, string _)
    {
        await using var k = await GlPostingKit.CreateAsync();
        (await Call(k.Payroll(permissions: new[] { "payroll.read", "payroll.write", "payroll.approve", "payroll.lock" }), action))
            .Should().BeOfType<ForbidResult>();
    }

    // ── Who holds what, on a freshly seeded tenant ────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin", true, true, true, true)]
    [InlineData("Finance Approver", false, true, true, true)]
    [InlineData("Payroll Manager", true, false, false, false)]
    [InlineData("Payroll Officer", false, false, false, false)]
    [InlineData("HR Director", false, false, false, false)]
    [InlineData("HR Manager", false, false, false, false)]
    [InlineData("HR Officer", false, false, false, false)]
    [InlineData("Finance", false, false, false, false)]
    [InlineData("Manager", false, false, false, false)]
    [InlineData("Employee", false, false, false, false)]
    public async Task SeededRoles_HoldExactlyTheirKeys(string role, bool batch, bool wps, bool settle, bool remit)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase("pay-keys-" + Guid.NewGuid().ToString("N")).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "Pay", Slug = $"pk-{Guid.NewGuid():N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, CancellationToken.None);

        var held = (await db.Roles.Where(r => r.TenantId == tenantId && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync()).ToHashSet();
        held.Contains(Batch).Should().Be(batch, $"{role}: {Batch}");
        held.Contains(WpsStatus).Should().Be(wps, $"{role}: {WpsStatus}");
        held.Contains(Settle).Should().Be(settle, $"{role}: {Settle}");
        held.Contains(Remit).Should().Be(remit, $"{role}: {Remit}");
    }

    [Fact]
    public async Task TheFourKeysAreInTheCatalog_AndPrivileged()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase("pay-keys-cat-" + Guid.NewGuid().ToString("N")).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "Pay", Slug = $"pc-{Guid.NewGuid():N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, CancellationToken.None);

        var catalog = await db.Permissions.Select(p => p.Key).ToListAsync();
        catalog.Should().Contain(AllFour);
        // Money-moving keys are privileged: a holder must have signed in with a second factor.
        foreach (var key in AllFour) PrivilegedMfaPolicy.PrivilegedPermissions.Should().Contain(key);
    }
}


/// <summary>
/// M2 maker-checker: someone who created a payment batch or generated its bank file may not settle it with only the narrow settle key. payroll.export
/// holders (Admin) are exempt, exactly as before, so a single-admin tenant still works.
/// </summary>
public class PaymentSettleMakerCheckerTests
{
    private static async Task<(GlPostingKit K, Guid BatchId)> ArrangeAsync(Guid? createdBy, Guid? fileBy)
    {
        var k = await GlPostingKit.CreateAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 1_000m) }, Array.Empty<(int, string, string, decimal)>(), net: 1_000m);
        run.Status = "Locked";
        var batch = new Zayra.Api.Models.PayrollPaymentBatch
        {
            TenantId = k.TenantId, PayrollRunId = run.Id, BatchNumber = "PAY-RG-1", WpsStatus = Zayra.Api.Models.WpsStatuses.Accepted,
            TotalAmount = 1_000m, Currency = "SAR",
        };
        k.Db.PayrollPaymentBatches.Add(batch);
        if (createdBy is { } c)
            k.Db.PayrollAuditLogs.Add(new Zayra.Api.Models.PayrollAuditLog
            {
                TenantId = k.TenantId, Action = Zayra.Api.Infrastructure.Payroll.PaymentBatchExclusions.CreatedAuditAction,
                EntityName = "PayrollPaymentBatch", EntityId = batch.Id.ToString(), UserId = c,
            });
        if (fileBy is { } f)
            k.Db.WPSFileBatches.Add(new Zayra.Api.Models.WPSFileBatch
            {
                TenantId = k.TenantId, PaymentBatchId = batch.Id, GeneratedByUserId = f,
            });
        await k.Db.SaveChangesAsync();
        return (k, batch.Id);
    }

    private static int? Status(IActionResult r) => (r as ObjectResult)?.StatusCode ?? (r as StatusCodeResult)?.StatusCode;
    private static string? Error(IActionResult r) => (r as ObjectResult)?.Value?.GetType().GetProperty("error")?.GetValue((r as ObjectResult)!.Value) as string;

    [Fact]
    public async Task TheBatchCreator_WithOnlyTheSettleKey_IsRefused()
    {
        var maker = Guid.NewGuid();
        var (k, batchId) = await ArrangeAsync(createdBy: maker, fileBy: null);
        await using var _ = k;
        var res = await k.Payroll(userId: maker, permissions: "payroll.payment.settle").SettlePaymentBatch(batchId, new SettlePaymentBatchRequest(), default);
        Status(res).Should().Be(403);
        Error(res).Should().Be("settlement_needs_second_person");
    }

    [Fact]
    public async Task TheBankFileGenerator_WithOnlyTheSettleKey_IsRefused()
    {
        var maker = Guid.NewGuid();
        var (k, batchId) = await ArrangeAsync(createdBy: null, fileBy: maker);
        await using var _ = k;
        var res = await k.Payroll(userId: maker, permissions: "payroll.payment.settle").SettlePaymentBatch(batchId, new SettlePaymentBatchRequest(), default);
        Status(res).Should().Be(403);
        Error(res).Should().Be("settlement_needs_second_person");
    }

    [Fact]
    public async Task ASecondPerson_IsNotStoppedByTheMakerCheck()
    {
        var (k, batchId) = await ArrangeAsync(createdBy: Guid.NewGuid(), fileBy: Guid.NewGuid());
        await using var _ = k;
        var res = await k.Payroll(userId: Guid.NewGuid(), permissions: "payroll.payment.settle").SettlePaymentBatch(batchId, new SettlePaymentBatchRequest(), default);
        Error(res).Should().NotBe("settlement_needs_second_person");
    }

    [Fact]
    public async Task PayrollExportHolders_AreExempt_SoASingleAdminTenantStillWorks()
    {
        var maker = Guid.NewGuid();
        var (k, batchId) = await ArrangeAsync(createdBy: maker, fileBy: maker);
        await using var _ = k;
        var res = await k.Payroll(userId: maker, permissions: "payroll.export").SettlePaymentBatch(batchId, new SettlePaymentBatchRequest(), default);
        Error(res).Should().NotBe("settlement_needs_second_person");
    }

    // ── The other actions that post net pay against the batch follow the same duty split ─────────────

    private static Task<IActionResult> PostingAction(PayrollController c, string action, Guid batchId) => action switch
    {
        "RecordOutsidePayment" => c.RecordOutsidePayment(batchId, new OutsidePaymentRequest(1, "Cash", "RCPT-1"), default),
        "ReverseOutsidePayment" => c.ReverseOutsidePayment(batchId, 1, new PayrollReasonRequest("cheque returned"), default),
        "ReverseSettlement" => c.ReverseSettlement(batchId, new PayrollReasonRequest("bank recalled"), default),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    [Theory]
    [InlineData("RecordOutsidePayment")]
    [InlineData("ReverseOutsidePayment")]
    [InlineData("ReverseSettlement")]
    public async Task TheBatchCreator_WithOnlyTheSettleKey_CannotPostOtherNetPayActionsEither(string action)
    {
        var maker = Guid.NewGuid();
        var (k, batchId) = await ArrangeAsync(createdBy: maker, fileBy: null);
        await using var _ = k;
        var res = await PostingAction(k.Payroll(userId: maker, permissions: "payroll.payment.settle"), action, batchId);
        Status(res).Should().Be(403);
        Error(res).Should().Be("settlement_needs_second_person");
    }

    [Theory]
    [InlineData("RecordOutsidePayment")]
    [InlineData("ReverseOutsidePayment")]
    [InlineData("ReverseSettlement")]
    public async Task TheBankFileGenerator_WithOnlyTheSettleKey_CannotPostOtherNetPayActionsEither(string action)
    {
        var maker = Guid.NewGuid();
        var (k, batchId) = await ArrangeAsync(createdBy: null, fileBy: maker);
        await using var _ = k;
        var res = await PostingAction(k.Payroll(userId: maker, permissions: "payroll.payment.settle"), action, batchId);
        Error(res).Should().Be("settlement_needs_second_person");
    }

    [Theory]
    [InlineData("RecordOutsidePayment")]
    [InlineData("ReverseOutsidePayment")]
    [InlineData("ReverseSettlement")]
    public async Task ASecondPerson_IsNotStoppedByTheMakerCheck_OnOtherNetPayActions(string action)
    {
        var (k, batchId) = await ArrangeAsync(createdBy: Guid.NewGuid(), fileBy: Guid.NewGuid());
        await using var _ = k;
        var res = await PostingAction(k.Payroll(userId: Guid.NewGuid(), permissions: "payroll.payment.settle"), action, batchId);
        Error(res).Should().NotBe("settlement_needs_second_person");
    }

    [Theory]
    [InlineData("RecordOutsidePayment")]
    [InlineData("ReverseOutsidePayment")]
    [InlineData("ReverseSettlement")]
    public async Task PayrollExportHolders_AreExempt_OnOtherNetPayActions(string action)
    {
        var maker = Guid.NewGuid();
        var (k, batchId) = await ArrangeAsync(createdBy: maker, fileBy: maker);
        await using var _ = k;
        var res = await PostingAction(k.Payroll(userId: maker, permissions: "payroll.export"), action, batchId);
        Error(res).Should().NotBe("settlement_needs_second_person");
    }
}
