using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Wave 0 — query-filter bypass RATCHET.
///
/// <para>A bare <c>.IgnoreQueryFilters()</c> drops the tenant filter AND the company filter at once.
/// The repository carries 210 such call sites inherited from before this guard existed. Removing them
/// all in one change would touch every module at once and risk the very isolation it protects, so this
/// test freezes the surface instead: the count per file is pinned below, and the build fails if a file
/// gains a call site or a new file introduces one.</para>
///
/// <para>THE RULE FOR NEW CODE: do not add a raw bypass. Use
/// <c>Zayra.Api.Infrastructure.Data.ScopedBypass</c>, which forces the author to name the actor and
/// re-applies whatever restriction must survive — <c>TenantWide</c> (drops company scope, pins tenant),
/// <c>ForCompanies</c> (user-triggered: pins tenant AND the caller's authorised entities), or
/// <c>SystemWide</c> (worker-only, no principal, bounded batch).</para>
///
/// <para>The counts may only ever go DOWN. Lowering one is a refactor toward ScopedBypass and should be
/// accompanied by lowering the number here. Raising one requires a documented entry in
/// docs/QUERY_FILTER_BYPASS_REGISTER.md stating actor, tenant restriction, company restriction,
/// required permission, and the tests proving authorised and unauthorised behaviour.</para>
/// </summary>
public class QueryFilterBypassRatchetTests
{
    // file (relative to Zayra.Api) -> approved number of raw .IgnoreQueryFilters() call sites.
    private static readonly Dictionary<string, int> ApprovedBypassCounts = new()
    {
        ["Application/Employees/EmployeeOrgFieldResolver.cs"] = 3,
        ["Controllers/EmployeesController.cs"] = 8,
        ["Controllers/EstablishmentController.cs"] = 12,
        ["Controllers/Finance/AdvancesController.cs"] = 2,
        ["Controllers/Finance/BonusesController.cs"] = 3,
        ["Controllers/Finance/GlJournalExportsController.cs"] = 6,
        ["Controllers/Finance/LoansController.cs"] = 2,
        ["Controllers/FinanceGlController.cs"] = 20,
        ["Controllers/GosiController.cs"] = 2,
        ["Controllers/OrganizationStructureImportController.cs"] = 6,
        ["Controllers/PayrollController.cs"] = 8,
        ["Controllers/PlatformController.cs"] = 3,
        ["Controllers/RatesController.cs"] = 11,
        ["Controllers/StatutoryRulesController.cs"] = 1,
        ["Data/ZayraDbContext.cs"] = 3,
        // 2 -> 4 (Wave 1 B1). ResolveEmployee gained two: the device-ingest webhook is
        // [AllowAnonymous], and Employee is the only entity on that path that is
        // ICompanyScopedOperational as well as tenant-owned. The company clause of the read filter
        // is independent of the system-scope bypass, so in Production (strict mode) an anonymous
        // request resolved to an EMPTY company scope and this lookup matched zero rows — every
        // device punch was recorded as unmatched and no daily record was ever created. Both new
        // sites re-apply `TenantId == device.TenantId`, a value read from the database after the
        // device key is authenticated, so the scope is the same one the filter would have applied.
        ["Infrastructure/Attendance/AttendanceService.cs"] = 4,
        ["Infrastructure/Auth/AuthService.cs"] = 2,
        ["Infrastructure/Boot/CompanyScopeBackfill.cs"] = 1,
        ["Infrastructure/Boot/PayrollAuditChainBackfill.cs"] = 3,
        ["Infrastructure/Compliance/GosiReadinessReportService.cs"] = 1,
        ["Infrastructure/Compliance/SaudiComplianceDashboardService.cs"] = 1,
        ["Infrastructure/CountryPack/StatutoryRuleReader.cs"] = 1,
        ["Infrastructure/Email/SmtpEmailService.cs"] = 1,
        ["Infrastructure/Employees/EmployeeDuplicateDetector.cs"] = 2,
        ["Infrastructure/Employees/EmployeeImportEstablishmentEvaluator.cs"] = 5,
        ["Infrastructure/Employees/EmployeeManagementService.cs"] = 1,
        ["Infrastructure/Employees/EmployeeReadinessPolicyResolver.cs"] = 2,
        ["Infrastructure/Finance/ErpPostingEvidence.cs"] = 4,
        ["Infrastructure/Finance/JournalExportBuilder.cs"] = 1,
        ["Infrastructure/Finance/JournalExportService.cs"] = 2,
        ["Infrastructure/Finance/PeriodHandoffReconciler.cs"] = 6,
        ["Infrastructure/Governance/CompanyTaxPolicyResolver.cs"] = 1,
        ["Infrastructure/Leave/LeaveService.cs"] = 2,
        ["Infrastructure/Notifications/NotificationDeliveryWorker.cs"] = 2,
        ["Infrastructure/Notifications/NotificationProviderConfig.cs"] = 1,
        ["Infrastructure/Notifications/NotificationRecipientResolver.cs"] = 6,
        ["Infrastructure/Notifications/NotificationService.cs"] = 3,
        ["Infrastructure/Organization/EstablishmentGuardService.cs"] = 8,
        ["Infrastructure/Organization/OrganizationSetupService.cs"] = 1,
        ["Infrastructure/Payroll/BonusGlLedger.cs"] = 6,
        ["Infrastructure/Payroll/CompanyRateResolvers.cs"] = 2,
        ["Infrastructure/Payroll/EosbProvisionLedger.cs"] = 2,
        ["Infrastructure/Payroll/FinalSettlementGlLedger.cs"] = 3,
        ["Infrastructure/Payroll/GlAccountResolver.cs"] = 4,
        ["Infrastructure/Payroll/GlControlAccounts.cs"] = 1,
        ["Infrastructure/Payroll/PayrollVoidService.cs"] = 10,
        ["Infrastructure/Payroll/PeriodCloseGuard.cs"] = 1,
        ["Infrastructure/Qiwa/QiwaSyncWorker.cs"] = 7,
        ["Infrastructure/Seed/EstablishmentSeeder.cs"] = 1,
        ["Infrastructure/Seed/GlDriverSeeder.cs"] = 5,
        ["Infrastructure/Seed/GosiRuleSeeder.cs"] = 2,
        ["Infrastructure/Seed/PayComponentSeeder.cs"] = 1,
        ["Infrastructure/Seed/StatutoryRuleSeeder.cs"] = 1,
        ["Infrastructure/Seed/TenantProvisioningBundle.cs"] = 11,
        ["Infrastructure/WorkWeek/WorkWeekService.cs"] = 3,
    };

