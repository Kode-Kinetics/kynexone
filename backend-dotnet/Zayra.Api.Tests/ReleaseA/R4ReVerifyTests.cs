using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Controllers.Contracts;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The R4 re-verification at ccccebb4 as tests (reviewer probe AdvProbeR4bTests: r4b-deviation-probe, r4b-cascade-probe).
/// Amendments go through the REAL Supersede and activation endpoints (UpdateStatus → PendingApproval → Active with the
/// Release A hooks): a drafted amendment changes nothing until activated; on activation the review moves onto it — same end
/// keeps everything, a later end withdraws the options, an earlier end re-derives them (notice passed ⇒ no non-renewal,
/// alike on the options, the radar and the drawer). Art. 55 keeps the term's first-version anchor across an amendment.
/// </summary>
public class R4ReVerifyTests
{
    private static readonly Guid Masar = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly DateOnly Today = new(2026, 10, 18);

    // ── P2: a drafted amendment changes nothing; activation carries the review ───────────────────

    [Fact]
    public async Task ADraftAmendment_ChangesNothing_UntilActivated_ThenTheReviewMovesOntoIt()
    {
        await using var db = InMemory();
        var s = await SeedAsync(db, "Filipino", WorkerNationalityClasses.NonSaudi, RenewalStates.OfferInPreparation);
        var draft = await AmendAsync(db, s, new DateOnly(2026, 6, 1), new DateOnly(2026, 12, 31));

        // Drafted: the review, the radar row, the badges and the reconciliation stay on the version in force.
        var during = await Radar(db, s.Tenant);
        during.Items.Single().ContractId.Should().Be(s.Term.Id);
        during.Items.Single().AmendmentPending.Should().BeTrue();
        (during.Reconciliation.DueActiveContracts, during.Reconciliation.WithOpenReview).Should().Be((1, 1));
        (await db.ContractRenewalCases.SingleAsync()).AllowedActions.Should().BeEquivalentTo(s.Case.AllowedActions);
        (await db.ComplianceAuditLogs.CountAsync(l => l.EntityId == s.Case.Id.ToString())).Should().Be(0);

        await ActivateAsync(db, s.Tenant, draft.Id);

        var carried = await db.ContractRenewalCases.SingleAsync();
        (carried.State, carried.ExpiringContractId, carried.ExpiringEndDate).Should().Be((RenewalStates.OfferInPreparation, s.Term.Id, new DateOnly(2026, 12, 31)));
        carried.AllowedActions.Should().BeEquivalentTo(s.Case.AllowedActions);
        (await db.ComplianceAuditLogs.SingleAsync(l => l.EntityId == carried.Id.ToString() && l.Action == "CarriedToVersion")).MetadataJson
            .Should().Contain(draft.Id.ToString());
        var after = await Radar(db, s.Tenant);
        after.Items.Single().ContractId.Should().Be(draft.Id);
        after.Items.Single().AmendmentPending.Should().BeFalse();
        after.Exceptions.ExpiringWithoutCase.Should().BeEmpty();
        (await Opener(db).OpenOneAsync(s.Tenant, draft.Id, Today, null, "test", default)).Result.Should().Be(RenewalOpenOutcome.AlreadyOpen);
    }

    [Fact]
    public async Task AnActivatedExtension_WithdrawsTheOptions_AndAnExtensionAfterAnActionIsRefused()
    {
        await using var db = InMemory();
        var s = await SeedAsync(db, "Filipino", WorkerNationalityClasses.NonSaudi, RenewalStates.Open);
        var draft = await AmendAsync(db, s, new DateOnly(2026, 6, 1), new DateOnly(2027, 6, 30));
        await ActivateAsync(db, s.Tenant, draft.Id);
        var review = await db.ContractRenewalCases.SingleAsync();
        (review.State, review.ExpiringEndDate).Should().Be((RenewalStates.Open, new DateOnly(2027, 6, 30)));
        review.AllowedActions.Should().BeEmpty("an extension is a renewal decision: HR confirms the history first");

        await using var db2 = InMemory();
        var s2 = await SeedAsync(db2, "Filipino", WorkerNationalityClasses.NonSaudi, RenewalStates.OfferInPreparation, ContractActions.RenewAsIs);
        var refused = await Contracts(db2, s2.Tenant).Supersede(s2.Term.Id, Request(s2, new DateOnly(2026, 6, 1), new DateOnly(2027, 6, 30)), default);
        Error(refused).Should().Be(ReleaseABlockReasons.RenewalCaseInProgress);
    }

