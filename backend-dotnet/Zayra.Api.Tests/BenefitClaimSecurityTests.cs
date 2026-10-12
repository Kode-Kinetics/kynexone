using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class BenefitClaimSecurityTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Fact]
    public async Task PendingAndApprovedClaimsReserveLimit_ApprovalNeverCreatesPayroll()
    {
        await using var db = Db(); var f = await Seed(db);
        var request = await Submit(db, f);
        var p = BenefitClaims.Read(request);
        Assert.Equal(400m, await BenefitClaims.ReservedAsync(db, p, null, default));
        Assert.Equal("AwaitingApproval", (await BenefitClaims.ToDtoAsync(db, request, default)).SettlementStatus);
        await BenefitClaims.ApplyAsync(db, request, "Approved", Context(f, f.Approver, "employees.approve"), default, Scope(f), f.Storage, new Clock());
        request.Status = "Approved"; await db.SaveChangesAsync();
        Assert.Empty(db.PayrollAdjustments); Assert.Empty(db.BenefitContributions);
        var dto = await BenefitClaims.ToDtoAsync(db, request, default);
        Assert.Equal("AwaitingPayroll", dto.SettlementStatus); Assert.Equal(600m, dto.RemainingAmount);
        Assert.DoesNotContain("StorageKey", JsonSerializer.Serialize(dto));
        Assert.DoesNotContain(f.Receipt.StorageUrl, JsonSerializer.Serialize(dto));
        Assert.Single(db.AuditLogs.Where(x => x.Action == "benefits.claim.approved"));
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("wrong-employee")]
    [InlineData("wrong-company")]
    [InlineData("no-upload-provenance")]
    [InlineData("wrong-type")]
    [InlineData("bad-bytes")]
    [InlineData("duplicate-ids")]
    public async Task ReceiptsMustBeOwnedVerifiedDedicatedUploads(string kind)
    {
        await using var db = Db(); var f = await Seed(db); var input = Input(f);
        switch (kind)
        {
            case "missing-receipt": input = input with { DocumentIds = [] }; break;
            case "wrong-employee": f.Receipt.EmployeeId++; break;
            case "wrong-company": f.Receipt.CompanyId = Guid.NewGuid(); break;
            case "no-upload-provenance": input = input with { DocumentIds = [Guid.NewGuid()] }; db.EmployeeDocuments.Add(new EmployeeDocument
                { Id = input.DocumentIds[0], TenantId = f.Tenant, CompanyId = f.Employee.CompanyId, EmployeeId = f.Employee.Id,
                    DocumentType = BenefitClaims.ReceiptType, FileName = "school.pdf", ContentType = "application/pdf", StorageUrl = f.Receipt.StorageUrl }); break;
            case "wrong-type": f.Receipt.DocumentType = "Passport"; break;
            case "bad-bytes": f.Storage.Bytes[f.Receipt.StorageUrl] = [1, 2, 3]; break;
            case "duplicate-ids": input = input with { DocumentIds = [f.Receipt.Id, f.Receipt.Id] }; break;
        }
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, input));
        Assert.Empty(db.ApprovalRequests);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("precision")]
    [InlineData("cap")]
    [InlineData("future-expense")]
    [InlineData("old-expense")]
    [InlineData("missing-invoice")]
    [InlineData("missing-description")]
    [InlineData("no-policy")]
    [InlineData("waived")]
    [InlineData("release-a")]
    public async Task InvalidClaimCannotReserveBudget(string kind)
    {
        await using var db = Db(); var f = await Seed(db, configured: kind != "no-policy"); var input = Input(f);
        input = kind switch
        {
            "negative" => input with { Amount = -1 }, "precision" => input with { Amount = 1.001m },
            "cap" => input with { Amount = 1001 }, "future-expense" => input with { ExpenseDate = Today.AddDays(1) },
            "old-expense" => input with { ExpenseDate = Today.AddDays(-31) },
            "missing-invoice" => input with { InvoiceReference = "" }, "missing-description" => input with { Description = "" }, _ => input,
        };
        if (kind == "waived") f.Enrollment.Status = "Waived";
        if (kind == "release-a") db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = f.Tenant, FeatureKey = "release_a", IsEnabled = true });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, input));
        Assert.Empty(db.ApprovalRequests);
    }

    [Fact]
    public async Task PendingClaimsCountAcrossDatedSuccessors_AndDuplicateInvoicesCannotBePaidTwice()
    {
        await using var db = Db(); var f = await Seed(db);
        var first = await Submit(db, f, Input(f) with { Amount = 700 });
        var next = new BenefitEnrollment { TenantId = f.Tenant, CompanyId = f.Employee.CompanyId, EmployeeId = f.Employee.Id,
            BenefitPlanId = f.Plan.Id, EffectiveFrom = Today, MaximumBenefitAmount = 1000, LimitPeriod = "Annual",
            OriginalEnrollmentId = f.Enrollment.Id, PaymentPolicySnapshotJson = f.Enrollment.PaymentPolicySnapshotJson };
        db.BenefitEnrollments.Add(next); await db.SaveChangesAsync();
        var receipt = await Receipt(db, f, "second");
        var second = Input(f) with { EnrollmentId = next.Id, Amount = 301, InvoiceReference = "SCHOOL-002", DocumentIds = [receipt.Id], ExpenseDate = Today };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, second));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, second with { Amount = 200, InvoiceReference = "SCHOOL-001" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, second with { Amount = 200, DocumentIds = [f.Receipt.Id] }));
        first.Status = "Rejected"; await db.SaveChangesAsync();
        await Submit(db, f, second);
        Assert.Equal(301m, await BenefitClaims.ReservedAsync(db, BenefitClaims.Read(first), null, default));
    }

    [Theory]
    [InlineData("maker")]
    [InlineData("pointer")]
    [InlineData("deleted-link")]
    [InlineData("missing-permission")]
    [InlineData("scope")]
    public async Task ClaimDecisionRequiresIndependentScopedAuthority(string kind)
    {
        await using var db = Db(); var f = await Seed(db); var request = await Submit(db, f);
        var actor = kind == "maker" ? f.Maker : f.Approver;
        if (kind == "pointer") f.Employee.UserAccountId = actor;
        if (kind == "deleted-link") db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = f.Tenant, EmployeeId = f.Employee.Id, UserId = actor, Status = "Disabled", IsDeleted = true });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BenefitClaims.ValidateDecisionAsync(db, request,
            Context(f, actor, kind == "missing-permission" ? "approvals.override" : "employees.approve"), default,
            kind == "scope" ? EntityScopeContext.ForCompanies([Guid.NewGuid()]) : Scope(f)));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("row-version")]
    [InlineData("company")]
    [InlineData("grade")]
    [InlineData("route")]
    public async Task FinalApprovalRevalidatesSealedFacts(string kind)
    {
        await using var db = Db(); var f = await Seed(db); var request = await Submit(db, f);
        if (kind == "receipt") f.Storage.Bytes[f.Receipt.StorageUrl] = System.Text.Encoding.UTF8.GetBytes("%PDF-modified");
        if (kind == "row-version") f.Enrollment.CoverageTier = "Changed";
        if (kind == "company") f.Employee.CompanyId = Guid.NewGuid();
        if (kind == "grade") f.Employee.GradeId = Guid.NewGuid();
        if (kind == "route") (await db.ApprovalWorkflowSteps.SingleAsync()).ApproverRole = "Other role";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BenefitClaims.ApplyAsync(db, request, "Approved",
            Context(f, f.Approver, "employees.approve"), default, Scope(f), f.Storage, new Clock()));
        Assert.Empty(db.PayrollAdjustments);
    }

    [Fact]
    public async Task UsedReceiptIsRetainedAndClaimPayloadCannotBeResealed()
    {
        await using var db = Db(); var f = await Seed(db); var request = await Submit(db, f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BenefitClaims.EnsureReceiptMutableAsync(db, f.Tenant, f.Receipt.Id, default));
        request.Payload = request.Payload!.Replace("SCHOOL-001", "SCHOOL-999");
        request.PayloadSha256 = AdditionalBenefitGrants.Digest(request.Payload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task GenericApprovalCreationCannotForgeClaim()
    {
        await using var db = Db(); var f = await Seed(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ApprovalWorkflowService(db, new AuditService(db)).CreateRequestAsync(f.Tenant,
            new(null, BenefitClaims.EntityName, Guid.NewGuid().ToString(), "Forged", f.Employee.Id, f.Employee.CompanyId), Context(f, f.Maker, "employees.write"), default));
    }

    [Fact]
    public async Task PolicyRequiresDedicatedComponent_AndLegacyNeverPays()
    {
        await using var db = Db(); var f = await Seed(db);
        Assert.Equal("Coverage", BenefitPaymentPolicies.ReadSnapshot(new BenefitEnrollment()).Delivery);
        var component = await db.SalaryComponents.SingleAsync(); component.SalaryStructureId = Guid.NewGuid(); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BenefitPaymentPolicies.ConfigureAsync(db, f.Plan,
            new("SalaryAllowance", 100, SalaryComponentId: component.Id), default));
        component.SalaryStructureId = null; await db.SaveChangesAsync();
        var unused = new BenefitPlan { TenantId = f.Tenant, CompanyId = f.Company, Name = "Unused", Currency = "AED" };
        var json = await BenefitPaymentPolicies.ConfigureAsync(db, unused, new("SalaryAllowance", 100, SalaryComponentId: component.Id), default);
        Assert.Equal("SCHOOL", BenefitPaymentPolicies.ReadSnapshotEnvelope(json)!.SalaryComponent!.Code);
    }

    [Fact]
    public async Task SameReceiptCannotBeReusedAcrossDifferentBenefitPlans()
    {
        await using var db = Db(); var f = await Seed(db); await Submit(db, f);
        var plan = new BenefitPlan { TenantId = f.Tenant, CompanyId = f.Company, Name = "Other reimbursement", Code = "OTHER", Currency = "AED" };
        db.BenefitPlans.Add(plan);
        plan.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(db, plan, BenefitPaymentPolicies.ReadPlan(f.Plan), default); plan.PolicyVersion++;
        var row = new BenefitEnrollment { TenantId = f.Tenant, CompanyId = f.Company, EmployeeId = f.Employee.Id, BenefitPlanId = plan.Id,
            EffectiveFrom = Today.AddDays(-2), MaximumBenefitAmount = 1000, LimitPeriod = "Annual", PaymentPolicySnapshotJson = BenefitPaymentPolicies.Snapshot(plan) };
        db.BenefitEnrollments.Add(row); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Input(f) with { EnrollmentId = row.Id, InvoiceReference = "DIFFERENT-INVOICE" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsedPlanCannotChangeDelivery_IncludingRetainedRequestsWithoutCurrentEnrollment(bool requestOnly)
    {
        await using var db = Db(); var f = await Seed(db);
        if (requestOnly)
        {
            await Submit(db, f);
            db.BenefitEnrollments.Remove(f.Enrollment); await db.SaveChangesAsync();
        }
        var component = await db.SalaryComponents.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BenefitPaymentPolicies.ConfigureAsync(db, f.Plan,
            new("SalaryAllowance", 100, SalaryComponentId: component.Id), default));
        var sameMode = await BenefitPaymentPolicies.ConfigureAsync(db, f.Plan,
            BenefitPaymentPolicies.ReadPlan(f.Plan) with { Instructions = "Updated future assignments" }, default);
        Assert.Equal(2, BenefitPaymentPolicies.ReadSnapshotEnvelope(sameMode)!.Version);
    }

    [Fact]
    public async Task ConfiguredUnusedPlanKeepsDelivery_LegacyFirstConfigurationRemainsAllowed()
    {
        await using var db = Db(); var f = await Seed(db, configured: false);
        var component = await db.SalaryComponents.SingleAsync();
        // A first policy affects future assignments; the already assigned legacy row remains coverage.
        var first = await BenefitPaymentPolicies.ConfigureAsync(db, f.Plan,
            new("Reimbursement", SalaryComponentId: component.Id), default);
        Assert.Equal("Coverage", BenefitPaymentPolicies.ReadSnapshot(f.Enrollment).Delivery);
        var unused = new BenefitPlan { TenantId = f.Tenant, CompanyId = f.Company, Currency = "AED" };
        unused.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(db, unused, new("Reimbursement", SalaryComponentId: component.Id), default);
        unused.PolicyVersion = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => BenefitPaymentPolicies.ConfigureAsync(db, unused,
            new("SalaryAllowance", 100, SalaryComponentId: component.Id), default));
        Assert.Equal("Reimbursement", BenefitPaymentPolicies.ReadSnapshotEnvelope(first)!.Policy.Delivery);
    }

    [Fact]
    public async Task ReceiptChangedBeforeSubmissionFailsOriginalUploadHash()
    {
        await using var db = Db(); var f = await Seed(db);
        f.Storage.Bytes[f.Receipt.StorageUrl] = System.Text.Encoding.UTF8.GetBytes("%PDF-changed-before-submission");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f));
    }

    [Fact]
    public async Task EmptyCatalogueCanCreateDedicatedMappingWithoutAnyPayment()
    {
        await using var db = Db(); var tenant = Guid.NewGuid(); var controller = Controller(db, tenant);
        var before = Assert.IsType<OkObjectResult>(await controller.PaymentComponents(default));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<BenefitPaymentComponentDto>>(before.Value));
        var created = Assert.IsType<OkObjectResult>(await controller.CreatePaymentComponent(new("BEN_SCHOOL", "School reimbursement", "Earning"), default));
        Assert.IsType<BenefitPaymentComponentDto>(created.Value);
        var component = await db.SalaryComponents.SingleAsync();
        Assert.Null(component.SalaryStructureId); Assert.Equal(0, component.Amount); Assert.Equal(0, component.Percentage);
        Assert.Empty(db.PayrollAdjustments);
        Assert.IsType<ConflictObjectResult>(await controller.CreatePaymentComponent(new("BEN_SCHOOL", "Duplicate", "Earning"), default));
    }

    [Fact]
    public async Task FirstPaymentPolicyUsesRequestedCurrency_ExistingEnrollmentRetainsFrozenCurrency()
    {
        await using var db = Db(); var f = await Seed(db);
        var unused = new BenefitPlan { TenantId = f.Tenant, CompanyId = f.Company, Code = "NEW", Name = "New policy", Currency = "AED" };
        db.BenefitPlans.Add(unused); await db.SaveChangesAsync();
        var component = await db.SalaryComponents.SingleAsync();
        var result = await Controller(db, f.Tenant).UpdatePlan(unused.Id,
            new(unused.Name, null, "USD", unused.EffectiveFrom, unused.EffectiveTo,
                PaymentPolicy: new("Reimbursement", SalaryComponentId: component.Id), ExpectedPolicyVersion: 0), default);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("USD", BenefitPaymentPolicies.ReadSnapshotEnvelope(unused.PaymentPolicyJson)!.Currency);
        Assert.Equal("AED", BenefitPaymentPolicies.ReadSnapshotEnvelope(f.Enrollment.PaymentPolicySnapshotJson)!.Currency);
    }

    [Fact]
    public async Task BalanceUsesSelectedExpensePeriod_ApprovalDelayDoesNotInvalidateTimelySubmission()
    {
        await using var db = Db(); var f = await Seed(db);
        f.Enrollment.LimitPeriod = "Monthly"; await db.SaveChangesAsync();
        var request = await Submit(db, f);
        var current = await BenefitClaims.BalancesAsync(db, f.Tenant, f.Employee.Id, [f.Enrollment], Today, default);
        var previous = await BenefitClaims.BalancesAsync(db, f.Tenant, f.Employee.Id, [f.Enrollment], Today, default, Today.AddMonths(-1));
        Assert.Equal(600m, current[f.Enrollment.Id].RemainingAmount); Assert.Equal(1000m, previous[f.Enrollment.Id].RemainingAmount);
        await BenefitClaims.ApplyAsync(db, request, "Approved", Context(f, f.Approver, "employees.approve"), default, Scope(f), f.Storage, new Clock(Today.AddMonths(2)));
    }

    private static BenefitsController Controller(ZayraDbContext db, Guid tenant) => new(db, new Clock())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new("tenant_id", tenant.ToString()), new(ClaimTypes.Role, "Admin"),
                new("permission", "employees.approve"), new("permission", "payroll.write"), new("entity_scope", "{\"v\":2,\"m\":\"group\",\"c\":[]}")], "Test")) } },
    };

    private static Task<ApprovalRequest> Submit(ZayraDbContext db, Fixture f, BenefitClaimRequest? input = null) => BenefitClaims.SubmitAsync(db,
        new ApprovalRouter(db), f.Storage, f.Tenant, input ?? Input(f), Context(f, f.Maker, "employees.write"), new Clock(), default, Scope(f));
    private static BenefitClaimRequest Input(Fixture f) => new(f.Enrollment.Id, 400, Today.AddDays(-1), "SCHOOL-001", "School fees", [f.Receipt.Id]);
    private static RequestContext Context(Fixture f, Guid user, params string[] permissions) => new(null, "test", user, f.Tenant, ["HR Manager"], permissions);
    private static EntityScopeContext Scope(Fixture f) => EntityScopeContext.ForCompanies([f.Company]);
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<Fixture> Seed(ZayraDbContext db, bool configured = true)
    {
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        var employee = new Employee { TenantId = tenant, CompanyId = company, GradeId = Guid.NewGuid(), FullName = "Synthetic claimant", EmployeeCode = "CLAIM-1", Status = "Active" };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company, Code = "SCHOOL", Name = "Education", Currency = "AED", EffectiveFrom = Today.AddMonths(-3) };
        var component = new SalaryComponent { TenantId = tenant, Code = "SCHOOL", Name = "School reimbursement", ComponentType = "Earning" };
        var workflow = new ApprovalWorkflow { TenantId = tenant, Code = "CLAIM", Name = "Benefit claim authority", EntityName = BenefitClaims.EntityName, IsActive = true, IsDefault = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenant, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR review", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.AddRange(employee, plan, component, workflow); await db.SaveChangesAsync();
        if (configured)
        {
            plan.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(db, plan, new("Reimbursement", SalaryComponentId: component.Id,
                ReceiptRequired: true, ClaimWindowDays: 30), default); plan.PolicyVersion++;
        }
        var enrollment = new BenefitEnrollment { TenantId = tenant, CompanyId = company, EmployeeId = employee.Id, BenefitPlanId = plan.Id,
            AssignmentSource = "GradeDefault", EffectiveFrom = Today.AddMonths(-3), EffectiveTo = Today.AddYears(1), MaximumBenefitAmount = 1000, LimitPeriod = "Annual", PaymentPolicySnapshotJson = BenefitPaymentPolicies.Snapshot(plan) };
        db.BenefitEnrollments.Add(enrollment); await db.SaveChangesAsync();
        var f = new Fixture(tenant, company, employee, plan, enrollment, null!, new Storage(), Guid.NewGuid(), Guid.NewGuid());
        return f with { Receipt = await Receipt(db, f, "original") };
    }
    private static async Task<EmployeeDocument> Receipt(ZayraDbContext db, Fixture f, string name)
    {
        var doc = new EmployeeDocument { TenantId = f.Tenant, CompanyId = f.Company, EmployeeId = f.Employee.Id, DocumentType = BenefitClaims.ReceiptType,
            FileName = "school.pdf", ContentType = "application/pdf", StorageUrl = $"{f.Tenant:D}/{name}.pdf" };
        f.Storage.Bytes[doc.StorageUrl] = System.Text.Encoding.UTF8.GetBytes("%PDF-" + name);
        db.EmployeeDocuments.Add(doc); db.AuditLogs.Add(new AuditLog { TenantId = f.Tenant, CompanyId = f.Company, EntityName = nameof(EmployeeDocument),
            EntityId = doc.Id.ToString(), Action = "benefits.receipt.uploaded", Metadata = JsonSerializer.Serialize(new BenefitReceiptUploadWitness(f.Employee.Id, doc.VersionNumber, BenefitClaims.Hash(f.Storage.Bytes[doc.StorageUrl]))) }); await db.SaveChangesAsync(); return doc;
    }
    private sealed record Fixture(Guid Tenant, Guid Company, Employee Employee, BenefitPlan Plan, BenefitEnrollment Enrollment,
        EmployeeDocument Receipt, Storage Storage, Guid Maker, Guid Approver);
    private sealed class Clock(DateOnly? date = null) : ITenantClock { public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(date ?? Today); }
    private sealed class Storage : IDocumentStorage
    {
        public Dictionary<string, byte[]> Bytes { get; } = [];
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Bytes[storageUrl]);
        public string ResolvePath(string storageUrl) => throw new NotSupportedException();
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
    }
}
