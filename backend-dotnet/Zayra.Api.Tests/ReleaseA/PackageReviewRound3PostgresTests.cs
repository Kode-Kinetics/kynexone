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
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Review round 3 of #192 on PostgreSQL with R0's triggers — the reviewer's probes (ZzReviewProbeR2cTests) as assertions:
/// no route around four eyes (supersede, a new term, the import), nothing confirmed onto a term that has ended, a refused
/// import row leaves the database unchanged, and the as-of view follows the term that owned the date.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PackageReviewRound3PostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);

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
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(s, userId, "entitlements.manage") } } };
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

    private async Task<IActionResult> ActivateAsync(PackageSeed s, Guid id, DateOnly today)
    {
        await using (var db0 = fixture.CreateDb())
            await Contracts(db0, s, today).UpdateStatus(id, new UpdateContractStatusRequest("PendingApproval", null), default);
        await using var db = fixture.CreateDb();
        return await Contracts(db, s, today).UpdateStatus(id, new UpdateContractStatusRequest("Active", "HR"), default);
    }

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

    private async Task<Guid> ContractDocAsync(PackageSeed s)
    {
        await using var db = fixture.CreateDb();
        var d = new EmployeeDocument { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.Id, DocumentType = "Contract",
            FileName = "c.pdf", StorageUrl = "x", UploadedBy = Guid.NewGuid() };
        db.EmployeeDocuments.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    private static string? Code(IActionResult result) => result is ObjectResult { Value: { } v }
        && JsonDocument.Parse(JsonSerializer.Serialize(v)).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;

    private async Task<int> RowCountAsync(PackageSeed s)
    {
        await using var db = fixture.CreateDb();
        return await db.EmployeeEntitlements.CountAsync(x => x.TenantId == s.TenantId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SupersedingARunningUnpackagedTerm_DoesNotFreezeFromTheGradeTable_ItWaitsForAProposal(int daysAhead)
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
            Assert.Equal(PackageReasons.TermRunningNeedsProposal,
                Code(await Package(db, s, s.UserId, Today).Freeze(s.Mohammed.Id, new PackageContractRequest(s.Term.Id), default)));
        EmployeeContract v2;
        await using (var db = fixture.CreateDb())
            v2 = (EmployeeContract)((OkObjectResult)await Contracts(db, s, Today).Supersede(s.Term.Id,
                new CreateContractRequest(s.Mohammed.PublicId, null, null, null, Today.AddDays(daysAhead), PackageSeed.TermEnd, 8000m, "SAR", null, null, null), default)).Value!;

        Assert.IsType<OkObjectResult>(await ActivateAsync(s, v2.Id, Today));
        Assert.Equal(0, await RowCountAsync(s)); // one person cannot write a package for a predecessor nobody confirmed
        await using var db2 = fixture.CreateDb();
        var status = (IEnumerable<ContractPackageStatusDto>)((OkObjectResult)await Package(db2, s, Guid.NewGuid(), Today)
            .ContractsPackageStatus([v2.Id], default)).Value!;
        Assert.Equal(ContractPackageNextActions.ProposeBenefits, Assert.Single(status).NextAction);
    }

    [Fact]
    public async Task ANewTermStartingToday_AfterAnUnpackagedTerm_WaitsForAProposal_ButAFirstTermIsFrozenDirectly()
    {
        var s = await SeedAsync(seed => seed.Term.Status = "PendingApproval");
        // First term, not yet started: the grade defaults are written on activation.
        Assert.IsType<OkObjectResult>(await ActivateAsync(s, s.Term.Id, PackageSeed.FreezeDay));
        Assert.Equal(2, await RowCountAsync(s));

        // The colleague's term was never packaged; a new term for them starting today waits for a second person.
        var next = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.OtherCompany.Id, EmployeeId = s.Colleague.PublicId, EmployeeName = "Colleague",
            ContractNumber = "CON-COLLEAGUE-2", Status = "PendingApproval", StartDate = Today.AddDays(1), EndDate = new DateOnly(2027, 10, 6),
            BasicSalary = 6000m, CurrencyCode = "SAR", WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb()) { db.EmployeeContracts.Add(next); await db.SaveChangesAsync(); }
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, Today).UpdateStatus(next.Id, new UpdateContractStatusRequest("Active", "HR"), default));
        await using var verify = fixture.CreateDb();
        Assert.False(await verify.EmployeeEntitlements.AnyAsync(x => x.ContractId == next.Id));
    }

    [Fact]
    public async Task AProposalCannotBeConfirmedAfterTheTermWasTerminated_ItIsClosedWithTheTerm()
    {
        var s = await SeedAsync();
        var batch = await ProposeJobAsync(s, s.Term.Id, Guid.NewGuid(), Today);
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, Today).UpdateStatus(s.Term.Id, new UpdateContractStatusRequest("Terminated", null), default));
        await using (var db = fixture.CreateDb())
        {
            var closed = await db.ComplianceAuditLogs.SingleAsync(x => x.TenantId == s.TenantId && x.EntityType == PackageProposals.AuditEntity);
            Assert.Equal((PackageProposals.Closed, PackageProposals.Key(batch, s.Term.Id)), (closed.Action, closed.EntityId));
        }
        var doc = await ContractDocAsync(s);
        await using (var db = fixture.CreateDb())
            Assert.Equal(PackageReasons.ProposalClosed,
                Code(await Package(db, s, Guid.NewGuid(), Today.AddDays(1)).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, doc), default)));
        // Even with the ledger bypassed, the writer refuses a term that is no longer active.
        await using (var db = fixture.CreateDb())
        {
            var proposal = (await Writer(db, Today).ProposeAsync(s.TenantId, s.ColleagueTerm.Id, default))! with { ContractId = s.Term.Id };
            var ex = await Assert.ThrowsAsync<EntitlementWriteRefusedException>(() => Writer(db, Today).WriteConfirmedProposalAsync(s.TenantId, proposal, default));
            Assert.Equal(PackageReasons.ContractNotInForce, ex.Code);
        }
        Assert.Equal(0, await RowCountAsync(s));
    }

    [Fact]
    public async Task AConfirmThatWouldWriteNothing_IsRefused_AndTheProposalStaysOpen()
    {
        var s = await SeedAsync();
        var batch = await ProposeJobAsync(s, s.Term.Id, Guid.NewGuid(), Today);
        // Another term now owns these benefits for the same dates, so every proposed row would overlap.
        var other = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, EmployeeName = "M",
            ContractNumber = "CON-OTHER", Status = "Active", StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2027, 8, 31),
            BasicSalary = 8000m, CurrencyCode = "SAR", WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb())
        {
            db.EmployeeContracts.Add(other);
            await db.SaveChangesAsync();
            var w = Writer(db, Today);
            await w.WriteConfirmedProposalAsync(s.TenantId, (await w.ProposeAsync(s.TenantId, other.Id, default))!, default);
            await db.SaveChangesAsync();
        }
        var doc = await ContractDocAsync(s);
        await using (var db = fixture.CreateDb())
            Assert.Equal(PackageReasons.TermOverlap,
                Code(await Package(db, s, Guid.NewGuid(), Today).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, doc), default)));
        await using var verify = fixture.CreateDb();
        Assert.Empty(await PackageProposals.DecisionsAsync(verify, s.TenantId, batch, default)); // still open
    }

    [Fact]
    public async Task ThreeConfirmsAtOnce_WriteThePackageOnce()
    {
        var s = await SeedAsync();
        var batch = await ProposeJobAsync(s, s.Term.Id, Guid.NewGuid(), Today);
        var doc = await ContractDocAsync(s);
        var results = await Race(3, async _ =>
        {
            await using var db = fixture.CreateRetryingDb();
            return await Package(db, s, Guid.NewGuid(), Today).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, doc), default);
        });
        Assert.Single(results, r => r is OkObjectResult);
        Assert.All(results.Where(r => r is not OkObjectResult), r => Assert.Equal(PackageReasons.ProposalClosed, Code(r)));
        Assert.Equal(2, await RowCountAsync(s));
    }

    [Fact]
    public async Task AConfirmRacingTheActivationOfAnOverlappingTerm_NeverFails_AndNoBenefitIsFixedTwice()
    {
        var s = await SeedAsync();
        var next = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, EmployeeName = "M",
            ContractNumber = "CON-NEW", Status = "PendingApproval", StartDate = Today, EndDate = new DateOnly(2027, 10, 5),
            BasicSalary = 8000m, CurrencyCode = "SAR", WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb()) { db.EmployeeContracts.Add(next); await db.SaveChangesAsync(); }
        var batch = await ProposeJobAsync(s, s.Term.Id, Guid.NewGuid(), Today);
        var doc = await ContractDocAsync(s);
        var results = await Race(2, async n =>
        {
            await using var db = fixture.CreateRetryingDb();
            return n == 0
                ? await Package(db, s, Guid.NewGuid(), Today).ConfirmProposal(batch, new ConfirmProposalRequest(s.Term.Id, doc), default)
                : await Contracts(db, s, Today).UpdateStatus(next.Id, new UpdateContractStatusRequest("Active", "HR"), default);
        });
        Assert.All(results, r => Assert.True(r is OkObjectResult || r is ConflictObjectResult, r.GetType().Name));
        await using var verify = fixture.CreateDb();
        var rows = await verify.EmployeeEntitlements.Where(x => x.TenantId == s.TenantId).ToListAsync();
        Assert.Equal(rows.Count, rows.Select(r => r.PayComponentCode).Distinct().Count());
    }

    [Fact]
    public async Task AnImportRowTheHooksRefuse_LeavesTheDatabaseUnchanged()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
        { await Writer(db, PackageSeed.FreezeDay).FreezeTermAsync(s.TenantId, s.Term.Id, default); await db.SaveChangesAsync(); }
        MigrationReconciliationDto dto;
        await using (var db = fixture.CreateDb())
        {
            var import = new MigrationImportController(db, new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(),
                new Zayra.Api.Infrastructure.Audit.AuditService(db), Dispatcher(db, PackageSeed.FreezeDay))
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(s, s.UserId) } } };
            const string header = "EmployeeCode,ContractNumber,ContractType,Status,StartDate,EndDate,BasicSalary,CurrencyCode\n";
            var result = await import.Commit(new MigrationPackageRequest($"r3-{Guid.NewGuid():N}", new Dictionary<string, string>
            {
                // Terminating a term before its benefits start is refused; the row must leave nothing behind.
                ["contracts"] = header + "E-1,CON-1,Employment,Terminated,2026-02-01,2027-01-31,8000,SAR\n",
            }), default);
            dto = (MigrationReconciliationDto)Assert.IsType<OkObjectResult>(result.Result).Value!;
        }
        Assert.Contains(dto.Errors, e => e.Contains("never removed"));
        await using var verify = fixture.CreateDb();
        Assert.Equal("Active", await verify.EmployeeContracts.Where(x => x.Id == s.Term.Id).Select(x => x.Status).SingleAsync());
        Assert.All(await verify.EmployeeEntitlements.Where(x => x.ContractId == s.Term.Id).ToListAsync(), r => Assert.Equal((DateOnly?)PackageSeed.TermEnd, r.EffectiveTo));
    }

    [Fact]
    public async Task AfterABackDatedAmendment_TheAsOfViewFollowsTheTermThatOwnedTheDate()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            var w = Writer(db, new DateOnly(2026, 3, 1));
            await w.WriteConfirmedProposalAsync(s.TenantId, (await w.ProposeAsync(s.TenantId, s.Term.Id, default))!, default);
            await db.SaveChangesAsync();
        }
        EmployeeContract v2;
        await using (var db = fixture.CreateDb())
            v2 = (EmployeeContract)((OkObjectResult)await Contracts(db, s, Today).Supersede(s.Term.Id,
                new CreateContractRequest(s.Mohammed.PublicId, null, null, null, new DateOnly(2026, 4, 1), PackageSeed.TermEnd, 8000m, "SAR", null, null, null), default)).Value!;
        Assert.IsType<OkObjectResult>(await ActivateAsync(s, v2.Id, Today));

        await using var verify = fixture.CreateDb();
        var may = await new EntitlementResolver(verify).ResolveAsync(s.TenantId, s.Mohammed.Id, new DateOnly(2026, 5, 1), default);
        var medicalMay = may.Lines.Single(l => l.ComponentCode == "MEDICAL");
        Assert.Equal(PackageLineSources.ContractFrozen, medicalMay.Source); // the predecessor's row, which owned 1 May
        var rowMay = await verify.EmployeeEntitlements.SingleAsync(x => x.Id == medicalMay.EmployeeEntitlementId);
        Assert.Equal(s.Term.Id, rowMay.ContractId);
        var later = await new EntitlementResolver(verify).ResolveAsync(s.TenantId, s.Mohammed.Id, Today.AddDays(1), default);
        var laterRowId = later.Lines.Single(l => l.ComponentCode == "MEDICAL").EmployeeEntitlementId;
        var rowLater = await verify.EmployeeEntitlements.SingleAsync(x => x.Id == laterRowId);
        Assert.Equal(v2.Id, rowLater.ContractId);
    }

    private static async Task<IActionResult[]> Race(int n, Func<int, Task<IActionResult>> body)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        return await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
        {
            if (Interlocked.Increment(ref arrived) == n) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return await body(i);
        }));
    }
}
