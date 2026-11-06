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
    // object initialiser; and raw SQL that UPDATEs employees' attribution columns.
    private const string Column = "(?:CostCenterId|CostCenterIsOverride|DepartmentId|PositionId)";
    private static readonly Regex[] AttributionWritePatterns =
    [
        new(@"[\w\]\)]+[!?]?\." + Column + @"\s*=(?![=>])", RegexOptions.Compiled),
        new(@"SetProperty\(\s*\w+\s*=>\s*(?:\w+\." + Column + @"\b|EF\.Property<[^>]*>\([^,()]+,\s*""" + Column + @"""\s*\))", RegexOptions.Compiled),
        new(@"new\s+(?:Models\.)?Employee\s*(?:\(\s*\))?\s*\{[^{}]*\b" + Column + @"\s*=(?![=>])", RegexOptions.Compiled),
        new(@"UPDATE\s+""?employees""?\s+SET[^;]*\b(?:department_id|position_id|cost_center_id|cost_center_is_override)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private static bool WritesAttribution(string code) => AttributionWritePatterns.Any(p => p.IsMatch(code));

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
    [InlineData("if (employee.DepartmentId == id) {}", false)]
    [InlineData("var d = employee.DepartmentId;", false)]
    [InlineData("q.Select(e => e.DepartmentId => 1)", false)]
    [InlineData("employee.NewDepartmentId = id;", false)]
    [InlineData("var e = new Employee { TenantId = t, FullName = n };", false)]
    [InlineData("UPDATE employees SET full_name = @n WHERE id = @id", false)]
    public void TheWritePatterns_CatchEveryShape_AndOnlyWrites(string line, bool isWrite) => WritesAttribution(line).Should().Be(isWrite, line);

    private const string LedgerStamp = "Stamps the cost centre the resolver chose (or the source line's) onto ledger, payroll or settlement lines: system-derived, never an employee's attribution.";
    private const string MasterData = "Writes a department's, designation's or position's own column (master data), behind organization.costcenter.manage where it re-points cost (CostCentrePermissionTests.S028).";

    /// <summary>Files that write those columns and are not employee transfer doors, each with why. A new entry is a decision.</summary>
    private static readonly Dictionary<string, string> NotATransferDoor = new(StringComparer.Ordinal)
    {
        ["Application/Employees/EmployeeOrgFieldResolver.cs"] = "The shared rule itself (MoveDepartmentAsync, ApplyCostCentreAsync).",
        ["Infrastructure/Employees/EmployeeManagementService.cs"] = "The profile form door: sets the explicit company/department/position the form names, and the override only through its own reasoned ApplyCostCentreAsync (gated by organization.costcenter.manage at the controller and, for drafts, FormMovesEmployeeAttribution). A form department change keeps an override, as the transfer approval does; carrying keep/drop through the form is Wave B.",
        ["Controllers/EstablishmentController.cs"] = "The establishment repair links an employee with NO department id to the department their text already names: not a move, no override is decided, and it goes through the run guard and the attribution audit.",
        ["Controllers/EmployeesController.cs"] = "CSV import CREATES employees with their initial attribution (an import naming cost centres needs organization.costcenter.manage). It moves nobody: the PUT and ApproveHrTransfer go through PrepareAttributionAsync / MoveDepartmentAsync (asserted below). RequestTransfer (manager.approve) only records a request and writes no attribution; its permission is unchanged in Wave A (CTO decision), and its approval keeps an override only after re-validating it.",
        ["Infrastructure/Employees/EmployeeImportRowResolver.cs"] = "Fills ResolvedImportRow (the import's working row), not an employee.",
        ["Infrastructure/Attendance/AttendanceImpactReconciliation.cs"] = "An untracked, read-only projection (Select(e => new Employee { ... })) the attendance rules evaluate; nothing is saved.",
        ["Infrastructure/Compensation/MeritLineCalculator.cs"] = "Copies the employee's department onto a merit planning line for reporting.",
        ["Infrastructure/Approvals/ApprovalWorkflowService.cs"] = "An approval workflow's department scope.",
        ["Controllers/AttendancePoliciesController.cs"] = "An attendance policy's department scope.",
        ["Infrastructure/Organization/OrganizationSetupService.cs"] = MasterData,
        ["Controllers/OrganizationStructureImportController.cs"] = MasterData,
        ["Controllers/PositionsController.cs"] = MasterData,
        ["Controllers/PlanningController.cs"] = MasterData,
        ["Infrastructure/Payroll/BonusCostSplit.cs"] = LedgerStamp,
        ["Infrastructure/Payroll/FinalSettlementGlLedger.cs"] = LedgerStamp,
        ["Infrastructure/Payroll/EosbProvisionAccrual.cs"] = LedgerStamp,
        ["Infrastructure/Payroll/PayrollCostStamper.cs"] = LedgerStamp,
        ["Controllers/PayrollController.cs"] = LedgerStamp,
        ["Controllers/Finance/GlAdjustmentsController.cs"] = LedgerStamp,
        ["Controllers/Finance/LoansController.Ledger.cs"] = LedgerStamp,
    };

    [Fact]
    public void TransferDoorParityRatchet_EveryDoorUsesTheSharedRule()
    {
        var root = SourceScan.ResolveApiRoot();
        var writers = SourceScan.Files(root, "*.cs", "*.sql")
            .Where(f => !f.Relative.StartsWith("Migrations/", StringComparison.Ordinal))
            .Where(f => WritesAttribution(f.Code))
            .Select(f => f.Relative)
            .ToList();

        writers.Where(f => !NotATransferDoor.ContainsKey(f)).Should().BeEmpty(
            "a door that moves an employee's department or position, or sets the override flag, must go through EmployeeOrgFieldResolver (PrepareAttributionAsync / MoveDepartmentAsync)");
        NotATransferDoor.Keys.Where(k => !writers.Contains(k)).Should().BeEmpty("remove an entry once its file no longer writes those columns");

        string Code(string relative) => SourceScan.StripCSharpComments(File.ReadAllText(Path.Combine(root, relative)));
        Code("Controllers/EmployeesController.cs").Should().Contain("EmployeeOrgFieldResolver.PrepareAttributionAsync", "the PUT decides keep/drop through the shared rule")
            .And.Contain("EmployeeOrgFieldResolver.MoveDepartmentAsync", "the HR transfer approval moves the department through the shared rule");
        Code("Application/Employees/EmployeeChangeApplier.cs").Should().Contain("EmployeeOrgFieldResolver.PrepareAttributionAsync", "approval paths fail closed through the shared rule");
    }
}
