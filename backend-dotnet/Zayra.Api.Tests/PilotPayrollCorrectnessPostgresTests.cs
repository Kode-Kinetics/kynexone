using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Pilot-readiness track B — payroll correctness for a mid-year real-data pilot, on real Postgres and
/// through the real <c>PayrollController.Process</c> path. Each test computes real payslips end to end.
///
/// <list type="bullet">
/// <item><b>GOSI, one source:</b> an admin typing "9" for a rate is refused with a coded reason; the
/// payslip, the per-employee preview and the readiness report reach the same GOSI for the same wage,
/// from the same effective-dated <c>statutory_rules</c> rows.</item>
/// <item><b>YTD opening balances:</b> a carried opening balance and a payslip this product locked
/// for the same month are never both counted.</item>
/// <item><b>EOSB carried service:</b> three years carried in plus one year here is a four-year award.</item>
/// </list>
/// </summary>
[Collection("Integration")]
public class PilotPayrollCorrectnessPostgresTests
{
    private readonly PostgresFixture _fx;
    public PilotPayrollCorrectnessPostgresTests(PostgresFixture fx) => _fx = fx;

    // ═══════════════════════════════════════════════════════════════════════════
    //  1. GOSI — one source, one unit
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gosi_AdminTypingNine_IsRefusedWithACodedReason_AndNothingIsSaved()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var ctrl = new StatutoryRulesController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } },
        };
        // "9" for the GOSI rate: a tenant cannot set a GOSI rate at all, so it is refused as statutory.
        var result = await ctrl.Create(new CreateStatutoryRuleRequest(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiSaudiEmployeeRate,
            "9", "decimal", "Typed from the GOSI circular", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null),
            CancellationToken.None);
        AssertGosiRefusal(result.Result);

        // "35" meaning 35% for a rate a tenant MAY override is refused for its unit, with a code.
        var mistyped = await ctrl.Create(new CreateStatutoryRuleRequest(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, "nitaqat.default_target_ratio",
            "35", "decimal", "Typed as a percentage", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null),
            CancellationToken.None);
        var bad = mistyped.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Prop<string>(bad.Value!, "code").Should().Be(StatutoryValueUnits.UnitRefusalCode,
            "a client must be able to branch on WHY the value was refused, not parse prose");
        Prop<string>(bad.Value!, "message").Should().Contain("0.35").And.Contain("Nothing has been saved");

        (await db.StatutoryRules.IgnoreQueryFilters().AnyAsync(r => r.TenantId == tenantId))
            .Should().BeFalse("a refused rate must not be written anywhere");

        // The retired GOSI rate list refuses too — with its own code — instead of storing a rate nothing reads.
        var gosi = new GosiController(db, TestReconciliation.For(db), new StatutoryRuleReader(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } },
        };
        var retired = await gosi.CreateContributionRule(new CreateGosiRuleRequest(
            GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9m,
            new DateOnly(2026, 1, 1), SourceReference: "circular"), CancellationToken.None);
        var gone = retired.Should().BeOfType<ObjectResult>().Subject;
        gone.StatusCode.Should().Be(StatusCodes.Status410Gone);
        Prop<string>(gone.Value!, "code").Should().Be(GosiController.GosiRateStoreRetiredCode);
        Prop<string>(gone.Value!, "statutoryRuleKey").Should().Be(RuleKeys.GosiSaudiEmployeeRate);
        (await db.GosiContributionRules.IgnoreQueryFilters().AnyAsync(r => r.TenantId == tenantId)).Should().BeFalse();
    }

    /// <summary>
    /// The same wage through the payslip, the preview and the readiness report, all reading the
    /// platform statutory rules seeded exactly as production seeds them. 12,500 covered: 9.75% EE
    /// (1,218.75) and 11.75% ER (1,468.75). 60,000 covered is capped at the 45,000 ceiling: 4,387.50 /
    /// 5,287.50. An expatriate pays nothing; the employer pays 2% occupational hazards.
    /// </summary>
    [Theory]
    [InlineData("Saudi",  10_000, 2_500, 1_218.75, 1_468.75)]
    [InlineData("Saudi",  50_000, 10_000, 4_387.50, 5_287.50)]
    [InlineData("Egypt",  10_000, 2_500, 0, 250.00)]
    public async Task Gosi_PayslipPreviewAndReadinessReport_ReachTheSameFigure(
        string nationality, double basicD, double housingD, double expectedEeD, double expectedErD)
    {
        decimal basic = (decimal)basicD, housing = (decimal)housingD;
        decimal expectedEe = (decimal)expectedEeD, expectedEr = (decimal)expectedErD;

        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, nationality, basic, housing,
            joining: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var run = await NewRun(db, tenantId, company, today.Year, today.Month);

        (await Payroll(db, tenantId, reader).Process(run.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var gosiLines = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == run.Id && d.EmployeeId == employee.Id
                     && d.ComponentCode.StartsWith("GOSI"))
            .ToListAsync();
        var payslipEe = gosiLines.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount);
        var payslipEr = gosiLines.Where(d => d.IsEmployerContribution).Sum(d => d.Amount);

        payslipEe.Should().Be(expectedEe, "the payslip deducts the statutory employee share");
        payslipEr.Should().Be(expectedEr, "the payslip accrues the statutory employer share");

        // The per-employee preview.
        var gosi = new GosiController(db, new GosiReconciliationService(db, new _PilotKsaResolver(reader)), reader)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } },
        };
        var readiness = (await gosi.GetEmployeeReadiness(employee.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value!;
        var preview = readiness.GetType().GetProperty("contributionPreview")!.GetValue(readiness);
        preview.Should().NotBeNull("a ready employee gets a contribution preview");
        Prop<decimal>(preview!, "EmployeeTotal").Should().Be(payslipEe, "the preview is computed by the payslip's engine");
        Prop<decimal>(preview!, "EmployerTotal").Should().Be(payslipEr);

        // The readiness report — the figure a finance team reconciles against the GOSI portal.
        var report = await new GosiReadinessReportService(db, reader).BuildAsync(tenantId, CancellationToken.None);
        var row = report.Employees.Single(e => e.EmployeeId == employee.Id);
        row.EmployeeContributionTotal.Should().Be(payslipEe);
        row.EmployerContributionTotal.Should().Be(payslipEr);
        row.Lines.Should().OnlyContain(l => l.Rate > 0m && l.Rate <= StatutoryValueUnits.MaxContributionRateFraction,
            "every line states the FRACTION the payslip applied");
    }

    /// <summary>
    /// The divergence the second store made possible: a tenant row in gosi_contribution_rules (written
    /// before this change, or by hand) used to move the preview and the readiness report but never the
    /// payslip. It must now move nothing.
    /// </summary>
    [Fact]
    public async Task Gosi_AStaleRowInTheRetiredStore_NoLongerMovesThePreview()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        db.GosiContributionRules.Add(new GosiContributionRule
        {
            TenantId = tenantId, CountryCode = "SA", Classification = GosiClassifications.Saudi,
            Branch = GosiBranches.Annuities, Payer = GosiPayers.Employee, Rate = 0.20m,
            EffectiveFrom = new DateOnly(2020, 1, 1), IsActive = true, SourceReference = "stale override",
        });
        await db.SaveChangesAsync();

        var report = await new GosiReadinessReportService(db, reader).BuildAsync(tenantId, CancellationToken.None);
        report.Employees.Single(e => e.EmployeeId == employee.Id).EmployeeContributionTotal
            .Should().Be(1_218.75m, "the statutory 9% + 0.75% applies, not the stale 20% row nobody pays on");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  1b. GOSI rates and the ceiling are statutory — no tenant can set them
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every tenant-level write path refuses a GOSI rate or ceiling with 422 GOSI_RATE_IS_STATUTORY
    /// (EN + AR), even when the value is a perfectly well-formed fraction — because payroll would never
    /// read it. A non-GOSI statutory key is still accepted, so the guard is not a blanket ban.
    /// </summary>
    [Fact]
    public async Task GosiStatutory_EveryTenantWritePath_IsRefusedWithACode()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var reader = new StatutoryRuleReader(db);
        var ef = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 1. /api/statutory-rules — create, for a rate and for the ceiling.
        var stat = WithUser(new StatutoryRulesController(db), tenantId);
        AssertGosiRefusal((await stat.Create(new CreateStatutoryRuleRequest(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            RuleKeys.GosiSaudiEmployeeRate, "0.10", "decimal", "board decision", ef, null), CancellationToken.None)).Result);
        AssertGosiRefusal((await stat.Create(new CreateStatutoryRuleRequest(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            KsaGosiWageBounds.CeilingRuleKey, "60000", "decimal", "board decision", ef, null), CancellationToken.None)).Result);

        // ...and supersede of a row saved before the guard existed.
        var legacy = new StatutoryRule
        {
            TenantId = tenantId, CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = RuleKeys.GosiSanedRate, RuleValue = "0.01", DataType = "decimal", EffectiveFrom = ef,
        };
        db.StatutoryRules.Add(legacy);
        await db.SaveChangesAsync();
        AssertGosiRefusal((await stat.Update(legacy.Id, new UpdateStatutoryRuleRequest("0.012", "again", ef.AddMonths(1), null),
            CancellationToken.None)).Result);

        // A non-GOSI statutory key is still overridable.
        (await stat.Create(new CreateStatutoryRuleRequest(CountryCodes.Saudi, Jurisdictions.KsaMainland,
            "ot.standard_multiplier", "1.75", "decimal", "company policy above statute", ef, null), CancellationToken.None))
            .Result.Should().BeOfType<CreatedAtActionResult>();

        // 2. Company statutory override (maker-checker): refused at request…
        var rates = WithUser(new RatesController(db, reader, new StatutoryRateResolver(db, reader)), tenantId);
        AssertGosiRefusal(await rates.CreateStatutoryOverride(new StatutoryOverrideRequest(
            company.Id, CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiSaudiEmployerRate, "0.10",
            new DateOnly(2026, 1, 1), "board decision", new DateOnly(2026, 12, 31)), CancellationToken.None));

        // …and at approval, for a request made before the guard existed.
        var pending = new CompanyStatutoryOverride
        {
            TenantId = tenantId, CompanyId = company.Id, CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = RuleKeys.GosiSaudiEmployerRate, OverrideValue = "0.10", DataType = "decimal",
            EffectiveFrom = new DateOnly(2026, 1, 1), ReviewBy = new DateOnly(2026, 12, 31), Reason = "legacy",
            Status = "PendingApproval", CreatedBy = Guid.NewGuid(),
        };
        db.CompanyStatutoryOverrides.Add(pending);
        await db.SaveChangesAsync();
        AssertGosiRefusal(await rates.ApproveStatutoryOverride(pending.Id, CancellationToken.None));
        (await db.CompanyStatutoryOverrides.AsNoTracking().SingleAsync(o => o.Id == pending.Id)).Status
            .Should().Be("PendingApproval", "nothing can be approved and then ignored");

        // 3. Setup assistant apply.
        var setup = WithUser(new SetupAssistantController(db, new _PilotNoSetupPreview(), new Zayra.Api.Infrastructure.Audit.AuditService(db)), tenantId);
        var draft = Zayra.Api.Application.Setup.SetupDraft.Empty() with
        {
            StatutoryRules = [new Zayra.Api.Application.Setup.DraftStatutoryRule("gosi.employee_rate", "0.0975", "decimal", "wizard")],
        };
        AssertGosiRefusal(await setup.Apply(new ApplySetupRequest(draft, "SA", "SAR"), CancellationToken.None));

        // 4. Tenant-admin country rules.
        var admin = WithUser(new TenantAdminController(db), tenantId);
        AssertGosiRefusal(await admin.CreateCountryRule(new CreateCountryRuleRequest(
            "SAU", RuleKeys.GosiExpOhRate, "0.03", "decimal", "hazard", true, ef, null), CancellationToken.None));

        // Only the pre-seeded legacy rows exist; every refused write wrote nothing.
        (await db.StatutoryRules.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId && r.RuleKey.StartsWith("gosi.")))
            .Should().Be(1);
        (await db.CountryPayrollRules.IgnoreQueryFilters().AnyAsync(r => r.TenantId == tenantId && r.RuleKey.StartsWith("gosi.")))
            .Should().BeFalse();
        (await db.CompanyStatutoryOverrides.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId)).Should().Be(1);
    }

    /// <summary>
    /// Override rows saved before the guard — in all three tenant stores, ACTIVE and generous — change
    /// nothing on the payslip, which deducts the GOSI-published 9% + 0.75%. They are kept, the readiness
    /// report and dashboard warn about them, and the runbook query lists them.
    /// </summary>
    [Fact]
    public async Task GosiStatutory_ExistingOverrideRows_DoNotTouchThePayslip_AndAreWarnedAbout()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ef = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.StatutoryRules.Add(new StatutoryRule
        {
            TenantId = tenantId, CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = RuleKeys.GosiSaudiEmployeeRate, RuleValue = "0.20", DataType = "decimal", EffectiveFrom = ef,
        });
        db.CompanyStatutoryOverrides.Add(new CompanyStatutoryOverride
        {
            TenantId = tenantId, CompanyId = company.Id, CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = KsaGosiWageBounds.CeilingRuleKey, OverrideValue = "5000", DataType = "decimal",
            EffectiveFrom = new DateOnly(2020, 1, 1), ReviewBy = new DateOnly(2030, 1, 1), Reason = "legacy",
            Status = "Active", CreatedBy = Guid.NewGuid(), ApprovedBy = Guid.NewGuid(),
        });
        db.CountryPayrollRules.Add(new CountryPayrollRule
        {
            TenantId = tenantId, CountryCode = "SAU", RuleKey = RuleKeys.GosiSanedRate, RuleValue = "0.05",
            DataType = "decimal", EffectiveFrom = ef,
        });
        await db.SaveChangesAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var run = await NewRun(db, tenantId, company, today.Year, today.Month);
        (await Payroll(db, tenantId, reader).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var gosi = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == run.Id && d.EmployeeId == employee.Id && d.ComponentCode.StartsWith("GOSI"))
            .ToListAsync();
        gosi.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount).Should().Be(1_218.75m,
            "9% + 0.75% of 12,500 — not the 20% rate, the 5% SANED or the 5,000 ceiling saved at tenant level");
        gosi.Where(d => d.IsEmployerContribution).Sum(d => d.Amount).Should().Be(1_468.75m);

        // The readiness report says so, plainly.
        var report = await new GosiReadinessReportService(db, reader).BuildAsync(tenantId, CancellationToken.None);
        var warning = report.TenantWarnings.Should().ContainSingle(w => w.Code == GosiStatutoryValues.IgnoredOverrideWarningCode).Subject;
        warning.Message.Should().Contain("saved but never applied").And.Contain("GOSI-published rate")
            .And.Contain(RuleKeys.GosiSaudiEmployeeRate).And.Contain(KsaGosiWageBounds.CeilingRuleKey).And.Contain(RuleKeys.GosiSanedRate);

        // The Rates screen shows the GOSI-published value as resolved and flags the override as never applied.
        var rates = WithUser(new RatesController(db, reader, new StatutoryRateResolver(db, reader)), tenantId);
        var listed = (await rates.ListStatutory(company.Id, CountryCodes.Saudi, Jurisdictions.KsaMainland, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value!;
        var ceilingRow = ((System.Collections.IEnumerable)listed).Cast<object>()
            .Single(r => Prop<string>(r, "ruleKey") == KsaGosiWageBounds.CeilingRuleKey);
        Prop<bool>(ceilingRow, "neverApplied").Should().BeTrue();
        Prop<bool>(ceilingRow, "statutoryLocked").Should().BeTrue();
        ceilingRow.GetType().GetProperty("resolvedValue")!.GetValue(ceilingRow).Should().Be(45_000m,
            "payroll resolves the GOSI-published ceiling, not the 5,000 the company saved");

        // The retired GOSI rule listing says so on every row.
        var gosiCtrl = WithUser(new GosiController(db, TestReconciliation.For(db), reader), tenantId);
        var retiredList = (await gosiCtrl.GetContributionRules(CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        ((System.Collections.IEnumerable)retiredList).Cast<object>().Should().NotBeEmpty()
            .And.OnlyContain(r => Prop<bool>(r, "retired") && !Prop<bool>(r, "appliedToPayroll"));

        // The rows are kept…
        (await db.StatutoryRules.IgnoreQueryFilters().AnyAsync(r => r.TenantId == tenantId && r.RuleKey == RuleKeys.GosiSaudiEmployeeRate))
            .Should().BeTrue("existing override rows are an audit record and are not deleted");

        // …and the runbook's read-only query lists all three, exactly as written in the runbook.
        var sql = RunbookGosiOverrideQuery();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var sources = new List<string>();
        await using (var reader2 = await cmd.ExecuteReaderAsync())
            while (await reader2.ReadAsync())
                if (reader2.GetGuid(reader2.GetOrdinal("tenant_id")) == tenantId)
                    sources.Add(reader2.GetString(reader2.GetOrdinal("source")));
        sources.Should().BeEquivalentTo(new[] { "statutory_rules", "company_statutory_overrides", "country_payroll_rules" });
    }

    private static string RunbookGosiOverrideQuery()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "DEPLOY_ROLLBACK_RUNBOOK.md"))) dir = dir.Parent;
        dir.Should().NotBeNull("the runbook must be reachable from the test run");
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "DEPLOY_ROLLBACK_RUNBOOK.md"));
        var section = text[text.IndexOf("## GOSI tenant overrides that were saved but never applied", StringComparison.Ordinal)..];
        var start = section.IndexOf("```sql", StringComparison.Ordinal) + "```sql".Length;
        return section[start..section.IndexOf("```", start, StringComparison.Ordinal)];
    }

    private static void AssertGosiRefusal(IActionResult? result)
    {
        var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop<string>(refused.Value!, "code").Should().Be(GosiStatutoryValues.RefusalCode);
        Prop<string>(refused.Value!, "message").Should().Contain("GOSI publishes").And.Contain("Nothing has been saved");
        Prop<string>(refused.Value!, "messageAr").Should().Contain("للتأمينات الاجتماعية");
    }

    private static T WithUser<T>(T controller, Guid tenantId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } };
        return controller;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3. YTD opening balances — one period, one source
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The customer goes live on 1 September 2026. The old system's year-to-date as at 31 August is
    /// carried in (100,000 gross). The consultant also ran and locked AUGUST in this product as a
    /// parallel run. August is inside the 100,000 already, so September's YTD is 100,000 + September —
    /// not 100,000 + August + September.
    /// </summary>
    [Fact]
    public async Task Ytd_CarriedBalanceAndAPreCutoverRun_CountAugustOnce()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var rules = KsaRules();

        var august = await ProcessAndLock(db, tenantId, company, rules, 2026, 8);
        var augustSlip = await Slip(db, august.Id, employee.Id);

        db.CompanyCutovers.Add(new CompanyCutover
        {
            TenantId = tenantId, CompanyId = company.Id, CutoverDate = new DateOnly(2026, 9, 1),
            SourceSystem = "SAP", Status = CutoverStatuses.Active,
        });
        AddOpeningYtd(db, tenantId, company, employee, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var september = await NewRun(db, tenantId, company, 2026, 9);
        (await Payroll(db, tenantId, rules).Process(september.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var sepSlip = await Slip(db, september.Id, employee.Id);

        sepSlip.YtdGross.Should().Be(100_000m + sepSlip.GrossSalary,
            "the carried 100,000 already contains August; the August payslip must not be added again");
        sepSlip.YtdGross.Should().NotBe(100_000m + augustSlip.GrossSalary + sepSlip.GrossSalary);
        sepSlip.YtdDeductions.Should().Be(9_750m + sepSlip.Deductions);
        sepSlip.YtdNet.Should().Be(90_250m + sepSlip.NetSalary);

        (await Findings(db, september.Id)).Should().Contain(f =>
            f.Code == PayrollYtdBasis.PreCutoverExcludedCode && f.Severity == "Warning" && f.EmployeeId == employee.Id,
            "the preparer is told which payslips the opening balance stands in for");

        // The month after: September is in-product and counts; August still does not.
        await db.PayrollRuns.Where(r => r.Id == september.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Locked"));
        var october = await NewRun(db, tenantId, company, 2026, 10);
        (await Payroll(db, tenantId, rules).Process(october.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var octSlip = await Slip(db, october.Id, employee.Id);
        octSlip.YtdGross.Should().Be(100_000m + sepSlip.GrossSalary + octSlip.GrossSalary);
    }

    /// <summary>
    /// The other side of the boundary. A run for a month BEFORE the cutover — processed after the
    /// balances were loaded — must not add an opening balance that is stated as at a later date and
    /// already contains that very month.
    /// </summary>
    [Fact]
    public async Task Ytd_ARunBeforeTheCutover_DoesNotAddTheLaterOpeningBalance()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.CompanyCutovers.Add(new CompanyCutover
        {
            TenantId = tenantId, CompanyId = company.Id, CutoverDate = new DateOnly(2026, 9, 1),
            SourceSystem = "SAP", Status = CutoverStatuses.Active,
        });
        AddOpeningYtd(db, tenantId, company, employee, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var august = await NewRun(db, tenantId, company, 2026, 8);
        (await Payroll(db, tenantId, KsaRules()).Process(august.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var slip = await Slip(db, august.Id, employee.Id);

        slip.YtdGross.Should().Be(slip.GrossSalary,
            "the 31 August opening balance already includes August; adding it to an August payslip double-counts August");
    }

    /// <summary>
    /// No cutover declared, but carried YTD and this product's locked payslips both exist for the year.
    /// Nothing says where the carried figures end, so the run is blocked with a coded reason — by
    /// Process and, identically, by /validate.
    /// </summary>
    [Fact]
    public async Task Ytd_NoCutover_BothSourcesPresent_IsBlockedNotGuessed()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var rules = KsaRules();

        await ProcessAndLock(db, tenantId, company, rules, 2026, 8);
        AddOpeningYtd(db, tenantId, company, employee, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var september = await NewRun(db, tenantId, company, 2026, 9);
        (await Payroll(db, tenantId, rules).Process(september.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await Findings(db, september.Id)).Should().Contain(f =>
            f.Code == PayrollYtdBasis.UnresolvedOverlapCode && f.Severity == "Error" && f.EmployeeId == employee.Id
            && f.Message.Contains("Declare a cutover of 2026-08-01"),
            "the block names the month to declare: the first month this product locked this year");

        (await Payroll(db, tenantId, rules).Validate(september.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await Findings(db, september.Id)).Should().Contain(f =>
            f.Code == PayrollYtdBasis.UnresolvedOverlapCode && f.Severity == "Error",
            "/validate replaces the findings wholesale and must re-derive the same block");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3b. Cutover lifecycle — the boundary a locked run used cannot move
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A finished migration is CLOSED after months have been locked. Closing does not move the boundary,
    /// so it is accepted, and a Closed cutover keeps governing: November's YTD is still the carried
    /// 100,000 plus September, October and November, with no unresolved-overlap block.
    /// </summary>
    [Fact]
    public async Task Cutover_ClosingAfterMonthTwo_IsAllowed_AndKeepsGoverningTheYtd()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var rules = KsaRules();

        Ok(await Migration(db, tenantId).Commit(CutoverPackage(company, "2026-09-01", "Active", "wave1"), CancellationToken.None))
            .Errors.Should().BeEmpty();
        AddOpeningYtd(db, tenantId, company, employee, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var sep = await ProcessAndLock(db, tenantId, company, rules, 2026, 9);
        var oct = await ProcessAndLock(db, tenantId, company, rules, 2026, 10);

        Ok(await Migration(db, tenantId).Commit(CutoverPackage(company, "2026-09-01", "Closed", "close"), CancellationToken.None))
            .Errors.Should().BeEmpty("closing does not move the boundary a locked run used");
        (await db.CompanyCutovers.AsNoTracking().SingleAsync(c => c.CompanyId == company.Id)).Status.Should().Be(CutoverStatuses.Closed);

        var nov = await NewRun(db, tenantId, company, 2026, 11);
        (await Payroll(db, tenantId, rules).Process(nov.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var novSlip = await Slip(db, nov.Id, employee.Id);
        novSlip.YtdGross.Should().Be(100_000m + (await Slip(db, sep.Id, employee.Id)).GrossSalary
                                     + (await Slip(db, oct.Id, employee.Id)).GrossSalary + novSlip.GrossSalary,
            "a Closed cutover still says where the carried figures end");
        (await Findings(db, nov.Id)).Should().NotContain(f => f.Code == PayrollYtdBasis.UnresolvedOverlapCode);
    }

    /// <summary>
    /// Moving the cutover — or turning it back to Planned — after a run in or after the boundary is
    /// locked is refused with a coded EN/AR reason, by a package that carries ONLY the cutover row.
    /// </summary>
    [Theory]
    [InlineData("2026-10-01", "Active")]
    [InlineData("2026-08-01", "Active")]
    [InlineData("2026-09-01", "Planned")]
    public async Task Cutover_MovedAfterALockedRun_IsRefused_EvenInACutoverOnlyPackage(string newDate, string newStatus)
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m, joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.CompanyCutovers.Add(new CompanyCutover
        {
            TenantId = tenantId, CompanyId = company.Id, CutoverDate = new DateOnly(2026, 9, 1),
            SourceSystem = "SAP", Status = CutoverStatuses.Active,
        });
        await db.SaveChangesAsync();
        await ProcessAndLock(db, tenantId, company, KsaRules(), 2026, 9);

        var result = await Migration(db, tenantId).Commit(CutoverPackage(company, newDate, newStatus, "move"), CancellationToken.None);

        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
        json.Should().Contain(CutoverStatuses.ChangeAfterLockedRunCode).And.Contain("2026-09")
            .And.Contain("ReasonAr", "the reason is given in Arabic too");
        var stored = await db.CompanyCutovers.AsNoTracking().SingleAsync(c => c.CompanyId == company.Id);
        stored.CutoverDate.Should().Be(new DateOnly(2026, 9, 1));
        stored.Status.Should().Be(CutoverStatuses.Active);
    }

    /// <summary>
    /// Payslip YTD opening balances are refused unless a cutover is in force or declared in the same
    /// package — the import that used to create the month-two deadlock. Detail buckets stay optional.
    /// </summary>
    [Fact]
    public async Task Import_PayslipYtdBalancesWithNoCutover_AreRefused()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var dto = Ok(await Migration(db, tenantId).Commit(new MigrationPackageRequest($"ytd-{Guid.NewGuid():N}", new Dictionary<string, string>
        {
            ["payrollOpeningBalances"] = "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n"
                + $"{employee.EmployeeCode},2026,YTD_GROSS,TOTAL,100000,SAR,SAP,Y1\n"
                + $"{employee.EmployeeCode},2026,YTD_STATUTORY_EE,GOSI,9750,SAR,SAP,Y2\n",
        }, false), CancellationToken.None));

        dto.Errors.Should().ContainSingle(e => e.Contains(CutoverStatuses.BalanceNeedsCutoverCode) && e.Contains("YTD_GROSS"));
        (await db.PayrollOpeningBalances.Where(b => b.EmployeeId == employee.Id).Select(b => b.BalanceType).ToListAsync())
            .Should().BeEquivalentTo(new[] { OpeningBalanceTypes.YtdStatutoryEmployee },
                "the payslip aggregate is refused; a detail bucket is never summed into a payslip and stays allowed");
    }

    /// <summary>
    /// THE DEADLOCK, RESOLVED. Legacy carried balances with no cutover; September run and locked here;
    /// October blocked by 14b, which names 2026-09-01. A FIRST declaration of that month is not a change
    /// and is accepted; October then counts every month once.
    /// </summary>
    [Fact]
    public async Task Cutover_FirstDeclarationOfTheNamedMonth_ResolvesTheMonthTwoDeadlock()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var rules = KsaRules();
        AddOpeningYtd(db, tenantId, company, employee, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var sep = await ProcessAndLock(db, tenantId, company, rules, 2026, 9);
        var oct = await NewRun(db, tenantId, company, 2026, 10);
        (await Payroll(db, tenantId, rules).Process(oct.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await Findings(db, oct.Id)).Should().Contain(f => f.Code == PayrollYtdBasis.UnresolvedOverlapCode
            && f.Message.Contains("Declare a cutover of 2026-09-01"));

        Ok(await Migration(db, tenantId).Commit(CutoverPackage(company, "2026-09-01", "Active", "first"), CancellationToken.None))
            .Errors.Should().BeEmpty("a first declaration on the earliest locked month is not a change");

        // Validating the run again (what the 14b message asks for) re-derives the YTD basis with the cutover.
        (await Payroll(db, tenantId, rules).Validate(oct.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var octSlip = await Slip(db, oct.Id, employee.Id);
        octSlip.YtdGross.Should().Be(100_000m + (await Slip(db, sep.Id, employee.Id)).GrossSalary + octSlip.GrossSalary,
            "the carried figure ends at August; September and October are counted once each");
        var basis = await PayrollYtdBasis.LoadAsync(db, tenantId, company.Id, oct, new[] { employee.Id }, CancellationToken.None);
        basis.PriorSlips.Should().ContainSingle(p => p.RunId == sep.Id, "September is on the earned-here side of a 2026-09-01 cutover");
        basis.OpeningBalancesByEmployee.Should().ContainKey(employee.Id);
        (await Findings(db, oct.Id)).Should().NotContain(f => f.Code == PayrollYtdBasis.UnresolvedOverlapCode);
    }

    /// <summary>A first declaration LATER than a locked run is refused, naming the latest month allowed.</summary>
    [Fact]
    public async Task Cutover_FirstDeclarationAfterALockedRun_IsRefusedNamingTheMonth()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m, joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await ProcessAndLock(db, tenantId, company, KsaRules(), 2026, 9);

        var result = await Migration(db, tenantId).Commit(CutoverPackage(company, "2026-10-01", "Active", "late"), CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value);
        json.Should().Contain(CutoverStatuses.FirstDeclarationTooLateCode).And.Contain("2026-09-01").And.Contain("ReasonAr");
        (await db.CompanyCutovers.AnyAsync(c => c.CompanyId == company.Id)).Should().BeFalse();
    }

    /// <summary>A cutover must be the 1st of a month; the refusal is coded and names the month.</summary>
    [Fact]
    public async Task Cutover_MidMonth_IsRefusedNamingTheMonth()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);

        var dto = Ok(await Migration(db, tenantId).Commit(CutoverPackage(company, "2026-09-15", "Active", "mid"), CancellationToken.None));

        var error = dto.Errors.Should().ContainSingle().Subject;
        error.Should().Contain(CutoverStatuses.NotFirstOfMonthCode).And.Contain("September 2026")
            .And.Contain("2026-09-01").And.Contain("2026-10-01");
        (await db.CompanyCutovers.AnyAsync(c => c.CompanyId == company.Id)).Should().BeFalse();
    }

    /// <summary>
    /// Per employee: someone hired during the parallel run is not in the legacy file. Their August
    /// payslip is the only record of August and must count; only the employee whose carried balance
    /// contains August has it excluded.
    /// </summary>
    [Fact]
    public async Task Ytd_ParallelRunHire_WithNoCarriedBalance_KeepsTheirPreCutoverPayslip()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var migrated = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newHire = await SeedEmployee(db, tenantId, company, "Saudi", 8_000m, 2_000m,
            joining: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var rules = KsaRules();

        var august = await ProcessAndLock(db, tenantId, company, rules, 2026, 8);
        db.CompanyCutovers.Add(new CompanyCutover
        {
            TenantId = tenantId, CompanyId = company.Id, CutoverDate = new DateOnly(2026, 9, 1),
            SourceSystem = "SAP", Status = CutoverStatuses.Active,
        });
        AddOpeningYtd(db, tenantId, company, migrated, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var september = await NewRun(db, tenantId, company, 2026, 9);
        (await Payroll(db, tenantId, rules).Process(september.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var migratedSep = await Slip(db, september.Id, migrated.Id);
        migratedSep.YtdGross.Should().Be(100_000m + migratedSep.GrossSalary);

        var hireAug = await Slip(db, august.Id, newHire.Id);
        var hireSep = await Slip(db, september.Id, newHire.Id);
        hireSep.YtdGross.Should().Be(hireAug.GrossSalary + hireSep.GrossSalary,
            "the new hire has no carried balance, so their August payslip is the only record of August");

        var findings = await Findings(db, september.Id);
        findings.Should().Contain(f => f.Code == PayrollYtdBasis.PreCutoverExcludedCode && f.EmployeeId == migrated.Id);
        findings.Should().NotContain(f => f.Code == PayrollYtdBasis.PreCutoverExcludedCode && f.EmployeeId == newHire.Id);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  1c. GOSI readiness is the run's own verdict
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// For each cohort the run treats specially, readiness shows the SAME code at the SAME severity the
    /// run raises: a new entrant and a GCC national with no home-scheme rates are Not ready (the run
    /// blocks them); a Saudi with no first-registration date is Ready with the cohort warning. The
    /// report and the preview both carry the cohort and the engine's basis.
    /// </summary>
    [Fact]
    public async Task Readiness_EachCohort_MatchesTheRunsVerdictAndCode()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var joining = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newEntrant = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m, joining, gosiFirstRegistered: new DateOnly(2025, 1, 1));
        var unknown = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m, joining, noGosiFirstRegistered: true);
        var bahraini = await SeedEmployee(db, tenantId, company, "Bahraini", 10_000m, 2_500m, joining);
        var existing = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m, joining);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var run = await NewRun(db, tenantId, company, today.Year, today.Month);
        (await Payroll(db, tenantId, reader).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var runFindings = await Findings(db, run.Id);

        var report = await new GosiReadinessReportService(db, reader).BuildAsync(tenantId, CancellationToken.None);
        GosiEmployeeReadinessRow Row(Employee e) => report.Employees.Single(r => r.EmployeeId == e.Id);

        foreach (var (emp, code) in new[]
                 {
                     (newEntrant, PayrollValidationEngine.GosiNewEntrantScheduleNotModelled),
                     (bahraini, PayrollValidationEngine.GosiGccSchemeNotConfigured),
                 })
        {
            runFindings.Should().Contain(f => f.EmployeeId == emp.Id && f.Code == code && f.Severity == "Error",
                $"the run blocks {emp.Nationality} with {code}");
            Row(emp).IsReady.Should().BeFalse($"readiness must not say Ready for someone the run blocks ({code})");
            Row(emp).BlockingIssues.Should().Contain(i => i.Code == code);
        }

        runFindings.Should().Contain(f => f.EmployeeId == unknown.Id && f.Code == PayrollValidationEngine.GosiCohortNotRecorded && f.Severity == "Warning");
        Row(unknown).IsReady.Should().BeTrue("the run only warns when the cohort is not recorded");
        Row(unknown).Warnings.Should().Contain(w => w.Code == PayrollValidationEngine.GosiCohortNotRecorded);
        Row(unknown).Cohort.Should().Be(GosiCohorts.Unknown);
        Row(unknown).Basis.Should().Contain("unverified");

        Row(existing).IsReady.Should().BeTrue();
        Row(existing).Cohort.Should().Be(GosiCohorts.PreJuly2024);
        Row(existing).Basis.Should().Contain("existing subscriber");
        Row(newEntrant).Cohort.Should().Be(GosiCohorts.NewEntrant);

        // The preview carries the cohort and the basis too.
        var gosi = new GosiController(db, new GosiReconciliationService(db, new _PilotKsaResolver(reader)), reader)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } },
        };
        var body = (await gosi.GetEmployeeReadiness(unknown.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop<string>(body, "cohort").Should().Be(GosiCohorts.Unknown);
        Prop<string>(body, "basis").Should().Contain("unverified");
        var blockedBody = (await gosi.GetEmployeeReadiness(newEntrant.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop<bool>(blockedBody, "IsReady").Should().BeFalse();
    }

    /// <summary>
    /// The preview prices the salary the RUN will use: effective by the period END, so a raise dated
    /// later this month is previewed as payroll will pay it, not at today's figure.
    /// </summary>
    [Fact]
    public async Task Preview_UsesTheSalaryAsOfThePeriodEnd_LikeTheRun()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var periodEnd = GosiReadinessValidator.PeriodEnd(DateOnly.FromDateTime(DateTime.UtcNow));
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 20_000m, HousingAllowance = 5_000m, Currency = "SAR", EffectiveDate = periodEnd, IsActive = true,
        });
        await db.SaveChangesAsync();

        var gosi = new GosiController(db, new GosiReconciliationService(db, new _PilotKsaResolver(reader)), reader)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } },
        };
        var body = (await gosi.GetEmployeeReadiness(employee.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        var preview = body.GetType().GetProperty("contributionPreview")!.GetValue(body)!;
        Prop<decimal>(preview, "EmployeeTotal").Should().Be(2_437.50m,
            "9.75% of the 25,000 covered wage in force at the period end, not of today's 12,500");
    }

    private static MigrationImportController Migration(ZayraDbContext db, Guid tenantId) =>
        WithUser(new MigrationImportController(db, new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(),
            new Zayra.Api.Infrastructure.Audit.AuditService(db)), tenantId);

    private static MigrationPackageRequest CutoverPackage(Company company, string date, string status, string id) =>
        new($"cutover-{id}-{Guid.NewGuid():N}", new Dictionary<string, string>
        {
            ["companyCutover"] = "CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes\n"
                               + $"{company.RegistrationNumber},,{date},SAP,{status},test\n",
        }, false);

    private static MigrationReconciliationDto Ok(ActionResult<MigrationReconciliationDto> result) =>
        (MigrationReconciliationDto)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    // ═══════════════════════════════════════════════════════════════════════════
    //  4. EOSB — carried prior service
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Three years with the previous system (carried as PriorServiceStartDate 2022-09-01) plus one year
    /// here (joined this product 2025-09-01), terminated 2026-08-31: the Art. 84 award is the FOUR-year
    /// award — identical to an employee who joined this product on 2022-09-01 — and not the one-year one.
    /// </summary>
    [Fact]
    public async Task Eosb_ThreeYearsCarriedPlusOneHere_IsTheFourYearAward()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = company.CountryCode, EosbEnabled = true });
        await db.SaveChangesAsync();

        var migrated = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_000m,
            joining: new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var native = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_000m,
            joining: new DateTime(2022, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var oneYear = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_000m,
            joining: new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        db.EmployeeEosbOpeningBalances.Add(new EmployeeEosbOpeningBalance
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = migrated.Id, EmployeeCode = migrated.EmployeeCode,
            AsAtDate = new DateOnly(2025, 8, 31), PriorServiceStartDate = new DateOnly(2022, 9, 1),
            AccruedMonths = 36m, AccruedAmount = 18_000m, Currency = "SAR", SourceSystem = "SAP", SourceRecordId = "EOSB-PILOT-1",
        });
        await db.SaveChangesAsync();

        var leave = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var rules = KsaRules();
        var migratedAward = await Eosb(db, tenantId, rules, migrated.Id, leave);
        var nativeAward = await Eosb(db, tenantId, rules, native.Id, leave);
        var oneYearAward = await Eosb(db, tenantId, rules, oneYear.Id, leave);

        migratedAward.Should().Be(nativeAward,
            "3 years carried + 1 year here is one continuous four-year service period (Art. 84)");
        migratedAward.Should().BeApproximately(4m * 0.5m * 12_000m, 0.005m * 24_000m,
            "four years at half a month of the 12,000 last wage is about 24,000");
        oneYearAward.Should().BeLessThan(migratedAward / 3m,
            "without the carried service the award would be the one-year figure");
    }

    // ══════════════════════ Fixture ══════════════════════

    private static async Task<(Guid TenantId, Company Company)> SeedTenantAndCompany(ZayraDbContext db)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = $"Pilot Co {Guid.NewGuid():N}",
            TradeName = "Pilot Co",
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RegistrationNumber = $"PIL-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
            GosiEmployerId = "GOSI-ER-1",
            CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (tenantId, company);
    }

    private static async Task<Employee> SeedEmployee(
        ZayraDbContext db, Guid tenantId, Company company, string nationality, decimal basic, decimal housing, DateTime joining,
        DateOnly? gosiFirstRegistered = null, bool noGosiFirstRegistered = false)
    {
        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"PIL-{Guid.NewGuid():N}"[..16],
            FullName = "Pilot Employee",
            Nationality = nationality,
            Status = "Active",
            ContractType = "Indefinite",
            JoiningDate = joining,
            CountryCode = CountryCodes.Saudi,
            Salary = basic,
            GosiReference = "GOSI-123456",
            GosiFirstRegisteredOn = noGosiFirstRegistered ? null : gosiFirstRegistered ?? new DateOnly(2015, 1, 1),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            SalaryStructureId = Guid.NewGuid(),
            BasicSalary = basic,
            HousingAllowance = housing,
            TransportAllowance = 0m,
            Currency = "SAR",
            EffectiveDate = DateOnly.FromDateTime(joining),
            IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            Iban = "SA4420000001234567891234",
            MolId = $"MOL-{Guid.NewGuid():N}",
            SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        return employee;
    }

    private static async Task<PayrollRun> NewRun(ZayraDbContext db, Guid tenantId, Company company, int year, int month)
    {
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = year, Month = month, Status = "Draft",
            CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Process a month and mark it Locked, as a finished in-product month (or a parallel run) would be.</summary>
    private static async Task<PayrollRun> ProcessAndLock(
        ZayraDbContext db, Guid tenantId, Company company, IStatutoryRuleReader rules, int year, int month)
    {
        var run = await NewRun(db, tenantId, company, year, month);
        (await Payroll(db, tenantId, rules).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await db.PayrollRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Locked"));
        return run;
    }

    private static void AddOpeningYtd(ZayraDbContext db, Guid tenantId, Company company, Employee employee,
        decimal gross, decimal deductions, decimal net)
    {
        foreach (var (type, amount) in new[]
                 {
                     (OpeningBalanceTypes.YtdGross, gross),
                     (OpeningBalanceTypes.YtdDeductions, deductions),
                     (OpeningBalanceTypes.YtdNet, net),
                 })
            db.PayrollOpeningBalances.Add(new PayrollOpeningBalance
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
                Year = 2026, BalanceType = type, ComponentCode = "TOTAL", Amount = amount, Currency = "SAR",
                SourceSystem = "SAP", SourceRecordId = $"YTD-{type}",
            });
    }

    private static async Task<PayrollSlip> Slip(ZayraDbContext db, Guid runId, int employeeId) =>
        await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId && s.EmployeeId == employeeId);

    private static async Task<List<PayrollValidationResult>> Findings(ZayraDbContext db, Guid runId) =>
        await db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == runId).ToListAsync();

    private static async Task<decimal> Eosb(ZayraDbContext db, Guid tenantId, IStatutoryRuleReader rules, int employeeId, DateTime asOf)
    {
        var result = await Payroll(db, tenantId, rules).CalculateEosb(
            new EosbCalculationRequest(employeeId, asOf, "Termination"), CancellationToken.None);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return Prop<decimal>(ok.Value!, "eosbAmount");
    }

    private static StubRuleReader KsaRules() => new StubRuleReader()
        .Set("ot.hourly_base", "wage")
        .Set("ot.standard_multiplier", 1.5m)
        .Set("ot.restday_multiplier", 2.0m)
        .Set("ot.holiday_multiplier", 2.0m)
        .Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m)
        .Set("lop.standard_work_minutes_per_day", 480m)
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m);

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, IStatutoryRuleReader rules)
    {
        var ctrl = new PayrollController(
            db,
            new _PilotScope(),
            new HttpContextAccessor(),
            new _PilotNotifications(),
            new _PilotKsaResolver(rules),
            rules,
            new _PilotLetters(),
            new NullDocumentStorage(),
            new PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } };
        return ctrl;
    }

    private static ClaimsPrincipal Principal(Guid tenantId) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()),
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "Pilot Tester"),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim("permission", "payroll.read"),
        new Claim("permission", "payroll.write"),
        new Claim("permission", "payroll.rates.statutory_override"),
        new Claim("permission", "organization.setup.apply"),
    }, "Test"));

    private static T Prop<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _PilotScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
}

file sealed class _PilotNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _PilotKsaResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _rules;
    public _PilotKsaResolver(IStatutoryRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(_rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new KsaEndOfServiceCalculator(_rules);
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _PilotNoSetupPreview : Zayra.Api.Application.Setup.ISetupAssistantService
{
    public Task<Zayra.Api.Application.Setup.SetupPreviewResult> GenerateAsync(
        Zayra.Api.Application.Setup.SetupRequester requester, Zayra.Api.Application.Setup.CompanyProfile profile, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Setup.SetupPreviewResult(Zayra.Api.Application.Setup.SetupDraft.Empty(), [], "test"));
}

file sealed class _PilotLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
