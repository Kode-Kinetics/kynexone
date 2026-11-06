using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Cost centre on transfer, Wave A D3 and the door-parity item of the threat model (section 5).
/// <para><b>CachedCostCenterStringReadRatchet.</b> <c>Employee.CostCenter</c> is a cached copy of the resolved code, kept for legacy readers. It
/// went stale through several doors, and the permission gate once compared patches with it (D1). Nothing may READ it to make a decision or to
/// show it: user-facing values come from the resolver. Writes are fine (every writer stores the resolved code). Per-file counts may only go down.</para>
/// <para><b>TransferDoorParityRatchet.</b> Every door that moves an employee's department or position, or sets the override flag, goes through the
/// shared rule in <c>EmployeeOrgFieldResolver</c> (keep or drop, re-validated, audited), or is named here with the reason it is not a transfer.</para>
/// </summary>
public sealed class EmployeeCostCentreCacheReadRatchetTests
{
    // Every shape that reads the cached Employee.CostCenter string (security review M2):
    //  1. a member read through any receiver: x.CostCenter, x?.CostCenter, x!.CostCenter, (expr).CostCenter, list[0].CostCenter,
    //     slip.Employee.CostCenter (a navigation), but not a type such as OrgRowKind.CostCenter or Zayra.Api.Models.CostCenter, and not an assignment;
    //  2. a property pattern: is { CostCenter: ... };
    //  3. EF.Property<string>(e, "CostCenter") and nameof(Employee.CostCenter);
    //  4. raw SQL on the column: cost_center (cost_center_id and cost_centers do not match).
    private static readonly Regex[] CachedReadPatterns =
    [
        new(@"(?:\b[a-z_]\w*|\.(?!Models\b|OrgRowKind\b)[A-Z]\w*|[)\]])!?\??\.CostCenter\b(?![\w(])(?!\s*=(?!=))", RegexOptions.Compiled),
        new(@"[{,]\s*CostCenter\s*:(?!:)", RegexOptions.Compiled),
        new(@"EF\.Property<[^>]*>\([^,()]+,\s*""CostCenter""\s*\)|nameof\(\s*(?:\w+\.)*Employee\.CostCenter\s*\)", RegexOptions.Compiled),
        new(@"\bcost_center\b", RegexOptions.Compiled),
        new(@"\.Property\(\s*(?:""CostCenter""|nameof\([^()]*\bEmployee\.CostCenter\))\s*\)\s*\.(?:CurrentValue|OriginalValue)\b", RegexOptions.Compiled),
    ];

    private static int CachedReads(string code) => CachedReadPatterns.Sum(p => p.Matches(code).Count);

    [Theory]
    [InlineData("var c = e.CostCenter;", true)]
    [InlineData("var c = employee?.CostCenter;", true)]
    [InlineData("var c = employee!.CostCenter;", true)]
    [InlineData("var c = (employee as Employee).CostCenter;", true)]
    [InlineData("var c = list[0].CostCenter;", true)]
    [InlineData("var c = slip.Employee.CostCenter;", true)]
    [InlineData("if (e.CostCenter == \"X\") {}", true)]
    [InlineData("if (e is { CostCenter: \"X\" }) {}", true)]
    [InlineData("q.Where(e => EF.Property<string>(e, \"CostCenter\") == code)", true)]
    [InlineData("var n = nameof(Employee.CostCenter);", true)]
    [InlineData("SELECT cost_center FROM employees", true)]
    [InlineData("var v = db.Entry(employee).Property(\"CostCenter\").CurrentValue;", true)]
    [InlineData("var v = db.Entry(employee).Property(\"CostCenterId\").CurrentValue;", false)]
    [InlineData("e.CostCenter = code;", false)]
    [InlineData("employee!.CostCenter = code;", false)]
    [InlineData("e.CostCenterId = id;", false)]
    [InlineData("var k = OrgRowKind.CostCenter;", false)]
    [InlineData("var t = typeof(Zayra.Api.Models.CostCenter);", false)]
    [InlineData("SELECT cost_center_id FROM cost_centers", false)]
    [InlineData("new Employee { CostCenter = code }", false)]
    public void TheReadPatterns_CatchEveryShape_AndOnlyReads(string line, bool isRead) => (CachedReads(line) > 0).Should().Be(isRead, line);

