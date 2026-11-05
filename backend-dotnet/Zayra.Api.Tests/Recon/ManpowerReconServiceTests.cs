using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// The reconciliation service over a seeded run pair: every movement category, the value bridge, the final-settlement block,
/// the three modes, scope, and I5 (the per-employee gross movement is exactly what the existing reconciliation endpoint reports).
/// </summary>
public class ManpowerReconServiceTests
{
    internal sealed class Kit
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid CompanyId { get; } = Guid.NewGuid();
        public Guid OtherCompanyId { get; } = Guid.NewGuid();
        public Guid MakerId { get; } = Guid.NewGuid();
        public PayrollRun Prior { get; private set; } = null!;
        public PayrollRun Current { get; private set; } = null!;
        public PayrollRun OtherCompanyPrior { get; private set; } = null!;

        public static async Task<Kit> CreateAsync(bool withPrior = true, string currentType = PayrollRunTypes.Regular)
        {
            var k = new Kit();
            k.Db.Companies.Add(new Company { Id = k.CompanyId, TenantId = k.TenantId, LegalNameEn = "Co One", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" });
            if (withPrior)
                k.Prior = k.AddRun(2026, 9, "Locked");
            k.Current = k.AddRun(2026, 10, "Processed", currentType, createdBy: k.MakerId, processedBy: k.MakerId);
            await k.Db.SaveChangesAsync();
            return k;
        }

        public PayrollRun AddRun(int year, int month, string status, string type = PayrollRunTypes.Regular, Guid? company = null,
            Guid? createdBy = null, Guid? processedBy = null)
        {
            var r = new PayrollRun
            {
                TenantId = TenantId, CompanyId = company ?? CompanyId, Year = year, Month = month, Status = status, RunType = type,
                CreatedByUserId = createdBy, ProcessedByUserId = processedBy, ProcessVersion = 1,
            };
            Db.PayrollRuns.Add(r);
            return r;
        }

        public Employee AddEmployee(int id, string status = "Active", DateTime? joined = null, Guid? company = null, DateOnly? original = null)
        {
            var e = new Employee
            {
                Id = id, TenantId = TenantId, CompanyId = company ?? CompanyId, EmployeeCode = $"E{id}", FullName = $"Employee {id}",
                Status = status, JoiningDate = joined ?? new DateTime(2024, 1, 1), OriginalJoiningDate = original,
            };
            Db.Employees.Add(e);
            return e;
        }

        /// <summary>A slip plus matching earning and deduction lines, so the line-versus-register invariant holds.</summary>
        public PayrollSlip AddSlip(PayrollRun run, int emp, decimal basic, decimal housing = 0m, decimal gosiEe = 0m, decimal gosiEr = 0m,
            bool finalWage = false, decimal? fullBasic = null, decimal? factor = null, Guid? company = null)
        {
            var gross = basic + housing;
            var slip = new PayrollSlip
            {
                TenantId = TenantId, CompanyId = company ?? run.CompanyId, RunId = run.Id, EmployeeId = emp, EmployeeCode = $"E{emp}", EmployeeName = $"Employee {emp}",
                BasicSalary = basic, HousingAllowance = housing, GrossSalary = gross, Deductions = gosiEe, NetSalary = gross - gosiEe,
                EmployerStatutoryTotal = gosiEr, IsFinalWageMonth = finalWage, FullBasicSalary = fullBasic, ProrationFactor = factor,
            };
            Db.PayrollSlips.Add(slip);
            Db.PayrollEarnings.Add(new PayrollEarning { TenantId = TenantId, PayrollRunId = run.Id, EmployeeId = emp, ComponentCode = "BASIC", ComponentName = "Basic salary", Amount = basic, Source = "Salary" });
            if (housing != 0m)
                Db.PayrollEarnings.Add(new PayrollEarning { TenantId = TenantId, PayrollRunId = run.Id, EmployeeId = emp, ComponentCode = "HOUSING", ComponentName = "Housing allowance", Amount = housing, Source = "Salary" });
            if (gosiEe != 0m)
                Db.PayrollDeductions.Add(new PayrollDeduction { TenantId = TenantId, CompanyId = run.CompanyId, PayrollRunId = run.Id, EmployeeId = emp, ComponentCode = "GOSI-EE", ComponentName = "GOSI employee", Amount = gosiEe, Source = "Statutory" });
            if (gosiEr != 0m)
                Db.PayrollDeductions.Add(new PayrollDeduction { TenantId = TenantId, CompanyId = run.CompanyId, PayrollRunId = run.Id, EmployeeId = emp, ComponentCode = "GOSI-ER", ComponentName = "GOSI employer", Amount = gosiEr, Source = "Statutory", IsEmployerContribution = true });
            return slip;
        }

        public ReconCaller Caller(EntityScopeContext? entity = null, DataScope? data = null, bool approve = true, Guid? userId = null) =>
            new(userId ?? Guid.NewGuid(), "Reviewer", approve, entity ?? EntityScopeContext.GroupLevel,
                data ?? new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });

        public Task<ReconBuildResult> Build(ReconCaller? caller = null, Guid? priorOverride = null, PayrollRun? run = null) =>
            new ManpowerReconService(Db).BuildAsync(TenantId, (run ?? Current).Id, caller ?? Caller(), priorOverride, CancellationToken.None);

        /// <summary>The scenario used by most tests: one employee in every movement category.</summary>
        public async Task SeedEveryCategoryAsync()
        {
            OtherCompanyPrior = AddRun(2026, 9, "Locked", company: OtherCompanyId);
            // 1 continuing: basic 1000 -> 1100, housing 250 -> 250, GOSI 90 -> 99, employer 110 -> 121
            AddEmployee(1); AddSlip(Prior, 1, 1000, 250, 90, 110); AddSlip(Current, 1, 1100, 250, 99, 121);
            // 2 leaver with an approved final settlement accrued into this run's period
            AddEmployee(2, "Offboarded"); AddSlip(Prior, 2, 2000, 500, 180, 220, finalWage: true);
            // 3 leaver with no settlement yet
            AddEmployee(3, "Offboarded"); AddSlip(Prior, 3, 1500, 0, 135, 165, finalWage: true);
            // 4 new joiner
            AddEmployee(4, joined: new DateTime(2026, 10, 5)); AddSlip(Current, 4, 900, 0, 81, 99);
            // 5 deliberately held out
            AddEmployee(5); AddSlip(Prior, 5, 800); Db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
            { TenantId = TenantId, CompanyId = CompanyId, PayrollRunId = Current.Id, EmployeeId = 5, Mode = "Exclude", Reason = "On unpaid sabbatical", Outcome = PayrollRunSelectionOutcomes.Excluded });
            // 6 moved to the sister company
            AddEmployee(6, company: OtherCompanyId); AddSlip(Prior, 6, 700);
            // 7 arrived from the sister company
            AddEmployee(7, joined: new DateTime(2022, 3, 1)); AddSlip(OtherCompanyPrior, 7, 600); AddSlip(Current, 7, 650);
            // 8 a rehire
            AddEmployee(8, joined: new DateTime(2026, 10, 12), original: new DateOnly(2019, 2, 1)); AddSlip(Current, 8, 500);
            // 9 gone for no recorded reason
            AddEmployee(9); AddSlip(Prior, 9, 400);

            var offboarding = new EmployeeOffboarding { TenantId = TenantId, CompanyId = CompanyId, EmployeeId = 2, EmployeeCode = "E2", LastWorkingDay = new DateOnly(2026, 9, 30), Status = "InProgress" };
            Db.EmployeeOffboardings.Add(offboarding);
            var settlement = new EmployeeFinalSettlement
            {
                TenantId = TenantId, CompanyId = CompanyId, EmployeeId = 2, EmployeeCode = "E2", EmployeeName = "Employee 2", OffboardingId = offboarding.Id,
                LastWorkingDay = new DateOnly(2026, 9, 30), Status = "Approved", GlPeriod = "2026-10", GrossPayable = 5000m, TotalDeductions = 0m,
            };
            Db.EmployeeFinalSettlements.Add(settlement);
            Db.FinalSettlementLines.Add(new FinalSettlementLine { TenantId = TenantId, SettlementId = settlement.Id, ComponentCode = "EOSB_GRATUITY", LineType = "Earning", Amount = 5000m });
            await Db.SaveChangesAsync();
        }
    }

    // ── The bridges ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryCategory_IsCountedOnce_AndTheHeadcountBridgeTies()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var result = await k.Build();

        result.Status.Should().Be(ReconBuildStatus.Ok);
        var dto = result.Build!.Dto;
        dto.Mode.Should().Be(ReconModes.Full);
        dto.Period.Should().Be("2026-10");
        dto.PriorPeriod.Should().Be("2026-09");
        dto.PriorRunId.Should().Be(k.Prior.Id);
        dto.Currency.Should().Be("SAR");
        dto.Volume.Opening.Should().Be(6);   // 1,2,3,5,6,9
        dto.Volume.Closing.Should().Be(4);   // 1,4,7,8
        int Count(string c) => dto.Volume.Lines.Single(l => l.Category == c).Count;
        Count("NEW_JOINER").Should().Be(1);
        Count("REHIRE").Should().Be(1);
        Count("TRANSFER_IN").Should().Be(1);
        Count("LATE_JOINER").Should().Be(0);
        Count("REINSTATED").Should().Be(0);
        Count("LEAVER").Should().Be(2);
        Count("TRANSFER_OUT").Should().Be(1);
        Count("NOT_PAID").Should().Be(1);
        Count("UNEXPLAINED").Should().Be(1);
        dto.Volume.Ties.Should().BeTrue();
        var leaver = dto.Volume.Lines.Single(l => l.Category == "LEAVER");
        leaver.FsApproved.Should().Be(1);
        leaver.FsMissing.Should().Be(1);
        dto.Volume.Lines.Where(l => l.Category != "LEAVER").Should().OnlyContain(l => l.FsApproved == null && l.FsMissing == null);
        dto.Volume.Lines.Select(l => l.Kind).Should().Equal("Add", "Add", "Add", "Add", "Add", "Exit", "Exit", "Exit", "Exit");
        dto.Volume.Memo.FinalWageMonth.Should().Be(0, "the final wage months in the CURRENT run: none");
    }

    [Fact]
    public async Task TheValueBridge_TiesForEveryItemAndTheHeadline()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var dto = (await k.Build()).Build!.Dto;

        dto.Value.Ties.Should().BeTrue();
        dto.Integrity.Ok.Should().BeTrue(string.Join("; ", dto.Integrity.Issues.Select(i => i.Message)));
        var basic = dto.Value.Sections.Single(s => s.Kind == "Earning").Items.Single(i => i.Code == "BASIC");
        basic.Prior.Should().Be(1000 + 2000 + 1500 + 800 + 700 + 400);
        basic.Current.Should().Be(1100 + 900 + 650 + 500);
        basic.Adds.NewJoiner.Should().Be(900);
        basic.Adds.Rehire.Should().Be(500);
        basic.Adds.TransferIn.Should().Be(650);
        basic.Exits.Leaver.Should().Be(-3500);
        basic.Exits.NotPaid.Should().Be(-800);
        basic.Exits.TransferOut.Should().Be(-700);
        basic.Exits.Unexplained.Should().Be(-400);
        basic.Continuing.Package.Should().Be(100);
        (basic.Prior + basic.Adds.Total + basic.Exits.Total + basic.Continuing.Total).Should().Be(basic.Current);

        var h = dto.Value.Headline;
        h.Net.Current.Should().Be(h.Gross.Current - h.Deductions.Current);
        h.Employer.Current.Should().Be(220m, "employer cost is its own section and never part of net");
        dto.Value.Sections.Single(s => s.Kind == "Employer").Items.Should().ContainSingle(i => i.Code == "GOSI-ER");
        dto.Value.RegisterDifference.Should().Be(0m);
    }

    [Fact]
    public async Task TheFinalSettlementBlock_ShowsAccruedSettlements_AndTheLeaverWhoseIsMissing()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var fs = (await k.Build()).Build!.Dto.FinalSettlement;

        fs.Cells.Single(c => c.Code == "LEAVER_IN_BRIDGE").Should().Be(new ReconFsCellDto("LEAVER_IN_BRIDGE", 1));
        fs.Cells.Sum(c => c.Count).Should().Be(1);
        fs.PerComponent.Single(c => c.Code == "EOSB_GRATUITY").Should().Be(new ReconFsComponentDto("EOSB_GRATUITY", 0m, 5000m, 5000m));
        fs.LeaversWithoutFs.Should().Equal(new ReconLeaverWithoutFsDto(3, "E3", "Employee 3", null));
        fs.Ties.Should().BeTrue();
    }

    [Fact]
    public async Task PackageAndProration_AreSplitUsingTheSlipWitnesses()
    {
        var k = await Kit.CreateAsync();
        k.AddEmployee(1);
        k.AddSlip(k.Prior, 1, 1000, fullBasic: 1000m, factor: 1m);
        k.AddSlip(k.Current, 1, 600, fullBasic: 1200m, factor: 0.5m);
        await k.Db.SaveChangesAsync();

        var basic = (await k.Build()).Build!.Dto.Value.Sections.Single(s => s.Kind == "Earning").Items.Single(i => i.Code == "BASIC");
        basic.Continuing.Package.Should().Be(100);
        basic.Continuing.Proration.Should().Be(-500);
    }

    [Fact]
    public async Task ARehire_ARunWithAFinalWageMonth_AndAProratedContinuer_AreAllRecordedWithoutBreakingTheTie()
    {
        var k = await Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 500, finalWage: true, factor: 0.5m);
        await k.Db.SaveChangesAsync();
        var build = (await k.Build()).Build!;
        build.Dto.Volume.Memo.FinalWageMonth.Should().Be(1);
        build.Employees[1].FinalWageMonth.Should().BeTrue();
        build.Employees[1].Prorated.Should().BeTrue();
        build.Dto.Volume.Ties.Should().BeTrue();
    }

    // ── I5: the same per-employee movement as the endpoint that already exists ───────────────────

    [Fact]
    public async Task I5_ThePerEmployeeGrossMovement_IsExactlyWhatTheExistingReconciliationEndpointReports()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var build = (await k.Build()).Build!;

        var old = await OldEndpointAsync(k);
        var variances = old.GetProperty("variances").EnumerateArray().ToList();
        variances.Should().NotBeEmpty();
        foreach (var v in variances)
        {
            var id = v.GetProperty("employeeId").GetInt32();
            var info = build.Employees[id];
            (info.CurrentGross - info.PriorGross).Should().Be(v.GetProperty("grossDelta").GetDecimal(), $"employee {id}");
            info.PriorGross.Should().Be(v.GetProperty("priorGross").GetDecimal());
            info.CurrentGross.Should().Be(v.GetProperty("currentGross").GetDecimal());
        }
        // The old endpoint's continuing population is exactly the bridge's CONTINUING + TRANSFER_IN is not in it (different prior company).
        build.Bridge!.Count(ReconCategory.Continuing).Should().Be(old.GetProperty("variances").GetArrayLength());
        build.Dto.Volume.Opening.Should().Be(old.GetProperty("priorHeadcount").GetInt32());
        build.Dto.Volume.Closing.Should().Be(old.GetProperty("currentHeadcount").GetInt32());
    }

    private static async Task<JsonElement> OldEndpointAsync(Kit k)
    {
        var rules = PayComponentNetPayDefectTests.KsaRules();
        var ctrl = new PayrollController(k.Db, new DataScopeService(k.Db), new HttpContextAccessor(), new F2NullNotifications(),
            new KsaTestPackResolver(rules), rules, new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        var claims = new List<Claim> { new("tenant_id", k.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin"), new("permission", "payroll.read"), new("permission", "employees.read") };
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        var res = await ctrl.Reconciliation(k.Current.Id, CancellationToken.None);
        return JsonSerializer.SerializeToElement(((OkObjectResult)res).Value);
    }

    // ── Modes ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithNoPriorRun_TheModeIsNoPrior_AndOnlyTheCompositionIsShown()
    {
        var k = await Kit.CreateAsync(withPrior: false);
        k.AddEmployee(1); k.AddSlip(k.Current, 1, 1000, 250, 90, 110);
        k.AddEmployee(2); k.AddSlip(k.Current, 2, 800, 0, 72, 88);
        await k.Db.SaveChangesAsync();

        var dto = (await k.Build()).Build!.Dto;
        dto.Mode.Should().Be(ReconModes.NoPrior);
        dto.PriorRunId.Should().BeNull();
        dto.Volume.Opening.Should().Be(0);
        dto.Volume.Closing.Should().Be(2);
        dto.Volume.Lines.Should().BeEmpty();
        dto.Value.Headline.Gross.Current.Should().Be(2050);
        dto.Value.Headline.Gross.Prior.Should().Be(0);
        dto.Value.Sections.SelectMany(s => s.Items).Should().OnlyContain(i => !i.Flagged && i.DeltaPct == null && i.Delta == i.Current);
        dto.LastRunPeriod.Should().BeNull("no earlier period-owning run exists at all");
        dto.Integrity.Ok.Should().BeTrue();
        dto.Scope.CanSignOff.Should().BeTrue("a first run still needs its review signed");
    }

    [Fact]
    public async Task WithOnlyAVoidedPrior_TheModeIsNoPrior_AndSaysItWasVoided()
    {
        var k = await Kit.CreateAsync(withPrior: false);
        k.AddRun(2026, 9, "Voided");
        await k.Db.SaveChangesAsync();
        var dto = (await k.Build()).Build!.Dto;
        dto.Mode.Should().Be(ReconModes.NoPrior);
        dto.PriorVoided.Should().BeTrue();
    }

    [Fact]
    public async Task WithAGapInTheCalendar_NoPriorSaysWhichPeriodWasLastRun()
    {
        var k = await Kit.CreateAsync(withPrior: false);
        k.AddRun(2026, 7, "Locked");
        await k.Db.SaveChangesAsync();
        var dto = (await k.Build()).Build!.Dto;
        dto.Mode.Should().Be(ReconModes.NoPrior);
        dto.LastRunPeriod.Should().Be("2026-07");
    }

    [Fact]
    public async Task AnOffCycleRun_IsNotApplicable_AndCannotBeSigned()
    {
        var k = await Kit.CreateAsync(currentType: PayrollRunTypes.OffCycle);
        var dto = (await k.Build()).Build!.Dto;
        dto.Mode.Should().Be(ReconModes.NotApplicable);
        dto.Scope.CanSignOff.Should().BeFalse();
        dto.Scope.Reasons.Should().BeEmpty();
        dto.Volume.Lines.Should().BeEmpty();
        dto.Value.Sections.Should().HaveCount(3).And.OnlyContain(s => s.Items.Count == 0);
    }

    [Fact]
    public async Task AnInvalidPriorOverride_IsReported_NotSwallowed()
    {
        var k = await Kit.CreateAsync();
        (await k.Build(priorOverride: Guid.NewGuid())).Status.Should().Be(ReconBuildStatus.PriorRunInvalid);
    }

    [Fact]
    public async Task AValidPriorOverride_IsUsed_AndThePriorProcessVersionIsRecorded()
    {
        var k = await Kit.CreateAsync(withPrior: false);
        var gap = k.AddRun(2026, 6, "Locked");
        gap.ProcessVersion = 4;
        await k.Db.SaveChangesAsync();
        var build = (await k.Build(priorOverride: gap.Id)).Build!;
        build.Dto.PriorRunId.Should().Be(gap.Id);
        build.PriorProcessVersion.Should().Be(4);
    }

    // ── Scope ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunOfACompanyTheCallerCannotAccess_IsNotFound()
    {
        var k = await Kit.CreateAsync();
        var result = await k.Build(k.Caller(entity: EntityScopeContext.ForCompanies(new[] { Guid.NewGuid() })));
        result.Status.Should().Be(ReconBuildStatus.NotFound);
        result.Build.Should().BeNull();
    }

    [Fact]
    public async Task ACallerWithAccessToTheCompany_Sees_ItsRun()
    {
        var k = await Kit.CreateAsync();
        (await k.Build(k.Caller(entity: EntityScopeContext.ForCompanies(new[] { k.CompanyId })))).Status.Should().Be(ReconBuildStatus.Ok);
    }

    [Fact]
    public async Task ADepartmentScopedCaller_GetsAPartialBridgeOverTheirOwnPeople_AndCannotSign()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var scoped = new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = new[] { 1, 4 } };
        var dto = (await k.Build(k.Caller(data: scoped))).Build!.Dto;

        dto.Scope.Level.Should().Be("Department");
        dto.Scope.Partial.Should().BeTrue();
        dto.Scope.CanSignOff.Should().BeFalse();
        dto.Scope.Reasons.Should().Equal("signoff_requires_organization_scope");
        dto.Volume.Closing.Should().Be(2);   // 1 and 4 only
        dto.Volume.Opening.Should().Be(1);   // 1 only
        dto.Value.Sections.Single(s => s.Kind == "Earning").Items.Single(i => i.Code == "BASIC").Current.Should().Be(1100 + 900);
        dto.FinalSettlement.Cells.Sum(c => c.Count).Should().Be(0, "the settlement belongs to an employee outside the caller's scope");
    }

    [Fact]
    public async Task CanSignOff_SpeaksForScopeOnly_PermissionAndMakerCheckerAreTheSignoffEndpointsChecks()
    {
        var k = await Kit.CreateAsync();
        (await k.Build(k.Caller(approve: false))).Build!.Dto.Scope.CanSignOff.Should().BeTrue();
        (await k.Build(k.Caller(userId: k.MakerId))).Build!.Dto.Scope.CanSignOff.Should().BeTrue();
        k.Current.Status = "Approved";
        await k.Db.SaveChangesAsync();
        (await k.Build()).Build!.Dto.Scope.CanSignOff.Should().BeTrue();
    }

    // ── Integrity ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALineTotalOutsideTheRoundingTolerance_IsAnIntegrityIssue_AndBlocksSigning()
    {
        var k = await Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); var bad = k.AddSlip(k.Current, 1, 1000);
        bad.GrossSalary = 1050m; // the register says 1050, the lines say 1000
        await k.Db.SaveChangesAsync();
        var dto = (await k.Build()).Build!.Dto;
        dto.Integrity.Ok.Should().BeFalse();
        dto.Integrity.Issues.Should().Contain(i => i.Code == "lineRegister" && i.Message.Contains("E1"));
        dto.Value.RegisterDifference.Should().Be(50m, "the difference is exposed, not absorbed");
    }

    [Fact]
    public async Task IntegrityIssues_ComeOutInAFixedOrder_WhateverOrderThePayslipsWereStoredIn()
    {
        var k = await Kit.CreateAsync();
        foreach (var e in new[] { 3, 1, 2 })
        {
            k.AddEmployee(e); k.AddSlip(k.Prior, e, 1000);
            k.AddSlip(k.Current, e, 1000).GrossSalary = 1050m;
        }
        await k.Db.SaveChangesAsync();
        var issues = (await k.Build()).Build!.Dto.Integrity.Issues;
        issues.Select(i => i.Code).Should().AllBe("lineRegister");
        issues.Select(i => i.Message).Should().BeInAscendingOrder(StringComparer.Ordinal);
        issues.Should().HaveCount(3);
    }

    [Fact]
    public async Task ADuplicateSlip_IsFlagged_AndTheEmployeeIsStillCountedOnce()
    {
        var k = await Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1000);
        k.Db.PayrollSlips.Add(new PayrollSlip { TenantId = k.TenantId, CompanyId = k.CompanyId, RunId = k.Current.Id, EmployeeId = 1, EmployeeCode = "E1" });
        await k.Db.SaveChangesAsync();
        var dto = (await k.Build()).Build!.Dto;
        dto.Integrity.Issues.Should().Contain(i => i.Code == "duplicateSlip");
        dto.Volume.Closing.Should().Be(1);
    }

    // ── Thresholds ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCompanysOwnThresholds_AreUsed_AndSaidSo()
    {
        var k = await Kit.CreateAsync();
        k.Db.CompanyPayrollRules.Add(new CompanyPayrollRule { TenantId = k.TenantId, CompanyId = k.CompanyId, RuleKey = "recon_variance_pct", RuleValue = "20", EffectiveFrom = new DateOnly(2020, 1, 1) });
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1100);
        await k.Db.SaveChangesAsync();
        var dto = (await k.Build()).Build!.Dto;
        dto.Thresholds.Should().Be(new ReconThresholds(20m, 0m, "company"));
        dto.Value.Sections.Single(s => s.Kind == "Earning").Items.Single().Flagged.Should().BeFalse("a 10% move is inside this company's 20%");
    }

    [Fact]
    public async Task WithoutCompanyRules_TheDefaultFivePercentApplies()
    {
        var k = await Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1100);
        await k.Db.SaveChangesAsync();
        var build = (await k.Build()).Build!;
        build.Dto.Thresholds.Should().Be(ReconThresholds.Default);
        build.Dto.Value.Sections.Single(s => s.Kind == "Earning").Items.Single().Flagged.Should().BeTrue();
        build.FlaggedEmployeeIds.Should().Equal(1);
        build.RequiredExplanations.Should().ContainSingle(r => r.Scope == "Item" && r.Key == "Earning:BASIC");
    }

    [Fact]
    public async Task AnUnpaidAndAnUnexplainedEmployee_EachNeedAnEmployeeExplanation()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var required = (await k.Build()).Build!.RequiredExplanations;
        required.Where(r => r.Scope == "Employee").Select(r => r.Key).Should().BeEquivalentTo(new[] { "5", "9" });
    }

    // ── Snapshot and drill-down ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSnapshotHash_IsStableAcrossBuilds_AndMovesWhenTheFiguresDo()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var a = (await k.Build()).Build!.SnapshotHash;
        (await k.Build()).Build!.SnapshotHash.Should().Be(a);

        k.Db.PayrollEarnings.Add(new PayrollEarning { TenantId = k.TenantId, PayrollRunId = k.Current.Id, EmployeeId = 1, ComponentCode = "BONUS_X", ComponentName = "Bonus", Amount = 10, Source = "Bonus" });
        var slip = await k.Db.PayrollSlips.FirstAsync(s => s.RunId == k.Current.Id && s.EmployeeId == 1);
        slip.GrossSalary += 10; slip.NetSalary += 10;
        await k.Db.SaveChangesAsync();
        (await k.Build()).Build!.SnapshotHash.Should().NotBe(a);
    }

    private static ReconDrillQuery Q(string section, string? category = null, string? kind = null, string? itemCode = null,
        string? bucket = null, int page = 1, int pageSize = 50) => new(section, category, kind, itemCode, bucket, page, pageSize);

    [Fact]
    public async Task DrillDown_ListsTheEmployeesBehindAVolumeCategory_Paged()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var build = (await k.Build()).Build!;
        var page = ManpowerReconService.DrillDown(build, Q("volume", "LEAVER", pageSize: 1))!;
        page.Total.Should().Be(2);
        page.Items.Should().HaveCount(1);
        page.PageSize.Should().Be(1);
        var all = ManpowerReconService.DrillDown(build, Q("volume", "LEAVER"))!;
        all.Items.Select(i => i.EmployeeCode).Should().Equal("E2", "E3");
        all.Items.Should().OnlyContain(i => i.Category == "LEAVER");
        all.Items.Select(i => i.Note).Should().Equal("Final settlement accrued", "No final settlement yet");
        ManpowerReconService.DrillDown(build, Q("volume", "nonsense")).Should().BeNull();
        ManpowerReconService.DrillDown(build, Q("elsewhere")).Should().BeNull();
    }

    [Fact]
    public async Task DrillDown_OpeningAndClosing_ListEveryoneInThePriorAndThisRun()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var build = (await k.Build()).Build!;
        ManpowerReconService.DrillDown(build, Q("volume", "OPENING"))!.Items.Select(i => i.EmployeeCode).Should().Equal("E1", "E2", "E3", "E5", "E6", "E9");
        ManpowerReconService.DrillDown(build, Q("volume", "CLOSING"))!.Items.Select(i => i.EmployeeCode).Should().Equal("E1", "E4", "E7", "E8");
        ManpowerReconService.DrillDown(build, Q("volume", "CONTINUING"))!.Items.Select(i => i.EmployeeCode).Should().Equal("E1");
        var e1 = ManpowerReconService.DrillDown(build, Q("volume", "CONTINUING"))!.Items.Single();
        e1.Prior.Should().Be(1250m);
        e1.Current.Should().Be(1350m);
        e1.Delta.Should().Be(100m);
        // A held-out employee says why, in the words the operator gave.
        ManpowerReconService.DrillDown(build, Q("volume", "NOT_PAID"))!.Items.Single().Note.Should().Be("Held out of this run: On unpaid sabbatical");
    }

    [Fact]
    public async Task DrillDown_OnAValueCell_ListsTheEmployeesWhoseMoneyIsInIt()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var build = (await k.Build()).Build!;

        var leavers = ManpowerReconService.DrillDown(build, Q("value", "LEAVER", "Earning", "BASIC"))!;
        leavers.Items.Select(i => i.EmployeeCode).Should().Equal("E2", "E3");
        leavers.Items.Select(i => i.Delta).Should().Equal(-2000m, -1500m);

        var package = ManpowerReconService.DrillDown(build, Q("value", kind: "Earning", itemCode: "BASIC", bucket: "package"))!;
        package.Items.Should().ContainSingle(i => i.EmployeeCode == "E1" && i.Delta == 100m && i.Prior == 1000m && i.Current == 1100m);

        var prior = ManpowerReconService.DrillDown(build, Q("value", kind: "Earning", itemCode: "BASIC", bucket: "prior"))!;
        prior.Items.Select(i => i.EmployeeCode).Should().Equal("E1", "E2", "E3", "E5", "E6", "E9");
        var current = ManpowerReconService.DrillDown(build, Q("value", kind: "Earning", itemCode: "BASIC", bucket: "current"))!;
        current.Items.Select(i => i.EmployeeCode).Should().Equal("E1", "E4", "E7", "E8");

        // A kind keeps a deduction apart from an earning of the same code, and a bad bucket or category is refused, not ignored.
        ManpowerReconService.DrillDown(build, Q("value", kind: "Deduction", itemCode: "BASIC", bucket: "prior"))!.Total.Should().Be(0);
        ManpowerReconService.DrillDown(build, Q("value", kind: "Earning", itemCode: "BASIC", bucket: "sideways")).Should().BeNull();
        ManpowerReconService.DrillDown(build, Q("value", "CONTINUING", "Earning", "BASIC")).Should().BeNull();
        ManpowerReconService.DrillDown(build, Q("value", bucket: "package")).Should().BeNull("a driver needs an item to be about");
    }

    [Fact]
    public async Task DrillDown_FlaggedEmployees_AreTheSameSetTheSignoffCounts()
    {
        var k = await Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1300);
        k.AddEmployee(2); k.AddSlip(k.Prior, 2, 1000); k.AddSlip(k.Current, 2, 1010);
        await k.Db.SaveChangesAsync();
        var build = (await k.Build()).Build!;
        var flagged = ManpowerReconService.DrillDown(build, Q("value", bucket: "flagged"))!;
        flagged.Items.Select(i => i.EmployeeCode).Should().Equal("E1");
        flagged.Total.Should().Be(build.FlaggedEmployeeIds.Count);
        flagged.Items.Single().Note.Should().Be("+30% on gross");
    }

    [Fact]
    public async Task DrillDown_CapsThePageSize()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var build = (await k.Build()).Build!;
        ManpowerReconService.DrillDown(build, Q("volume", "CONTINUING", pageSize: 5000))!.PageSize.Should().Be(200);
    }

    [Fact]
    public async Task DrillDown_OnAFinalSettlementCellOrComponent_ListsTheSettlements()
    {
        var k = await Kit.CreateAsync();
        await k.SeedEveryCategoryAsync();
        var build = (await k.Build()).Build!;
        var cell = ManpowerReconService.DrillDown(build, Q("finalSettlement", "LEAVER_IN_BRIDGE"))!;
        cell.Items.Should().ContainSingle(i => i.EmployeeCode == "E2" && i.Current == 5000m);
        var component = ManpowerReconService.DrillDown(build, Q("finalSettlement", itemCode: "EOSB_GRATUITY"))!;
        component.Items.Should().ContainSingle(i => i.EmployeeCode == "E2" && i.Current == 5000m && i.Prior == null);
        ManpowerReconService.DrillDown(build, Q("finalSettlement", "MADE_UP")).Should().BeNull();
        ManpowerReconService.DrillDown(build, Q("finalSettlement")).Should().BeNull();
    }
}