    // ── P1-2: a shortening amendment ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AShorteningAmendment_ReDerivesOptionsAndDeadlines_AndAPassedNoticeDateWithdrawsNonRenewalEverywhere()
    {
        await using var db = InMemory();
        var s = await SeedAsync(db, "Filipino", WorkerNationalityClasses.NonSaudi, RenewalStates.Open);
        s.Case.AllowedActions.Should().Contain(ContractActions.NonRenew);
        // End brought forward to 30 Nov: the notice date becomes 1 Oct, already passed on 18 Oct.
        var draft = await AmendAsync(db, s, new DateOnly(2026, 9, 1), new DateOnly(2026, 11, 30));
        await ActivateAsync(db, s.Tenant, draft.Id);

        var review = await db.ContractRenewalCases.SingleAsync();
        (review.ExpiringEndDate, review.NoticeDueOn).Should().Be((new DateOnly(2026, 11, 30), (DateOnly?)new DateOnly(2026, 10, 1)));
        review.AllowedActions.Should().NotContain(ContractActions.NonRenew);
        var row = (await Radar(db, s.Tenant)).Items.Single();
        row.AllowedActions.Should().NotContain(ContractActions.NonRenew);
        row.BlockReasons.Select(b => b.Code).Should().Contain(ReleaseABlockReasons.RenewalNoticeDatePassed);
        var chain = (ContractChainDto)((OkObjectResult)(await Renewals(db, s.Tenant).Chain(draft.Id, default)).Result!).Value!;
        chain.NextAllowedActions.Should().NotContain(ContractActions.NonRenew);
        chain.BlockReasons.Select(b => b.Code).Should().Contain(ReleaseABlockReasons.RenewalNoticeDatePassed);
    }

    [Fact]
    public async Task AShorteningAmendment_IsRefusedOnceAnActionIsChosen_WithACodedArabicReason()
    {
        await using var db = InMemory();
        var s = await SeedAsync(db, "Filipino", WorkerNationalityClasses.NonSaudi, RenewalStates.InApproval, ContractActions.RenewAsIs);
        var refused = await Contracts(db, s.Tenant).Supersede(s.Term.Id, Request(s, new DateOnly(2026, 9, 1), new DateOnly(2026, 11, 30)), default);
        Error(refused).Should().Be(ReleaseABlockReasons.RenewalCaseInProgress);
        Prop(((ObjectResult)refused).Value!, "messageAr").Should().MatchRegex(@"\p{IsArabic}");
        (await db.EmployeeContracts.SingleAsync(c => c.Id == s.Term.Id)).Status.Should().Be("Active", "nothing was superseded");
    }

    // ── P1-1: Art. 55 keeps the term's first-version anchor across an amendment ───────────────────

    [Fact]
    public async Task AKeepEndAmendment_DoesNotShortenTheTerm_ConvertOrNonRenewStays_With12Months()
    {
        // Saudi, chain from 1 Jan 2024; renewal #1 runs 1 Jan – 31 Dec 2026; amended from 1 Jul 2026 (same end).
        await using var db = InMemory();
        var s = await SeedAsync(db, "Saudi", WorkerNationalityClasses.Saudi, RenewalStates.Open, chainStart: new DateOnly(2024, 1, 1), renewal: 1,
            allowed: [ContractActions.ConvertIndefinite, ContractActions.NonRenew]);
        db.EmployeeContracts.Add(new EmployeeContract
        {
            TenantId = s.Tenant, CompanyId = Masar, EmployeeId = s.Employee.PublicId, ContractNumber = "CON-0", Status = "Expired",
            StartDate = new DateOnly(2024, 1, 1), EndDate = new DateOnly(2025, 12, 31), WorkerNationalityClass = WorkerNationalityClasses.Saudi,
            RenewalNumber = 0, ChainStartedOn = new DateOnly(2024, 1, 1), ChainSource = ChainSources.Derived,
        });
        await db.SaveChangesAsync();
        var draft = await AmendAsync(db, s, new DateOnly(2026, 7, 1), new DateOnly(2026, 12, 31));
        await ActivateAsync(db, s.Tenant, draft.Id);

        var confirmed = await Renewals(db, s.Tenant).ConfirmChain(draft.Id,
            new ChainConfirmRequest(null, new DateOnly(2024, 1, 1), WorkerNationalityClasses.Saudi, true, null, 1), Opener(db), default);
        var chain = (ContractChainDto)confirmed.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

        (await db.ContractRenewalCases.SingleAsync()).AllowedActions.Should().BeEquivalentTo([ContractActions.ConvertIndefinite, ContractActions.NonRenew]);
        chain.NextAllowedActions.Should().BeEquivalentTo([ContractActions.ConvertIndefinite, ContractActions.NonRenew]);
        chain.Art55.Should().Match<Art55Meter>(m => m.ThresholdReached && m.YearsIfRenewed == 4.0m && m.YearsServed == 3.0m);
        var dto = (RenewalCaseDto)((OkObjectResult)(await Renewals(db, s.Tenant).GetCase(s.Case.Id, default)).Result!).Value!;
        dto.Art55.YearsIfRenewed.Should().Be(4.0m, "the renewed term is 12 months from 1 Jan 2027, not 6");
        AllowedActionsDeriver.TermMonths(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).Should().Be(12);
    }