    /// <summary>Remaining reads, each with why it is not a user-facing or decision read. The counts may only go down.</summary>
    private static readonly Dictionary<string, (int Max, string Why)> AllowedReads = new(StringComparer.Ordinal)
    {
        ["Application/Employees/EmployeeManagementDtos.cs"] = (2,
            "EmployeeDetailDto.Project maps the cached copy for legacy readers (GET detail overwrites it with the resolved code; every writer stores the resolved code), "
            + "and the draft DTO maps EmployeeDraft.CostCenter, a different entity's field."),
        ["Infrastructure/Organization/OrgRowRemoval.cs"] = (1, "\"cost_center\" is the audit label of OrgRowKind.CostCenter (a kind name), not SQL on employees.cost_center."),
        ["Controllers/EmployeesController.cs"] = (3,"The onboarding draft maps EmployeeDraft.CostCenter / EmployeeDraftRequest.CostCenter: a different entity, not the employee's cache."),
    };

    [Fact]
    public void CachedCostCenterStringReadRatchet_NoNewReadsOfTheCachedCode()
    {
        var root = SourceScan.ResolveApiRoot();
        var counts = SourceScan.Files(root, "*.cs", "*.sql")
            .Where(f => !f.Relative.StartsWith("Migrations/", StringComparison.Ordinal))
            .Select(f => (f.Relative, Count: CachedReads(f.Code)))
            .Where(f => f.Count > 0)
            .ToDictionary(f => f.Relative, f => f.Count);

        foreach (var (file, count) in counts)
            count.Should().BeLessThanOrEqualTo(AllowedReads.TryGetValue(file, out var allowed) ? allowed.Max : 0,
                $"{file} reads the cached Employee.CostCenter string; derive the code from CostCentreResolver (EmployeeCostCentreView) instead");
        counts.Should().NotContainKey("Controllers/OrgWriteRules.cs", "the permission gate is judged on the override flag and ids (D1)");
        AllowedReads.Keys.Where(k => !counts.ContainsKey(k)).Should().BeEmpty("an allow-list entry for a file that no longer reads the cache must be removed");
    }

