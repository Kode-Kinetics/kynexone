using FluentAssertions;
using Zayra.Api.Infrastructure.Policy;

namespace Zayra.Api.Tests;

/// <summary>Security review L4: a Csv policy value is bounded in length and token count, tokens are plain field names, and a key with a closed vocabulary only accepts members of it.</summary>
public class PolicyRegistryCsvValidationTests
{
    private const string Floor = "bank_iban,national_id,passport_number,salary";
    private static readonly PolicyDefinition Def = PolicyRegistry.Get(PolicyRegistry.EmployeesProfileChangeApprovalFields);

    private static string Tokens(int count) => string.Join(',', Enumerable.Range(1, count).Select(i => $"f{i}"));

    private static bool Try(string raw, out string normalised, out string error, out string code) =>
        PolicyRegistry.TryNormalise(Def, raw, out normalised, out error, out code);

    [Fact]
    public void A_value_over_2000_characters_is_refused()
    {
        var raw = Floor + "," + new string('a', 2001 - Floor.Length - 1);
        raw.Length.Should().Be(2001);

        Try(raw, out _, out var error, out var code).Should().BeFalse();
        code.Should().Be("policy_value_invalid");
        error.Should().Contain("2000");
    }

    [Fact]
    public void A_value_of_exactly_2000_characters_is_accepted()
    {
        var raw = Floor + "," + new string('a', 2000 - Floor.Length - 1);
        raw.Length.Should().Be(2000);

        Try(raw, out _, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void More_than_100_tokens_are_refused_and_exactly_100_are_accepted()
    {
        Try(Floor + "," + Tokens(97), out _, out var error, out var code).Should().BeFalse("101 tokens");
        code.Should().Be("policy_value_invalid");
        error.Should().Contain("100");
        Try(Floor + "," + Tokens(96), out _, out _, out _).Should().BeTrue("100 tokens");
    }

    [Theory]
    [InlineData("work location")]
    [InlineData("semi;colon")]
    [InlineData("slash/x")]
    [InlineData("<script>")]
    [InlineData("quote\"d")]
    [InlineData("café")]
    [InlineData("tab\tbed")]
    public void A_token_that_is_not_a_plain_field_name_is_refused(string token)
    {
        Try(Floor + "," + token, out _, out var error, out var code).Should().BeFalse();
        code.Should().Be("policy_value_invalid");
        error.Should().Contain("token");
    }

    [Theory]
    [InlineData("work_location")]
    [InlineData("a-b")]
    [InlineData("a.b")]
    [InlineData("Field9")]
    public void Plain_field_names_are_accepted_and_lowercased(string token)
    {
        Try(Floor + "," + token, out var normalised, out _, out _).Should().BeTrue();
        normalised.Split(',').Should().Contain(token.ToLowerInvariant());
    }

    [Fact]
    public void A_key_with_a_closed_vocabulary_only_accepts_members_of_it()
    {
        var closed = new PolicyDefinition("test.closed_csv", PolicyValueType.Csv, "a", ["a", "b"], "closed vocabulary", "Test");

        PolicyRegistry.TryNormalise(closed, "a,c", out _, out var error, out var code).Should().BeFalse();
        code.Should().Be("policy_value_invalid");
        error.Should().Contain("c");
        PolicyRegistry.TryNormalise(closed, "A, b", out var normalised, out _, out _).Should().BeTrue();
        normalised.Should().Be("a,b");
    }

    [Fact]
    public void The_floor_check_runs_after_the_shape_check_and_has_its_own_code()
    {
        Try("salary", out _, out _, out var floorCode).Should().BeFalse();
        floorCode.Should().Be("policy_floor_violated");
        Try("salary,bad token", out _, out _, out var shapeCode).Should().BeFalse();
        shapeCode.Should().Be("policy_value_invalid");
    }
}
