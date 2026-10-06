using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Release A R2, review round 1: the bulk run only PROPOSES; a different HR user confirms each proposal against the signed
/// contract document (or rejects it), and only then are rows written. And HR records the dependants the package counts.
/// </summary>
public sealed class PackageProposalAndDependantsTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static async Task<(ZayraDbContext Db, PackageSeed Seed)> SeededAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"r2-prop-{Guid.NewGuid():N}").Options);
        var seed = new PackageSeed();
        await seed.SaveAsync(db);
        return (db, seed);
    }

    private static T Bind<T>(T controller, Guid tenantId, Guid userId, params string[] permissions) where T : ControllerBase
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, "HR Manager"), new("is_group_scope", "true") };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    private static EmployeePackageController Package(ZayraDbContext db, PackageSeed s, Guid userId)
    {
        var resolver = new EntitlementResolver(db);
        var clock = new FixedTenantClock(Today);
        return Bind(new EmployeePackageController(db, resolver, new EntitlementWriter(db, clock, resolver), clock), s.TenantId, userId, "entitlements.manage");
    }

    /// <summary>What the bulk job leaves behind for one term: a job (the batch) and a checkpoint holding the proposal.</summary>
    private static async Task<Guid> ProposeAsync(ZayraDbContext db, PackageSeed s, Guid requester)
    {
        var resolver = new EntitlementResolver(db);
        var proposal = await new EntitlementWriter(db, new FixedTenantClock(Today), resolver).ProposeAsync(s.TenantId, s.Term.Id, default);
        var job = new BackgroundJob { TenantId = s.TenantId, JobType = PackageFreezeJobHandler.JobType, IdempotencyKey = "k", CreatedByUserId = requester };
        db.BackgroundJobs.Add(job);
        db.BackgroundJobItems.Add(new BackgroundJobItem { TenantId = s.TenantId, JobId = job.Id, ItemKey = PackageFreezeJobHandler.ItemKey(s.Term.Id),
            ResultJson = JsonSerializer.Serialize(proposal) });
        await db.SaveChangesAsync();
        return job.Id;
    }

    [Fact]
    public async Task AProposal_IsConfirmedOnlyByAnotherPerson_AgainstTheSignedContract_AndOnlyThenWritten()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var requester = Guid.NewGuid();
        var batch = await ProposeAsync(db, s, requester);
        (await db.EmployeeEntitlements.CountAsync()).Should().Be(0, "proposing writes nothing");

        // The HR panel shows the open proposal instead of "fix the year".
        var view = (EmployeePackageView)((OkObjectResult)(await Package(db, s, requester).Get(s.Mohammed.Id, null, default)).Result!).Value!;
        (view.Proposal!.BatchId, view.Proposal.RequestedByYou, view.CanFreeze, view.CanPropose).Should().Be((batch, true, false, false));
        view.Proposal.Rows.Should().OnlyContain(r => r.VerificationState == EntitlementVerificationStates.Unverified);
        (view.FreezeStatus.Fixed, view.FreezeStatus.Total).Should().Be((0, 2));
        view.FreezeStatus.NotFixed.Should().OnlyContain(n => n.ReasonCode == PackageReasons.ProposalOpen);

        // P1: the direct freeze cannot be used to skip the second person — not for a running term, not beside a proposal.
        var direct = (ObjectResult)await Package(db, s, requester).Freeze(s.Mohammed.Id, new PackageContractRequest(s.Term.Id), default);
        (direct.StatusCode, JsonSerializer.Serialize(direct.Value).Contains(PackageReasons.TermRunningNeedsProposal)).Should().Be((409, true));

        var self = await Package(db, s, requester).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, null), default);
        ((ObjectResult)self).StatusCode.Should().Be(409);
        JsonSerializer.Serialize(((ObjectResult)self).Value).Should().Contain(PackageReasons.ProposalSameUser);

        var checker = Guid.NewGuid();
        var noDocument = await Package(db, s, checker).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, null), default);
        JsonSerializer.Serialize(((ObjectResult)noDocument).Value).Should().Contain(PackageReasons.ProposalDocumentRequired);

        var passport = new EmployeeDocument { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.Id,
            DocumentType = "Passport", FileName = "passport.pdf", StorageUrl = "p" };
        var byRequester = new EmployeeDocument { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.Id,
            DocumentType = "Contract", FileName = "uploaded-by-requester.pdf", StorageUrl = "r", UploadedBy = requester };
        var contractDoc = new EmployeeDocument { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.Id,
            DocumentType = "Contract", FileName = "signed.pdf", StorageUrl = "x", UploadedBy = Guid.NewGuid() };
        db.EmployeeDocuments.AddRange(passport, byRequester, contractDoc);
        await db.SaveChangesAsync();
        var ownEvidence = await Package(db, s, checker).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, byRequester.Id), default);
        JsonSerializer.Serialize(((ObjectResult)ownEvidence).Value).Should().Contain(PackageReasons.ProposalDocumentRequired,
            "a contract the requester uploaded is not a second person's evidence");
        var notAContract = await Package(db, s, checker).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, passport.Id), default);
        JsonSerializer.Serialize(((ObjectResult)notAContract).Value).Should().Contain(PackageReasons.ProposalDocumentRequired,
            "only the signed contract counts, not any document on file");
        (await Package(db, s, checker).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, contractDoc.Id), default)).Should().BeOfType<OkObjectResult>();

        var rows = await db.EmployeeEntitlements.ToListAsync();
        rows.Should().HaveCount(2).And.OnlyContain(r => r.Source == EntitlementSources.Migrated && r.VerificationState == EntitlementVerificationStates.Verified);
        var audit = await db.ComplianceAuditLogs.SingleAsync(x => x.EntityType == EmployeePackageController.ProposalAuditEntity);
        (audit.Action, audit.PerformedByUserId).Should().Be(("Confirmed", (Guid?)checker));
        audit.MetadataJson.Should().Contain(batch.ToString()).And.Contain(contractDoc.Id.ToString()).And.Contain(rows[0].Id.ToString());

        var again = await Package(db, s, checker).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, contractDoc.Id), default);
        JsonSerializer.Serialize(((ObjectResult)again).Value).Should().Contain(PackageReasons.ProposalClosed);
    }

    [Fact]
    public async Task ARejectedProposal_WritesNothing_AndIsClosed()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var batch = await ProposeAsync(db, s, Guid.NewGuid());
        var checker = Guid.NewGuid();
        (await Package(db, s, checker).RejectProposal(batch, new RejectProposalRequest(s.Term.Id, " "), default)).Should().BeOfType<BadRequestObjectResult>();
        (await Package(db, s, checker).RejectProposal(batch, new RejectProposalRequest(s.Term.Id, "Contract gives Business class"), default))
            .Should().BeOfType<OkObjectResult>();
        (await db.EmployeeEntitlements.CountAsync()).Should().Be(0);
        var view = (EmployeePackageView)((OkObjectResult)(await Package(db, s, checker).Get(s.Mohammed.Id, null, default)).Result!).Value!;
        (view.Proposal, view.CanFreeze, view.CanPropose).Should().Be(((PackageProposalDto?)null, false, true),
            "the term is running: proposing again is offered, a direct freeze never is");
    }

    [Fact]
    public async Task HR_AddsEditsAndRemovesDependants_WithAudit_AndThePackageCountsThem()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        db.EmployeeDependents.RemoveRange(db.EmployeeDependents);
        await db.SaveChangesAsync();
        var hr = Guid.NewGuid();
        var controller = Bind(new EmployeeDependantsController(db, new FixedTenantClock(Today)), s.TenantId, hr, "employees.write");

        var view = (EmployeePackageView)((OkObjectResult)(await Package(db, s, hr).Get(s.Mohammed.Id, null, default)).Result!).Value!;
        view.DependantsOnFile.Should().Be(0, "the screen says 'no dependants on file', not '0 covered'");

        (await controller.Add(s.Mohammed.Id, new DependantInput("Wife", "Wife", new DateOnly(1990, 1, 1), null), default))
            .Should().BeOfType<BadRequestObjectResult>("relationship is an enum");
        (await controller.Add(s.Mohammed.Id, new DependantInput("Baby", DependantRelationships.Child, new DateOnly(2027, 1, 1), null), default))
            .Should().BeOfType<BadRequestObjectResult>("not born yet");
        foreach (var (name, rel, dob) in new[] { ("Amal", DependantRelationships.Spouse, new DateOnly(1990, 3, 1)),
                     ("Omar", DependantRelationships.Child, new DateOnly(2015, 5, 1)), ("Lina", DependantRelationships.Child, new DateOnly(2018, 7, 1)) })
            (await controller.Add(s.Mohammed.Id, new DependantInput(name, rel, dob, "2" + new string('1', 9)), default)).Should().BeOfType<OkObjectResult>();
        var lina = await db.EmployeeDependents.SingleAsync(x => x.FullName == "Lina");
        (await controller.Update(s.Mohammed.Id, lina.Id, new DependantInput("Lina Abdelrahman", DependantRelationships.Child, lina.DateOfBirth, null), default))
            .Should().BeOfType<OkObjectResult>();

        var medical = (await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default)).Lines.Single(l => l.ComponentCode == "MEDICAL");
        medical.DependantsCovered.Should().Be(3, "Mohammed's wife and two children");

        (await controller.Remove(s.Mohammed.Id, lina.Id, default)).Should().BeOfType<NoContentResult>();
        var audits = await db.ComplianceAuditLogs.Where(x => x.EntityType == "EmployeeDependent").ToListAsync();
        audits.Select(a => a.Action).Should().BeEquivalentTo("DependantAdded", "DependantAdded", "DependantAdded", "DependantChanged", "DependantRemoved");
        audits.Should().OnlyContain(a => !a.MetadataJson.Contains("2111111111"), "the ID number is never written to the audit log in full");

        // Out of the caller's companies: not found, never written.
        var scoped = new EmployeeDependantsController(db, new FixedTenantClock(Today));
        scoped.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", s.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, hr.ToString()),
             new Claim("entity_access", JsonSerializer.Serialize(new { companyId = s.OtherCompany.Id }))], "Test")) } };
        (await scoped.Add(s.Mohammed.Id, new DependantInput("X", DependantRelationships.Other, new DateOnly(1960, 1, 1), null), default))
            .Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ADirectFreeze_IsRefused_BesideAnOpenProposal_EvenForATermNotStarted()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var term = await db.EmployeeContracts.SingleAsync(x => x.Id == s.Term.Id);
        (term.StartDate, term.EndDate) = (new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31));
        await db.SaveChangesAsync();
        await ProposeAsync(db, s, Guid.NewGuid());
        var refused = (ObjectResult)await Package(db, s, Guid.NewGuid()).Freeze(s.Mohammed.Id, new PackageContractRequest(s.Term.Id), default);
        (refused.StatusCode, JsonSerializer.Serialize(refused.Value).Contains(PackageReasons.ProposalOpen)).Should().Be((409, true));
        (await db.EmployeeEntitlements.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AProposalWithNoRecordedRequester_CannotBeConfirmed()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var batch = await ProposeAsync(db, s, Guid.Empty);
        (await db.BackgroundJobs.SingleAsync(x => x.Id == batch)).CreatedByUserId = null;
        await db.SaveChangesAsync();
        var refused = (ObjectResult)await Package(db, s, Guid.NewGuid()).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, Guid.NewGuid()), default);
        (refused.StatusCode, JsonSerializer.Serialize(refused.Value).Contains(PackageReasons.ProposalRequesterUnknown)).Should().Be((409, true));
    }

    [Fact]
    public async Task DependantIdNumbers_AreMasked_ForReadOnlyCallers_AndRemovalIsSoft()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var son = await db.EmployeeDependents.SingleAsync(x => x.FullName == "Son");
        son.NationalId = "2123456789";
        await db.SaveChangesAsync();

        var reader = Bind(new EmployeeDependantsController(db, new FixedTenantClock(Today)), s.TenantId, Guid.NewGuid(), "employees.read");
        var masked = ((IEnumerable<DependantDto>)((OkObjectResult)await reader.List(s.Mohammed.Id, default)).Value!).Single(d => d.FullName == "Son");
        masked.NationalId.Should().Be("***6789");
        foreach (var permission in new[] { "employees.write", "employees.sensitive" })
        {
            var full = Bind(new EmployeeDependantsController(db, new FixedTenantClock(Today)), s.TenantId, Guid.NewGuid(), "employees.read", permission);
            ((IEnumerable<DependantDto>)((OkObjectResult)await full.List(s.Mohammed.Id, default)).Value!).Single(d => d.FullName == "Son")
                .NationalId.Should().Be("2123456789", permission);
        }

        var hr = Guid.NewGuid();
        var writer = Bind(new EmployeeDependantsController(db, new FixedTenantClock(Today)), s.TenantId, hr, "employees.write");
        (await writer.Remove(s.Mohammed.Id, son.Id, default)).Should().BeOfType<NoContentResult>();
        var kept = await db.EmployeeDependents.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == son.Id); // soft-deleted rows are filtered by convention
        (kept.IsDeleted, kept.DeletedBy).Should().Be((true, (Guid?)hr));
        ((IEnumerable<DependantDto>)((OkObjectResult)await writer.List(s.Mohammed.Id, default)).Value!).Should().NotContain(d => d.Id == son.Id);
        Line(await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default), "MEDICAL")
            .DependantsCovered.Should().Be(2, "a removed dependant is no longer covered");
    }

    [Fact]
    public async Task Supersede_KeepsTheStampedNationalityClass()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var contracts = new Zayra.Api.Controllers.Compliance.ContractsController(db);
        contracts.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", s.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
             new Claim(ClaimTypes.Role, "HR Manager")], "Test")) } };
        var result = await contracts.Supersede(s.Term.Id, new Zayra.Api.Controllers.Compliance.CreateContractRequest(
            s.Mohammed.PublicId, null, null, null, new DateOnly(2026, 11, 1), PackageSeed.TermEnd, 8000m, "SAR", null, null, null), default);
        ((EmployeeContract)((OkObjectResult)result).Value!).WorkerNationalityClass.Should().Be(WorkerNationalityClasses.NonSaudi);
    }

    private static Application.Entitlements.PackageLine Line(Application.Entitlements.EmployeePackage p, string code) =>
        p.Lines.Single(l => l.ComponentCode == code);

    // ── Back-dated activation: the benefits wait for a proposal, with one clear next action ──────────

    private static async Task<IActionResult> ActivateAsync(ZayraDbContext db, PackageSeed s, DateOnly today)
    {
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = s.TenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        var resolver = new EntitlementResolver(db);
        var hook = new PackageFreezeOnActivation(new EntitlementWriter(db, new FixedTenantClock(today), resolver));
        var dispatcher = new Zayra.Api.Infrastructure.Contracts.ContractTermLifecycleDispatcher([hook],
            new Zayra.Api.Infrastructure.Modules.TenantModuleService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())));
        var contracts = Bind(new Zayra.Api.Controllers.Compliance.ContractsController(db, dispatcher), s.TenantId, s.UserId);
        return await contracts.UpdateStatus(s.Term.Id, new Zayra.Api.Controllers.Compliance.UpdateContractStatusRequest("Active", "HR Lead"), default);
    }

    private static async Task<ContractPackageStatusDto[]> StatusAsync(ZayraDbContext db, PackageSeed s) =>
        ((IEnumerable<ContractPackageStatusDto>)((OkObjectResult)await Package(db, s, Guid.NewGuid())
            .ContractsPackageStatus([s.Term.Id, s.ColleagueTerm.Id], default)).Value!).ToArray();

    [Theory]
    [InlineData(2026, 1, 20)] // before the start
    [InlineData(2026, 2, 1)]  // on the start date
    public async Task ActivationOnOrBeforeTheStart_StillWritesTheGradeDefaults_AndNeedsNoNextAction(int y, int m, int d)
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        (await db.EmployeeContracts.SingleAsync(x => x.Id == s.Term.Id)).Status = "PendingApproval";
        await db.SaveChangesAsync();
        (await ActivateAsync(db, s, new DateOnly(y, m, d))).Should().BeOfType<OkObjectResult>();
        (await db.EmployeeEntitlements.Where(x => x.ContractId == s.Term.Id).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(r => r.Source == EntitlementSources.GradeDefault && r.EffectiveFrom == PackageSeed.TermStart);
        (await StatusAsync(db, s)).Should().NotContain(x => x.ContractId == s.Term.Id);
    }

    [Fact]
    public async Task BackDatedActivation_WritesNothing_AndTheNextActionIsProposeThenReview()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        (await db.EmployeeContracts.SingleAsync(x => x.Id == s.Term.Id)).Status = "PendingApproval";
        await db.SaveChangesAsync();
        (await ActivateAsync(db, s, Today)).Should().BeOfType<OkObjectResult>("activation itself is never blocked by the package");
        (await db.EmployeeEntitlements.CountAsync(x => x.ContractId == s.Term.Id)).Should().Be(0);

        // Contract register and package panel agree: one next action, propose the benefits.
        (await StatusAsync(db, s)).Should().ContainSingle(x => x.ContractId == s.Term.Id)
            .Which.Should().Be(new ContractPackageStatusDto(s.Term.Id, s.Mohammed.Id, ContractPackageNextActions.ProposeBenefits));
        var view = (EmployeePackageView)((OkObjectResult)(await Package(db, s, Guid.NewGuid()).Get(s.Mohammed.Id, null, default)).Result!).Value!;
        (view.CanPropose, view.CanFreeze, view.Proposal).Should().Be((true, false, (PackageProposalDto?)null));

        // Once proposed, the next action is to review it.
        await ProposeAsync(db, s, Guid.NewGuid());
        (await StatusAsync(db, s)).Should().ContainSingle(x => x.ContractId == s.Term.Id).Which.NextAction.Should().Be(ContractPackageNextActions.ReviewProposal);
        view = (EmployeePackageView)((OkObjectResult)(await Package(db, s, Guid.NewGuid()).Get(s.Mohammed.Id, null, default)).Result!).Value!;
        (view.CanPropose, view.Proposal is not null).Should().Be((false, true));
    }
}