    private static string? ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static Dictionary<string, int> ScanActual(string apiRoot)
    {
        var actual = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            // ScopedBypass is the approved wrapper — its own internals are the sanctioned bypass.
            if (Path.GetFileName(file) == "ScopedBypass.cs") continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var count = File.ReadAllLines(file)
                .Count(l => l.Contains(".IgnoreQueryFilters()", StringComparison.Ordinal)
                         && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            if (count > 0)
                actual[Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/')] = count;
        }
        return actual;
    }

    [Fact]
    public void NoNewRawQueryFilterBypassMayBeIntroduced()
    {
        var apiRoot = ResolveApiRoot();
        // D14: a guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default — a CI layout change would silently disable every isolation lint while still
        // reporting green. Throwing is the only honest behaviour for a control, and it also
        // narrows the nullable so the scan below cannot be handed a null root.
        apiRoot = apiRoot ?? throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");

        var actual = ScanActual(apiRoot);
        var violations = new List<string>();

        foreach (var (file, count) in actual.OrderBy(x => x.Key))
        {
            if (!ApprovedBypassCounts.TryGetValue(file, out var approved))
            {
                violations.Add(
                    $"  NEW FILE {file} introduces {count} raw .IgnoreQueryFilters() call(s). " +
                    "Use ScopedBypass.TenantWide / ForCompanies / SystemWide instead.");
            }
            else if (count > approved)
            {
                violations.Add(
                    $"  {file} grew from {approved} to {count} raw .IgnoreQueryFilters() call(s). " +
                    "New bypasses must go through ScopedBypass and be registered.");
            }
        }

        violations.Should().BeEmpty(
            "a raw .IgnoreQueryFilters() drops the tenant AND company filter together. New code must use " +
            "Zayra.Api.Infrastructure.Data.ScopedBypass, which names the actor and re-applies the " +
            "restriction that must survive. See docs/QUERY_FILTER_BYPASS_REGISTER.md.\n\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void RatchetBaselineMustNotDriftUpwardsSilently()
    {
        var apiRoot = ResolveApiRoot();
        // D14: a guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default — a CI layout change would silently disable every isolation lint while still
        // reporting green. Throwing is the only honest behaviour for a control, and it also
        // narrows the nullable so the scan below cannot be handed a null root.
        apiRoot = apiRoot ?? throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");

        var actual = ScanActual(apiRoot);
        var total = actual.Values.Sum();
        var approvedTotal = ApprovedBypassCounts.Values.Sum();

        total.Should().BeLessThanOrEqualTo(approvedTotal,
            $"the raw-bypass surface may only shrink. Approved total {approvedTotal}, found {total}. " +
            "Each removal should lower the pinned count in ApprovedBypassCounts.");
    }
}
