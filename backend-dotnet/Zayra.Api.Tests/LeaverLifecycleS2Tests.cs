using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Wave 2.5 stream S2 — the leaver lifecycle. Every test here fails against the code as it stood on
/// <c>develop@188620b</c>; none of the 2,033 tests that passed there covered any of it, because the demo
/// tenant has nobody on notice.
///
/// <list type="bullet">
///   <item><b>B1</b> — the WPS file could not be generated in any month in which anyone was serving
///     notice, because <c>OffboardingController</c> sets <c>Status = "Offboarded"</c> at notice time,
///     payroll deliberately keeps paying those people, and <c>WpsSifValidator</c> then refused the wage
///     file that delivers the pay.</item>
///   <item><b>B2</b> — a leaver settled by bank transfer could never be closed out, and the
///     "Access revoked" tickbox revoked nothing.</item>
///   <item><b>B3</b> — <c>Article80</c> was unreachable from the screen and the screen's own values were
///     written past the closed vocabulary with no normalisation.</item>
///   <item><b>F4</b> — the last working day was computed and locked, notice was never reconciled against
///     the contract, and nothing validated the dates.</item>
/// </list>
/// </summary>
public class LeaverLifecycleS2Tests
{
    // ── Shared fixtures ──────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OffboardingController Controller(ZayraDbContext db, Guid tenantId, Guid? actorId = null)
        => new(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, (actorId ?? Guid.NewGuid()).ToString()),
                        new Claim(ClaimTypes.Name, "HR Tester"),
                        new Claim(ClaimTypes.Role, "Admin"),
                    }, "test")),
                },
            },
        };

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    // ── WPS validator fixtures (shape mirrored from WpsTests) ────────────────────────────────────

    private const string ValidSaudiIban = "SA0380000000608010167519";

    private static PayrollRun ApprovedRun(Guid tenantId) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, Status = "Approved", Year = 2026, Month = 9,
    };

    private static PayrollSlip Slip(Guid tenantId, Guid runId, int empId, string code, decimal net) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, RunId = runId,
        EmployeeId = empId, EmployeeCode = code,
        BasicSalary = net, GrossSalary = net, NetSalary = net,
    };

    private static EmployeePayrollProfile Profile(Guid tenantId, int empId) => new()
    {
        TenantId = tenantId, EmployeeId = empId, Iban = ValidSaudiIban, WpsEligible = true,
    };

    private static Employee WpsEmployee(Guid tenantId, int id, string status) => new()
    {
        Id = id, TenantId = tenantId, EmployeeCode = $"EMP-{id:000}", FullName = $"Employee {id}",
        IdNumber = $"10000000{id:00}", Status = status, JoiningDate = new DateTime(2020, 1, 1),
    };

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // B1 — the wage file
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE headline defect. Before the fix this produced a blocking <c>INACTIVE_EMPLOYEE</c> error and
    /// <c>CanExport == false</c>, which refused the WHOLE establishment's monthly WPS submission because
    /// one person had keyed a resignation.
    /// </summary>
    [Theory]
    [InlineData("Offboarded", "EMPLOYEE_SERVING_NOTICE")]
    [InlineData("Suspended", "EMPLOYEE_SUSPENDED")]
    [InlineData("Terminated", "EMPLOYEE_SEPARATION_RECORDED")]
    public void Wps_StillEmployedLeaver_IsPaid_NotBlocked(string status, string expectedWarning)
    {
        var tenantId = Guid.NewGuid();
        var run = ApprovedRun(tenantId);
        var slip = Slip(tenantId, run.Id, 1, "EMP-001", 9_000m);
        run.TotalNetSalary = slip.NetSalary;

        var result = WpsSifValidator.Validate(
            run, new[] { slip }, new[] { Profile(tenantId, 1) }, new[] { WpsEmployee(tenantId, 1, status) });

        Assert.True(result.CanExport,
            $"a '{status}' employee is still employed and their wage is legally due — refusing the wage file "
            + "withholds it and misses the MOL submission for everyone else in the run too");
        Assert.DoesNotContain(result.BlockingErrors, e => e.Code == "INACTIVE_EMPLOYEE");
        Assert.Contains(result.Warnings, w => w.Code == expectedWarning);
    }

    /// <summary>
    /// The other half of B1: the guard must still fire for a row that could never have been payable. If
    /// relaxing the set had also let these through, the fix would have swept never-activated and
    /// fully-closed-out people into a wage file.
    /// </summary>
    [Theory]
    [InlineData("Archived")]
    [InlineData("Exited")]
    [InlineData("Draft")]
    [InlineData("Invited")]
    [InlineData("Inactive")]
    public void Wps_TerminalStatus_StillBlocksExport(string status)
    {
        var tenantId = Guid.NewGuid();
        var run = ApprovedRun(tenantId);
        var slip = Slip(tenantId, run.Id, 1, "EMP-001", 9_000m);
        run.TotalNetSalary = slip.NetSalary;

        var result = WpsSifValidator.Validate(
            run, new[] { slip }, new[] { Profile(tenantId, 1) }, new[] { WpsEmployee(tenantId, 1, status) });

        Assert.False(result.CanExport);
        Assert.Contains(result.BlockingErrors, e => e.Code == "INACTIVE_EMPLOYEE");
    }

    /// <summary>
    /// The wage file's money must still tie out with a leaver in it. The relaxation must not have
    /// disturbed the run-total reconciliation, which is the check that stops a file paying a figure the
    /// payroll never computed.
    /// </summary>
    [Fact]
    public void Wps_RunWithLeaver_TotalStillTiesToNetPay()
    {
        var tenantId = Guid.NewGuid();
        var run = ApprovedRun(tenantId);
        var active = Slip(tenantId, run.Id, 1, "EMP-001", 12_000m);
        var leaver = Slip(tenantId, run.Id, 2, "EMP-002", 7_500m);   // serving notice, paid to LWD
        run.TotalNetSalary = active.NetSalary + leaver.NetSalary;

        var ok = WpsSifValidator.Validate(
            run, new[] { active, leaver },
            new[] { Profile(tenantId, 1), Profile(tenantId, 2) },
            new[] { WpsEmployee(tenantId, 1, "Active"), WpsEmployee(tenantId, 2, "Offboarded") });

        Assert.True(ok.CanExport);
        Assert.DoesNotContain(ok.BlockingErrors, e => e.Code == "TOTAL_MISMATCH");
        Assert.Equal(19_500m, run.TotalNetSalary);

        // …and the tie-out is real, not vacuous: drop the leaver's pay from the header and it fails.
        run.TotalNetSalary = active.NetSalary;
        var drifted = WpsSifValidator.Validate(
            run, new[] { active, leaver },
            new[] { Profile(tenantId, 1), Profile(tenantId, 2) },
            new[] { WpsEmployee(tenantId, 1, "Active"), WpsEmployee(tenantId, 2, "Offboarded") });
        Assert.False(drifted.CanExport);
        Assert.Contains(drifted.BlockingErrors, e => e.Code == "TOTAL_MISMATCH");
    }

    /// <summary>The precise per-person gate the exit cascade sets is untouched and still load-bearing.</summary>
    [Fact]
    public void Wps_WpsIneligibleProfile_StillBlocks_EvenWhileServingNotice()
    {
        var tenantId = Guid.NewGuid();
        var run = ApprovedRun(tenantId);
        var slip = Slip(tenantId, run.Id, 1, "EMP-001", 9_000m);
        run.TotalNetSalary = slip.NetSalary;
        var profile = Profile(tenantId, 1);
        profile.WpsEligible = false;

        var result = WpsSifValidator.Validate(
            run, new[] { slip }, new[] { profile }, new[] { WpsEmployee(tenantId, 1, "Offboarded") });

        Assert.False(result.CanExport);
        Assert.Contains(result.BlockingErrors, e => e.Code == "WPS_INELIGIBLE");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // B3 + F4 — what the screen can express, and what the API accepts
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    private static (Guid TenantId, Employee Employee) SeedEmployee(
        ZayraDbContext db, string status = "Active", int? noticePeriodDays = null)
    {
        var tenantId = Guid.NewGuid();
        var emp = new Employee
        {
            Id = 5001, TenantId = tenantId, EmployeeCode = "EMP-5001", FullName = "Leaver Person",
            Status = status, JoiningDate = new DateTime(2019, 3, 1), NoticePeriodDays = noticePeriodDays,
        };
        db.Employees.Add(emp);
        db.SaveChanges();
        return (tenantId, emp);
    }

    /// <summary>
    /// B3 — the screen's own list contained two values the domain rejects, and <c>Initiate</c> wrote them
    /// verbatim with no normalisation. Before the fix this returned 200 and persisted "End of Contract",
    /// which then throws the first time the record is routed through PATCH /employees/{id}/status.
    /// </summary>
    [Theory]
    [InlineData("End of Contract")]
    [InlineData("Other")]
    [InlineData("Art. 80")]
    [InlineData("Gross Misconduct")]
    [InlineData("Abscondment")]
    public async Task Initiate_OffVocabularySeparationType_IsRejected(string offVocabulary)
    {
        await using var db = CreateDb();
        var (tenantId, emp) = SeedEmployee(db);

        var result = await Controller(db, tenantId).Initiate(
            new InitiateOffboardingRequest(emp.Id, offVocabulary, "reason", null, 30, null),
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("unknown_separation_type", Json(bad.Value));
        Assert.False(await db.EmployeeOffboardings.AnyAsync());
        Assert.Equal("Active", (await db.Employees.SingleAsync()).Status);
    }

    /// <summary>The vocabulary is accepted, case-insensitively, and stored canonically.</summary>
    [Theory]
    [InlineData("endofcontract", "EndOfContract")]
    [InlineData("article80", "Article80")]
    [InlineData("Death", "Death")]
    [InlineData("ProbationFailure", "ProbationFailure")]
    [InlineData("Redundancy", "Redundancy")]
    public async Task Initiate_CanonicalisesAllowedSeparationTypes(string given, string stored)
    {
        await using var db = CreateDb();
        var (tenantId, emp) = SeedEmployee(db);

        var result = await Controller(db, tenantId).Initiate(
            new InitiateOffboardingRequest(emp.Id, given, "documented ground", null, 30, null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(stored, (await db.EmployeeOffboardings.SingleAsync()).SeparationType);
    }

    /// <summary>
    /// B3 — Article 80 forfeits the award in full, so it cannot be keyed without a stated ground.
    /// </summary>
    [Fact]
    public async Task Initiate_Article80_WithoutAReason_IsRejected()
    {
        await using var db = CreateDb();
        var (tenantId, emp) = SeedEmployee(db);

        var result = await Controller(db, tenantId).Initiate(
            new InitiateOffboardingRequest(emp.Id, "Article80", "   ", null, 30, null),
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("article80_reason_required", Json(bad.Value));
    }

    /// <summary>
    /// B3, the consequence that matters. Before the fix Article80 was UNREACHABLE from the screen, so a
    /// summary dismissal was keyed as "Termination" and paid a full Art. 84 award — the exact failure the
    /// closed vocabulary exists to prevent. Asserted as FORFEITURE (zero award) versus a paid award, not
    /// as an amount, so it is independent of the wage base.
    /// </summary>
    [Fact]
    public async Task Initiate_Article80_ForfeitsTheAward_WhileTerminationDoesNot()
    {
        await using var db = CreateDb();
        var (tenantId, emp) = SeedEmployee(db);

        var dismissal = await Controller(db, tenantId).Initiate(
            new InitiateOffboardingRequest(emp.Id, "Article80", "Art. 80(2) — assault on the line manager",
                new DateOnly(2026, 9, 1), 0, new DateOnly(2026, 9, 1)),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(dismissal);

        var persisted = await db.EmployeeOffboardings.SingleAsync();
        Assert.Equal("Article80", persisted.SeparationType);

        // Wave-25 integration: S2 passed `null!` here because, on S2's base, the calculator never
        // read a rule. S1 made the [CONF]/[COUNSEL] switches effective-dated StatutoryRule rows, so
        // the reader is now dereferenced. An EMPTY StubRuleReader seeds nothing, which means every
        // switch falls back to its statutory default — so this test still asserts exactly what it
        // always asserted: forfeiture (zero) versus a paid award, never an amount.
        var eosb = new KsaEndOfServiceCalculator(new StubRuleReader());
        var salary = new SalaryBreakdown(10_000m, 0m, 0m, 0m);
        EndOfServiceInput Input(string reason) => new(
            Guid.NewGuid(), Guid.NewGuid(), salary,
            DateOnly.FromDateTime(emp.JoiningDate), persisted.LastWorkingDay,
            reason, "Unlimited", "SA");

        // What the record now produces.
        var forfeited = await eosb.CalculateAsync(Input(persisted.SeparationType));
        Assert.Equal(0m, forfeited.TotalGratuity);

        // What the screen used to produce for the same dismissal, because Article80 was not offerable.
        var asPlainTermination = await eosb.CalculateAsync(Input("Termination"));
        Assert.True(asPlainTermination.TotalGratuity > 0m,
            "a plain Termination is paid a full award — which is what a summary dismissal used to receive");
    }

    /// <summary>The served catalogue and the domain vocabulary are the same list — they cannot drift.</summary>
    [Fact]
    public void SeparationTypeCatalog_MatchesTheDomainVocabularyExactly()
    {
        var served = SeparationTypeCatalog.All.Select(t => t.Code).OrderBy(c => c, StringComparer.Ordinal);
        var domain = EmployeeManagementService.AllowedSeparationTypes.OrderBy(c => c, StringComparer.Ordinal);
        Assert.Equal(domain, served);

        var article80 = Assert.Single(SeparationTypeCatalog.All.Where(t => t.ForfeitsEndOfServiceAward));
        Assert.Equal("Article80", article80.Code);
        Assert.True(article80.RequiresReason);
    }

    /// <summary>F4 — an explicit, EARLIER last working day is honoured (negotiated early release / pay in
    /// lieu), and the CONTRACTUAL notice is still stored so the settlement's unserved-notice test fires.
    /// The screen used to post a read-only computed LWD, so this case was unenterable at all.</summary>
    [Fact]
    public async Task Initiate_ExplicitEarlyLastWorkingDay_IsHonoured_AndKeepsTheContractualNotice()
    {
        await using var db = CreateDb();
        var (tenantId, emp) = SeedEmployee(db);
        var notice = new DateOnly(2026, 9, 1);

        var result = await Controller(db, tenantId).Initiate(
            new InitiateOffboardingRequest(emp.Id, "Termination", "role eliminated", notice, 30,
                LastWorkingDay: new DateOnly(2026, 9, 10)),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var off = await db.EmployeeOffboardings.SingleAsync();
        Assert.Equal(new DateOnly(2026, 9, 10), off.LastWorkingDay);
        Assert.Equal(30, off.NoticePeriodDays);
        // The settlement plan's own predicate: contractual notice runs past the LWD ⇒ pay in lieu is due.
        Assert.True(off.NoticeDate.AddDays(off.NoticePeriodDays) > off.LastWorkingDay);
        Assert.Contains("\"noticeShortfallDays\":21", Json(ok.Value));
    }

    /// <summary>F4 — nothing validated the dates. An LWD before the notice date, or before the joining
    /// date (which is what the end-of-service span is computed from), was accepted.</summary>
    [Fact]
    public async Task Initiate_ValidatesTheLastWorkingDay()
    {
        await using var db = CreateDb();
        var (tenantId, emp) = SeedEmployee(db);
        var controller = Controller(db, tenantId);

        var beforeNotice = await controller.Initiate(
            new InitiateOffboardingRequest(emp.Id, "Resignation", null, new DateOnly(2026, 9, 10), 30,
                new DateOnly(2026, 9, 1)),
            CancellationToken.None);
        Assert.Contains("last_working_day_before_notice",
            Json(Assert.IsType<BadRequestObjectResult>(beforeNotice).Value));

        var beforeJoining = await controller.Initiate(
            new InitiateOffboardingRequest(emp.Id, "Resignation", null, new DateOnly(2018, 1, 1), 0,
                new DateOnly(2018, 1, 1)),
            CancellationToken.None);
        Assert.Contains("last_working_day_before_joining",
            Json(Assert.IsType<BadRequestObjectResult>(beforeJoining).Value));

        Assert.False(await db.EmployeeOffboardings.AnyAsync());
    }

    /// <summary>F4 — the contractual notice period on the employment record was ignored entirely; whoever
    /// raised the offboarding typed a number and nothing reconciled it.</summary>
    [Fact]
    public async Task Initiate_FallsBackToTheEmployeesContractualNoticePeriod()
    {
        await using var db = CreateDb();
        var (tenantId, emp) = SeedEmployee(db, noticePeriodDays: 60);

        var result = await Controller(db, tenantId).Initiate(
            new InitiateOffboardingRequest(emp.Id, "Resignation", null, new DateOnly(2026, 9, 1),
                NoticePeriodDays: -1, LastWorkingDay: null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var off = await db.EmployeeOffboardings.SingleAsync();
        Assert.Equal(60, off.NoticePeriodDays);
        Assert.Equal(new DateOnly(2026, 10, 31), off.LastWorkingDay);
    }

    /// <summary>F4 — a SUSPENDED employee can be offboarded (the "suspended pending investigation, then
    /// dismissed" path). The API always allowed it; the screen's <c>status: 'Active'</c> filter did not.</summary>
    [Fact]
    public async Task Initiate_SuspendedEmployee_IsAccepted()
    {
        await using var db = CreateDb();
        var (tenantId, emp) = SeedEmployee(db, status: "Suspended");

        var result = await Controller(db, tenantId).Initiate(
            new InitiateOffboardingRequest(emp.Id, "Article80", "Art. 80(1) — proven forgery", null, 0, null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Offboarded", (await db.Employees.SingleAsync()).Status);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // B2 — closing a leaver out
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    private sealed record LeaverFixture(
        Guid TenantId, Guid CompanyId, Employee Employee, User User,
        EmployeeOffboarding Offboarding, Guid ActorId);

    /// <summary>A leaver past their last working day, clearance done, with a live login.</summary>
    private static async Task<LeaverFixture> SeedLeaverAsync(ZayraDbContext db, string password = "CorrectPassword1!")
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "KynexOne HQ", Slug = "kynexone" };
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, LegalNameEn = "KynexOne KSA",
            TradeName = "KynexOne", CountryCode = "SA", DefaultCurrency = "SAR",
        };
        var userId = Guid.NewGuid();
        var employee = new Employee
        {
            Id = 7001, TenantId = tenant.Id, CompanyId = company.Id, UserAccountId = userId,
            EmployeeCode = "EMP-7001", FullName = "Departing Person",
            Status = EmployeeStatuses.Offboarded, JoiningDate = new DateTime(2019, 3, 1),
        };
        var user = new User
        {
            Id = userId, TenantId = tenant.Id, Email = "departing@kynexone.local",
            NormalizedEmail = "DEPARTING@KYNEXONE.LOCAL", FullName = "Departing Person",
            PasswordHash = new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher().Hash(password),
            IsActive = true, Status = "Active", AccessMode = AccessModes.FullPortal,
        };
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenant.Id, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, SeparationType = "Resignation",
            Status = "InProgress",
            NoticeDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-31)),
            NoticePeriodDays = 30,
            LastWorkingDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            AssetsReturned = true, KnowledgeHandover = true, ExitInterviewStatus = "Waived",
        };

        db.Tenants.Add(tenant);
        db.Companies.Add(company);
        db.Employees.Add(employee);
        db.Users.Add(user);
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = tenant.Id, EmployeeId = employee.Id, UserId = userId,
            AccessMode = AccessModes.FullPortal, Status = "Active", RequiresPasswordSetup = false,
        });
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId, TokenHash = "live-session", ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
        });
        db.SecuritySettings.Add(new SecuritySetting
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, MaxFailedLoginAttempts = 5, LockoutDurationMinutes = 15,
        });
        db.EmployeeOffboardings.Add(offboarding);
        await db.SaveChangesAsync();
        return new LeaverFixture(tenant.Id, company.Id, employee, user, offboarding, Guid.NewGuid());
    }

    /// <summary>Approved settlement + its live accrual, exactly as PayrollController.ApproveFinalSettlement
    /// leaves them: 2320 credited at the GROSS of the earning lines.</summary>
    private static async Task<EmployeeFinalSettlement> SeedApprovedSettlementAsync(
        ZayraDbContext db, LeaverFixture fx, decimal gross = 60_000m, decimal deductions = 1_000m)
    {
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = fx.TenantId, CompanyId = fx.CompanyId,
            EmployeeId = fx.Employee.Id, EmployeeCode = fx.Employee.EmployeeCode,
            EmployeeName = fx.Employee.FullName, OffboardingId = fx.Offboarding.Id,
            LastWorkingDay = fx.Offboarding.LastWorkingDay,
            ServiceStartDate = DateOnly.FromDateTime(fx.Employee.JoiningDate),
            SettlementDueDate = fx.Offboarding.LastWorkingDay,
            TerminationReason = "Resignation", Currency = "SAR",
            GrossPayable = gross, TotalDeductions = deductions, NetPayable = gross - deductions,
            Status = FinalSettlementStatuses.Approved,
            GlPostedAtUtc = DateTime.UtcNow, GlPeriod = "2026-09",
        };
        db.EmployeeFinalSettlements.Add(settlement);
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = fx.TenantId, CompanyId = fx.CompanyId,
            SourceModule = FinalSettlementGlDescriptions.SourceModule,
            SourceEntityId = settlement.Id,
            SourceEntityRef = FinalSettlementGlDescriptions.SettlementRef(settlement.Id),
            EventType = GlEventTypes.SettlementAccrual,
            DebitAccount = string.Empty, CreditAccount = "2320 - Final Settlement Payable",
            Amount = gross, Currency = "SAR",
            EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = "2026-09",
            Description = FinalSettlementGlDescriptions.AccrualPrefix + settlement.EmployeeCode,
        });
        await db.SaveChangesAsync();
        return settlement;
    }

    /// <summary>
    /// B2, the blocking half. <c>Complete</c> required <c>Status == Paid</c>, which only a payroll-run
    /// disbursement produces — so a client who settled one mid-month leaver by bank transfer could never
    /// complete the offboarding and the employee stayed <c>Offboarded</c> (occupying a seat) forever.
    /// </summary>
    [Fact]
    public async Task Complete_SucceedsOnASettlementPaidOutsidePayroll()
    {
        await using var db = CreateDb();
        var fx = await SeedLeaverAsync(db);
        var settlement = await SeedApprovedSettlementAsync(db, fx);
        var controller = Controller(db, fx.TenantId, fx.ActorId);

        // Before the money is recorded, completion is still (correctly) refused.
        var premature = await controller.Complete(fx.Offboarding.Id, CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(premature);
        Assert.Contains("final_settlement_not_paid", Json(conflict.Value));
        Assert.Contains("external-payment", Json(conflict.Value));

        var paid = await controller.RecordExternalSettlementPayment(
            fx.Offboarding.Id,
            new ExternalSettlementPaymentRequest("BankTransfer", "TRF-8842193", settlement.NetPayable,
                DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(paid);

        var stored = await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync();
        Assert.Equal(FinalSettlementStatuses.Paid, stored.Status);
        Assert.True(stored.PaidOutsidePayroll);
        Assert.Equal("BankTransfer", stored.ExternalPaymentMethod);
        Assert.Equal("TRF-8842193", stored.ExternalPaymentReference);
        Assert.Null(stored.PayrollRunId);
        Assert.Null(stored.PaymentBatchId);

        var completed = await controller.Complete(fx.Offboarding.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(completed);
        Assert.Equal("Completed", (await db.EmployeeOffboardings.AsNoTracking().SingleAsync()).Status);
        // …and the seat is released: Archived is not an occupying status.
        var employee = await db.Employees.AsNoTracking().SingleAsync();
        Assert.Equal(EmployeeStatuses.Archived, employee.Status);
        Assert.False(Zayra.Api.Application.Organization.EstablishmentOccupancy.IsOccupyingStatus(employee.Status));
    }

    /// <summary>
    /// B2 — recording the payment is a JOURNAL, not a flag. If it were only a flag, 2320 would carry a
    /// liability that no longer exists for the rest of the entity's life.
    /// </summary>
    [Fact]
    public async Task ExternalSettlementPayment_ClearsThePayableToZero()
    {
        await using var db = CreateDb();
        var fx = await SeedLeaverAsync(db);
        var settlement = await SeedApprovedSettlementAsync(db, fx, gross: 60_000m, deductions: 1_000m);

        var result = await Controller(db, fx.TenantId, fx.ActorId).RecordExternalSettlementPayment(
            fx.Offboarding.Id,
            new ExternalSettlementPaymentRequest("BankTransfer", "TRF-1", 59_000m, null),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        const string payable = "2320 - Final Settlement Payable";
        var entries = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.SourceEntityId == settlement.Id).ToListAsync();
        var credited = entries.Where(x => x.CreditAccount == payable).Sum(x => x.Amount);
        var debited = entries.Where(x => x.DebitAccount == payable).Sum(x => x.Amount);
        Assert.Equal(60_000m, credited);
        Assert.Equal(60_000m, debited);   // 2320 closes to zero, exactly as the payroll rail leaves it

        var discharge = entries.Where(x => x.EventType == GlEventTypes.SettlementExternalPayment).ToList();
        Assert.Equal(60_000m, discharge.Sum(x => x.Amount));          // balanced against the payable
        Assert.Equal(59_000m, discharge.Where(x => x.CreditAccount.Contains("Cash")).Sum(x => x.Amount));
        Assert.All(discharge, e => Assert.Equal("SAR", e.Currency));

        // The payable is now spent: a payroll run must never clear it a second time.
        Assert.True(await FinalSettlementGlLedger.HasLiveClearingAsync(
            db, fx.TenantId, settlement.Id, CancellationToken.None));
    }

    /// <summary>The discharge is the NARROW path, and it says so rather than quietly mis-posting.</summary>
    [Fact]
    public async Task ExternalSettlementPayment_RefusesWhatTheRunRailMustOwn()
    {
        await using var db = CreateDb();
        var fx = await SeedLeaverAsync(db);
        var settlement = await SeedApprovedSettlementAsync(db, fx);
        var controller = Controller(db, fx.TenantId, fx.ActorId);

        var wrongAmount = await controller.RecordExternalSettlementPayment(
            fx.Offboarding.Id, new ExternalSettlementPaymentRequest("BankTransfer", "TRF-1", 1m, null),
            CancellationToken.None);
        Assert.Contains("amount_does_not_match_settlement",
            Json(Assert.IsType<UnprocessableEntityObjectResult>(wrongAmount).Value));

        var noReference = await controller.RecordExternalSettlementPayment(
            fx.Offboarding.Id, new ExternalSettlementPaymentRequest("BankTransfer", "  ", settlement.NetPayable, null),
            CancellationToken.None);
        Assert.Contains("payment_reference_required",
            Json(Assert.IsType<BadRequestObjectResult>(noReference).Value));

        var badMethod = await controller.RecordExternalSettlementPayment(
            fx.Offboarding.Id, new ExternalSettlementPaymentRequest("Crypto", "TRF-1", settlement.NetPayable, null),
            CancellationToken.None);
        Assert.Contains("unknown_payment_method",
            Json(Assert.IsType<BadRequestObjectResult>(badMethod).Value));

        // A settlement that recovers employee debt belongs on the run, whose deduction lines relieve the
        // control accounts. Paying it here would be a double recovery.
        settlement.PlannedLoanRecovery = 3_000m;
        await db.SaveChangesAsync();
        var withDebt = await controller.RecordExternalSettlementPayment(
            fx.Offboarding.Id, new ExternalSettlementPaymentRequest("Cheque", "CHQ-9", settlement.NetPayable, null),
            CancellationToken.None);
        Assert.Contains("settlement_recovers_employee_debt",
            Json(Assert.IsType<UnprocessableEntityObjectResult>(withDebt).Value));

        Assert.Equal(FinalSettlementStatuses.Approved,
            (await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync()).Status);
        Assert.False(await db.FinanceGlEntries
            .AnyAsync(x => x.EventType == GlEventTypes.SettlementExternalPayment));
    }

    /// <summary>A closed GL period refuses the discharge, the same guard approval uses.</summary>
    [Fact]
    public async Task ExternalSettlementPayment_RefusesAClosedPeriod()
    {
        await using var db = CreateDb();
        var fx = await SeedLeaverAsync(db);
        var settlement = await SeedApprovedSettlementAsync(db, fx);
        var paidOn = new DateOnly(2026, 8, 20);
        db.GlPeriodCloses.Add(new GlPeriodClose
        {
            TenantId = fx.TenantId, CompanyId = fx.CompanyId, Period = "2026-08",
            Status = GlPeriodStatuses.Closed,
        });
        await db.SaveChangesAsync();

        var result = await Controller(db, fx.TenantId, fx.ActorId).RecordExternalSettlementPayment(
            fx.Offboarding.Id,
            new ExternalSettlementPaymentRequest("BankTransfer", "TRF-1", settlement.NetPayable, paidOn),
            CancellationToken.None);

        Assert.Contains("gl_period_closed",
            Json(Assert.IsType<UnprocessableEntityObjectResult>(result).Value));
    }

    // ── Access revocation ────────────────────────────────────────────────────────────────────────

    private static Zayra.Api.Infrastructure.Auth.AuthService BuildAuth(ZayraDbContext db)
    {
        var jwt = Microsoft.Extensions.Options.Options.Create(new Zayra.Api.Application.Auth.JwtOptions
        {
            Issuer = "Zayra.Tests",
            TenantAudience = "kynexone-tenant-test",
            PlatformAudience = "kynexone-platform-test",
            SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_LEAVER_LIFECYCLE",
            AccessTokenMinutes = 30,
            RefreshTokenDays = 7,
        });
        return new Zayra.Api.Infrastructure.Auth.AuthService(
            db,
            new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(),
            new Zayra.Api.Infrastructure.Auth.JwtTokenService(jwt),
            new AuditService(db),
            new NullLeaverEmailService(),
            jwt,
            new NullLeaverMfaService(),
            new Zayra.Api.Infrastructure.Auth.TotpService(
                Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("ZayraTests")),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Zayra.Api.Infrastructure.Auth.AuthService>.Instance);
    }

    /// <summary>
    /// B2, the live security hole. Ticking "Access revoked" set a boolean and revoked nothing: the only
    /// revocation code ran inside <c>Complete</c>, which cannot run until the settlement is discharged.
    /// So HR killed access on the last working day and the ex-employee could still log in for weeks.
    /// This asserts the outcome that matters — they can no longer AUTHENTICATE.
    /// </summary>
    [Fact]
    public async Task TickingAccessRevoked_ActuallyStopsTheLeaverAuthenticating()
    {
        await using var db = CreateDb();
        var fx = await SeedLeaverAsync(db);
        var auth = BuildAuth(db);
        var ctx = new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "tests");
        var credentials = new Zayra.Api.Application.Auth.LoginRequest(
            "departing@kynexone.local", "CorrectPassword1!", "kynexone");

        // Baseline: the login genuinely works before the tickbox is touched.
        var before = await auth.LoginAsync(credentials, ctx, CancellationToken.None);
        Assert.NotNull(before.Tokens);   // the fixture has a working login, or the test proves nothing

        var result = await Controller(db, fx.TenantId, fx.ActorId).Checklist(
            fx.Offboarding.Id, new OffboardingChecklistRequest(null, AccessRevoked: true, null, null),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.LoginAsync(credentials, ctx, CancellationToken.None));

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == fx.User.Id);
        Assert.False(user.IsActive);
        Assert.Equal(AccessModes.NoLogin, user.AccessMode);
        var link = await db.EmployeeUserAccounts.AsNoTracking().SingleAsync();
        Assert.Equal("NoLogin", link.Status);
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == fx.User.Id && t.RevokedAtUtc == null));

        var off = await db.EmployeeOffboardings.AsNoTracking().SingleAsync();
        Assert.True(off.AccessRevoked);
        Assert.NotNull(off.AccessRevokedAtUtc);
        Assert.Equal(fx.ActorId, off.AccessRevokedByUserId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "offboarding.access_revoked"));

        // The employment has NOT ended (they are serving out the settlement window), so the link is kept.
        Assert.NotNull((await db.Employees.AsNoTracking().SingleAsync()).UserAccountId);
    }

    /// <summary>Revocation is one-way: un-ticking a box cannot hand a leaver their login back.</summary>
    [Fact]
    public async Task UntickingAccessRevoked_IsRefused()
    {
        await using var db = CreateDb();
        var fx = await SeedLeaverAsync(db);
        var controller = Controller(db, fx.TenantId, fx.ActorId);

        await controller.RevokeAccess(fx.Offboarding.Id, CancellationToken.None);
        var result = await controller.Checklist(
            fx.Offboarding.Id, new OffboardingChecklistRequest(null, AccessRevoked: false, null, null),
            CancellationToken.None);

        Assert.Contains("access_revocation_is_irreversible",
            Json(Assert.IsType<ConflictObjectResult>(result).Value));
        Assert.True((await db.EmployeeOffboardings.AsNoTracking().SingleAsync()).AccessRevoked);
        Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == fx.User.Id)).IsActive);
    }

    /// <summary>
    /// Rescinding restores the employment lifecycle, but revoked credentials stay fail-closed. Access
    /// must be re-approved through the controlled invitation flow rather than silently restored with
    /// the employee's old roles, scope, password and MFA state.
    /// </summary>
    [Fact]
    public async Task Rescind_RestoresEmployment_ButRequiresControlledReinvite()
    {
        await using var db = CreateDb();
        var fx = await SeedLeaverAsync(db);
        var controller = Controller(db, fx.TenantId, fx.ActorId);
        await controller.RevokeAccess(fx.Offboarding.Id, CancellationToken.None);

        var result = await controller.Cancel(
            fx.Offboarding.Id, new CancelOffboardingRequest("Resignation withdrawn — counter-offer accepted"),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Contains("\"accessReprovisioningRequired\":true", Json(((OkObjectResult)result).Value));

        var off = await db.EmployeeOffboardings.AsNoTracking().SingleAsync();
        Assert.Equal("Cancelled", off.Status);
        Assert.True(off.AccessRevoked);
        Assert.Equal(fx.ActorId, off.CancelledByUserId);
        Assert.NotNull(off.CancelledAtUtc);
        Assert.Equal("Resignation withdrawn — counter-offer accepted", off.CancelReason);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "offboarding.rescinded"));

        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == fx.Employee.Id);
        Assert.Equal("Active", employee.Status);
        Assert.Equal(fx.User.Id, employee.UserAccountId);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == fx.User.Id);
        Assert.False(user.IsActive);
        Assert.Equal("PendingPasswordSetup", user.Status);
        Assert.Equal(AccessModes.NoLogin, user.AccessMode);
        var link = await db.EmployeeUserAccounts.AsNoTracking().SingleAsync();
        Assert.Equal("NoLogin", link.Status);
        Assert.Equal(AccessModes.NoLogin, link.AccessMode);
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == fx.User.Id && t.RevokedAtUtc == null));

        var auth = BuildAuth(db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.LoginAsync(
            new Zayra.Api.Application.Auth.LoginRequest("departing@kynexone.local", "CorrectPassword1!", "kynexone"),
            new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "tests"), CancellationToken.None));
    }
}

