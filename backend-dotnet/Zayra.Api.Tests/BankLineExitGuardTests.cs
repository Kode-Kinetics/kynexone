using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Final gate security P2-D, the exits from a hold for a person:
/// <list type="bullet">
/// <item>a request carrying exits needs payroll.wps_status.update besides the import's payroll.export (the plain import does not);</item>
/// <item>no exit on a record whose employee is linked to the acting login (employee_user_accounts): 403 with its own code;</item>
/// <item>the reason is at most 500 characters and the bank reference at most 140 (the column limits): 400;</item>
/// <item>both are masked (IBAN- and ID-shaped text) before they reach the audit row.</item>
/// </list>
/// Synthetic data only.
/// </summary>
public class BankLineExitGuardTests
{
    private const string Iban = "SA0380000000608010167519";

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(Guid TenantId, PayrollPaymentBatch Batch, PayrollPaymentRecord Record);

    /// <summary>One Paid record (employee 7, W1) with an open "rejected after Paid" hold.</summary>
    private static async Task<World> SeedHeld(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, CompanyId = Guid.NewGuid(), Year = 2026, Month = 7, Status = "Locked" };
        var batch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id, BatchNumber = "PB-7", Currency = "SAR", TotalAmount = 100m };
        var record = new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = 7, Amount = 100m, Status = PaymentRecordStatuses.Paid,
            Iban = Iban, WpsReference = "W1",
        };
        db.AddRange(run, batch, record);
        await db.SaveChangesAsync();
        var w = new World(tenantId, batch, record);
        var (held, refusal) = await Service(db).ImportAsync(tenantId, batch.Id, "first.csv", Csv("2026-07-30"), "generic-csv",
            new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
        refusal.Should().BeNull();
        await db.SaveChangesAsync();
        held!.HeldForPerson.Should().ContainSingle();
        return w;
    }

    private static string Csv(string valueDate) =>
        $"wpsReference,amount,valueDate,status,reasonCode\nW1,100,{valueDate},REJECTED,AC04\n";

    private static BankConfirmationService Service(ZayraDbContext db) =>
        new(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() });

    private static BankConfirmationImportOptions Exit(World w, string reason = "Bank letter", string? reference = "BNK-1") =>
        new(Exits: new[] { new BankLineExit(BankLineExitKinds.AllowPaidToRejected, w.Record.Id, null, reason, reference) });

    private static Task<(BankConfirmationImportResult? Result, JournalExportRefusal? Refusal)> ExitImport(
        ZayraDbContext db, World w, BankConfirmationImportOptions options, Guid? actor) =>
        Service(db).ImportAsync(w.TenantId, w.Batch.Id, "letter.csv", Csv("2026-08-02"), "generic-csv", options, actor, "Finance", default);

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    // ── own record ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExitOnTheActorsOwnRecord_IsRefused403_AndAppliesNothing()
    {
        using var db = NewDb();
        var w = await SeedHeld(db);
        var actor = Guid.NewGuid();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = w.TenantId, EmployeeId = w.Record.EmployeeId, UserId = actor });
        await db.SaveChangesAsync();

        var (result, refusal) = await ExitImport(db, w, Exit(w), actor);

        result.Should().BeNull();
        refusal!.Status.Should().Be(403);
        Json(refusal.Payload).Should().Contain("exit_on_own_payment_record");
        db.ChangeTracker.HasChanges().Should().BeFalse();
        w.Record.Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    [Fact]
    public async Task AnExitOnSomeoneElsesRecord_IsAccepted()
    {
        using var db = NewDb();
        var w = await SeedHeld(db);
        var actor = Guid.NewGuid();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = w.TenantId, EmployeeId = 99, UserId = actor });
        await db.SaveChangesAsync();

        var (result, refusal) = await ExitImport(db, w, Exit(w), actor);

        refusal.Should().BeNull();
        result!.Overrides.Should().ContainSingle();
    }

    // ── caps ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(501, 10, "exit_reason_too_long")]
    [InlineData(10, 141, "exit_bank_reference_too_long")]
    public async Task AnOverlongReasonOrBankReference_IsRefused400(int reasonLength, int referenceLength, string code)
    {
        using var db = NewDb();
        var w = await SeedHeld(db);

        var (result, refusal) = await ExitImport(db, w, Exit(w, new string('r', reasonLength), new string('B', referenceLength)), Guid.NewGuid());

        result.Should().BeNull();
        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain(code);
    }

    [Fact]
    public async Task AReasonOf500AndAReferenceOf140_AreAccepted()
    {
        using var db = NewDb();
        var w = await SeedHeld(db);

        var (_, refusal) = await ExitImport(db, w, Exit(w, new string('r', 500), new string('B', 140)), Guid.NewGuid());

        refusal.Should().BeNull();
    }

    // ── masking ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheReasonAndBankReference_AreMaskedBeforeTheAuditRow()
    {
        using var db = NewDb();
        var w = await SeedHeld(db);

        var (_, refusal) = await ExitImport(db, w, Exit(w, $"Statement for {Iban} shows no debit", $"REF {Iban}"), Guid.NewGuid());
        refusal.Should().BeNull();
        await db.SaveChangesAsync();

        var audit = db.PayrollAuditLogs.AsEnumerable().Single(a => a.MetadataJson!.Contains(BankLineExitKinds.AllowPaidToRejected));
        audit.MetadataJson.Should().NotContain(Iban).And.Contain("Statement for").And.Contain("shows no debit");
    }

    // ── the import endpoint: exits need the payment-status permission ────────

    private static BankConfirmationsController Controller(ZayraDbContext db, World w, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Finance"), new("is_group_scope", "true"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new BankConfirmationsController(db, Service(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
    }

    private static async Task<IActionResult> Post(BankConfirmationsController ctrl, World w, bool withExit)
    {
        var payload = JsonSerializer.Serialize(new
        {
            fileName = "letter.json",
            rows = new[] { new { wpsReference = "W1", amount = 100m, status = "REJECTED", reasonCode = "AC04", valueDate = "2026-08-03" } },
            exits = withExit ? new[] { new { kind = BankLineExitKinds.AllowPaidToRejected, paymentRecordId = w.Record.Id, reason = "Bank letter", bankReference = "BNK-1" } } : null,
        });
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        ctrl.ControllerContext.HttpContext.Request.ContentType = "application/json";
        return await ctrl.Import(w.Batch.Id, ct: default);
    }

    [Fact]
    public async Task TheImportPermissionAlone_CannotCarryAnExit()
    {
        using var db = NewDb();
        var w = await SeedHeld(db);

        var result = Assert.IsAssignableFrom<ObjectResult>(await Post(Controller(db, w, "payroll.export"), w, withExit: true));

        result.StatusCode.Should().Be(403);
        Json(result.Value).Should().Contain("exit_needs_payment_status_permission");
        w.Record.Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    [Fact]
    public async Task TheImportPermissionAlone_StillImportsWithoutAnExit()
    {
        using var db = NewDb();
        var w = await SeedHeld(db);

        var result = Assert.IsAssignableFrom<ObjectResult>(await Post(Controller(db, w, "payroll.export"), w, withExit: false));

        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task ImportAndPaymentStatusPermissions_CarryTheExit()
    {
        using var db = NewDb();
        var w = await SeedHeld(db);

        var result = Assert.IsAssignableFrom<ObjectResult>(
            await Post(Controller(db, w, "payroll.export", "payroll.wps_status.update"), w, withExit: true));

        result.StatusCode.Should().Be(200);
        w.Record.Status.Should().Be(PaymentRecordStatuses.Rejected);
    }
}
