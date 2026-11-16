using FluentAssertions;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Tests;

/// <summary>
/// CONTRACT: every edit key the field registry can name (a descriptor bound to an <c>Employee</c> column, <c>emp.X</c> -> <c>x</c>)
/// is either accepted by <c>PUT /api/employees/{id}</c> or listed below as deliberately written elsewhere, with the reason. A
/// registry field the update endpoint refuses and nothing else writes is a value no screen can ever correct while the wage files
/// still read it (EstablishmentId: the bank export raises <c>establishment_mismatch</c> from it). Enumerates the live registry and
/// the live allow-list, so a new registry field with no write path fails here.
/// </summary>
public class EmployeeEditKeyContractTests
{
    /// <summary>Registry keys that the generic edit PATCH must NOT write, and why. Adding to this list needs the same review as
    /// widening the allow-list: each entry is a field with its own write path or one derived from others.</summary>
    internal static readonly IReadOnlyDictionary<string, string> WrittenElsewhere = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["status"] = "lifecycle: activation, suspension, offboarding and rehire endpoints own every status move",
        ["employeeCode"] = "issued by the employee ID rule at hire; never renamed through an edit",
        ["fullName"] = "derived from the English and Arabic names",
        ["englishName(fromFullName)"] = "import-only transform of the FullName column",
        ["countryCode"] = "derived from the employing company",
        ["saudiOrNonSaudi"] = "derived from the nationality (EmployeeManagementService.DeriveWorkerClass)",
        ["idType"] = "derived from the identity numbers on file (EmployeeManagementService.PrimaryIdType)",
        ["managerEmployeeId(email)"] = "import-only lookup; the edit sets managerEmployeeId",
        ["managerEmployeeId(code)"] = "import-only lookup; the edit sets managerEmployeeId",
        ["supervisorEmployeeId(email)"] = "import-only lookup",
        ["supervisorEmployeeId(code)"] = "import-only lookup",
        ["qiwaSyncStatus"] = "written by the Qiwa sync worker",
        ["qiwaEmployeeReference"] = "returned by Qiwa and written by the sync worker",
        ["contractStartDate"] = "contract terms lifecycle (EmployeeContractTerms)",
        ["contractEndDate"] = "contract terms lifecycle (EmployeeContractTerms)",
        ["probationStartDate"] = "probation lifecycle",
        ["probationEndDate"] = "probation lifecycle",
        ["confirmationDate"] = "probation lifecycle: set when probation is confirmed",
        ["noticePeriodDays"] = "contract terms lifecycle",
        ["attendancePolicyCode"] = "policy assignment",
        ["leavePolicyCode"] = "policy assignment",
        ["shiftPolicyCode"] = "policy assignment",
    };

    [Fact]
    public void EveryRegistryEditKey_IsAcceptedByTheUpdateEndpoint_OrWrittenElsewhereForAStatedReason()
    {
        var refused = EmployeeFieldRegistry.Catalog
            .Select(d => (d.Key, Edit: EmployeesController.EmployeeEditKey(d)))
            .Where(x => x.Edit is not null
                        && !EmployeesController.EditableEmployeeFields.Contains(x.Edit)
                        && !WrittenElsewhere.ContainsKey(x.Edit))
            .Select(x => $"{x.Key} -> {x.Edit}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        string.Join(", ", refused).Should().BeEmpty(
            "a registry field bound to an Employee column must be writable through the change flow, or say where it is written");
    }

    [Fact]
    public void TheExclusionList_NamesOnlyRealRegistryKeys_ThatTheEndpointDoesRefuse()
    {
        var registryKeys = EmployeeFieldRegistry.Catalog.Select(EmployeesController.EmployeeEditKey).Where(k => k is not null).ToHashSet();
        WrittenElsewhere.Keys.Should().OnlyContain(k => registryKeys.Contains(k), "a stale exclusion hides nothing and must be removed");
        WrittenElsewhere.Keys.Should().NotContain(k => EmployeesController.EditableEmployeeFields.Contains(k),
            "a key that IS editable must not also be listed as written elsewhere");
    }

    [Theory]
    [InlineData("establishmentId")]
    [InlineData("occupationCode")]
    [InlineData("workLocationId")]
    [InlineData("contractReference")]
    [InlineData("workPermitReference")]
    public void TheQiwaReferences_AreEditable_AndApprovalGatedLikeTheQiwaContractNumber(string key)
    {
        EmployeesController.EditableEmployeeFields.Should().Contain(key);
        ProfileChangeApprovalRules.LegacySensitiveFields.Should().Contain(key);
        ProfileChangeApprovalRules.LegacySensitiveFields.Should().Contain("qiwaContractNumber");
    }
}
