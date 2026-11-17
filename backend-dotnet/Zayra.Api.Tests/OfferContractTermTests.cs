using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// An offer says whether the job is permanent or fixed-term and, for a fixed term, how long. The same words reach the letter
/// the candidate reads and the contract the new employee starts on. No database server: pure rules and the in-memory provider.
/// </summary>
public sealed class OfferContractTermTests
{
    [Theory]
    [InlineData(null, "Permanent")]
    [InlineData("", "Permanent")]
    [InlineData("permanent", "Permanent")]
    [InlineData("Permanent", "Permanent")]
    [InlineData("Fixed-term", "Fixed-term")]
    [InlineData("fixed term", "Fixed-term")]
    [InlineData("FIXED_TERM", "Fixed-term")]
    [InlineData("Casual", null)]
    public void The_type_is_recognised_in_any_spelling_and_an_unknown_one_is_refused(string? typed, string? expected) =>
        OfferContractTerm.Normalize(typed).Should().Be(expected);

    [Theory]
    [InlineData("Fixed-term", null, "offer_contract_term_required")]
    [InlineData("Fixed-term", 0, "offer_contract_term_out_of_range")]
    [InlineData("Fixed-term", 121, "offer_contract_term_out_of_range")]
    [InlineData("Fixed-term", 12, null)]
    [InlineData("Permanent", 12, "offer_contract_term_not_allowed")]
    [InlineData("Permanent", null, null)]
    [InlineData(null, null, null)]
    [InlineData("Casual", null, "offer_contract_type_invalid")]
    public void A_fixed_term_needs_a_length_and_a_permanent_offer_must_not_have_one(string? type, int? months, string? expectedError) =>
        OfferContractTerm.Validate(type, months).Should().Be(expectedError);

    [Fact]
    public void The_end_date_is_the_day_before_the_anniversary_and_only_a_fixed_term_has_one()
    {
        OfferContractTerm.EndDate("Fixed-term", new DateOnly(2027, 3, 1), 12).Should().Be(new DateOnly(2028, 2, 29));
        OfferContractTerm.EndDate("Fixed-term", new DateOnly(2027, 1, 31), 1).Should().Be(new DateOnly(2027, 2, 28));
        OfferContractTerm.EndDate("Permanent", new DateOnly(2027, 3, 1), null).Should().BeNull();
        OfferContractTerm.EndDate(null, new DateOnly(2027, 3, 1), null).Should().BeNull();
    }

    [Fact]
    public void The_letter_names_the_term_in_months()
    {
        OfferContractTerm.LetterTerm("Fixed-term", 12).Should().Be("12 months");
        OfferContractTerm.LetterTerm("Fixed-term", 1).Should().Be("1 month");
        OfferContractTerm.LetterTerm("Permanent", null).Should().BeNull();
    }

    [Fact]
    public async Task A_fixed_term_offers_letter_states_the_fixed_term_and_a_permanent_one_does_not()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase($"offer-term-{Guid.NewGuid():N}").Options);
        var tenantId = Guid.NewGuid();
        OfferLetter Offer(string? type, int? months) => new()
        {
            TenantId = tenantId, ApplicationId = Guid.NewGuid(), CandidateName = "Test Candidate", OfferedJobTitle = "Analyst",
            OfferedDepartment = "People", StartDate = new DateOnly(2027, 3, 1), BasicSalary = 10_000, GrossSalary = 10_000,
            ContractType = type, ContractTermMonths = months,
        };

        var fixedTerm = await OfferLetterDesign.SnapshotAsync(db, Offer("Fixed-term", 12), OfferLetterDesign.DynamicDefault(), null, null, CancellationToken.None);
        fixedTerm.Values["contract_term_clause"].Should().Contain("fixed-term contract of 12 months");

        var permanent = await OfferLetterDesign.SnapshotAsync(db, Offer("Permanent", null), OfferLetterDesign.DynamicDefault(), null, null, CancellationToken.None);
        permanent.Values["contract_term_clause"].Should().NotContain("fixed-term contract of");
    }
}
