using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.OfficerWording;

/// <summary>
/// Government wave 1, package F5 (officer-facing wording). Wording only: a status value, an error code or a machine key
/// never changes. These pin what the officer reads: a next step for every bank reason code, a duplicate that is never "owed",
/// labels with a named person to check, and no raw exit or enum words in a human sentence.
/// </summary>
public class BankReasonAndLabelWordingTests
{
    public static readonly TheoryData<string, string, string> NextSteps = new()
    {
        { "AC01", "Ask HR to correct the employee's IBAN, then pay this salary again.",
                  "اطلب من الموارد البشرية تصحيح آيبان الموظف ثم أعد دفع هذا الراتب." },
        { "AC04", "Check whether it is the employee's account or your paying account. If it is the employee's, ask HR for a new IBAN, then pay again.",
                  "تحقق هل هو حساب الموظف أم حساب الدفع لديكم. فإن كان حساب الموظف فاطلب من الموارد البشرية آيبانًا جديدًا ثم أعد الدفع." },
        { "AC06", "Check whether it is the employee's account or your paying account, and ask the bank what is needed to release it. Pay again once it is resolved.",
                  "تحقق هل هو حساب الموظف أم حساب الدفع لديكم، واسأل البنك عمّا يلزم لرفع الإيقاف، ثم أعد الدفع بعد المعالجة." },
        { "AM04", "Check the bank statement, add funds, and ask your bank or payroll team how to send the salaries again. Do not resend without checking.",
                  "راجع كشف حساب البنك وأودع المبلغ المطلوب، واسأل البنك أو فريق الرواتب عن طريقة إعادة إرسال الرواتب. لا تعد الإرسال قبل التحقق." },
        { "BE01", "Ask HR to check the employee's name, ID and IBAN against the bank's record.",
                  "اطلب من الموارد البشرية مطابقة اسم الموظف ورقم هويته وآيبانه مع سجل البنك." },
        { "DUPL", "Do not pay again until you have checked.", "لا تعد الدفع قبل التحقق من ذلك." },
        { "FF01", "Salaries in this file may not have been processed: check the bank statement and ask the bank which format it expects.",
                  "قد لا تكون رواتب هذا الملف قد عولجت: راجع كشف حساب البنك واسأل البنك عن الصيغة التي يطلبها." },
    };

    [Theory]
    [MemberData(nameof(NextSteps))]
    public void EveryRulingCode_HasANextStep_InEnglishAndArabic(string code, string english, string arabic)
    {
        var text = BankStatusReasonCatalogue.Describe(code);

        text.IsKnown.Should().BeTrue();
        text.NextStepEnglish.Should().Be(english);
        text.NextStepArabic.Should().Be(arabic);
        text.NextStepArabic.Any(ch => ch is >= '؀' and <= 'ۿ').Should().BeTrue();
    }

    [Fact]
    public void ADuplicate_NeverSaysOwed_AndSaysDoNotPayAgainUntilChecked()
    {
        var text = BankStatusReasonCatalogue.Describe("DUPL");
        foreach (var english in new[] { text.English, text.NextStepEnglish, BankOutcomeLabels.HeldDuplicateRejection.English })
            english.Should().NotContainEquivalentOf("owed").And.NotContainEquivalentOf("still due");
        foreach (var arabic in new[] { text.Arabic, text.NextStepArabic, BankOutcomeLabels.HeldDuplicateRejection.Arabic })
            arabic.Should().NotContain("مستحق");
        text.NextStepEnglish.Should().Contain("Do not pay again until you have checked");
        text.NextStepArabic.Should().Contain("لا تعد الدفع قبل التحقق");
    }

    [Fact]
    public void AnUnknownOrMissingCode_PointsToTheBank_InPoliteArabic()
    {
        foreach (var code in new[] { "XY99", null, "=1" })
        {
            var text = BankStatusReasonCatalogue.Describe(code);
            text.Arabic.Should().Contain("يرجى مراجعة البنك").And.NotContain("استفسر");
        }
    }

    [Fact]
    public void TheFailedLineView_CarriesTheNextStep_ForAKnownReason()
    {
        var record = new PayrollPaymentRecord { TenantId = Guid.NewGuid(), EmployeeId = 1, Amount = 100m, Status = PaymentRecordStatuses.Rejected };
        var confirmation = new BankPaymentConfirmation
        {
            TenantId = record.TenantId, PaymentRecordId = record.Id, EmployeeId = 1, Outcome = BankConfirmationOutcomes.Rejected,
            RawOutcome = "RJCT", ReasonCode = "AC01", Applied = true, ImportedAtUtc = DateTime.UtcNow,
        };

        var view = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(BankOutcomeLedger.FailedLine(
            record, BankConfirmationOutcomes.Rejected, BankOutcomeLabels.NotPaidRejectedByBank, new[] { confirmation })))
            .RootElement.GetProperty("reasonDescription");

