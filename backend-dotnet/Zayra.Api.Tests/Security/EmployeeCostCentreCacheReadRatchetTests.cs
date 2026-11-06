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
    // A lower-case receiver (a variable, not a type such as OrgRowKind) reading .CostCenter, not as the target of an assignment.
    private static readonly Regex CachedRead = new(@"\b[a-z_]\w*\.CostCenter\b(?![\w(])(?!\s*=(?!=))", RegexOptions.Compiled);

    /// <summary>Remaining reads, each with why it is not a user-facing or decision read. The counts may only go down.</summary>
    private static readonly Dictionary<string, (int Max, string Why)> AllowedReads = new(StringComparer.Ordinal)
    {
        ["Application/Employees/EmployeeManagementDtos.cs"] = (2,
            "EmployeeDetailDto.Project maps the cached copy for legacy readers (GET detail overwrites it with the resolved code; every writer stores the resolved code), "
            + "and the draft DTO maps EmployeeDraft.CostCenter, a different entity's field."),
        ["Controllers/EmployeesController.cs"] = (3,"The onboarding draft maps EmployeeDraft.CostCenter / EmployeeDraftRequest.CostCenter: a different entity, not the employee's cache."),
    };

    [Fact]
    public void CachedCostCenterStringReadRatchet_NoNewReadsOfTheCachedCode()
    {
        var root = SourceScan.ResolveApiRoot();
        var counts = SourceScan.Files(root, "*.cs")
            .Where(f => !f.Relative.StartsWith("Migrations/", StringComparison.Ordinal))
            .Select(f => (f.Relative, Count: CachedRead.Matches(SourceScan.StripCSharpComments(f.Code)).Count))
            .Where(f => f.Count > 0)
            .ToDictionary(f => f.Relative, f => f.Count);

        foreach (var (file, count) in counts)
            count.Should().BeLessThanOrEqualTo(AllowedReads.TryGetValue(file, out var allowed) ? allowed.Max : 0,
                $"{file} reads the cached Employee.CostCenter string; derive the code from CostCentreResolver (EmployeeCostCentreView) instead");
        counts.Should().NotContainKey("Controllers/OrgWriteRules.cs", "the permission gate is judged on the override flag and ids (D1)");
        AllowedReads.Keys.Where(k => !counts.ContainsKey(k)).Should().BeEmpty("an allow-list entry for a file that no longer reads the cache must be removed");
    }

    // An employee-like receiver writing the transfer-relevant columns.
    private static readonly Regex AttributionWrite = new(@"\b(?:employee|emp|e|existing)\.(?:DepartmentId|PositionId|CostCenterIsOverride)\s*=(?![=>])", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> NotATransferDoor = new(StringComparer.Ordinal)
    {
        ["Application/Employees/EmployeeOrgFieldResolver.cs"] = "The shared rule itself (MoveDepartmentAsync, ApplyCostCentreAsync).",
        ["Infrastructure/Employees/EmployeeManagementService.cs"] = "The profile form door: sets the explicit company/department/position the form names, and the override only through its own reasoned ApplyCostCentreAsync (gated by organization.costcenter.manage at the controller and, for drafts, FormMovesEmployeeAttribution). A form department change keeps an override, as the transfer approval does; carrying keep/drop through the form is Wave B.",
        ["Controllers/EstablishmentController.cs"] = "The establishment repair links an employee with NO department id to the department their text already names: not a move, no override is decided, and it goes through the run guard and the attribution audit.",
    };

    [Fact]
    public void TransferDoorParityRatchet_EveryDoorUsesTheSharedRule()
    {
        var root = SourceScan.ResolveApiRoot();
        var writers = SourceScan.Files(root, "*.cs")
            .Where(f => !f.Relative.StartsWith("Migrations/", StringComparison.Ordinal))
            .Where(f => AttributionWrite.IsMatch(SourceScan.StripCSharpComments(f.Code)))
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