// ── Local test doubles (file-scoped, mirroring AuthServiceTests') ───────────────────────────────────

file sealed class NullLeaverEmailService : Zayra.Api.Infrastructure.Email.IEmailService
{
    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<Zayra.Api.Infrastructure.Email.EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}

file sealed class NullLeaverMfaService : Zayra.Api.Application.Auth.IMfaService
{
    public Task<Zayra.Api.Application.Auth.MfaSetupInitDto> InitiateSetupAsync(Guid userId, Guid tenantId, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, Zayra.Api.Application.Auth.MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreateEnrollmentChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) => Task.FromResult("test-enrollment-token");
    public Task<Zayra.Api.Application.Auth.MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifyEnrollmentSetupAsync(string token, Zayra.Api.Application.Auth.MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Domain.Entities.User?> VerifyChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DisableAsync(Guid userId, Guid tenantId, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> AdminDisableAsync(Guid userId, Guid tenantId, Zayra.Api.Application.Auth.RequestContext context, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Application.Auth.MfaSetupInitDto> InitiatePlatformSetupAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifyPlatformSetupAsync(Guid id, Zayra.Api.Application.Auth.MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreatePlatformChallengeAsync(Guid id, string ip, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Models.PlatformUser?> VerifyPlatformChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Models.PlatformUser?> CompletePlatformChallengeAsync(string token, string code, Zayra.Api.Application.Auth.RequestContext context, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DisablePlatformAsync(Guid id, string code, CancellationToken ct) => throw new NotImplementedException();
}
