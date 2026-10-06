using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Review round 4 of #192 on PostgreSQL with R0's triggers: "first term" is decided by the joining date (and per benefit by
/// what the previous term had confirmed), never by which contract records happen to exist — probes (a), (b), (c) and the
/// partial-predecessor hunt as assertions — plus the import guard, the joining-date warning, the Supersede/confirm race,
/// the uploader rule and separation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PackageReviewRound4PostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateTime JoinedLongAgo = new(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private static EntitlementWriter Writer(ZayraDbContext db, DateOnly today) => new(db, new FixedTenantClock(today), new EntitlementResolver(db));

    private static ClaimsPrincipal User(PackageSeed s, Guid userId, params string[] perms)
    {
        var claims = new List<Claim> { new("tenant_id", s.TenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, "HR Manager"), new("is_group_scope", "true") };
        claims.AddRange(perms.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ContractTermLifecycleDispatcher Dispatcher(ZayraDbContext db, DateOnly today) =>
        new([new PackageFreezeOnActivation(Writer(db, today))], new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())));

    private static ContractsController Contracts(ZayraDbContext db, PackageSeed s, DateOnly today) =>
        new(db, Dispatcher(db, today), new FixedTenantClock(today))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(s, s.UserId) } } };

    private static EmployeePackageController Package(ZayraDbContext db, PackageSeed s, Guid userId, DateOnly today)
    {
        var resolver = new EntitlementResolver(db);
        var clock = new FixedTenantClock(today);
        return new EmployeePackageController(db, resolver, new EntitlementWriter(db, clock, resolver), clock)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(s, userId, "entitlements.manage", "entitlements.read") } } };
    }

    private async Task<PackageSeed> SeedAsync(Action<PackageSeed>? tweak = null)
    {
        var s = new PackageSeed();
        tweak?.Invoke(s);
        await using var db = fixture.CreateDb();
        await s.SaveAsync(db);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = s.TenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        return s;
    }

    private async Task<MigrationReconciliationDto> ImportAsync(PackageSeed s, DateOnly today, string rows)
    {
        await using var db = fixture.CreateDb();
        var import = new MigrationImportController(db, new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(),
            new Zayra.Api.Infrastructure.Audit.AuditService(db), Dispatcher(db, today))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(s, s.UserId) } } };
        const string header = "EmployeeCode,ContractNumber,ContractType,Status,StartDate,EndDate,BasicSalary,CurrencyCode\n";
        var result = await import.Commit(new MigrationPackageRequest($"r4-{Guid.NewGuid():N}", new Dictionary<string, string> { ["contracts"] = header + rows }), default);
        return (MigrationReconciliationDto)Assert.IsType<OkObjectResult>(result.Result).Value!;
    }

    private async Task<int> RowCountAsync(PackageSeed s)
    {
        await using var db = fixture.CreateDb();
        return await db.EmployeeEntitlements.CountAsync(x => x.TenantId == s.TenantId);
    }

    private static string? Code(IActionResult result) => result is ObjectResult { Value: { } v }
        && JsonDocument.Parse(JsonSerializer.Serialize(v)).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;

    private async Task<Guid> ProposeJobAsync(PackageSeed s, Guid contractId, Guid requester, DateOnly today)
    {
        await using var db = fixture.CreateDb();
        var proposal = await Writer(db, today).ProposeAsync(s.TenantId, contractId, default);
        var job = new BackgroundJob { TenantId = s.TenantId, JobType = PackageFreezeJobHandler.JobType, IdempotencyKey = $"k-{Guid.NewGuid():N}", CreatedByUserId = requester };
        db.BackgroundJobs.Add(job);
        db.BackgroundJobItems.Add(new BackgroundJobItem { TenantId = s.TenantId, JobId = job.Id, ItemKey = PackageFreezeJobHandler.ItemKey(contractId),
            ResultJson = JsonSerializer.Serialize(proposal) });
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<Guid> DocAsync(PackageSeed s, Guid uploadedBy, string type = "Contract", string url = "x")
    {
        await using var db = fixture.CreateDb();
        var d = new EmployeeDocument { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.Id, DocumentType = type,
            FileName = "c.pdf", StorageUrl = url, UploadedBy = uploadedBy };
        db.EmployeeDocuments.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    // ── Probe (a): an import re-dates a running, unpackaged term into the future, then one person "fixes" it ──
    [Fact]
    public async Task ProbeA_ReDatingARunningTermByImport_NeverMakesItDirectlyFreezable()
    {
        var s = await SeedAsync(seed => seed.Mohammed.JoiningDate = JoinedLongAgo);
        var dto = await ImportAsync(s, Today, "E-1,CON-1,Employment,Active,2026-10-07,2027-01-31,8000,SAR\n");
        Assert.Empty(dto.Errors);
        await using var db = fixture.CreateDb();
        Assert.Equal(PackageReasons.EarlierServiceUnconfirmed,
            Code(await Package(db, s, s.UserId, Today).Freeze(s.Mohammed.Id, new PackageContractRequest(s.Term.Id), default)));
        Assert.Equal(0, await RowCountAsync(s));
    }

    // ── Probe (b): import the term back to Draft, then Active with a future start ──
    [Fact]
    public async Task ProbeB_ImportDraftThenActiveFutureStart_WritesNothing()
    {
        var s = await SeedAsync(seed => seed.Mohammed.JoiningDate = JoinedLongAgo);
        Assert.Empty((await ImportAsync(s, Today, "E-1,CON-1,Employment,Draft,2026-02-01,2027-01-31,8000,SAR\n")).Errors);
        Assert.Empty((await ImportAsync(s, Today, "E-1,CON-1,Employment,Active,2026-10-07,2027-01-31,8000,SAR\n")).Errors);
        Assert.Equal(0, await RowCountAsync(s));
    }

    // ── Probe (c): an existing employee with no contract on file gets a first contract record today ──
    [Fact]
    public async Task ProbeC_AnExistingEmployeesFirstContractRecord_WaitsForAProposal()
    {
        var s = await SeedAsync(seed => { seed.Mohammed.JoiningDate = JoinedLongAgo; seed.Term.IsDeleted = true; });
        var first = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, EmployeeName = "M",
            ContractNumber = "CON-FIRST", Status = "PendingApproval", StartDate = Today, EndDate = new DateOnly(2027, 10, 5),
            BasicSalary = 8000m, CurrencyCode = "SAR", WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb()) { db.EmployeeContracts.Add(first); await db.SaveChangesAsync(); }
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, Today).UpdateStatus(first.Id, new UpdateContractStatusRequest("Active", "HR"), default));
        Assert.Equal(0, await RowCountAsync(s));
        await using var db2 = fixture.CreateDb();
        var status = (IEnumerable<ContractPackageStatusDto>)((OkObjectResult)await Package(db2, s, Guid.NewGuid(), Today)
            .ContractsPackageStatus([first.Id], default)).Value!;
        Assert.Equal(ContractPackageNextActions.ProposeBenefits, Assert.Single(status).NextAction);
    }

    [Fact]
    public async Task AGenuineNewHire_WithinTheJoiningTolerance_IsFrozenDirectly()
    {
        var s = await SeedAsync(seed =>
        {
            seed.Mohammed.JoiningDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            (seed.Term.Status, seed.Term.StartDate, seed.Term.EndDate) = ("PendingApproval", new DateOnly(2026, 11, 3), new DateOnly(2027, 11, 2));
        });
        await using (var db = fixture.CreateDb())
        {
            db.StatutoryRules.Add(new StatutoryRule { TenantId = s.TenantId, CountryCode = "SAU", Jurisdiction = "KSA-mainland",
                RuleKey = DirectFreezeBasis.JoiningToleranceRuleKey, RuleValue = "3", DataType = "int", EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, Today).UpdateStatus(s.Term.Id, new UpdateContractStatusRequest("Active", "HR"), default));
        Assert.Equal(2, await RowCountAsync(s)); // starts 2 days after joining, inside the tenant's 3-day tolerance
    }

    // ── Round 5: a joining date edited forward does not make a second contract a "new hire" ──
    [Fact]
    public async Task AJoiningDateEditedForward_DoesNotMakeASecondContractANewHire()
    {
        var tomorrow = Today.AddDays(1);
        var s = await SeedAsync(seed => seed.Mohammed.JoiningDate = tomorrow.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var second = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, EmployeeName = "M",
            ContractNumber = "CON-R5", Status = "PendingApproval", StartDate = tomorrow, EndDate = new DateOnly(2027, 10, 6),
            BasicSalary = 8000m, CurrencyCode = "SAR", WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb()) { db.EmployeeContracts.Add(second); await db.SaveChangesAsync(); }
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, Today).UpdateStatus(second.Id, new UpdateContractStatusRequest("Active", "HR"), default));
        Assert.Equal(0, await RowCountAsync(s)); // the running CON-1 has no confirmed benefits, so everything is proposed
    }

    // ── Hunt: a previous term with only SOME benefits confirmed ──
    [Fact]
    public async Task APredecessorsConfirmationCountsPerBenefit_TheUnconfirmedOneIsProposed()
    {
        var s = await SeedAsync(seed => { seed.Mohammed.JoiningDate = JoinedLongAgo; seed.Term.WorkerNationalityClass = null; });
        var batch = await ProposeJobAsync(s, s.Term.Id, Guid.NewGuid(), Today); // nationality unconfirmed: medical only
        var doc = await DocAsync(s, Guid.NewGuid());
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Package(db, s, Guid.NewGuid(), Today).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, doc), default));
        var next = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, EmployeeName = "M",
            ContractNumber = "CON-NEXT", Status = "PendingApproval", StartDate = new DateOnly(2027, 2, 1), EndDate = new DateOnly(2028, 1, 31),
            BasicSalary = 8000m, CurrencyCode = "SAR", WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb()) { db.EmployeeContracts.Add(next); await db.SaveChangesAsync(); }
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, Today).UpdateStatus(next.Id, new UpdateContractStatusRequest("Active", "HR"), default));
        await using var verify = fixture.CreateDb();
        var onNext = await verify.EmployeeEntitlements.Where(x => x.ContractId == next.Id).Select(x => x.PayComponentCode).ToListAsync();
        Assert.Equal(["MEDICAL"], onNext); // the ticket was never confirmed on the previous term: it is proposed, not written
    }

    [Fact]
    public async Task TheImportRefusesToReDateOrReStateATermWithFixedOrProposedBenefits()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
        { await Writer(db, PackageSeed.FreezeDay).FreezeTermAsync(s.TenantId, s.Term.Id, default); await db.SaveChangesAsync(); }
        var redate = await ImportAsync(s, Today, "E-1,CON-1,Employment,Active,2026-03-01,2027-01-31,8000,SAR\n");
        Assert.Contains(redate.Errors, e => e.Contains("cannot change its start date or status"));
        var colleagueBatch = await ProposeJobAsync(s, s.ColleagueTerm.Id, Guid.NewGuid(), Today);
        Assert.NotEqual(Guid.Empty, colleagueBatch);
        var restate = await ImportAsync(s, Today, "E-2,CON-2,Employment,Expired,2026-02-01,2027-01-31,6000,SAR\n");
        Assert.Contains(restate.Errors, e => e.Contains("cannot change its start date or status"));
        await using var verify = fixture.CreateDb();
        var term = await verify.EmployeeContracts.SingleAsync(x => x.Id == s.Term.Id);
        Assert.Equal((PackageSeed.TermStart, "Active"), (term.StartDate, term.Status));
        Assert.Equal("Active", await verify.EmployeeContracts.Where(x => x.Id == s.ColleagueTerm.Id).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task AJoiningDateMovedEarlierAfterADirectFreeze_RaisesACodedWarning_AndChangesNothing()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
        { await Writer(db, PackageSeed.FreezeDay).FreezeTermAsync(s.TenantId, s.Term.Id, default); await db.SaveChangesAsync(); }
        await using (var db = fixture.CreateDb())
        {
            (await db.Employees.SingleAsync(x => x.Id == s.Mohammed.Id)).JoiningDate = JoinedLongAgo;
            await db.SaveChangesAsync();
        }
        await using var verify = fixture.CreateDb();
        var package = await new EntitlementResolver(verify).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
        Assert.Contains(PackageReasons.JoiningDateChanged, package.BlockCodes);
        Assert.Equal(2, await verify.EmployeeEntitlements.CountAsync(x => x.ContractId == s.Term.Id && x.Source == EntitlementSources.GradeDefault));
        // The joining date is changed only through the employee update, which needs employees.write and writes history.
        Assert.Equal("employees.write", LegacyRolePermissionResolver.Resolve("Employees", nameof(EmployeesController.UpdateEmployee), ["PUT"]));
    }

    [Fact]
    public async Task SupersedeRacingAConfirm_NeverAnswers500()
    {
        for (var i = 0; i < 8; i++)
        {
            var s = await SeedAsync(seed => seed.Mohammed.JoiningDate = JoinedLongAgo);
            var batch = await ProposeJobAsync(s, s.Term.Id, Guid.NewGuid(), Today);
            var doc = await DocAsync(s, Guid.NewGuid());
            var checker = Guid.NewGuid();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrived = 0;
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async n =>
            {
                await using var db = fixture.CreateRetryingDb();
                if (Interlocked.Increment(ref arrived) == 2) ready.SetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                return n == 0
                    ? await Package(db, s, checker, Today).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, doc), default)
                    : await Contracts(db, s, Today).Supersede(s.Term.Id, new CreateContractRequest(s.Mohammed.PublicId, null, null, null, Today,
                        PackageSeed.TermEnd, 8000m, "SAR", null, null, null), default);
            }));
            Assert.All(results, r => Assert.True(r is OkObjectResult || r is ConflictObjectResult || (r is ObjectResult o && o.StatusCode == 409),
                $"iteration {i}: {r.GetType().Name} {(r as ObjectResult)?.StatusCode}"));
        }
    }

    [Fact]
    public async Task TheTermsOwnFile_UploadedByTheRequester_IsNotSecondPersonEvidence()
    {
        var s = await SeedAsync(seed => { seed.Mohammed.JoiningDate = JoinedLongAgo; seed.Term.FileUrl = "files/con-1.pdf"; });
        var requester = Guid.NewGuid();
        var batch = await ProposeJobAsync(s, s.Term.Id, requester, Today);
        var own = await DocAsync(s, requester, type: "Other", url: "files/con-1.pdf");
        await using (var db = fixture.CreateDb())
            Assert.Equal(PackageReasons.ProposalDocumentRequired,
                Code(await Package(db, s, Guid.NewGuid(), Today).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, own), default)));
        var hr = await DocAsync(s, Guid.NewGuid(), type: "Other", url: "files/con-1.pdf");
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Package(db, s, Guid.NewGuid(), Today).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, hr), default));
    }

    [Fact]
    public async Task SeparationEndsTheActiveTerm_SoANeverStartedBenefitNeverGoesLive()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
        { await Writer(db, PackageSeed.FreezeDay).FreezeTermAsync(s.TenantId, s.Term.Id, default); await db.SaveChangesAsync(); }
        var batch = await ProposeJobAsync(s, s.ColleagueTerm.Id, Guid.NewGuid(), Today);
        await using (var db = fixture.CreateDb())
        {
            // Separated before the term starts: the rows (from 1 Feb) cannot be removed or shortened, and are never shown.
            Assert.Equal(1, await SeparationTermEnder.EndActiveTermsAsync(db, Dispatcher(db, PackageSeed.FreezeDay), s.TenantId, s.Mohammed.PublicId, s.UserId, default));
            Assert.Equal(1, await SeparationTermEnder.EndActiveTermsAsync(db, Dispatcher(db, PackageSeed.FreezeDay), s.TenantId, s.Colleague.PublicId, s.UserId, default));
            await db.SaveChangesAsync();
        }
        await using var verify = fixture.CreateDb();
        Assert.Equal("Terminated", await verify.EmployeeContracts.Where(x => x.Id == s.Term.Id).Select(x => x.Status).SingleAsync());
        // After the termination day (the audited status change) the rows — still dated Feb 2026 to Jan 2027 — are never shown.
        var afterSeparation = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var later = await new EntitlementResolver(verify).ResolveAsync(s.TenantId, s.Mohammed.Id, afterSeparation, default);
        Assert.Null(later.ContractId);
        Assert.DoesNotContain(later.Lines, l => l.Source == PackageLineSources.ContractFrozen);
        Assert.Equal(PackageProposals.Closed, (await PackageProposals.DecisionsAsync(verify, s.TenantId, batch, default))[s.ColleagueTerm.Id]);
    }

    [Fact]
    public void ACommitTimeRefusal_IsMappedToo_NotOnlyOneInsideADbUpdateException()
    {
        var atCommit = new Npgsql.PostgresException("ENTITLEMENT_CONTRACT_NOT_IN_FORCE: entitlement 1 belongs to a contract that is draft", "ERROR", "ERROR", "23514");
        Assert.Equal(PackageReasons.ContractNotInForce, PackageReasons.FromDatabase(atCommit));
    }
}