    // ── P3-6 / P3-7: contradictions need an explicit "I've checked"; no-op confirmations write nothing ──

    [Fact]
    public async Task ConfirmingHistoryThatContradictsRecordedHistory_NeedsAnExplicitCheck_AndAnIdenticalConfirmWritesNothing()
    {
        await using var db = InMemory();
        var s = await SeedAsync(db, "Saudi", WorkerNationalityClasses.Saudi, RenewalStates.Open, chainStart: new DateOnly(2019, 1, 1), renewal: 4,
            allowed: [ContractActions.ConvertIndefinite, ContractActions.NonRenew]);
        var earlier = new EmployeeContract
        {
            TenantId = s.Tenant, CompanyId = Masar, EmployeeId = s.Employee.PublicId, ContractNumber = "CON-PREV", Status = "Expired",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), WorkerNationalityClass = WorkerNationalityClasses.Saudi,
            RenewalNumber = 3, ChainStartedOn = new DateOnly(2019, 1, 1), ChainSource = ChainSources.Recorded,
        };
        db.EmployeeContracts.Add(earlier);
        await db.SaveChangesAsync();
        var controller = Renewals(db, s.Tenant);
        var contradicting = new ChainConfirmRequest(null, new DateOnly(2021, 1, 1), WorkerNationalityClasses.Saudi, true, null, 1);

        var refused = await controller.ConfirmChain(s.Term.Id, contradicting, Opener(db), default);
        var body = refused.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        body.StatusCode.Should().Be(409);
        Prop(body.Value!, "error").Should().Be("chain_contradicts_recorded_history");
        Prop(body.Value!, "messageAr").Should().MatchRegex(@"\p{IsArabic}");
        (await db.EmployeeContracts.SingleAsync(c => c.Id == s.Term.Id)).RenewalNumber.Should().Be(4, "nothing was written");

        (await controller.ConfirmChain(s.Term.Id, contradicting with { AcknowledgeContradiction = true }, Opener(db), default)).Result
            .Should().BeOfType<OkObjectResult>();
        (await db.ComplianceAuditLogs.SingleAsync(l => l.Action == "ChainConfirmed")).MetadataJson.Should().Contain("\"acknowledgedContradiction\":true");

