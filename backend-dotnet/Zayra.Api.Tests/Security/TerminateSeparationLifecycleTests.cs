using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using Zayra.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// D1 — a direct termination must produce authoritative separation data.
///
/// <para>THE DEFECT. <c>POST /employees/{id}/terminate</c> set <c>Status = "Terminated"</c> and created
/// nothing else. <c>/final-settlement</c> requires an <c>EmployeeOffboarding</c> carrying a
/// <c>LastWorkingDay</c> (400 <c>not_a_leaver</c> otherwise), and <c>POST /api/offboarding</c> refuses a
/// non-occupying employee — and "Terminated" is not occupying. A directly-terminated employee could
/// therefore never be settled and never be routed back into offboarding: an unsettleable dead end, and
/// a hard blocker for migrating staff already terminated in a prior system.</para>
///
/// <para>Wave 0's hardening of <c>/final-settlement</c> is what made this reachable, so it is fixed at
/// the lifecycle source rather than by relaxing that gate — the gate is correct.</para>
/// </summary>
public class TerminateSeparationLifecycleTests
{
    private static readonly DateOnly Lwd = new(2026, 7, 31);

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (EmployeeManagementService Svc, Employee Emp) Seed(ZayraDbContext db, Guid tenantId)
    {
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E-100", FullName = "Ahmed Al-Rashidi",
            Department = "Finance", Designation = "Analyst",
            Status = EmployeeStatuses.Active, Nationality = "SAU",
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        db.SaveChanges();
        var svc = new EmployeeManagementService(
            db, new Zayra.Api.Infrastructure.Audit.AuditService(db), new NullDocumentStorage(),
            TestNotifications.For(db));
        return (svc, emp);
    }

    private static readonly Zayra.Api.Application.Auth.RequestContext Ctx =
        new("127.0.0.1", "tests", UserId: Guid.NewGuid());

    private static EmployeeStatusChangeRequest TerminateCmd(string? separationType = null) =>
        new("Terminated", Lwd, "Role eliminated", separationType);

    // ── One authoritative separation ──────────────────────────────────────────

