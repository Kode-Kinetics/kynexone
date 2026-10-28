using FluentAssertions;
using Zayra.Api.Application.Onboarding;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// CD63: the Saudi starter checklist states only what an official source states (72-CONSULTANT-AUDIT-P2P4 section 2), in English and Arabic.
/// Offsets are planning defaults the company can edit; the task TEXT never promises a deadline nobody verified.
/// </summary>
public class KsaStarterTemplateSourcesTests
{
    private static StarterTask Task(string startsWith) => KsaStarterTemplate.Tasks.Single(t => t.Title.StartsWith(startsWith, StringComparison.Ordinal));
    private static string All(StarterTask t) => string.Join(" | ", t.Title, t.TitleAr, t.Description, t.DescriptionAr);

    [Fact]
    public void Gosi_StatesTheFifteenDayRuleOfTheFollowingMonth()
    {
        var t = Task("GOSI registration");
        t.Description.Should().Contain("first 15 days of the month after");
        t.DescriptionAr.Should().Contain("أول 15 يومًا من الشهر التالي");
        All(t).Should().NotContain("15 to 30").And.NotContain("verify");
    }

    [Fact]
    public void Cchi_CoversEligibleDependants_NotAThreeDependantCap_AndTenWorkingDaysForExpats()
    {
        var t = Task("CCHI");
        t.Title.Should().Contain("eligible dependants").And.NotContain("up to 3");
        t.TitleAr.Should().Contain("ولتابعيه المستحقين").And.NotContain("3");
        t.Description.Should().Contain("10 working days of entry");
        t.DescriptionAr.Should().Contain("10 أيام عمل");
    }

    [Fact]
    public void WorkPermit_Is90DaysFromEntry_NotAnIqamaRule()
    {
        var t = Task("Work permit");
        t.Description.Should().Contain("work permit within 90 days of entry");
        t.DescriptionAr.Should().Contain("رخصة العمل خلال 90 يومًا من الدخول");
        t.Title.Should().NotContain("visa", because: "the 90-day rule is the work permit's");
    }

    [Fact]
    public void Mudad_TellsTheThirtyDayFileWindow_AndNoSevenDayWageClaim()
    {
        var t = Task("WPS and Mudad");
        t.Description.Should().Contain("Mudad window (30 days");
        t.DescriptionAr.Should().Contain("ارفع ملف الأجور على مُدد ضمن المهلة المحددة (30 يومًا حاليًا)");
        All(t).Should().NotContain("7 days").And.NotContain("7 أيام");
    }

    [Fact]
    public void Qiwa_MedicalFitness_AndTheContract_MakeNoUnverifiedDeadlineClaim()
    {
        foreach (var start in new[] { "Qiwa", "Medical fitness", "Offer and contract" })
        {
            var t = Task(start);
            var text = All(t);
            text.Should().NotContainAny("day 30", "30 days", "اليوم 30", "30 يومًا", "before the Iqama", "قبل إصدار الإقامة", "before the person starts", "must be written", "Art.", "Article");
        }
        Task("Qiwa").Description.Should().Contain("Confirm the deadline that applies");
        Task("Qiwa").DescriptionAr.Should().Contain("تأكد من الموعد");
    }

    [Fact]
    public void NoTaskTextUsesTheVerifyHedgeOrAClaimTheAuditCouldNotSource()
    {
        foreach (var t in KsaStarterTemplate.Tasks)
        {
            var text = All(t);
            text.Should().NotContain("(verify").And.NotContain("verify,").And.NotContain("يُتحقق").And.NotContain("(يُتحقق");
            t.TitleAr.Should().NotBeNullOrWhiteSpace(); t.DescriptionAr.Should().NotBeNullOrWhiteSpace();
        }
        KsaStarterTemplate.Tasks.Should().HaveCount(15);
        KsaStarterTemplate.Tasks.Should().OnlyContain(t => !t.BlocksActivation);
    }

    [Fact]
    public void OffsetsStayConfigurableDefaults_TheTemplateKeepsEveryOne()
    {
        KsaStarterTemplate.Tasks.Should().OnlyContain(t => t.DueOffsetDays >= -30 && t.DueOffsetDays <= 100);
    }
}
