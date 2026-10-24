using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Leave per company (owner decision 2026-10-23): leave types and leave policies belong to one company. The backfill runs
/// on its own PostgreSQL container (it touches every tenant); the service rules run on the shared fixture, each scenario
/// in its own tenant.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LeavePerCompanyBackfillPostgresTests
{
    private sealed record Seeded(
        Guid SoloTenant, Guid Solo, Guid SoloUnused, Guid SoloAnnual, Guid SoloAePolicy,
        Guid Tenant, Guid A, Guid B, Guid C, Guid DeptA, Guid DeptB,
        Guid Annual, Guid Sick, Guid Hajj, Guid Unused,
        Guid PolicyTw, Guid PolicyB, Guid PolicyHajj, Guid PolicyDraftSick, Guid PolicyAe,
        Guid EligibilityOpen, Guid EligibilityB, Guid Workflow,
        int A1, int A2, int B1, int C1, int N1,
        Guid PendingB1, Guid ApprovedB1, Guid PendingA1);

    private static readonly DateTime Jan2026 = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<Seeded> SeedAsync(ZayraDbContext db)
    {
        // Tenant 1: one company — an unused type and a policy that reaches nobody both go to it.
        var t1 = Guid.NewGuid();
        var solo = new Company { TenantId = t1, LegalNameEn = "Solo", RegistrationNumber = "S-1", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var soloUnused = new LeaveType { TenantId = t1, Code = "EXAM", NameEn = "Exam", Category = "Exam" };
        var soloAnnual = new LeaveType { TenantId = t1, Code = "ANNUAL", NameEn = "Annual", Category = "Annual" };
        var soloAe = new LeavePolicy { TenantId = t1, Name = "Default Annual — AE", LeaveTypeId = soloAnnual.Id, CountryCode = "AE", Status = "Active", AnnualEntitlementDays = 30 };
        db.AddRange(solo, soloUnused, soloAnnual, soloAe);

        // Tenant 2: A (oldest, stored as ISO-3), B, C (UAE) — and an employee with no company.
        var t = Guid.NewGuid();
        var a = new Company { TenantId = t, LegalNameEn = "Alpha", RegistrationNumber = "A-1", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var b = new Company { TenantId = t, LegalNameEn = "Beta", RegistrationNumber = "B-1", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        var c = new Company { TenantId = t, LegalNameEn = "Gamma", RegistrationNumber = "C-1", CountryCode = "AE", DefaultCurrency = "AED", IsActive = true, CreatedAtUtc = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc) };
        var branchB = new Branch { TenantId = t, CompanyId = b.Id, Code = "BHQ", NameEn = "Beta HQ" };
        // One workspace-wide OPS department used by A and B: the organisation backfill keeps it for A (tie → oldest) and
        // copies it for B; the rule copy then copies the workflow and the eligibility row that name it.
        var deptA = new Department { TenantId = t, Code = "OPS", NameEn = "Ops" };
        var workflow = new ApprovalWorkflow { TenantId = t, Code = "LEAVE-OPS", Name = "Ops leave approval", EntityName = nameof(LeaveRequest), DepartmentId = deptA.Id };
        var annual = new LeaveType { TenantId = t, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", SortOrder = 1 };
        var sick = new LeaveType { TenantId = t, Code = "SICK", NameEn = "Sick Leave", Category = "Sick", SortOrder = 2 };
        var hajj = new LeaveType { TenantId = t, Code = "HAJJ", NameEn = "Hajj Leave", Category = "Hajj", SortOrder = 3 };
        var unused = new LeaveType { TenantId = t, Code = "UNUSED", NameEn = "Unused", Category = "Other", SortOrder = 4 };
        db.AddRange(a, b, c, branchB, deptA, workflow, annual, sick, hajj, unused);
        await db.SaveChangesAsync();

        Employee E(Company? co, string code, Guid? dept = null) => new()
        {
            TenantId = t, CompanyId = co?.Id, EmployeeCode = code, FullName = code, Status = "Active",
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), DepartmentId = dept, Department = dept is null ? "" : "Ops",
        };
        var a1 = E(a, "A1", deptA.Id); var a2 = E(a, "A2"); var b1 = E(b, "B1", deptA.Id); var c1 = E(c, "C1"); var n1 = E(null, "N1");
        db.AddRange(a1, a2, b1, c1, n1);
        await db.SaveChangesAsync();

        // Tenant-wide Saudi annual policy (reaches A and B), with an open eligibility row on A's OPS and a B-only row.
        var policyTw = new LeavePolicy { TenantId = t, Name = "Default Annual — SA", LeaveTypeId = annual.Id, CountryCode = "SA", Status = "Active", AnnualEntitlementDays = 21, AccrualMethod = "Monthly", UpdatedAtUtc = Jan2026, ApprovalWorkflowId = workflow.Id };
        var eligOpen = new LeavePolicyEligibility { TenantId = t, LeavePolicyId = policyTw.Id, DepartmentId = deptA.Id, IsActive = true };
        var eligB = new LeavePolicyEligibility { TenantId = t, LeavePolicyId = policyTw.Id, CompanyId = b.Id, IsActive = true };
        var rule = new LeaveAccrualRule { TenantId = t, LeavePolicyId = policyTw.Id, AccrualDays = 1.75m };
        var policyB = new LeavePolicy { TenantId = t, Name = "Beta annual", LeaveTypeId = annual.Id, CompanyId = b.Id, Status = "Active", AnnualEntitlementDays = 25, UpdatedAtUtc = Jan2026 };
        var policyHajj = new LeavePolicy { TenantId = t, Name = "Hajj", LeaveTypeId = hajj.Id, Status = "Active", AnnualEntitlementDays = 10, UpdatedAtUtc = Jan2026 };
        var policyDraftSick = new LeavePolicy { TenantId = t, Name = "Sick draft (Beta HQ)", LeaveTypeId = sick.Id, BranchId = branchB.Id, Status = "Draft", AnnualEntitlementDays = 120, UpdatedAtUtc = Jan2026 };
        var policyAe = new LeavePolicy { TenantId = t, Name = "Default Annual — AE", LeaveTypeId = annual.Id, CountryCode = "AE", Status = "Active", AnnualEntitlementDays = 30, UpdatedAtUtc = Jan2026 };
        db.AddRange(policyTw, eligOpen, eligB, rule, policyB, policyHajj, policyDraftSick, policyAe);

        EmployeeLeaveBalance Bal(Employee e, LeaveType lt, int year, decimal entitled, decimal used = 0, decimal pending = 0, decimal accrued = 0, decimal cf = 0, decimal enc = 0) => new()
        {
            TenantId = t, EmployeeId = e.Id, EmployeeName = e.FullName, LeaveTypeId = lt.Id, LeaveTypeName = lt.NameEn, Year = year,
            Entitled = entitled, Used = used, Pending = pending, Accrued = accrued, CarriedForward = cf, Encashed = enc,
        };
        db.AddRange(
            Bal(a1, annual, 2026, 21, used: 5, pending: 2), Bal(a2, annual, 2026, 21, accrued: 3),
            Bal(b1, annual, 2026, 30, used: 4, pending: 3, cf: 2, enc: 1), Bal(b1, annual, 2025, 30, used: 30),
            Bal(b1, sick, 2026, 120, used: 2), Bal(n1, annual, 2026, 10));
        LeaveBalanceTransaction Txn(Employee e, LeaveType lt, string type, decimal amount, string reference) => new()
        {
            TenantId = t, CompanyId = e.CompanyId, EmployeeId = e.Id, LeaveTypeId = lt.Id, Year = 2026, TransactionType = type, Amount = amount, Reference = reference,
        };
        db.AddRange(Txn(a1, annual, "Accrual", 1.75m, "MONTHLY-ACCRUAL-2026-01"), Txn(b1, annual, "Accrual", 2.5m, "MONTHLY-ACCRUAL-2026-01"),
            Txn(b1, annual, "Debit", -4m, "REQ-1"), Txn(b1, annual, "CarryForward", 2m, "CF-2025"), Txn(b1, sick, "Debit", -2m, "REQ-4"));
        LeaveRequest Req(Employee e, LeaveType lt, string status, decimal days, Guid? policy) => new()
        {
            TenantId = t, CompanyId = e.CompanyId, EmployeeId = e.Id, EmployeeName = e.FullName, LeaveTypeId = lt.Id, LeaveTypeName = lt.NameEn,
            PolicyId = policy, Status = status, TotalDays = days, StartDate = new DateOnly(2026, 3, 1), EndDate = new DateOnly(2026, 3, 1).AddDays((int)days - 1),
        };
        var pendingB1 = Req(b1, annual, "PendingManagerApproval", 3, policyTw.Id);
        var approvedB1 = Req(b1, annual, "Approved", 4, policyTw.Id);
        var pendingA1 = Req(a1, annual, "PendingManagerApproval", 2, policyTw.Id);
        db.AddRange(pendingB1, approvedB1, pendingA1, Req(b1, sick, "Approved", 2, null));
        db.Add(new LeaveEncashmentRequest { TenantId = t, CompanyId = b.Id, EmployeeId = b1.Id, EmployeeName = "B1", LeaveTypeId = annual.Id, LeaveTypeName = "Annual Leave", Year = 2026, DaysToEncash = 1, Currency = "SAR" });
        db.Add(new AbsenceRegularizationRequest { TenantId = t, EmployeeId = b1.Id, EmployeeName = "B1", AbsenceRecordId = Guid.NewGuid(), LeaveTypeId = annual.Id });
        await db.SaveChangesAsync();

        return new Seeded(t1, solo.Id, soloUnused.Id, soloAnnual.Id, soloAe.Id,
            t, a.Id, b.Id, c.Id, deptA.Id, Guid.Empty, annual.Id, sick.Id, hajj.Id, unused.Id,
            policyTw.Id, policyB.Id, policyHajj.Id, policyDraftSick.Id, policyAe.Id, eligOpen.Id, eligB.Id, workflow.Id,
            a1.Id, a2.Id, b1.Id, c1.Id, n1.Id, pendingB1.Id, approvedB1.Id, pendingA1.Id);
    }

    private static Task RunBackfillAsync(ZayraDbContext db) => RunSqlAsync(db, Zayra.Api.Migrations.BackfillLeavePerCompany.BackfillSql);

    /// <summary>The migrations that run before the leave backfill, in production order (organisation backfill, rule copy).</summary>
    private static async Task RunOrganisationMigrationsAsync(ZayraDbContext db)
    {
        await RunSqlAsync(db, Zayra.Api.Migrations.BackfillOrganisationPerCompany.BackfillSql);
        await RunSqlAsync(db, Zayra.Api.Migrations.CopyWorkspaceRulesPerCompany.CopyRulesSql);
    }

    private static string PrecheckPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "runbooks", "org-per-company-precheck.sql"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "docs", "runbooks", "org-per-company-precheck.sql");
    }

    /// <summary>The runbook's balance-invariant query (the owner runs it before and after deploy): checksum per tenant.</summary>
    private static async Task<Dictionary<Guid, string>> PrecheckChecksumsAsync(ZayraDbContext db)
    {
        var text = await File.ReadAllTextAsync(PrecheckPath());
        var start = text.IndexOf("SELECT x.tenant_id, count(*) AS figure_rows", StringComparison.Ordinal);
        const string end = "ORDER BY x.tenant_id;";
        var sql = text[start..(text.IndexOf(end, start, StringComparison.Ordinal) + end.Length)];
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result[reader.GetGuid(0)] = $"{reader.GetInt64(1)}:{reader.GetString(2)}";
        return result;
    }

    private static async Task RunSqlAsync(ZayraDbContext db, string sql)
    {
        // A plain command, not ExecuteSqlRaw: the SQL holds braces that would be read as format placeholders.
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Every number an employee's leave is made of, keyed by employee and leave CODE (the id is what moves): balance
    /// columns per year, ledger sums per transaction type, request days per status, encashed days.
    /// </summary>
    private static async Task<SortedDictionary<string, decimal>> FiguresAsync(ZayraDbContext db, Guid tenant)
    {
        var types = await db.LeaveTypes.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToDictionaryAsync(x => x.Id, x => x.Code);
        var figures = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        void Add(string key, decimal value) => figures[key] = figures.GetValueOrDefault(key) + value;
        foreach (var x in await db.EmployeeLeaveBalances.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToListAsync())
        {
            var k = $"{x.EmployeeId}|{types[x.LeaveTypeId]}|{x.Year}";
            Add(k + "|entitled", x.Entitled); Add(k + "|accrued", x.Accrued); Add(k + "|used", x.Used); Add(k + "|pending", x.Pending);
            Add(k + "|carried", x.CarriedForward); Add(k + "|encashed", x.Encashed); Add(k + "|expired", x.Expired);
            Add(k + "|adjusted", x.ManualAdjustment); Add(k + "|available", x.Available); Add(k + "|rows", 1);
        }
        foreach (var x in await db.LeaveBalanceTransactions.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToListAsync())
            Add($"{x.EmployeeId}|{types[x.LeaveTypeId]}|ledger|{x.TransactionType}", x.Amount);
        foreach (var x in await db.LeaveRequests.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToListAsync())
            Add($"{x.EmployeeId}|{types[x.LeaveTypeId]}|request|{x.Status}", x.TotalDays);
        foreach (var x in await db.LeaveEncashmentRequests.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToListAsync())
            Add($"{x.EmployeeId}|{types[x.LeaveTypeId]}|encashment", x.DaysToEncash);
        return figures;
    }

    /// <summary>Which policy each employee resolves for each leave code: (name, entitlement) — ids change, the policy must not.</summary>
    private static async Task<SortedDictionary<string, string>> ResolutionAsync(ZayraDbContext db, Guid tenant, params int[] employeeIds)
    {
        var service = new LeaveService(db, new ApprovalRouter(db));
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in employeeIds)
        {
            var employee = await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id);
            var usable = await LeaveCompanyScope.UsableBy(db, tenant, employee.CompanyId).IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == tenant).ToListAsync();
            foreach (var type in usable)
            {
                var policy = await LeaveService.ResolveLeavePolicyAsync(db, tenant, employee, type.Id, null, default);
                result[$"{employee.EmployeeCode}|{type.Code}"] = policy is null ? "-" : $"{policy.Name}|{policy.AnnualEntitlementDays:0.##}";
            }
        }
        return result;
    }

    [Fact]
    public async Task Backfill_AssignsCopiesAndRepoints_KeepsEveryBalanceAndRequest_AndARerunChangesNothing()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using (var setup = new ZayraDbContext(options)) await setup.Database.EnsureCreatedAsync();

        Seeded s;
        SortedDictionary<string, decimal> figuresBefore;
        SortedDictionary<string, string> resolvedBefore;
        await using (var db = new ZayraDbContext(options)) s = await SeedAsync(db);
        await using (var db = new ZayraDbContext(options))
        {
            figuresBefore = await FiguresAsync(db, s.Tenant);
            resolvedBefore = await ResolutionAsync(db, s.Tenant, s.A1, s.A2, s.B1, s.C1);
        }
        // The whole read-only runbook runs on the pre-deploy shape, and its invariant checksum is taken.
        Dictionary<Guid, string> checksumsBefore;
        await using (var db = new ZayraDbContext(options))
        {
            await RunSqlAsync(db, await File.ReadAllTextAsync(PrecheckPath()));
            checksumsBefore = await PrecheckChecksumsAsync(db);
        }
        Guid deptB, workflowB, ruleCopyB;
        await using (var db = new ZayraDbContext(options)) await RunOrganisationMigrationsAsync(db);
        await using (var db = new ZayraDbContext(options))
        {
            deptB = await db.Departments.IgnoreQueryFilters().Where(x => x.TenantId == s.Tenant && x.Code == "OPS" && x.CompanyId == s.B).Select(x => x.Id).SingleAsync();
            workflowB = await db.ApprovalWorkflows.IgnoreQueryFilters().Where(x => x.TenantId == s.Tenant && x.DepartmentId == deptB).Select(x => x.Id).SingleAsync();
            // The rule copy's row for B: company B, B's department, still on the ORIGINAL policy.
            ruleCopyB = await db.LeavePolicyEligibilities.IgnoreQueryFilters()
                .Where(x => x.LeavePolicyId == s.PolicyTw && x.CompanyId == s.B && x.DepartmentId == deptB).Select(x => x.Id).SingleAsync();
        }
        await using (var db = new ZayraDbContext(options)) await RunBackfillAsync(db);

        await using (var db = new ZayraDbContext(options))
        {
            var types = db.LeaveTypes.IgnoreQueryFilters();
            var policies = db.LeavePolicies.IgnoreQueryFilters();

            // Single-company tenant: everything goes to the only company, even a policy that reaches nobody.
            (await types.SingleAsync(x => x.Id == s.SoloUnused)).CompanyId.Should().Be(s.Solo);
            (await types.SingleAsync(x => x.Id == s.SoloAnnual)).CompanyId.Should().Be(s.Solo);
            (await policies.SingleAsync(x => x.Id == s.SoloAePolicy)).CompanyId.Should().Be(s.Solo);

            // ANNUAL: A keeps it (2 employees), B (1 employee + its own policy) and C (reached by the UAE policy) get copies.
            var annual = await types.Where(x => x.TenantId == s.Tenant && x.Code == "ANNUAL").ToListAsync();
            annual.Should().HaveCount(3);
            var annualA = annual.Single(x => x.CompanyId == s.A);
            var annualB = annual.Single(x => x.CompanyId == s.B);
            var annualC = annual.Single(x => x.CompanyId == s.C);
            annualA.Id.Should().Be(s.Annual);
            (annualB.NameEn, annualB.Category, annualB.SortOrder).Should().Be(("Annual Leave", "Annual", 1), "a copy carries the same values");

            // B1's every row is on B's copy; A's and the company-less employee's are untouched.
            (await db.EmployeeLeaveBalances.IgnoreQueryFilters().Where(x => x.EmployeeId == s.B1 && x.LeaveTypeName == "Annual Leave").Select(x => x.LeaveTypeId).Distinct().ToListAsync())
                .Should().Equal(annualB.Id);
            (await db.LeaveBalanceTransactions.IgnoreQueryFilters().Where(x => x.EmployeeId == s.B1 && x.Reference != "REQ-4").Select(x => x.LeaveTypeId).Distinct().ToListAsync())
                .Should().Equal(annualB.Id);
            (await db.LeaveEncashmentRequests.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == s.B1)).LeaveTypeId.Should().Be(annualB.Id);
            (await db.AbsenceRegularizationRequests.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == s.B1)).LeaveTypeId.Should().Be(annualB.Id);
            (await db.EmployeeLeaveBalances.IgnoreQueryFilters().Where(x => x.EmployeeId == s.A1 || x.EmployeeId == s.A2 || x.EmployeeId == s.N1).Select(x => x.LeaveTypeId).Distinct().ToListAsync())
                .Should().Equal(s.Annual);

            // SICK: only B uses it → assigned. HAJJ: an active tenant-wide policy reaches all three → A keeps, B and C copy.
            (await types.SingleAsync(x => x.Id == s.Sick)).CompanyId.Should().Be(s.B);
            (await types.Where(x => x.TenantId == s.Tenant && x.Code == "HAJJ").Select(x => x.CompanyId).ToListAsync())
                .Should().BeEquivalentTo(new Guid?[] { s.A, s.B, s.C });
            (await types.SingleAsync(x => x.Id == s.Hajj)).CompanyId.Should().Be(s.A, "a tie on employees goes to the oldest company");
            // Unused in a multi-company tenant: stays unassigned.
            (await types.SingleAsync(x => x.Id == s.Unused)).CompanyId.Should().BeNull();

            // The Saudi default: A keeps it, B gets a copy on B's type; neither reaches the UAE company.
            var tw = await policies.Where(x => x.TenantId == s.Tenant && x.Name == "Default Annual — SA").ToListAsync();
            tw.Should().HaveCount(2);
            var twA = tw.Single(x => x.Id == s.PolicyTw);
            var twB = tw.Single(x => x.Id != s.PolicyTw);
            (twA.CompanyId, twA.LeaveTypeId, twA.LegacyGroupDefault).Should().Be((s.A, s.Annual, true));
            (twB.CompanyId, twB.LeaveTypeId, twB.LegacyGroupDefault).Should().Be((s.B, annualB.Id, true));
            (twB.AnnualEntitlementDays, twB.AccrualMethod, twB.UpdatedAtUtc).Should().Be((21m, "Monthly", twA.UpdatedAtUtc));
            twA.ApprovalWorkflowId.Should().Be(s.Workflow, "A keeps the original workflow");
            twB.ApprovalWorkflowId.Should().Be(workflowB, "B's copy names the workflow copy the rule copy made for B");
            (await db.LeaveAccrualRules.IgnoreQueryFilters().CountAsync(x => x.LeavePolicyId == twB.Id)).Should().Be(1, "child config is copied");
            // Eligibility: the B-only row and the rule copy's B row moved to B's copy; the open row stays on A's policy
            // and is not copied a second time for B (the rule copy already made B's version of it).
            (await db.LeavePolicyEligibilities.IgnoreQueryFilters().SingleAsync(x => x.Id == s.EligibilityB)).LeavePolicyId.Should().Be(twB.Id);
            var ruleCopy = await db.LeavePolicyEligibilities.IgnoreQueryFilters().SingleAsync(x => x.Id == ruleCopyB);
            (ruleCopy.LeavePolicyId, ruleCopy.CompanyId, ruleCopy.DepartmentId).Should().Be((twB.Id, (Guid?)s.B, (Guid?)deptB));
            (await db.LeavePolicyEligibilities.IgnoreQueryFilters().SingleAsync(x => x.Id == s.EligibilityOpen)).LeavePolicyId.Should().Be(s.PolicyTw);
            (await db.LeavePolicyEligibilities.IgnoreQueryFilters().SingleAsync(x => x.Id == s.EligibilityOpen)).DepartmentId.Should().Be(s.DeptA);
            (await db.LeavePolicyEligibilities.IgnoreQueryFilters().CountAsync(x => x.LeavePolicyId == twB.Id)).Should().Be(2);

            var policyB = await policies.SingleAsync(x => x.Id == s.PolicyB);
            (policyB.CompanyId, policyB.LeaveTypeId, policyB.LegacyGroupDefault).Should().Be((s.B, annualB.Id, false), "a company's own policy follows its company's type");
            (await policies.SingleAsync(x => x.Id == s.PolicyAe)).CompanyId.Should().Be(s.C);
            (await policies.SingleAsync(x => x.Id == s.PolicyAe)).LeaveTypeId.Should().Be(annualC.Id);
            (await policies.SingleAsync(x => x.Id == s.PolicyDraftSick)).CompanyId.Should().Be(s.B, "a branch-restricted policy goes to the branch's company");
            (await policies.Where(x => x.TenantId == s.Tenant && x.Name == "Hajj").Select(x => x.CompanyId).ToListAsync())
                .Should().BeEquivalentTo(new Guid?[] { s.A, s.B, s.C });

            // In-flight requests: B1's pending and approved requests name B's copy of the policy, A1's keeps A's.
            (await db.LeaveRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == s.PendingB1)).PolicyId.Should().Be(twB.Id);
            (await db.LeaveRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == s.ApprovedB1)).PolicyId.Should().Be(twB.Id);
            (await db.LeaveRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == s.PendingA1)).PolicyId.Should().Be(s.PolicyTw);

            // HARD INVARIANT 1: every balance, ledger, request and encashment figure per employee per leave code is identical,
            // and so is the runbook's own checksum.
            (await FiguresAsync(db, s.Tenant)).Should().Equal(figuresBefore);
            (await PrecheckChecksumsAsync(db)).Should().Equal(checksumsBefore);

            // HARD INVARIANT 2: every request still resolves its policy, inside its employee's company, on its own type;
            // and every leave row of an employee with a company names that company's type.
            var employees = await db.Employees.IgnoreQueryFilters().Where(x => x.TenantId == s.Tenant).ToDictionaryAsync(x => x.Id, x => x.CompanyId);
            var typeCompany = await types.Where(x => x.TenantId == s.Tenant).ToDictionaryAsync(x => x.Id, x => x.CompanyId);
            foreach (var r in await db.LeaveRequests.IgnoreQueryFilters().Where(x => x.TenantId == s.Tenant).ToListAsync())
            {
                if (employees[r.EmployeeId] is Guid company) typeCompany[r.LeaveTypeId].Should().Be(company);
                if (r.PolicyId is not Guid pid) continue;
                var p = await policies.SingleOrDefaultAsync(x => x.Id == pid);
                p.Should().NotBeNull();
                p!.LeaveTypeId.Should().Be(r.LeaveTypeId);
                p.CompanyId.Should().Be(employees[r.EmployeeId]);
            }
            foreach (var bal in await db.EmployeeLeaveBalances.IgnoreQueryFilters().Where(x => x.TenantId == s.Tenant).ToListAsync())
                if (employees[bal.EmployeeId] is Guid company) typeCompany[bal.LeaveTypeId].Should().Be(company);

            // Precedence is kept: each employee resolves the same policy (by name and days) as before the backfill.
            // A type assigned to a sister company (SICK → Beta) is no longer offered to Gamma's employee; it resolved no
            // policy for them before either.
            var resolvedAfter = await ResolutionAsync(db, s.Tenant, s.A1, s.A2, s.B1, s.C1);
            resolvedAfter.Should().NotBeEmpty();
            foreach (var (key, value) in resolvedAfter) value.Should().Be(resolvedBefore[key], key);
            foreach (var key in resolvedBefore.Keys.Except(resolvedAfter.Keys)) resolvedBefore[key].Should().Be("-", key);
            resolvedAfter["B1|ANNUAL"].Should().Be("Default Annual — SA|21", "the company-specific policy still loses to the default it lost to before");

            // One chained audit row per assigned / copied type and policy, and the tenant's chain still verifies.
            var audits = await db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == s.Tenant).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
            audits.Should().Contain(x => x.Action.StartsWith("leave.company_backfill_"));
            audits.Count(x => x.EntityName == "LeaveType" && x.Action == "leave.company_backfill_copied").Should().Be(4, "ANNUAL for B and C, HAJJ for B and C");
            audits.Count(x => x.EntityName == "LeavePolicy" && x.Action == "leave.company_backfill_copied").Should().Be(3, "the Saudi default for B, Hajj for B and C");
            AuditService.VerifyChain(audits).IsValid.Should().BeTrue();
        }

        // Idempotent: a second run changes nothing.
        string Snapshot(ZayraDbContext db) => JsonSerializer.Serialize(new
        {
            t = db.LeaveTypes.IgnoreQueryFilters().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId }).ToList(),
            p = db.LeavePolicies.IgnoreQueryFilters().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.LeaveTypeId, x.LegacyGroupDefault }).ToList(),
            e = db.LeavePolicyEligibilities.IgnoreQueryFilters().OrderBy(x => x.Id).Select(x => new { x.Id, x.LeavePolicyId, x.DepartmentId }).ToList(),
            b = db.EmployeeLeaveBalances.IgnoreQueryFilters().OrderBy(x => x.Id).Select(x => new { x.Id, x.LeaveTypeId }).ToList(),
            r = db.LeaveRequests.IgnoreQueryFilters().OrderBy(x => x.Id).Select(x => new { x.Id, x.LeaveTypeId, x.PolicyId }).ToList(),
            ar = db.LeaveAccrualRules.IgnoreQueryFilters().Count(),
            a = db.AuditLogs.IgnoreQueryFilters().Count(),
        });
        string before;
        await using (var db = new ZayraDbContext(options)) before = Snapshot(db);
        await using (var db = new ZayraDbContext(options)) await RunBackfillAsync(db);
        await using (var db = new ZayraDbContext(options)) Snapshot(db).Should().Be(before);
    }

    [Fact]
    public async Task SchemaMigration_ArchivesAndUnassigns_APolicyNamingNoCompanyOfItsTenant()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        await db.Database.EnsureCreatedAsync();
        // The FK exists in the model; drop it to recreate the pre-migration shape where an orphan could exist.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE leave_policies DROP CONSTRAINT \"FK_leave_policies_companies_tenant_id_company_id\"");
        var tenant = Guid.NewGuid();
        var type = new LeaveType { TenantId = tenant, Code = "ANNUAL", NameEn = "Annual" };
        var orphan = new LeavePolicy { TenantId = tenant, Name = "Orphan", LeaveTypeId = type.Id, CompanyId = Guid.NewGuid(), Status = "Active" };
        db.AddRange(type, orphan);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.PerCompanyLeaveColumns.OrphanPolicySql);
        await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.PerCompanyLeaveColumns.OrphanPolicySql);
        db.ChangeTracker.Clear();
        var reread = await db.LeavePolicies.IgnoreQueryFilters().SingleAsync(x => x.Id == orphan.Id);
        (reread.CompanyId, reread.Status).Should().Be(((Guid?)null, "Archived"));
        (await db.LeaveAuditLogs.IgnoreQueryFilters().CountAsync(x => x.EntityId == orphan.Id.ToString() && x.Action == "CompanyOrphanArchived")).Should().Be(1);
    }
}

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LeavePerCompanyPostgresTests(PostgresFixture fixture)
{
    private async Task<(Guid Tenant, Company A, Company B)> TwoCompaniesAsync()
    {
        await using var db = fixture.CreateDb();
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = $"A-{tenant:N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = $"B-{tenant:N}", CountryCode = "AE", DefaultCurrency = "AED", IsActive = true };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        return (tenant, a, b);
    }

    private static ClaimsPrincipal Admin(Guid tenant, Guid? company = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "HR"), new(ClaimTypes.Role, "HR Manager"),
        };
        if (company is null) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        if (company is Guid c) claims.Add(new Claim("entity_access", JsonSerializer.Serialize(new { c, r = "HR Manager" })));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static T WithUser<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static CreateLeaveTypeRequest TypeReq(string code, Guid? company) =>
        new(code, code + " leave", null, "Annual", true, false, false, false, false, 0, null, 1, company);

    private static CreateLeavePolicyRequest PolicyReq(Guid typeId, Guid? company, string name = "Annual policy", Guid? branch = null) =>
        new(name, typeId, null, company, branch, null, null, null, null, null, true, 21, "Yearly", 0, 0, false, 0, 1, 0, 0, false, false, "Full", null, "Active");

    private static string? ErrorCode(IActionResult result) =>
        result is BadRequestObjectResult bad ? JsonSerializer.SerializeToElement(bad.Value).TryGetProperty("error", out var e) ? e.GetString() : null : null;

    [Fact]
    public async Task SameLeaveTypeCode_IsAllowedInTwoCompanies_ButNotTwiceInOne_AndCreateNeedsACompany()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();
        var controller = WithUser(new LeaveTypesController(db, new LeaveService(db, new ApprovalRouter(db))), Admin(tenant));

        (await controller.Create(TypeReq("ANNUAL", a.Id), default)).Should().BeOfType<CreatedResult>();
        (await controller.Create(TypeReq("ANNUAL", b.Id), default)).Should().BeOfType<CreatedResult>();
        ErrorCode(await controller.Create(TypeReq("annual", a.Id), default)).Should().Be("leave_type_code_taken");
        ErrorCode(await controller.Create(TypeReq("SICK", null), default)).Should().Be("company_required", "a group with several companies must say which");

        // The database refuses it too (NULLS NOT DISTINCT unique on tenant, company, code).
        db.ChangeTracker.Clear();
        db.LeaveTypes.Add(new LeaveType { TenantId = tenant, CompanyId = a.Id, Code = "ANNUAL", NameEn = "Raw duplicate" });
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Policy_TakesItsCompany_RefusesASistersType_AndItsCompanyIsLocked()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();
        var typeA = new LeaveType { TenantId = tenant, CompanyId = a.Id, Code = "ANNUAL", NameEn = "Annual" };
        var legacy = new LeaveType { TenantId = tenant, Code = "EXAM", NameEn = "Exam" };
        db.AddRange(typeA, legacy);
        await db.SaveChangesAsync();
        var controller = WithUser(new LeavePoliciesController(db, new LeaveService(db, new ApprovalRouter(db))), Admin(tenant));

        ErrorCode(await controller.Create(PolicyReq(typeA.Id, b.Id), default)).Should().Be("company_mismatch");
        ErrorCode(await controller.Create(PolicyReq(typeA.Id, null), default)).Should().Be("company_required");
        var created = (LeavePolicy)((CreatedResult)await controller.Create(PolicyReq(typeA.Id, a.Id), default)).Value!;
        created.CompanyId.Should().Be(a.Id);
        created.LegacyGroupDefault.Should().BeFalse();
        // An unassigned legacy type is still a fallback for a company with no type of that code.
        (await controller.Create(PolicyReq(legacy.Id, b.Id, "Exam B"), default)).Should().BeOfType<CreatedResult>();

        var move = JsonSerializer.Deserialize<UpdateLeavePolicyRequest>(JsonSerializer.Serialize(new { CompanyId = b.Id }))!;
        ErrorCode(await controller.Update(created.Id, move, default)).Should().Be("company_locked");
    }

    [Fact]
    public async Task CompanyScopedUser_CannotReadASisterCompanysLeaveTypesOrPolicies()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        LeaveType typeA, typeB;
        LeavePolicy policyB;
        await using (var db = fixture.CreateDb())
        {
            typeA = new LeaveType { TenantId = tenant, CompanyId = a.Id, Code = "ANNUAL", NameEn = "Annual A" };
            typeB = new LeaveType { TenantId = tenant, CompanyId = b.Id, Code = "ANNUAL", NameEn = "Annual B" };
            policyB = new LeavePolicy { TenantId = tenant, CompanyId = b.Id, LeaveTypeId = typeB.Id, Name = "Beta annual", Status = "Active" };
            db.AddRange(typeA, typeB, policyB, new LeavePolicy { TenantId = tenant, CompanyId = a.Id, LeaveTypeId = typeA.Id, Name = "Alpha annual", Status = "Active" });
            await db.SaveChangesAsync();
        }

        var user = Admin(tenant, a.Id);
        await using var scoped = fixture.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
        var types = WithUser(new LeaveTypesController(scoped, new LeaveService(scoped, new ApprovalRouter(scoped))), user);
        var listed = (List<LeaveType>)((OkObjectResult)await types.List(null, null, null, null, default)).Value!;
        listed.Select(x => x.Id).Should().Equal(typeA.Id);

        var policies = WithUser(new LeavePoliciesController(scoped, new LeaveService(scoped, new ApprovalRouter(scoped))), user);
        (await policies.Get(policyB.Id, default)).Should().BeOfType<NotFoundResult>();
        var page = (Zayra.Api.Application.Common.PagedResult<LeavePolicy>)((OkObjectResult)await policies.List(null, null)).Value!;
        page.Items.Should().OnlyContain(p => p.CompanyId == a.Id);
    }

    [Fact]
    public async Task RequestValidationAndAccrual_UseTheEmployeesCompanyPolicy()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();
        var typeA = new LeaveType { TenantId = tenant, CompanyId = a.Id, Code = "ANNUAL", NameEn = "Annual", Category = "Annual" };
        var typeB = new LeaveType { TenantId = tenant, CompanyId = b.Id, Code = "ANNUAL", NameEn = "Annual", Category = "Annual" };
        var policyA = new LeavePolicy { TenantId = tenant, CompanyId = a.Id, LeaveTypeId = typeA.Id, Name = "Alpha 24", AnnualEntitlementDays = 24, AccrualMethod = "Monthly", AppliesOnProbation = true, Status = "Active" };
        var policyB = new LeavePolicy { TenantId = tenant, CompanyId = b.Id, LeaveTypeId = typeB.Id, Name = "Beta 36", AnnualEntitlementDays = 36, AccrualMethod = "Monthly", AppliesOnProbation = true, Status = "Active" };
        var employee = new Employee
        {
            TenantId = tenant, CompanyId = b.Id, EmployeeCode = $"B-{Guid.NewGuid():N}"[..12], FullName = "Beta worker", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), UserAccountId = Guid.NewGuid(),
        };
        db.AddRange(typeA, typeB, policyA, policyB, employee);
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenant);
        await TestApprovalConfig.EnsureDeciderAsync(db, tenant);

        var service = new LeaveService(db, new ApprovalRouter(db));
        // A sister company's type is refused outright.
        var wrong = () => service.SubmitRequestAsync(tenant, new LeaveRequest
        {
            TenantId = tenant, EmployeeId = employee.Id, LeaveTypeId = typeA.Id, StartDate = new DateOnly(2026, 11, 3), EndDate = new DateOnly(2026, 11, 3), DayType = "Full",
        }, employee.UserAccountId);
        (await wrong.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*belongs to another company*");

        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = tenant, EmployeeId = employee.Id, LeaveTypeId = typeB.Id, Year = 2026, Entitled = 36 });
        await db.SaveChangesAsync();
        var submitted = await new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(tenant, new LeaveRequest
        {
            TenantId = tenant, EmployeeId = employee.Id, LeaveTypeId = typeB.Id, StartDate = new DateOnly(2026, 11, 3), EndDate = new DateOnly(2026, 11, 3), DayType = "Full",
        }, employee.UserAccountId);
        submitted.PolicyId.Should().Be(policyB.Id, "the employee's company's own policy");

        await new LeaveService(db, new ApprovalRouter(db)).AccrueMonthlyAsync(tenant, new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc));
        var ledger = await db.LeaveBalanceTransactions.IgnoreQueryFilters().Where(x => x.TenantId == tenant && x.TransactionType == "Accrual").ToListAsync();
        ledger.Should().ContainSingle();
        ledger[0].LeaveTypeId.Should().Be(typeB.Id);
        ledger[0].Amount.Should().Be(3m, "Beta's 36 days a year, never Alpha's 24");
    }

    [Fact]
    public async Task Installer_InstallsPerCompany_IsIdempotent_AndThePlanMatchesTheCommit()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();

        var plan = await TenantProvisioningBundle.MissingDefaultLeaveForCountryAsync(db, tenant, a.Id, "SA", default);
        plan.Should().BeEquivalentTo(TenantProvisioningBundle.DefaultLeavePolicyTypes.Select(x => x.TypeCode));
        var (typesA, policiesA) = await TenantProvisioningBundle.InstallDefaultLeaveForCountryAsync(db, tenant, a.Id, "SA", default);
        await db.SaveChangesAsync();
        policiesA.Should().Be(plan.Count);
        typesA.Should().BeGreaterThan(0);

        var (typesB, policiesB) = await TenantProvisioningBundle.InstallDefaultLeaveForCountryAsync(db, tenant, b.Id, "AE", default);
        await db.SaveChangesAsync();
        (typesB, policiesB).Should().Be((typesA, policiesA), "the sister company gets its own set, not a share of A's");

        (await TenantProvisioningBundle.MissingDefaultLeaveForCountryAsync(db, tenant, a.Id, "SA", default)).Should().BeEmpty();
        (await TenantProvisioningBundle.InstallDefaultLeaveForCountryAsync(db, tenant, a.Id, "SA", default)).Should().Be((0, 0));

        // An archived default stays archived: it counts as present.
        var archived = await db.LeavePolicies.IgnoreQueryFilters().FirstAsync(x => x.TenantId == tenant && x.CompanyId == b.Id);
        archived.Status = "Archived";
        await db.SaveChangesAsync();
        (await TenantProvisioningBundle.InstallDefaultLeaveForCountryAsync(db, tenant, b.Id, "AE", default)).Should().Be((0, 0));

        var types = await db.LeaveTypes.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToListAsync();
        types.Should().OnlyContain(x => x.CompanyId == a.Id || x.CompanyId == b.Id);
        types.GroupBy(x => (x.CompanyId, x.Code)).Should().OnlyContain(g => g.Count() == 1);
        var policies = await db.LeavePolicies.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToListAsync();
        policies.Should().OnlyContain(p => types.Single(t => t.Id == p.LeaveTypeId).CompanyId == p.CompanyId,
            "every policy points at its own company's leave type");
        policies.Where(p => p.CompanyId == b.Id).Should().OnlyContain(p => p.CountryCode == "AE");
    }
}
