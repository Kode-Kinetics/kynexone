using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Tests;

/// <summary>
/// The shared ISO 20022 StatusReason catalogue (CTO gate 8.4.7): the plain English and Arabic text for a bank's reason
/// code, used by every reader that receives one (the generic parsers, the returned-file reader, the ANB mapper).
/// An unknown code is never guessed at: it says which code the bank sent and to ask the bank.
/// </summary>
public class BankStatusReasonCatalogueTests
{
    public static readonly TheoryData<string, string, string> KnownCodes = new()
    {
        { "AC01", "The account number (IBAN) is wrong.", "رقم الحساب (الآيبان) غير صحيح." },
        { "AC04", "The account is closed.", "الحساب مغلق." },
        { "AC06", "The account is blocked.", "الحساب موقوف." },
        { "AM04", "There was not enough money in the paying account.", "الرصيد في حساب الدفع غير كافٍ." },
        { "BE01", "The name or ID does not match the account holder.", "الاسم أو رقم الهوية لا يطابق صاحب الحساب." },
        // A duplicate refusal usually means the ORIGINAL payment went through: never "not paid, still owed" (CTO ruling 4).
        { "DUPL", "The bank refused this as a duplicate. Check whether the original payment reached the employee before paying again.",
                  "رفض البنك هذه العملية لأنها مكررة. تحقق مما إذا كانت عملية الدفع الأصلية قد وصلت إلى الموظف قبل الدفع مرة أخرى." },
        { "FF01", "The bank could not read the file format.", "تعذّر على البنك قراءة صيغة الملف." },
    };

    [Theory]
    [MemberData(nameof(KnownCodes))]
    public void KnownCode_HasPlainEnglishAndArabic(string code, string english, string arabic)
    {
        foreach (var form in new[] { code, code.ToLowerInvariant(), $" {code} " })
        {
            var text = BankStatusReasonCatalogue.Describe(form);
            text.IsKnown.Should().BeTrue($"'{form}'");
            text.Code.Should().Be(code);
            text.English.Should().Be(english);
            text.Arabic.Should().Be(arabic);
        }
    }

    [Fact]
    public void Catalogue_CoversAtLeastTheSevenRulingCodes() =>
        BankStatusReasonCatalogue.KnownCodes.Should().Contain(new[] { "AC01", "AC04", "AC06", "AM04", "BE01", "DUPL", "FF01" });

    [Theory]
    [InlineData("XY99")]
    [InlineData("MS03")]
    [InlineData("PAYROLL_FAILED")]
    public void UnknownCode_SaysWhichCodeAndToAskTheBank_NeverAGuess(string code)
    {
        var text = BankStatusReasonCatalogue.Describe(code);

        text.IsKnown.Should().BeFalse();
        text.Code.Should().Be(code);
        text.English.Should().Be($"Bank reason code {code}: ask your bank");
        text.Arabic.Should().Be($"رمز السبب من البنك {code}: يرجى مراجعة البنك");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoCode_SaysTheBankGaveNone(string? code)
    {
        var text = BankStatusReasonCatalogue.Describe(code);

        text.IsKnown.Should().BeFalse();
        text.Code.Should().BeNull();
        text.English.Should().Be("The bank gave no reason code: ask your bank");
        text.Arabic.Should().Be("لم يذكر البنك رمز السبب: يرجى مراجعة البنك");
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")")]
    [InlineData("AC01\u202E")]   // a right-to-left override hidden after a real code
    [InlineData("A\u0131C1")]     // a look-alike letter
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789X")] // 37 characters: longer than any ISO or proprietary code
    [InlineData("-A1")]     // a leading minus reads as a formula in a spreadsheet export
    [InlineData("_A1")]
    public void UnreadableCode_IsNeverEchoed(string code)
    {
        var text = BankStatusReasonCatalogue.Describe(code);

        text.IsKnown.Should().BeFalse();
        text.Code.Should().BeNull("text the bank file put in the code column is not shown back as if it were a code");
        text.English.Should().Be("The bank sent a reason code we cannot read: ask your bank");
        text.Arabic.Should().Be("أرسل البنك رمز سبب لا يمكن قراءته: يرجى مراجعة البنك");
    }