        var audits = await db.ComplianceAuditLogs.CountAsync();
        (await controller.ConfirmChain(s.Term.Id, contradicting with { AcknowledgeContradiction = true }, Opener(db), default)).Result
            .Should().BeOfType<OkObjectResult>();
        (await db.ComplianceAuditLogs.CountAsync()).Should().Be(audits, "re-confirming identical history changes nothing and records nothing");
    }

    // ── P2-4: the import guard follows the term ──────────────────────────────────────────────────

    [Fact]
    public async Task ReImportingAnAmendedVersionOfATermUnderReview_IsRefused()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        var company = new Company { Id = Masar, TenantId = tenantId, LegalNameEn = "Masar", RegistrationNumber = "CR-1", IsActive = true };
        var employee = new Employee { TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E-1", FullName = "Mohammed", Status = "Active" };
        db.AddRange(company, employee);
        var v1 = new EmployeeContract { TenantId = tenantId, CompanyId = Masar, EmployeeId = employee.PublicId, ContractNumber = "CON-V1",
            Status = "Superseded", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31) };
        var v2 = new EmployeeContract { TenantId = tenantId, CompanyId = Masar, EmployeeId = employee.PublicId, ContractNumber = "CON-V2",
            Status = "Active", StartDate = new DateOnly(2026, 6, 1), EndDate = new DateOnly(2026, 12, 31), PreviousVersionId = v1.Id, Version = 2 };
        db.EmployeeContracts.AddRange(v1, v2);
        db.ContractRenewalCases.Add(new ContractRenewalCase
        {
            TenantId = tenantId, CompanyId = Masar, EmployeeId = employee.PublicId, ExpiringContractId = v1.Id,
            ExpiringEndDate = new DateOnly(2026, 12, 31), AllowedActions = [ContractActions.RenewAsIs], NoticeDueOn = new DateOnly(2026, 11, 1),
        });
        await db.SaveChangesAsync();
        var controller = new MigrationImportController(db, new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(),
            new Zayra.Api.Infrastructure.Audit.AuditService(db)) { ControllerContext = Ctx(tenantId) };

        var result = await controller.Commit(new MigrationPackageRequest("r4-reimport", new Dictionary<string, string>
        {
            ["contracts"] = "EmployeeCode,ContractNumber,ContractType,Status,StartDate,EndDate,BasicSalary,CurrencyCode\n"
                            + "E-1,CON-V2,Employment,Active,2026-06-01,2027-06-30,9000,SAR\n",
        }), default);
        var dto = (MigrationReconciliationDto)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        dto.Errors.Should().ContainSingle(e => e.Contains("open renewal review"));
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record Seeded(Guid Tenant, Employee Employee, EmployeeContract Term, ContractRenewalCase Case);

    private static ZayraDbContext InMemory() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"r4-reverify-{Guid.NewGuid():N}").Options);

    private static async Task<Seeded> SeedAsync(ZayraDbContext db, string nationality, string nationalityClass, string state, string? action = null,
        DateOnly? chainStart = null, short renewal = 0, string[]? allowed = null)
    {
        var tenantId = Guid.NewGuid();
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        var emp = new Employee { TenantId = tenantId, CompanyId = Masar, EmployeeCode = "E-1", FullName = "Test Person", Nationality = nationality,
            Status = "Active", JoiningDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.Employees.Add(emp);
        var term = new EmployeeContract
        {
            TenantId = tenantId, CompanyId = Masar, EmployeeId = emp.PublicId, ContractNumber = "CON-1", Status = "Active",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), WorkerNationalityClass = nationalityClass,
            RenewalNumber = renewal, ChainStartedOn = chainStart ?? new DateOnly(2026, 1, 1), ChainSource = ChainSources.Derived,
            BasicSalary = 5000m, CurrencyCode = "SAR",
        };
        var d = RenewalDeadlineCalculator.Compute(term, RenewalRuleSet.Defaults);
        var review = new ContractRenewalCase
        {
            TenantId = tenantId, CompanyId = Masar, EmployeeId = emp.PublicId, ExpiringContractId = term.Id, ExpiringEndDate = term.EndDate!.Value,
            WorkerNationalityClass = nationalityClass,
            AllowedActions = allowed ?? [ContractActions.RenewAsIs, ContractActions.RenewWithChanges, ContractActions.NonRenew],
            State = state, ContractAction = action, NoticeDueOn = d.NoticeDueOn, OfferDueOn = d.OfferDueOn, QiwaSubmitDueOn = d.QiwaSubmitDueOn,
            QiwaGateDueOn = d.QiwaGateDueOn, QiwaRequired = true,
        };
        db.EmployeeContracts.Add(term);
        db.ContractRenewalCases.Add(review);
        await db.SaveChangesAsync();
        return new Seeded(tenantId, emp, term, review);
    }

    private static CreateContractRequest Request(Seeded s, DateOnly start, DateOnly end) =>
        new(s.Employee.PublicId, null, null, null, start, end, 6000m, "SAR", null, null, null);

    private static async Task<EmployeeContract> AmendAsync(ZayraDbContext db, Seeded s, DateOnly start, DateOnly end)
    {
        var result = await Contracts(db, s.Tenant).Supersede(s.Term.Id, Request(s, start, end), default);
        return (EmployeeContract)result.Should().BeOfType<OkObjectResult>().Subject.Value!;
    }

    /// <summary>Activates through the real endpoint: Draft → PendingApproval → Active, with the Release A hooks.</summary>
    private static async Task ActivateAsync(ZayraDbContext db, Guid tenantId, Guid contractId)
    {
        var contracts = Contracts(db, tenantId);
        (await contracts.UpdateStatus(contractId, new UpdateContractStatusRequest("PendingApproval", null), default)).Should().BeOfType<OkObjectResult>();
        (await contracts.UpdateStatus(contractId, new UpdateContractStatusRequest("Active", "HR Lead"), default)).Should().BeOfType<OkObjectResult>();
    }

    private static ContractsController Contracts(ZayraDbContext db, Guid tenantId) =>
        new(db, new ContractTermLifecycleDispatcher(
            [new ContractChainStamper(db, NullLogger<ContractChainStamper>.Instance, new FixedClock(Today))],
            new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions()))))
        { ControllerContext = Ctx(tenantId) };

    private static ContractRenewalsController Renewals(ZayraDbContext db, Guid tenantId) =>
        new(db, new FixedClock(Today)) { ControllerContext = Ctx(tenantId) };

    private static RenewalCaseOpener Opener(ZayraDbContext db) => new(db, new ContractChainCensus(db));

    private static Task<RenewalRadarDto> Radar(ZayraDbContext db, Guid tenantId) =>
        RenewalCaseReadModel.RadarAsync(db, Opener(db), tenantId, null, 120, Today, default);

    private static ControllerContext Ctx(Guid tenantId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "HR Manager"), new Claim("name", "HR Lead"),
            ], "Test")),
        },
    };

    private static string? Error(IActionResult result)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(409);
        return Prop(obj.Value!, "error");
    }

    private static string? Prop(object value, string name) => (string?)value.GetType().GetProperty(name)?.GetValue(value);

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }
}