    [Fact]
    public async Task DirectTerminate_CreatesExactlyOneAuthoritativeSeparation()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        var separations = await db.EmployeeOffboardings.Where(o => o.EmployeeId == emp.Id).ToListAsync();
        separations.Should().HaveCount(1);
        var sep = separations[0];
        sep.LastWorkingDay.Should().Be(Lwd, "the last working day is derived from the command's effective date");
        sep.SeparationType.Should().Be("Termination", "an employer-initiated command defaults conservatively");
        sep.Status.Should().Be("InProgress");
        sep.NoticePeriodDays.Should().Be(0, "a direct termination is immediate — no notice is being served");
    }

    [Fact]
    public async Task DirectTerminate_HonoursAnExplicitSeparationType()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd("Resignation"), Ctx, default);

        (await db.EmployeeOffboardings.SingleAsync(o => o.EmployeeId == emp.Id))
            .SeparationType.Should().Be("Resignation",
                "the separation type decides the gratuity, so it comes from the explicit command");
    }

    // ── Preserve an existing offboarding ──────────────────────────────────────

    [Fact]
    public async Task ExistingInProgressOffboarding_IsPreserved_NotOverwritten()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        var approvedLwd = new DateOnly(2026, 9, 30);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = emp.Id, SeparationType = "Resignation",
            Reason = "Approved resignation with notice", Status = "InProgress",
            LastWorkingDay = approvedLwd, NoticePeriodDays = 60, CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        var sep = await db.EmployeeOffboardings.SingleAsync(o => o.EmployeeId == emp.Id);
        sep.LastWorkingDay.Should().Be(approvedLwd, "an approved date must never be overwritten by a status command");
        sep.SeparationType.Should().Be("Resignation");
        sep.NoticePeriodDays.Should().Be(60);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatedTerminate_IsIdempotent()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);
        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);
        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        (await db.EmployeeOffboardings.CountAsync(o => o.EmployeeId == emp.Id))
            .Should().Be(1, "a retried termination must not create duplicate separations");
    }

    // ── Rehire gets a NEW service period ──────────────────────────────────────

    [Fact]
    public async Task RehireThenTerminate_CreatesANewSeparation_ForTheNewServicePeriod()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        // First separation, left exactly as the product leaves it — InProgress. Deliberately NOT
        // hand-set to Completed: an earlier version of this test did that, which constructed a state the
        // rehire path can never actually reach (Complete demands a PAID settlement), so it passed
        // without exercising the real behaviour.
        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        // Rehired through the ordinary reactivation path.
        await svc.ActivateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Active", new DateOnly(2026, 9, 1), "Rehired"),
            Ctx, default);

        (await db.EmployeeOffboardings.SingleAsync(o => o.EmployeeId == emp.Id)).Status
            .Should().Be("Cancelled", "reactivation closes the prior service period");

        await svc.TerminateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Terminated", new DateOnly(2027, 3, 31), "Second exit"),
            Ctx, default);

        var all = await db.EmployeeOffboardings.Where(o => o.EmployeeId == emp.Id).ToListAsync();
        all.Should().HaveCount(2, "the new service period gets its own separation");
        var live = all.Single(o => o.Status == "InProgress");
        live.LastWorkingDay.Should().Be(new DateOnly(2027, 3, 31),
            "settling the SECOND exit against the FIRST period's last working day would underpay the " +
            "gratuity, leave encashment and wage proration — with the employer's own settlement as evidence");
    }

    [Fact]
    public async Task AnAlreadyTerminatedEmployeeWithNoSeparation_IsRemediatedByReRunningTerminate()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        // The migrated / pre-existing population: already Terminated, no separation. An earlier version
        // of the fix also required the PREVIOUS status to be non-exit, which excluded exactly these
        // employees and left the dead end permanent for them.
        emp.Status = EmployeeStatuses.Terminated;
        await db.SaveChangesAsync();

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        (await db.EmployeeOffboardings.CountAsync(o => o.EmployeeId == emp.Id))
            .Should().Be(1, "an already-terminated employee must be recoverable, not permanently stranded");
    }

    [Fact]
    public void AnUnknownSeparationType_IsRefused_NotSilentlyPaidAsAFullAward()
    {
        // "Article 80" with a space, "Absconding", "Gross Misconduct" — all pass through
        // NormalizeTerminationReason to a FULL Art.84 award, where the statute forfeits it.
        var act = () => EmployeeManagementService.NormalizeSeparationType("Article 80");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*decides the end-of-service award*");
    }

    [Theory]
    [InlineData("resignation", "Resignation")]
    [InlineData("ARTICLE80", "Article80")]
    [InlineData(null, "Termination")]
    public void KnownSeparationTypes_NormaliseToTheCanonicalCasing(string? input, string expected)
        => EmployeeManagementService.NormalizeSeparationType(input).Should().Be(expected);

    // ── Atomicity: status and separation cannot disagree ──────────────────────

    [Fact]
    public async Task StatusAndSeparation_AreCommittedTogether()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        // Re-read from a FRESH context view of the store: whatever is durable must be consistent.
        var stored = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id);
        var sep = await db.EmployeeOffboardings.AsNoTracking().SingleOrDefaultAsync(o => o.EmployeeId == emp.Id);

        stored.Status.Should().Be("Terminated");
        sep.Should().NotBeNull(
            "a terminated employee with no separation is the dead-end state D1 exists to make unreachable");
    }

    // ── The salary evidence EOSB needs must survive the termination ───────────

    [Fact]
    public async Task Terminate_DoesNotDeactivateSalaryEvidenceNeededByEosb()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, BasicSalary = 10_000m, Currency = "SAR",
            EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
        });
        await db.SaveChangesAsync();

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        (await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == emp.Id))
            .IsActive.Should().BeTrue(
                "EOSB and final settlement read the active salary row for a Terminated employee — " +
                "deactivating it here would corrupt the gratuity");
    }

    // ── THE ACCEPTANCE PROOF: terminate → final settlement no longer dead-ends ──

    [Fact]
    public async Task DirectTerminate_ThenFinalSettlement_NoLongerFailsNotALeaver()
    {
        using var db = NewDb();
        var tenantId = Guid.NewGuid();

        // A complete, settleable KSA employee: legal entity, EOSB enabled, an active salary row.
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Test KSA Entity", CountryCode = "SA",
            DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E-200", FullName = "Ahmed Al-Rashidi",
            Status = EmployeeStatuses.Active, Nationality = "SAU", CompanyId = company.Id,
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, EosbMinYears = 1,
        });
        db.SaveChanges();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, BasicSalary = 10_000m, Currency = "SAR",
            EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
        });
        db.SaveChanges();

        var svc = new EmployeeManagementService(
            db, new Zayra.Api.Infrastructure.Audit.AuditService(db), new NullDocumentStorage(),
            TestNotifications.For(db));

        // The ONLY separation action taken is the direct terminate command.
        await svc.TerminateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Terminated", new DateOnly(2023, 1, 1), "Role eliminated"),
            Ctx, default);

        var payroll = MakePayrollCtrl(db, tenantId);
        var result = await payroll.FinalSettlement(
            new FinalSettlementRequest(emp.Id, new DateOnly(2023, 1, 1)), default);

        result.Should().NotBeOfType<BadRequestObjectResult>(
            "the direct-terminate path must now satisfy the leaver gate — this is the dead end D1 closes");
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value!;
        ((decimal)body.GetType().GetProperty("eosbAmount")!.GetValue(body)!)
            .Should().Be(15_000m, "3 years × 0.5 month × 10,000 — the Art.84 award for an employer termination");
    }

    private static PayrollController MakePayrollCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.Name, "Payroll User"),
        };
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "test"));
        var httpCtx = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal };
        var rules = new StubRuleReader();
        var ctrl = new PayrollController(
            db,
            new _D1UnrestrictedScope(),
            new _D1HttpAccessor(httpCtx),
            new _D1NullNotifications(),
            new _D1KsaPackResolver(rules),
            rules,
            new _D1NullLetterService(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // D1 REVIEW ROUND 2 — the service-period boundary.
    //
    // Independent HR-lifecycle and database reviewers, working separately, found the SAME Critical:
    // the reactivation carve-out protected an offboarding named by ANY settlement row, so a single
    // /final-settlement PREVIEW (which persists a Draft) pinned the prior period's separation live
    // forever. The next termination's live lookup then found it, created nothing, and the NEW service
    // period was settled against the OLD last working day — and the partial unique index meant no
    // correct second separation could be made either. These tests pin the corrected boundary.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A named store so a SECOND context can read what the first actually committed.</summary>
    private static ZayraDbContext NewDb(string store) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);

    private static EmployeeOffboarding Separation(Guid tenantId, int employeeId, string status, DateOnly lwd) =>
        new()
        {
            TenantId = tenantId, EmployeeId = employeeId, EmployeeName = "Ahmed Al-Rashidi",
            EmployeeCode = "E-100", SeparationType = "Termination", Reason = "First exit",
            NoticeDate = lwd, LastWorkingDay = lwd, Status = status, CreatedAtUtc = DateTime.UtcNow.AddYears(-2),
        };

    private static EmployeeFinalSettlement SettlementFor(Guid tenantId, int employeeId, Guid offboardingId, string status) =>
        new()
        {
            TenantId = tenantId, EmployeeId = employeeId, EmployeeCode = "E-100",
            EmployeeName = "Ahmed Al-Rashidi", OffboardingId = offboardingId, Status = status,
        };

    /// <summary>
    /// C1 — the atomicity requirement, driven through the REAL command rather than asserted about a
    /// successful one. NormalizeSeparationType throws AFTER the status and both history rows are
    /// staged, which is precisely the "status changed, separation missing" window. Nothing may survive.
    /// </summary>
    [Fact]
    public async Task ARefusedSeparationType_CommitsNothing_NotEvenTheStatusChange()
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        int empId;

        await using (var db = NewDb(store))
        {
            var (svc, emp) = Seed(db, tenantId);
            empId = emp.Id;
            // "Article 80" with a space is NOT in the closed vocabulary — it would otherwise have been
            // passed through to a FULL Art. 84 award.
            var act = async () => await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd("Article 80"), Ctx, default);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        // A genuinely fresh context over the same store — not AsNoTracking on the one that failed.
        await using var fresh = NewDb(store);
        (await fresh.Employees.SingleAsync(e => e.Id == empId)).Status.Should().Be(EmployeeStatuses.Active);
        (await fresh.EmployeeOffboardings.CountAsync()).Should().Be(0);
        (await fresh.EmployeeStatusHistories.CountAsync()).Should().Be(0);
        (await fresh.EmployeeHistories.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// CRITICAL 2 — a completed, settled service period is not reopened by repeating the command.
    /// The commit's own remediation advice is to re-run terminate across the archive; doing that over
    /// a properly offboarded leaver used to mint a brand-new separation dated today, and
    /// /final-settlement measures service from JoiningDate with its settle-twice guard keyed on
    /// OffboardingId — so it would accrue the ENTIRE tenure a second time.
    /// </summary>
    [Fact]
    public async Task ReTerminatingACompletedLeaver_CreatesNoSecondSeparation()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        emp.Status = "Archived";
        db.EmployeeOffboardings.Add(Separation(tenantId, emp.Id, "Completed", new DateOnly(2022, 6, 30)));
        await db.SaveChangesAsync();

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        var all = await db.EmployeeOffboardings.AsNoTracking().ToListAsync();
        all.Should().ContainSingle("a closed and settled period must not be reopened by re-running terminate");
        all[0].Status.Should().Be("Completed");
        all[0].LastWorkingDay.Should().Be(new DateOnly(2022, 6, 30));
    }

    /// <summary>
    /// The backlog case R2 exists for is NOT caught by that guard: an employee already sitting at
    /// Terminated with NO separation at all still gets one, or the migrated-leaver population stays
    /// permanently unsettleable.
    /// </summary>
    [Fact]
    public async Task ReTerminatingATerminatedEmployeeWithNoSeparation_StillRemediatesThem()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);
        emp.Status = "Terminated";
        await db.SaveChangesAsync();

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);

        var all = await db.EmployeeOffboardings.AsNoTracking().ToListAsync();
        all.Should().ContainSingle();
        all[0].LastWorkingDay.Should().Be(Lwd);
    }

    /// <summary>
    /// CRITICAL 1 — a DRAFT settlement must not pin the prior period's separation live. Reviewed
    /// independently by two SMEs. One /final-settlement preview is enough to persist a Draft.
    /// </summary>
    [Fact]
    public async Task ADraftSettlement_DoesNotPinThePriorSeparation_AndTheNewPeriodGetsItsOwn()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);
        var first = await db.EmployeeOffboardings.SingleAsync();
        db.EmployeeFinalSettlements.Add(SettlementFor(tenantId, emp.Id, first.Id, FinalSettlementStatuses.Draft));
        await db.SaveChangesAsync();

        // Rehire, then leave again a year later.
        await svc.ActivateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Active", new DateOnly(2026, 9, 1), "Rehired"), Ctx, default);
        var secondLwd = new DateOnly(2027, 3, 31);
        await svc.TerminateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Terminated", secondLwd, "Second exit", null), Ctx, default);

        var all = await db.EmployeeOffboardings.AsNoTracking().OrderBy(o => o.CreatedAtUtc).ToListAsync();
        all.Should().HaveCount(2, "the draft settlement must not have blocked the new service period");
        all.Single(o => o.Id == first.Id).Status.Should().Be("Cancelled");
        var live = all.Single(o => o.Status == "InProgress");
        live.LastWorkingDay.Should().Be(secondLwd, "the new period must be settled against ITS OWN last working day");
        // The original reason is evidence and must survive the supersede note.
        all.Single(o => o.Id == first.Id).Reason.Should().Contain("Role eliminated");
    }

    /// <summary>
    /// The other half of the same rule: an ACCRUED settlement has a posted journal behind it, so this
    /// code may neither cancel it silently nor leave it live for the next termination to reuse. It
    /// refuses the reactivation instead, and says what to do.
    /// </summary>
    [Fact]
    public async Task AnAccruedSettlement_RefusesTheReactivationRatherThanSettlingTwice()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);
        var first = await db.EmployeeOffboardings.SingleAsync();
        db.EmployeeFinalSettlements.Add(SettlementFor(tenantId, emp.Id, first.Id, FinalSettlementStatuses.Approved));
        await db.SaveChangesAsync();

        var act = async () => await svc.ActivateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Active", new DateOnly(2026, 9, 1), "Rehired"), Ctx, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ACCRUED final settlement*");
        (await db.EmployeeOffboardings.AsNoTracking().SingleAsync()).Status.Should().Be("InProgress");
    }

    /// <summary>
    /// The effective date becomes the last working day and drives the award, so a date before the
    /// employee ever joined is refused — including the 0001-01-01 an omitted [Required] DateOnly binds
    /// to, which would otherwise mint a separation that /final-settlement rejects as not_a_leaver while
    /// the live lookup makes the corrected retry a no-op.
    /// </summary>
    [Theory]
    [InlineData("0001-01-01")]
    [InlineData("2019-06-30")]
    public async Task ALastWorkingDayBeforeJoining_IsRefused_AndPersistsNothing(string effective)
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var tenantId = Guid.NewGuid();
        var (svc, emp) = Seed(db, tenantId);

        var act = async () => await svc.TerminateAsync(tenantId, emp.Id,
            new EmployeeStatusChangeRequest("Terminated", DateOnly.Parse(effective), "Bad date", null), Ctx, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.EmployeeOffboardings.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The brief requires the payroll-footprint change to be atomic with the status transition. It used
    /// to run in a LATER transaction, so a cancelled request left a Terminated employee still
    /// WPS-export-eligible. It is now staged into the same SaveChanges.
    /// </summary>
    [Fact]
    public async Task Terminate_DeactivatesTheWpsFootprint_InTheSameCommitAsTheStatusChange()
    {
        var store = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        int empId;

        await using (var db = NewDb(store))
        {
            var (svc, emp) = Seed(db, tenantId);
            empId = emp.Id;
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = emp.Id, WpsEligible = true,
            });
            await db.SaveChangesAsync();
            await svc.TerminateAsync(tenantId, emp.Id, TerminateCmd(), Ctx, default);
        }

        await using var fresh = NewDb(store);
        (await fresh.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == empId))
            .WpsEligible.Should().BeFalse();
        (await fresh.Employees.SingleAsync(e => e.Id == empId)).Status.Should().Be("Terminated");
        (await fresh.EmployeeOffboardings.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// The vocabulary must not admit a value that pays a full award where the doc-comment's own
    /// rationale says the statute forfeits it. NormalizeTerminationReason recognises only Resignation
    /// and Article80; "Abscondment" fell through to a FULL Art. 84 award.
    /// </summary>
    [Fact]
    public void TheSeparationVocabulary_DoesNotAdmitAValueThatSilentlyPaysAFullAward()
    {
        var act = () => EmployeeManagementService.NormalizeSeparationType("Abscondment");
        act.Should().Throw<InvalidOperationException>();
        EmployeeManagementService.NormalizeSeparationType("Article80").Should().Be("Article80");
    }
}

// ── File-scoped doubles (isolated from other test files) ─────────────────────

file sealed class _D1UnrestrictedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(
        System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _D1HttpAccessor : Microsoft.AspNetCore.Http.IHttpContextAccessor
{
    public _D1HttpAccessor(Microsoft.AspNetCore.Http.HttpContext ctx) => HttpContext = ctx;
    public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
}

file sealed class _D1NullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _D1NullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _D1KsaPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _D1KsaPackResolver(StubRuleReader rules) => _rules = rules;
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new Zayra.Api.Infrastructure.CountryPack.Ksa.KsaDeductionCalculator(_rules) : new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new Zayra.Api.Infrastructure.CountryPack.Ksa.KsaEndOfServiceCalculator(_rules) : new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
}