        view.GetProperty("nextStepEn").GetString().Should().Be("Ask HR to correct the employee's IBAN, then pay this salary again.");
        view.GetProperty("nextStepAr").GetString().Should().Contain("تصحيح آيبان الموظف");
    }

    // ── Labels ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThereIsALabelForBankAcceptedTheFileCreditNotConfirmed()
    {
        BankOutcomeLabels.AcceptedCreditNotConfirmed.English.Should().Be("Bank accepted the file; credit not confirmed");
        BankOutcomeLabels.AcceptedCreditNotConfirmed.Arabic.Should().Be("قبل البنك الملف؛ لم يُؤكَّد قيد المبلغ في حساب الموظف");
        BankOutcomeLabels.All.Should().Contain(BankOutcomeLabels.AcceptedCreditNotConfirmed);
    }

    [Fact]
    public void NoTwoLabelsShareTheSameArabic_AndNoneReadsLikeTheReconciledBatchStatus()
    {
        BankOutcomeLabels.All.Select(l => l.Arabic).Should().OnlyHaveUniqueItems();
        BankOutcomeLabels.All.Select(l => l.English).Should().OnlyHaveUniqueItems();
        // "Reconciled" (a batch tied to the bank statement) reads تمت المطابقة; "Settled by bank" is the bank's own settlement.
        BankOutcomeLabels.SettledByBank.Arabic.Should().NotBe("تمت المطابقة");
        BankOutcomeLabels.SettledByBank.Arabic.Should().NotBe("تمت التسوية");
    }

    [Fact]
    public void AHeldLabel_NamesWhoMustCheck_NeverOneOfTheEmployees()
    {
        var held = new[]
        {
            BankOutcomeLabels.HeldAcceptedWithChange, BankOutcomeLabels.HeldRejectionAfterPaid, BankOutcomeLabels.HeldFailedOutcomeChanged,
        };
        foreach (var label in held)
        {
            label.Arabic.Should().NotContain("أحد الموظفين", "that reads as one of the employees");
            label.Arabic.Should().Contain("مستخدم الرواتب أو المالية");
            label.English.Should().Contain("a payroll or finance user must check");
        }
    }

    [Fact]
    public void ARejectionAfterPaid_TellsTheOfficerToCheckTheStatement_AndThatTheSalaryMayStillBeOwed()
    {
        BankOutcomeLabels.HeldRejectionAfterPaid.English.Should().Contain("bank statement").And.Contain("still owed");
        BankOutcomeLabels.HeldRejectionAfterPaid.Arabic.Should().Contain("كشف حساب البنك").And.Contain("ما زال مستحقًا");
    }

    [Fact]
    public void NoLabelAndNoReasonText_ClaimsTheMoneyNeverLeft()
    {
        var all = BankOutcomeLabels.All.Select(l => l.English)
            .Concat(BankStatusReasonCatalogue.KnownCodes.SelectMany(c => new[]
            {
                BankStatusReasonCatalogue.Describe(c).English, BankStatusReasonCatalogue.Describe(c).NextStepEnglish,
            }))
            .Append(BankOutcomeMessages.NotReconciled(1, 1, 1));
        foreach (var text in all) text.Should().NotContainEquivalentOf("never left");
        foreach (var arabic in BankOutcomeLabels.All.Select(l => l.Arabic).Append(BankOutcomeMessages.NotReconciledAr(1, 1, 1)))
            arabic.Should().NotContain("لم يخرج");
    }

    // ── The "not reconciled" message (F1) ────────────────────────────────────────────────────────

    [Fact]
    public void TheNotReconciledMessage_SendsTheOfficerToTheBankStatement_NotToACertainty()
    {
        var english = BankOutcomeMessages.NotReconciled(1, 2, 3);
        english.Should().Contain("2 not paid: rejected by bank")
            .And.Contain("check the bank statement before paying again")
            .And.Contain("1 returned by the bank").And.Contain("3 not yet confirmed by the bank");
        english.Should().NotContainEquivalentOf("never left");

        var arabic = BankOutcomeMessages.NotReconciledAr(1, 2, 3);
        arabic.Should().Contain("لم يُدفع: رفضه البنك").And.Contain("راجع كشف حساب البنك قبل إعادة الدفع");
        arabic.Any(ch => ch is >= '؀' and <= 'ۿ').Should().BeTrue();
        BankOutcomeMessages.NotReconciledAr(0, 0, 3).Should().NotContain("رفضه");
    }

    [Fact]
    public void TheHeldBatchMessage_NamesAUserWhoMayRecordPaymentStatus_NotAPersonOrACompetentEmployee()
    {
        foreach (var action in new[] { BankOutcomeMessages.LedgerSettleAction, BankOutcomeMessages.ReconcileAction })
        {
            var (en, ar) = BankOutcomeMessages.HeldForPerson(action, 2);
            en.Should().Contain("a user who may record payment status must decide").And.NotContain("a person must decide");
            ar.Should().Contain("يحتاج إلى قرار من مستخدم مخوّل بتسجيل حالة الدفع").And.NotContain("موظف مختص");
        }
    }

    [Fact]
    public void TheHeldBatchMessage_AsksTheOfficerToCheckTheStatementAndRecordADecision_NeverNamesAnExit()
    {
        foreach (var action in new[] { BankOutcomeMessages.LedgerSettleAction, BankOutcomeMessages.ReconcileAction })
        {
            var (en, ar) = BankOutcomeMessages.HeldForPerson(action, 2);
            en.Should().Contain("2 salary line(s)").And.Contain("check the bank statement").And.Contain("record your decision with a reason and the bank's reference");
            en.Should().NotContainEquivalentOf("exit");
            ar.Should().Contain("كشف حساب البنك").And.Contain("سجّل قرارك مع السبب ومرجع البنك");
        }
        BankOutcomeMessages.HeldForPerson(BankOutcomeMessages.LedgerSettleAction, 1).En.Should().Contain("posted to the ledger").And.NotContain("settled");
        BankOutcomeMessages.HeldForPerson(BankOutcomeMessages.ReconcileAction, 1).En.Should().Contain("reconciled");
    }
}
