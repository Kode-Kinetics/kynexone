using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Education-style claim rules on the legacy Benefits path: per-child budgets, child limit with an HR exception,
/// itemised covered costs, school country, claim term and the contract-year period. Defaults must leave plain claims unchanged.</summary>
public class BenefitEducationClaimTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly string[] Gcc = ["SA", "AE", "KW", "BH", "OM", "QA"];

    [Fact]
    public async Task LegacyPolicy_WithoutTheNewFields_BehavesExactlyAsBefore()
    {
        await using var db = Db(); var f = await Seed(db, new("Reimbursement", ReceiptRequired: true));
        var request = await Submit(db, f, Claim(f, 400, "r1"));
        var p = BenefitClaims.Read(request);
        Assert.Null(p.DependantId); Assert.Null(p.Lines); Assert.Null(p.InvoiceTotal); Assert.Null(p.ExceptionReason);
        Assert.StartsWith("Benefit claim:", request.Title);
        var audit = db.AuditLogs.Single(x => x.Action == "benefits.claim.requested").Metadata!;
        Assert.DoesNotContain("exceptionReason", audit); Assert.DoesNotContain("dependantId", audit);
        var balance = (await BenefitClaims.BalancesAsync(db, f.Tenant, f.Employee.Id, [f.Enrollment], Today, default))[f.Enrollment.Id];
        Assert.Equal(600m, balance.RemainingAmount); Assert.Null(balance.Children); Assert.Equal("Employee", balance.Rules!.ClaimFor);
        Assert.Equal(new DateOnly(2026, 1, 1), balance.PeriodFrom);
        // Over the shared limit is still refused with today's message; a dependant on an employee benefit is refused, never silently kept.
        var over = await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 700, "r2")));
        Assert.Equal("The claim exceeds the remaining benefit limit, including pending and approved claims.", over.Message);
        var child = await Child(db, f, "Child", "Lina");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "r3") with { DependantId = child.Id }));
    }

    [Fact]
    public async Task ChildPolicy_RequiresARecordedChildAndASchool()
    {
        await using var db = Db(); var f = await Seed(db, Education(perChild: true));
        var spouse = await Child(db, f, "Spouse", "Partner"); var son = await Child(db, f, "son", "Omar");
        Assert.Equal("Choose the child this claim is for.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "a")))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "b", spouse.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "c", son.Id) with { SchoolName = " " }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "d", son.Id) with { SchoolName = new string('x', 201) }));
        var ok = BenefitClaims.Read(await Submit(db, f, Claim(f, 100, "e", son.Id)));
        Assert.Equal(son.Id, ok.DependantId); Assert.Equal("Omar", ok.DependantName); Assert.Equal("Riyadh School", ok.SchoolName); Assert.Equal(2, ok.Version);
    }

    [Fact]
    public async Task PerChildLimit_OneChildsFullBudgetDoesNotBlockAnother()
    {
        await using var db = Db(); var f = await Seed(db, Education(perChild: true));
        var a = await Child(db, f, "Child", "A", 2015); var b = await Child(db, f, "Child", "B", 2017); var c = await Child(db, f, "Child", "C", 2019);
        await Submit(db, f, Claim(f, 1000, "a1", a.Id));
        var full = await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 1, "a2", a.Id)));
        Assert.Contains("this child's remaining", full.Message);
        var second = await Submit(db, f, Claim(f, 1000, "b1", b.Id));
        Assert.Equal(0m, (await BenefitClaims.ToDtoAsync(db, second, default)).RemainingAmount);
        var balance = (await BenefitClaims.BalancesAsync(db, f.Tenant, f.Employee.Id, [f.Enrollment], Today, default))[f.Enrollment.Id];
        Assert.Equal(new[] { "A", "B", "C" }, balance.Children!.Select(x => x.Name));
        Assert.Equal(new[] { 0m, 0m, 1000m }, balance.Children!.Select(x => x.RemainingAmount));
        Assert.True(balance.CanClaim); Assert.Equal(1000m, balance.RemainingAmount); Assert.True(balance.Rules!.LimitPerChild);
        Assert.All(balance.Children!, x => Assert.True(x.WithinChildLimit));
        // The per-expense-date balance (claim form) carries the same children and rules as the listing.
        var dated = (await BenefitClaims.BalancesAsync(db, f.Tenant, f.Employee.Id, [f.Enrollment], Today, default, Today.AddDays(-1)))[f.Enrollment.Id];
        Assert.Equal(3, dated.Children!.Count); Assert.Equal("Child", dated.Rules!.ClaimFor); Assert.Equal(BenefitLimitPeriods.Annual, dated.Rules.LimitPeriod);
        _ = c;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourthChild_IsRefused_OrAcceptedAsAFlaggedHrException(bool byException)
    {
        await using var db = Db(); var f = await Seed(db, Education(perChild: true, maxChildren: 3, extra: byException));
        var kids = new List<EmployeeDependent>();
        for (var i = 0; i < 4; i++) kids.Add(await Child(db, f, "Child", $"Kid {i + 1}", 2012 + i));
        for (var i = 0; i < 3; i++) await Submit(db, f, Claim(f, 100, $"k{i}", kids[i].Id));
        var balance = (await BenefitClaims.BalancesAsync(db, f.Tenant, f.Employee.Id, [f.Enrollment], Today, default))[f.Enrollment.Id];
        Assert.Equal(new[] { true, true, true, false }, balance.Children!.Select(x => x.WithinChildLimit));
        // A child already counted keeps claiming normally.
        Assert.Null(BenefitClaims.Read(await Submit(db, f, Claim(f, 50, "k0b", kids[0].Id))).ExceptionReason);
        if (!byException)
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "k3", kids[3].Id)));
            Assert.Equal("This policy covers up to 3 children.", refused.Message);
            return;
        }
        var request = await Submit(db, f, Claim(f, 100, "k3", kids[3].Id));
        var p = BenefitClaims.Read(request);
        Assert.Equal("Child 4 of 3: beyond the policy's child limit — HR exception", p.ExceptionReason);
        Assert.StartsWith("Exception · Benefit claim:", request.Title);
        Assert.Contains("beyond the policy", db.AuditLogs.Where(x => x.Action == "benefits.claim.requested" && x.EntityId == request.Id.ToString()).Single().Metadata);
        Assert.Equal(p.ExceptionReason, (await BenefitClaims.ToDtoAsync(db, request, default)).ExceptionReason);
        await BenefitClaims.ApplyAsync(db, request, "Approved", Context(f, f.Approver, "employees.approve"), default, Scope(f), f.Storage, new Clock());
        Assert.Contains("HR exception", db.ChangeTracker.Entries<AuditLog>().Select(x => x.Entity).Single(x => x.Action == "benefits.claim.approved").Metadata);
    }

    [Fact]
    public async Task CoveredCosts_AmountMustEqualCoveredLines_TransportAndVatStayOutOfTheBudget()
    {
        await using var db = Db(); var f = await Seed(db, Education(perChild: true) with { CoveredCosts = ["Tuition"] });
        var kid = await Child(db, f, "Child", "Sara");
        BenefitClaimLine[] lines = [new("Tuition", 800), new("Transport", 150), new("Vat", 40)];
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 990, "x1", kid.Id) with { Lines = lines }));
        Assert.Equal("The claim amount must equal the covered costs (Tuition): 800.00.", mismatch.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 800, "x2", kid.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 800, "x3", kid.Id) with { Lines = [new("Fees", 800)] }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 800.001m, "x4", kid.Id) with { Lines = [new("Tuition", 800.001m)] }));
        var request = await Submit(db, f, Claim(f, 800, "x5", kid.Id) with { Lines = lines });
        var dto = await BenefitClaims.ToDtoAsync(db, request, default);
        Assert.Equal(800m, dto.Amount); Assert.Equal(990m, dto.InvoiceTotal); Assert.Equal(3, dto.Lines!.Count);
        Assert.Equal(new[] { "Tuition" }, dto.CoveredCosts!);
        Assert.Equal(200m, dto.RemainingAmount); // only the covered tuition counts against the 1000 limit
        Assert.Contains("\"lines\":[{\"type\":\"Tuition\",\"amount\":800", JsonSerializer.Serialize(dto, BenefitPaymentPolicies.Json));
    }

    [Fact]
    public async Task SchoolCountryOutsideTheList_IsRefused()
    {
        await using var db = Db(); var f = await Seed(db, Education(perChild: false) with { AllowedCountries = Gcc });
        var kid = await Child(db, f, "Child", "Yousef");
        var outside = await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "c1", kid.Id) with { SchoolCountry = "GB" }));
        Assert.Equal("Schools outside Saudi Arabia, the UAE, Kuwait, Bahrain, Oman, Qatar are not covered by this policy.", outside.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "c2", kid.Id) with { SchoolCountry = null }));
        Assert.Equal("AE", BenefitClaims.Read(await Submit(db, f, Claim(f, 100, "c3", kid.Id) with { SchoolCountry = "ae" })).SchoolCountry);
    }

    [Fact]
    public async Task ClaimTerm_IsRequiredWhenConfigured_AndMustBeAllowed()
    {
        await using var db = Db(); var f = await Seed(db, Education(perChild: false) with { ClaimTerms = ["Semester", "FullYear"] });
        var kid = await Child(db, f, "Child", "Huda");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "t1", kid.Id)));
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "t2", kid.Id) with { ClaimTerm = "HalfYear" }));
        Assert.Equal("Choose a claim term this policy allows: Semester, FullYear.", wrong.Message);
        Assert.Equal("Semester", BenefitClaims.Read(await Submit(db, f, Claim(f, 100, "t3", kid.Id) with { ClaimTerm = "Semester" })).ClaimTerm);
    }

    [Fact]
    public void ContractYear_StartsOnTheContractDate_AndStopsAtTheTermEnd()
    {
        Assert.Equal((new DateOnly(2026, 3, 15), new DateOnly(2027, 3, 14)), BenefitClaims.ContractYear(new(2026, 3, 15), null, new(2026, 10, 8)));
        Assert.Equal((new DateOnly(2026, 3, 15), new DateOnly(2027, 3, 14)), BenefitClaims.ContractYear(new(2024, 3, 15), null, new(2026, 3, 15)));
        Assert.Equal((new DateOnly(2025, 3, 15), new DateOnly(2026, 3, 14)), BenefitClaims.ContractYear(new(2024, 3, 15), null, new(2026, 3, 14)));
        Assert.Equal((new DateOnly(2026, 3, 15), new DateOnly(2026, 9, 30)), BenefitClaims.ContractYear(new(2026, 3, 15), new DateOnly(2026, 9, 30), new(2026, 6, 1)));
        Assert.Contains(BenefitLimitPeriods.ContractYear, BenefitLimitPeriods.All);
    }

    [Fact]
    public async Task ContractYear_UsesTheCoveringTerm_RenewalStartsANewBudget_NoContractIsRefused()
    {
        await using var db = Db(); var f = await Seed(db, new("Reimbursement"), BenefitLimitPeriods.ContractYear);
        var noContract = await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 100, "n1", expense: new(2026, 4, 1))));
        Assert.Equal("This allowance runs on the employee's contract year, and no contract covering 2026-04-01 is on file. HR needs to add the contract first.", noContract.Message);
        var blocked = (await BenefitClaims.BalancesAsync(db, f.Tenant, f.Employee.Id, [f.Enrollment], Today, default))[f.Enrollment.Id];
        Assert.False(blocked.CanClaim); Assert.Equal("This allowance runs on the employee's contract year, and no contract covering 2026-10-09 is on file. HR needs to add the contract first.", blocked.BlockedReason);

        Contract(db, f, new(2025, 3, 15), new(2026, 3, 14), "Superseded");
        Contract(db, f, new(2026, 3, 15), null, "Active");
        Contract(db, f, new(2026, 1, 1), null, "Draft"); // a draft never defines the year
        await db.SaveChangesAsync();
        var old = BenefitClaims.Read(await Submit(db, f, Claim(f, 1000, "o1", expense: new(2026, 3, 1))));
        Assert.Equal((new DateOnly(2025, 3, 15), new DateOnly(2026, 3, 14)), (old.PeriodFrom, old.PeriodTo));
        var renewed = BenefitClaims.Read(await Submit(db, f, Claim(f, 1000, "o2", expense: new(2026, 4, 1))));
        Assert.Equal((new DateOnly(2026, 3, 15), new DateOnly(2027, 3, 14)), (renewed.PeriodFrom, renewed.PeriodTo));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(db, f, Claim(f, 1, "o3", expense: new(2026, 10, 1))));
        var balance = (await BenefitClaims.BalancesAsync(db, f.Tenant, f.Employee.Id, [f.Enrollment], Today, default))[f.Enrollment.Id];
        Assert.Equal(new DateOnly(2026, 3, 15), balance.PeriodFrom); Assert.Equal(0m, balance.RemainingAmount); Assert.Null(balance.BlockedReason);
    }

    [Fact]
    public async Task Approval_ReappliesTheRules()
    {
        await using var db = Db(); var f = await Seed(db, Education(perChild: true), BenefitLimitPeriods.ContractYear);
        var contract = Contract(db, f, new(2026, 3, 15), null, "Active"); await db.SaveChangesAsync();
        var kid = await Child(db, f, "Child", "Rami");
        var childClaim = await Submit(db, f, Claim(f, 100, "p1", kid.Id));
        var termClaim = await Submit(db, f, Claim(f, 100, "p2", kid.Id));
        kid.IsDeleted = true; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BenefitClaims.ApplyAsync(db, childClaim, "Approved",
            Context(f, f.Approver, "employees.approve"), default, Scope(f), f.Storage, new Clock()));
        kid.IsDeleted = false; contract.IsDeleted = true; await db.SaveChangesAsync();
        var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => BenefitClaims.ApplyAsync(db, termClaim, "Approved",
            Context(f, f.Approver, "employees.approve"), default, Scope(f), f.Storage, new Clock()));
        Assert.StartsWith("This allowance runs on the employee's contract year", gone.Message);
        contract.IsDeleted = false; await db.SaveChangesAsync();
        await BenefitClaims.ApplyAsync(db, termClaim, "Approved", Context(f, f.Approver, "employees.approve"), default, Scope(f), f.Storage, new Clock());
    }

    [Fact]
    public async Task Version1Payload_IsStillReadable()
    {
        await using var db = Db(); var f = await Seed(db, new("Reimbursement", ReceiptRequired: true));
        var request = await Submit(db, f, Claim(f, 400, "v1"));
        var node = JsonNode.Parse(request.Payload!)!.AsObject();
        node["version"] = 1;
        foreach (var key in new[] { "dependantId", "dependantName", "schoolName", "schoolCountry", "claimTerm", "lines", "invoiceTotal", "exceptionReason" }) node.Remove(key);
        // A stored payload is immutable, so stand in for a row written before Version 2 with a detached copy.
        db.Entry(request).State = EntityState.Detached;
        request.Payload = node.ToJsonString(); request.PayloadSha256 = AdditionalBenefitGrants.Digest(request.Payload);
        var p = BenefitClaims.Read(request);
        Assert.Equal(1, p.Version); Assert.Null(p.DependantId); Assert.Null(p.Lines);
        var dto = await BenefitClaims.ToDtoAsync(db, request, default);
        Assert.Equal(400m, dto.Amount); Assert.Null(dto.ExceptionReason); Assert.Null(dto.CoveredCosts);
        await BenefitClaims.ApplyAsync(db, request, "Approved", Context(f, f.Approver, "employees.approve"), default, Scope(f), f.Storage, new Clock());
    }

    [Fact]
    public async Task PolicyValidation_RulesOnlyForReimbursement_AndVocabulariesAreEnforced()
    {
        await using var db = Db(); var f = await Seed(db, new("Reimbursement"));
        var fresh = new BenefitPlan { TenantId = f.Tenant, CompanyId = f.Company, Code = "NEW", Name = "New", Currency = "AED", EffectiveFrom = Today };
        Task<string> Configure(BenefitPaymentPolicy policy) => BenefitPaymentPolicies.ConfigureAsync(db, fresh, policy with { SalaryComponentId = f.Component }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("SalaryAllowance", 100, CoveredCosts: ["Tuition"])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", LimitPerChild: true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", ClaimFor: "Child", MaxChildren: 0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", ClaimFor: "Child", MaxChildren: 21)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", ClaimFor: "Spouse")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", CoveredCosts: [])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", CoveredCosts: ["Tuition", "Tuition"])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", CoveredCosts: ["Fees"])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", AllowedCountries: ["SAU"])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Configure(new("Reimbursement", ClaimTerms: ["Quarter"])));
        var ok = BenefitPaymentPolicies.ReadSnapshotEnvelope(await Configure(new("Reimbursement", ClaimFor: "Child", LimitPerChild: true, MaxChildren: 3,
            ExtraChildByException: true, CoveredCosts: ["Tuition"], AllowedCountries: ["sa", "AE"], ClaimTerms: ["Semester", "HalfYear", "FullYear"])))!.Policy;
        Assert.Equal(new[] { "SA", "AE" }, ok.AllowedCountries!); Assert.Equal(3, ok.MaxChildren); Assert.Equal("Child", ok.ClaimFor);
        // A policy written before these fields existed reads with today's defaults.
        var legacy = BenefitPaymentPolicies.ReadSnapshotEnvelope($"{{\"version\":1,\"planId\":\"{Guid.NewGuid()}\",\"currency\":\"AED\",\"policy\":{{\"delivery\":\"Reimbursement\",\"frequency\":\"Monthly\"}}}}")!.Policy;
        Assert.Equal("Employee", legacy.ClaimFor); Assert.False(legacy.LimitPerChild); Assert.Null(legacy.CoveredCosts);
    }

    private static BenefitPaymentPolicy Education(bool perChild, int? maxChildren = null, bool extra = false) =>
        new("Reimbursement", ReceiptRequired: true, ClaimFor: "Child", LimitPerChild: perChild, MaxChildren: maxChildren, ExtraChildByException: extra);

    private static async Task<ApprovalRequest> Submit(ZayraDbContext db, Fixture f, BenefitClaimRequest input) => await BenefitClaims.SubmitAsync(db,
        new ApprovalRouter(db), f.Storage, f.Tenant, input, Context(f, f.Maker, "employees.write"), new Clock(), default, Scope(f));
    private static BenefitClaimRequest Claim(Fixture f, decimal amount, string receipt, Guid? child = null, DateOnly? expense = null)
    {
        var doc = Receipt(f.Db, f, receipt).GetAwaiter().GetResult();
        return new(f.Enrollment.Id, amount, expense ?? Today.AddDays(-1), $"INV-{receipt}", "School fees", [doc.Id],
            DependantId: child, SchoolName: child.HasValue ? "Riyadh School" : null);
    }
    private static async Task<EmployeeDependent> Child(ZayraDbContext db, Fixture f, string relationship, string name, int year = 2016)
    {
        var row = new EmployeeDependent { TenantId = f.Tenant, EmployeeId = f.Employee.Id, FullName = name, Relationship = relationship, DateOfBirth = new(year, 1, 1) };
        db.EmployeeDependents.Add(row); await db.SaveChangesAsync(); return row;
    }
    private static EmployeeContract Contract(ZayraDbContext db, Fixture f, DateOnly start, DateOnly? end, string status)
    {
        var row = new EmployeeContract { TenantId = f.Tenant, CompanyId = f.Company, EmployeeId = f.Employee.PublicId, EmployeeName = f.Employee.FullName,
            ContractNumber = $"C-{start:yyyyMMdd}-{status}", Status = status, StartDate = start, EndDate = end };
        db.EmployeeContracts.Add(row); return row;
    }
    private static RequestContext Context(Fixture f, Guid user, params string[] permissions) => new(null, "test", user, f.Tenant, ["HR Manager"], permissions);
    private static EntityScopeContext Scope(Fixture f) => EntityScopeContext.ForCompanies([f.Company]);
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<Fixture> Seed(ZayraDbContext db, BenefitPaymentPolicy policy, string limitPeriod = "Annual")
    {
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        var employee = new Employee { TenantId = tenant, CompanyId = company, GradeId = Guid.NewGuid(), FullName = "Synthetic parent", EmployeeCode = "EDU-1", Status = "Active" };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company, Code = "EDU", Name = "Education allowance", Currency = "SAR", EffectiveFrom = Today.AddYears(-1) };
        var component = new SalaryComponent { TenantId = tenant, Code = "EDU", Name = "Education reimbursement", ComponentType = "Earning" };
        var workflow = new ApprovalWorkflow { TenantId = tenant, Code = "CLAIM", Name = "Benefit claim authority", EntityName = BenefitClaims.EntityName, IsActive = true, IsDefault = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenant, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR review", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.AddRange(employee, plan, component, workflow); await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDeciderAsync(db, tenant);
        plan.PaymentPolicyJson = await BenefitPaymentPolicies.ConfigureAsync(db, plan, policy with { SalaryComponentId = component.Id }, default); plan.PolicyVersion++;
        var enrollment = new BenefitEnrollment { TenantId = tenant, CompanyId = company, EmployeeId = employee.Id, BenefitPlanId = plan.Id,
            AssignmentSource = "GradeDefault", EffectiveFrom = Today.AddYears(-1), EffectiveTo = Today.AddYears(1), MaximumBenefitAmount = 1000,
            LimitPeriod = limitPeriod, PaymentPolicySnapshotJson = BenefitPaymentPolicies.Snapshot(plan) };
        db.BenefitEnrollments.Add(enrollment); await db.SaveChangesAsync();
        return new(db, tenant, company, employee, enrollment, component.Id, new Storage(), Guid.NewGuid(), Guid.NewGuid());
    }
    private static async Task<EmployeeDocument> Receipt(ZayraDbContext db, Fixture f, string name)
    {
        var doc = new EmployeeDocument { TenantId = f.Tenant, CompanyId = f.Company, EmployeeId = f.Employee.Id, DocumentType = BenefitClaims.ReceiptType,
            FileName = "school.pdf", ContentType = "application/pdf", StorageUrl = $"{f.Tenant:D}/{name}.pdf" };
        f.Storage.Bytes[doc.StorageUrl] = System.Text.Encoding.UTF8.GetBytes("%PDF-" + name);
        db.EmployeeDocuments.Add(doc); db.AuditLogs.Add(new AuditLog { TenantId = f.Tenant, CompanyId = f.Company, EntityName = nameof(EmployeeDocument),
            EntityId = doc.Id.ToString(), Action = "benefits.receipt.uploaded", Metadata = JsonSerializer.Serialize(new BenefitReceiptUploadWitness(f.Employee.Id, doc.VersionNumber, BenefitClaims.Hash(f.Storage.Bytes[doc.StorageUrl]))) });
        await db.SaveChangesAsync(); return doc;
    }
    private sealed record Fixture(ZayraDbContext Db, Guid Tenant, Guid Company, Employee Employee, BenefitEnrollment Enrollment, Guid Component,
        Storage Storage, Guid Maker, Guid Approver);
    private sealed class Clock(DateOnly? date = null) : ITenantClock { public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(date ?? Today); }
    private sealed class Storage : IDocumentStorage
    {
        public Dictionary<string, byte[]> Bytes { get; } = [];
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Bytes[storageUrl]);
        public string ResolvePath(string storageUrl) => throw new NotSupportedException();
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
    }
}