    // Every shape that writes a transfer-relevant column (security review M2): a member assignment through any receiver (x., x!., x?., (expr).,
    // list[0].), CostCenterId included (it re-points an override without touching the flag); a bulk SetProperty / EF.Property; an Employee
    // object initialiser; a tuple assignment; a change-tracker CurrentValue/OriginalValue (incl. the cached CostCenter); and raw SQL that UPDATEs
    // employees' attribution columns, with or without a schema prefix, quotes or an alias.
    private const string Column = "(?:CostCenterId|CostCenterIsOverride|DepartmentId|PositionId)";
    private const string Tracked = "(?:CostCenterId|CostCenterIsOverride|DepartmentId|PositionId|CostCenter)";
    private static readonly Regex[] AttributionWritePatterns =
    [
        new(@"[\w\]\)]+[!?]?\." + Column + @"\s*=(?![=>])", RegexOptions.Compiled),
        new(@"SetProperty\(\s*\w+\s*=>\s*(?:\w+\." + Column + @"\b|EF\.Property<[^>]*>\([^,()]+,\s*""" + Column + @"""\s*\))", RegexOptions.Compiled),
        new(@"new\s+(?:Models\.)?Employee\s*(?:\(\s*\))?\s*\{[^{}]*\b" + Column + @"\s*=(?![=>])", RegexOptions.Compiled),
        new(@"UPDATE\s+(?:""?\w+""?\.)?""?employees""?(?!\w)(?:\s+(?:AS\s+)?(?!SET\b)\w+)?\s+SET[^;]*\b(?:department_id|position_id|cost_center_id|cost_center_is_override)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\((?=[^()]*,)[^()]*\." + Column + @"\b[^()]*\)\s*=(?![=>])", RegexOptions.Compiled),
        new(@"\.Property\(\s*(?:""" + Tracked + @"""|nameof\([^()]*\b" + Tracked + @"\))\s*\)\s*\.(?:CurrentValue|OriginalValue)\b", RegexOptions.Compiled),
    ];

    private static int AttributionWrites(string code) => AttributionWritePatterns.Sum(p => p.Matches(code).Count);
    private static bool WritesAttribution(string code) => AttributionWrites(code) > 0;

    [Theory]
    [InlineData("employee.DepartmentId = id;", true)]
    [InlineData("person.PositionId = id;", true)]
    [InlineData("target!.CostCenterIsOverride = true;", true)]
    [InlineData("employee.CostCenterId = other;", true)]
    [InlineData("(employee).DepartmentId = id;", true)]
    [InlineData("employees[0].DepartmentId = id;", true)]
    [InlineData(".ExecuteUpdateAsync(s => s.SetProperty(e => e.DepartmentId, id))", true)]
    [InlineData(".ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<Guid?>(e, \"CostCenterId\"), id))", true)]
    [InlineData("var e = new Employee { TenantId = t, DepartmentId = d };", true)]
    [InlineData("UPDATE employees SET department_id = @d WHERE id = @id", true)]
    [InlineData("UPDATE public.employees SET department_id = @d", true)]
    [InlineData("UPDATE employees e SET cost_center_id = @c", true)]
    [InlineData("UPDATE employees AS e SET cost_center_is_override = true", true)]
    [InlineData("UPDATE \"employees\" SET position_id = @p", true)]
    [InlineData("(employee.DepartmentId, employee.Department) = (id, name);", true)]
    [InlineData("db.Entry(employee).Property(\"DepartmentId\").CurrentValue = id;", true)]
    [InlineData("db.Entry(employee).Property(nameof(Employee.CostCenterIsOverride)).CurrentValue = true;", true)]
    [InlineData("var v = db.Entry(employee).Property(\"CostCenter\").CurrentValue;", true)]
    [InlineData("if (employee.DepartmentId == id) {}", false)]
    [InlineData("var d = employee.DepartmentId;", false)]
    [InlineData("q.Select(e => e.DepartmentId => 1)", false)]
    [InlineData("employee.NewDepartmentId = id;", false)]
    [InlineData("var e = new Employee { TenantId = t, FullName = n };", false)]
    [InlineData("UPDATE employees SET full_name = @n WHERE id = @id", false)]
    [InlineData("UPDATE employee_documents SET department_id = @d", false)]
    [InlineData("UPDATE public.employees_archive SET department_id = @d", false)]
    [InlineData("if ((employee.DepartmentId, x) == (a, b)) {}", false)]
    [InlineData("var t = Pair(employee.DepartmentId, name);", false)]
    [InlineData("db.Entry(employee).Property(\"FullName\").CurrentValue = n;", false)]
    public void TheWritePatterns_CatchEveryShape_AndOnlyWrites(string line, bool isWrite) => WritesAttribution(line).Should().Be(isWrite, line);

    private const string LedgerStamp = "Stamps the cost centre the resolver chose (or the source line's) onto ledger, payroll or settlement lines: system-derived, never an employee's attribution.";
    private const string MasterData = "Writes a department's, designation's or position's own column (master data), behind organization.costcenter.manage where it re-points cost (CostCentrePermissionTests.S028).";

    /// <summary>Files that write those columns and are not employee transfer doors: the number of writes pinned at today's count (it may only go
    /// down, so a NEW write in an allow-listed file fails too) and why. A new entry or a higher count is a decision.</summary>
    private static readonly Dictionary<string, (int Max, string Why)> NotATransferDoor = new(StringComparer.Ordinal)
    {
        ["Application/Employees/EmployeeOrgFieldResolver.cs"] = (7, "The shared rule itself (MoveDepartmentAsync, ApplyCostCentreAsync)."),
        ["Infrastructure/Employees/EmployeeManagementService.cs"] = (4, "The profile form door: sets the explicit company/department/position the form names, and the override only through its own reasoned ApplyCostCentreAsync (gated by organization.costcenter.manage at the controller and, for drafts, FormMovesEmployeeAttribution). A form department change keeps an override, as the transfer approval does; carrying keep/drop through the form is Wave B."),
        ["Controllers/EstablishmentController.cs"] = (1, "The establishment repair links an employee with NO department id to the department their text already names: not a move, no override is decided, and it goes through the run guard and the attribution audit."),
        ["Controllers/EmployeesController.cs"] = (1, "CSV import CREATES employees with their initial attribution (an import naming cost centres needs organization.costcenter.manage). It moves nobody: the PUT and ApproveHrTransfer go through PrepareAttributionAsync / MoveDepartmentAsync (asserted below). RequestTransfer (manager.approve) only records a request and writes no attribution; its permission is unchanged in Wave A (CTO decision), and its approval keeps an override only after re-validating it."),
        ["Infrastructure/Employees/EmployeeImportRowResolver.cs"] = (4, "Fills ResolvedImportRow (the import's working row), not an employee."),
        ["Infrastructure/Attendance/AttendanceImpactReconciliation.cs"] = (1, "An untracked, read-only projection (Select(e => new Employee { ... })) the attendance rules evaluate; nothing is saved."),
        ["Infrastructure/Compensation/MeritLineCalculator.cs"] = (1, "Copies the employee's department onto a merit planning line for reporting."),
        ["Infrastructure/Approvals/ApprovalWorkflowService.cs"] = (1, "An approval workflow's department scope."),
        ["Controllers/AttendancePoliciesController.cs"] = (1, "An attendance policy's department scope."),
        ["Infrastructure/Organization/OrganizationSetupService.cs"] = (2, MasterData),
        ["Controllers/OrganizationStructureImportController.cs"] = (4, MasterData),
        ["Controllers/PositionsController.cs"] = (2, MasterData),
        ["Controllers/PlanningController.cs"] = (1, MasterData),
        ["Infrastructure/Payroll/BonusCostSplit.cs"] = (1, LedgerStamp),
        ["Infrastructure/Payroll/FinalSettlementGlLedger.cs"] = (1, LedgerStamp),
        ["Infrastructure/Payroll/EosbProvisionAccrual.cs"] = (1, LedgerStamp),
        ["Infrastructure/Payroll/PayrollCostStamper.cs"] = (3, LedgerStamp),
        ["Controllers/PayrollController.cs"] = (5, LedgerStamp),
        ["Controllers/Finance/GlAdjustmentsController.cs"] = (1, LedgerStamp),
        ["Controllers/Finance/LoansController.Ledger.cs"] = (1, LedgerStamp),
    };

    private static List<string> WriteCountViolations(IReadOnlyDictionary<string, int> writes) =>
        writes.Where(w => w.Value > (NotATransferDoor.TryGetValue(w.Key, out var allowed) ? allowed.Max : 0))
            .Select(w => $"{w.Key}: {w.Value} write(s), allowed {(NotATransferDoor.TryGetValue(w.Key, out var a) ? a.Max : 0)}").ToList();

    [Fact]
    public void TransferDoorParityRatchet_ANewWriteInAnAllowListedFile_IsAViolation()
    {
        WriteCountViolations(new Dictionary<string, int> { ["Controllers/EmployeesController.cs"] = 1 }).Should().BeEmpty();
        WriteCountViolations(new Dictionary<string, int> { ["Controllers/EmployeesController.cs"] = 2 }).Should().ContainSingle("one more `employee.DepartmentId = ...` in the controller must fail");
        WriteCountViolations(new Dictionary<string, int> { ["Controllers/SomethingNew.cs"] = 1 }).Should().ContainSingle();
    }

    [Fact]
    public void TransferDoorParityRatchet_EveryDoorUsesTheSharedRule()
    {
        var root = SourceScan.ResolveApiRoot();
        var writes = SourceScan.Files(root, "*.cs", "*.sql")
            .Where(f => !f.Relative.StartsWith("Migrations/", StringComparison.Ordinal))
            .Select(f => (f.Relative, Count: AttributionWrites(f.Code)))
            .Where(f => f.Count > 0)
            .ToDictionary(f => f.Relative, f => f.Count);

        WriteCountViolations(writes).Should().BeEmpty(
            "a door that moves an employee's department or position, or sets the override flag, must go through EmployeeOrgFieldResolver (PrepareAttributionAsync / MoveDepartmentAsync); allow-listed files may not gain writes");
        NotATransferDoor.Keys.Where(k => !writes.ContainsKey(k)).Should().BeEmpty("remove an entry once its file no longer writes those columns");

        string Code(string relative) => SourceScan.StripCSharpComments(File.ReadAllText(Path.Combine(root, relative)));
        Code("Controllers/EmployeesController.cs").Should().Contain("EmployeeOrgFieldResolver.PrepareAttributionAsync", "the PUT decides keep/drop through the shared rule")
            .And.Contain("EmployeeOrgFieldResolver.MoveDepartmentAsync", "the HR transfer approval moves the department through the shared rule");
        Code("Application/Employees/EmployeeChangeApplier.cs").Should().Contain("EmployeeOrgFieldResolver.PrepareAttributionAsync", "approval paths fail closed through the shared rule");
    }
}
