using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F10 — final-settlement maker/checker. Approving a settlement posts the accrual journal that makes
/// it a real liability, so whoever produced the figures (computed or recomputed it) or put them forward
/// (submitted it) can never also be the one who signs them off. There used to be an
/// <c>acknowledgeSelfApproval</c> escape that let the creator approve their own settlement by ticking
/// a box, and the submitter or a recomputer was never checked at all.
/// </summary>
public class FinalSettlementMakerCheckerTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    internal static PayrollController Controller(ZayraDbContext db, Guid tenantId, Guid? userId, string name = "Approver")
    {
        var rules = PayComponentNetPayDefectTests.KsaRules();
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            new F2NullNotifications(), new KsaTestPackResolver(rules), rules,
            new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Role, "Payroll Manager"),
            new("permission", "payroll.read"),
            new("permission", "payroll.write"),
            new("permission", "payroll.approve"),
        };
        if (userId is Guid id) claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return ctrl;
    }

    /// <summary>
    /// The request exactly as a browser sends it. Parsed from JSON so the retired
    /// <c>acknowledgeSelfApproval</c> flag can still be sent: an older client (the frontend deploys ahead
    /// of the backend) must not be able to reopen the escape by sending it.
    /// </summary>
    internal static ApproveFinalSettlementRequest ApproveBody(bool sendOldSelfApprovalFlag = false) =>
        JsonSerializer.Deserialize<ApproveFinalSettlementRequest>(
            sendOldSelfApprovalFlag
                ? """{"confirmTerminationReason":"Termination","acknowledgeSelfApproval":true}"""
                : """{"confirmTerminationReason":"Termination"}""",
            Web)!;

    internal sealed record SettlementFixture(Guid TenantId, Guid CompanyId, EmployeeFinalSettlement Settlement);

    /// <summary>
    /// A PendingApproval settlement ready to approve in every other respect: the reason is confirmed by
    /// <see cref="ApproveBody"/>, the wage side is paid (WagesPaidByRunId), there is no wage-base delta,
    /// no overlapping settlement, no accrual yet, and one gratuity earning line.
    /// </summary>
    internal static async Task<SettlementFixture> SeedPendingSettlementAsync(
        ZayraDbContext db, Guid createdBy, Guid? submittedBy, decimal gross = 12_000m)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Maker Checker Tenant", Slug = $"mc-{Guid.NewGuid():N}" };
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, LegalNameEn = "Maker Checker KSA",
            RegistrationNumber = $"MC-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true,
        };
        var employee = new Employee
        {
            TenantId = tenant.Id, CompanyId = company.Id, EmployeeCode = $"MC{Guid.NewGuid():N}"[..12],
            FullName = "Leaving Person", Status = EmployeeStatuses.Offboarded,
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.AddRange(tenant, company, employee);
        await db.SaveChangesAsync();

        var lastDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenant.Id, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, SeparationType = "Termination", Status = "InProgress",
            NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = tenant.Id, CompanyId = company.Id, EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName, OffboardingId = offboarding.Id,
            LastWorkingDay = lastDay, ServiceStartDate = new DateOnly(2020, 1, 1), SettlementDueDate = lastDay.AddDays(7),
            TerminationReason = "Termination", ServiceYears = 6m, Currency = "SAR",
            GratuityAmount = gross, GrossPayable = gross, TotalDeductions = 0m, NetPayable = gross,
            WagesPaidByRunId = Guid.NewGuid(), WageBaseDeltaAmount = 0m,
            Status = submittedBy is null ? FinalSettlementStatuses.Draft : FinalSettlementStatuses.PendingApproval,
            CreatedByUserId = createdBy, CreatedByName = "Maker",
            SubmittedByUserId = submittedBy, SubmittedByName = submittedBy is null ? null : "Submitter",
            SubmittedAtUtc = submittedBy is null ? null : DateTime.UtcNow,
        };
        db.EmployeeOffboardings.Add(offboarding);
        db.EmployeeFinalSettlements.Add(settlement);
        db.FinalSettlementLines.Add(new FinalSettlementLine
        {
            TenantId = tenant.Id, SettlementId = settlement.Id,
            ComponentCode = FinalSettlementComponents.Gratuity, ComponentName = "End-of-service gratuity",
            LineType = FinalSettlementLineTypes.Earning, Source = FinalSettlementComponents.SettlementSource,
            Amount = gross, SortOrder = 0,
        });
        // The audit trail the real endpoints leave behind: who computed it, and who submitted it.
        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tenant.Id, Action = "payroll.final_settlement.drafted", EntityName = "EmployeeFinalSettlement",
            EntityId = settlement.Id.ToString(), UserId = createdBy,
        });
        if (submittedBy is Guid sub)
            db.PayrollAuditLogs.Add(new PayrollAuditLog
            {
                TenantId = tenant.Id, Action = "payroll.final_settlement.submitted", EntityName = "EmployeeFinalSettlement",
                EntityId = settlement.Id.ToString(), UserId = sub,
            });
        await db.SaveChangesAsync();
        return new SettlementFixture(tenant.Id, company.Id, settlement);
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static async Task AssertUntouchedAsync(ZayraDbContext db, EmployeeFinalSettlement settlement, string expectedStatus)
    {
        db.ChangeTracker.Clear();
        var stored = await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(x => x.Id == settlement.Id);
        stored.Status.Should().Be(expectedStatus);
        stored.ApprovedByUserId.Should().BeNull();
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == settlement.Id))
            .Should().BeFalse("a refused approval must not post any accrual");
    }

    [Fact]
    public async Task Creator_CannotApprove_EvenWhenAnOldClientSendsTheSelfApprovalAcknowledgement()
    {
        await using var db = CreateDb();
        var maker = Guid.NewGuid();
        var fx = await SeedPendingSettlementAsync(db, createdBy: maker, submittedBy: Guid.NewGuid());

        var result = await Controller(db, fx.TenantId, maker)
            .ApproveFinalSettlement(fx.Settlement.Id, ApproveBody(sendOldSelfApprovalFlag: true), CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        Json(conflict.Value).Should().Contain("segregation_of_duties").And.Contain("calculated");
        await AssertUntouchedAsync(db, fx.Settlement, FinalSettlementStatuses.PendingApproval);
    }

    [Fact]
    public async Task Submitter_CannotApprove_WhatTheySubmitted()
    {
        await using var db = CreateDb();
        var submitter = Guid.NewGuid();
        var fx = await SeedPendingSettlementAsync(db, createdBy: Guid.NewGuid(), submittedBy: submitter);

        var result = await Controller(db, fx.TenantId, submitter)
            .ApproveFinalSettlement(fx.Settlement.Id, ApproveBody(), CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        Json(conflict.Value).Should().Contain("segregation_of_duties").And.Contain("submitted");
        await AssertUntouchedAsync(db, fx.Settlement, FinalSettlementStatuses.PendingApproval);
    }

    /// <summary>
    /// A Draft or PendingApproval settlement is recomputed IN PLACE by POST /final-settlement, which keeps
    /// the original CreatedByUserId. The person who recomputed it produced the figures being approved, and
    /// the audit trail is the only record of that, so the check reads it.
    /// </summary>
    [Fact]
    public async Task Recomputer_CannotApprove_TheFiguresTheyProduced()
    {
        await using var db = CreateDb();
        var recomputer = Guid.NewGuid();
        var fx = await SeedPendingSettlementAsync(db, createdBy: Guid.NewGuid(), submittedBy: Guid.NewGuid());
        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = fx.TenantId, Action = "payroll.final_settlement.drafted", EntityName = "EmployeeFinalSettlement",
            EntityId = fx.Settlement.Id.ToString(), UserId = recomputer,
        });
        await db.SaveChangesAsync();

        var result = await Controller(db, fx.TenantId, recomputer)
            .ApproveFinalSettlement(fx.Settlement.Id, ApproveBody(), CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        Json(conflict.Value).Should().Contain("segregation_of_duties");
        await AssertUntouchedAsync(db, fx.Settlement, FinalSettlementStatuses.PendingApproval);
    }

    [Fact]
    public async Task Creator_CannotApprove_ADraftTheyNeverSubmitted()
    {
        await using var db = CreateDb();
        var maker = Guid.NewGuid();
        var fx = await SeedPendingSettlementAsync(db, createdBy: maker, submittedBy: null);

        var result = await Controller(db, fx.TenantId, maker)
            .ApproveFinalSettlement(fx.Settlement.Id, ApproveBody(sendOldSelfApprovalFlag: true), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        await AssertUntouchedAsync(db, fx.Settlement, FinalSettlementStatuses.Draft);
    }

    /// <summary>With no user identity there is nobody to check against — and nobody to record — so it is refused.</summary>
    [Fact]
    public async Task Approval_WithNoUserIdentity_IsForbidden()
    {
        await using var db = CreateDb();
        var fx = await SeedPendingSettlementAsync(db, createdBy: Guid.NewGuid(), submittedBy: Guid.NewGuid());

        var result = await Controller(db, fx.TenantId, userId: null)
            .ApproveFinalSettlement(fx.Settlement.Id, ApproveBody(), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        await AssertUntouchedAsync(db, fx.Settlement, FinalSettlementStatuses.PendingApproval);
    }

    [Fact]
    public async Task IndependentApprover_Approves_AndTheAuditNamesMakerSubmitterAndApprover()
    {
        await using var db = CreateDb();
        var maker = Guid.NewGuid();
        var submitter = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var fx = await SeedPendingSettlementAsync(db, createdBy: maker, submittedBy: submitter);

        var result = await Controller(db, fx.TenantId, approver, name: "Finance Checker")
            .ApproveFinalSettlement(fx.Settlement.Id, ApproveBody(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var stored = await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(x => x.Id == fx.Settlement.Id);
        stored.Status.Should().Be(FinalSettlementStatuses.Approved);
        stored.ApprovedByUserId.Should().Be(approver);
        stored.ApprovedByName.Should().Be("Finance Checker");
        (await db.FinanceGlEntries.CountAsync(x => x.SourceEntityId == fx.Settlement.Id
            && x.EventType == GlEventTypes.SettlementAccrual && !x.IsReversed)).Should().BeGreaterThan(0);

        var audit = await db.PayrollAuditLogs.AsNoTracking()
            .SingleAsync(x => x.Action == "payroll.final_settlement.approved" && x.EntityId == fx.Settlement.Id.ToString());
        audit.UserId.Should().Be(approver);
        audit.MetadataJson.Should().Contain(maker.ToString()).And.Contain(submitter.ToString()).And.Contain(approver.ToString());
    }
}
