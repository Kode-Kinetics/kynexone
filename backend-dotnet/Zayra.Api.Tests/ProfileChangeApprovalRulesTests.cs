using FluentAssertions;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Policy;

namespace Zayra.Api.Tests;

/// <summary>PEO-03 — which profile fields go through approval is driven by employees.profile_change_approval_fields.</summary>
public class ProfileChangeApprovalRulesTests
{
    private static readonly IReadOnlyList<string> Default =
        PolicyRegistry.ParseCsv(PolicyRegistry.Get(PolicyRegistry.EmployeesProfileChangeApprovalFields).Default);

    [Fact]
    public void Default_list_includes_nationality()
        => Default.Should().Contain("nationality");

    [Theory]
    [InlineData("bankIban")]
    [InlineData("idNumber")]          // policy name: national_id
    [InlineData("passportNumber")]
    [InlineData("salary")]
    [InlineData("iqamaNumber")]
    [InlineData("sponsorName")]
    [InlineData("nationality")]
    public void Default_list_gates_the_existing_fields(string field)
        => ProfileChangeApprovalRules.RequiresApproval(field, Default).Should().BeTrue();

    [Theory]
    [InlineData("phone")]
    [InlineData("preferredName")]
    public void Fields_outside_the_list_apply_directly(string field)
        => ProfileChangeApprovalRules.RequiresApproval(field, Default).Should().BeFalse();

    [Fact]
    public void A_field_added_to_the_list_is_gated()
        => ProfileChangeApprovalRules.RequiresApproval("phone", new[] { "phone" }).Should().BeTrue();

    [Fact]
    public void A_snake_case_name_matches_the_camel_case_field()
        => ProfileChangeApprovalRules.RequiresApproval("emergencyContactPhone", new[] { "emergency_contact_phone" }).Should().BeTrue();

    [Fact]
    public void Removing_a_policy_governed_field_lets_it_apply_directly()
    {
        ProfileChangeApprovalRules.RequiresApproval("nationality", new[] { "salary" }).Should().BeFalse();
        ProfileChangeApprovalRules.RequiresApproval("salary", Array.Empty<string>()).Should().BeFalse();
    }

    [Theory]
    [InlineData("gosiReference")]
    [InlineData("salaryBreakdown")]
    [InlineData("medicalInformation")]
    public void Structural_fields_outside_the_policy_vocabulary_stay_gated_even_with_an_empty_list(string field)
        => ProfileChangeApprovalRules.RequiresApproval(field, Array.Empty<string>()).Should().BeTrue();
}