    // ── Outcome labels ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RejectedLabel_SaysNotPaid_NeverReturned()
    {
        BankOutcomeLabels.NotPaidRejectedByBank.English.Should().Be("Not paid: rejected by bank");
        BankOutcomeLabels.NotPaidRejectedByBank.Arabic.Should().Be("لم يُدفع: رفضه البنك");
        BankOutcomeLabels.NotPaidRejectedByBank.English.Should().NotContainEquivalentOf("return");
    }

    [Fact]
    public void SettledLabel_SaysSettledByBank_NeverEmployeeReceived()
    {
        BankOutcomeLabels.SettledByBank.English.Should().Be("Settled by bank");
        BankOutcomeLabels.SettledByBank.Arabic.Should().Be("تمت التسوية لدى البنك");
        BankOutcomeLabels.SettledByBank.English.Should().NotContainEquivalentOf("receiv");
        BankOutcomeLabels.SettledByBank.English.Should().NotContainEquivalentOf("employee");
    }

    [Fact]
    public void ReturnedLabel_SaysTheMoneyCameBack()
    {
        BankOutcomeLabels.ReturnedMoneyCameBack.English.Should().Be("Returned: the money came back");
        BankOutcomeLabels.ReturnedMoneyCameBack.Arabic.Should().Be("مُرتجع: أعاد البنك المبلغ");
    }

    [Fact]
    public void AcceptedWithChangeLabel_SaysHeldForAPerson()
    {
        BankOutcomeLabels.HeldAcceptedWithChange.English.Should().Be(
            "Held: the bank accepted it with a change; a payroll or finance user must check what changed");
        BankOutcomeLabels.HeldAcceptedWithChange.Arabic.Should().Be(
            "معلّق: قبل البنك التعليمات مع تعديل، ويجب أن يراجع مستخدم الرواتب أو المالية ما الذي تغيّر");
    }

    [Fact]
    public void DuplicateHeldLabel_TellsThePersonToCheckTheOriginal_NeverStillOwed()
    {
        BankOutcomeLabels.HeldDuplicateRejection.English.Should().Be(BankStatusReasonCatalogue.Describe("DUPL").English);
        BankOutcomeLabels.HeldDuplicateRejection.Arabic.Should().Be(BankStatusReasonCatalogue.Describe("DUPL").Arabic);
        BankOutcomeLabels.HeldDuplicateRejection.English.Should().NotContainEquivalentOf("owed").And.NotContainEquivalentOf("not paid");
        BankStatusReasonCatalogue.IsDuplicate(" dupl ").Should().BeTrue();
        BankStatusReasonCatalogue.IsDuplicate("AC01").Should().BeFalse();
        BankStatusReasonCatalogue.IsDuplicate(null).Should().BeFalse();
    }

    [Fact]
    public void EveryArabicString_FollowsTheGlossary()
    {
        var arabic = BankStatusReasonCatalogue.KnownCodes.Select(c => BankStatusReasonCatalogue.Describe(c).Arabic)
            .Concat(BankOutcomeLabels.All.Select(l => l.Arabic))
            .Append(BankStatusReasonCatalogue.Describe("XY99").Arabic)
            .Append(BankStatusReasonCatalogue.Describe(null).Arabic)
            .Append(BankStatusReasonCatalogue.Describe("=1").Arabic)
            .ToList();

        arabic.Should().OnlyContain(a => a.Length > 0 && a.Any(ch => ch >= '؀' && ch <= 'ۿ'), "every string has Arabic");
        foreach (var a in arabic)
        {
            // ANB is always البنك العربي الوطني, never البنك العربي alone; a payroll run is مسير الرواتب.
            a.Replace("البنك العربي الوطني", string.Empty).Should().NotContain("البنك العربي");
            a.Should().NotContain("تشغيل الرواتب");
        }
    }
}
