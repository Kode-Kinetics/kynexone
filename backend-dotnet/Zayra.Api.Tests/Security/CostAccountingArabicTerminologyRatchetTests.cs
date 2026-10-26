using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Terminology ratchet for the Arabic that the cost-accounting release puts in server responses (Arabic review, binding
/// glossary; mirrors <c>frontend/unit/costAccountingTerminology.spec.ts</c>). It fails on a banned term in an Arabic
/// line of the files that carry cost-accounting messages. Comment lines are ignored. Files that mix cost accounting with
/// other features are scanned only on the lines that talk about cost centres or the cost classification.
/// </summary>
public class CostAccountingArabicTerminologyRatchetTests
{
    private const string Ar = "ء-ي";

    // Whole-file scans: every Arabic line in these files belongs to cost accounting.
    private static readonly string[] WholeFiles =
    [
        "Application/Organization/CostCentreRuleException.cs",
        "Infrastructure/Finance/GlCostCentreRuleFilter.cs",
        "Infrastructure/Finance/CostAccountingGuards.cs",
        "Infrastructure/Payroll/PayrollCostStamper.cs",
        "Controllers/FinanceGlController.CostAccounting.cs",
    ];

    // Mixed files: only Arabic lines that mention cost centres / the functional class / the posting rule.
    private static readonly string[] MixedFiles =
    [
        "Controllers/FinanceGlController.cs",
        "Controllers/PayrollController.cs",
        "Controllers/DepartmentsController.cs",
    ];

    private static readonly Regex MixedLine = new("تكلفة|التصنيف|قاعدة الترحيل|قاعدة مركز", RegexOptions.Compiled);
    private static readonly Regex ArabicLine = new($"[{Ar}]", RegexOptions.Compiled);

    private static readonly (string Name, Regex Re)[] Banned =
    [
        ("مسيرة (use مسير الرواتب)", new("مسيرة|المسيرة", RegexOptions.Compiled)),
        ("الأب / أبًا for a parent (use المركز الأعلى)", new($"(^|[^{Ar}])[وبلف]?الأب(?![{Ar}])|أبًا", RegexOptions.Compiled)),
        ("تخصيص (use مركز تكلفة استثنائي للموظف)", new("تخصيص", RegexOptions.Compiled)),
        ("slash plural (use a list form)", new($"[{Ar}]+/[{Ar}]+", RegexOptions.Compiled)),
        ("bare المالية (use الإدارة المالية)", new("(?<!إدارة )(?<!المراجعة )المالية", RegexOptions.Compiled)),
        ("خانات / خانتين / الخانة (use منازل عشرية)", new("خانات|خانتين|الخانة", RegexOptions.Compiled)),
        ("روابط / رابط / يمسح / مسح (use ارتباطات / يزيل)", new("روابط|رابط|يمسح|سيمسح|مسح", RegexOptions.Compiled)),
        ("بند (use سطر)", new($"(^|[^{Ar}])[وبلكف]?(ال)?بنو?د", RegexOptions.Compiled)),
        ("حساب أرباح وخسائر (use حسابات قائمة الدخل)", new("حساب أرباح وخسائر", RegexOptions.Compiled)),
        ("bare القفل (use قفل مسير الرواتب)", new("القفل التالي|أعد القفل|يتوقف القفل|بالقفل", RegexOptions.Compiled)),
        ("دفتر اليومية (use قيد اليومية)", new("دفتر اليومية", RegexOptions.Compiled)),
        ("المستأجر (use the company)", new("المستأجر", RegexOptions.Compiled)),
        ("الخلايا الصغيرة / خلايا صغيرة (use مراكز تكلفة بعدد موظفين قليل)", new("الخلايا الصغيرة|خلايا صغيرة", RegexOptions.Compiled)),
        ("short التصنيف الوظيفي (use التصنيف الوظيفي للتكلفة)", new("التصنيف الوظيفي(?! للتكلفة)", RegexOptions.Compiled)),
        ("قابل للرسملة (feminine: قابلة للرسملة)", new("قابل للرسملة", RegexOptions.Compiled)),
        ("قيمة ERP (use رمز ERP)", new("قيمة ERP|قيمة مركز التكلفة في نظام", RegexOptions.Compiled)),
        ("Latin enum names joined by أو (use «إلزامي (Required)»)", new("Required أو|Optional أو|SELLING أو|COGS أو", RegexOptions.Compiled)),
        ("colloquial ستقوم بـ / يقوم بـ", new("ستقوم|يقوم بقفل|يقوم بذلك", RegexOptions.Compiled)),
        ("كلاهما after a preposition (use بكليهما)", new("وليس كلاهما", RegexOptions.Compiled)),
        ("رحّله على (use رحِّله إلى)", new("رحّله على", RegexOptions.Compiled)),
        ("حلقة in a tree (use دورة مغلقة)", new("حلقة", RegexOptions.Compiled)),
        ("sentence-final بعد (use حاليًا)", new("بعد(?=[\\.،\"])", RegexOptions.Compiled)),
        ("vague أعد المعالجة والاعتماد", new("أعد المعالجة والاعتماد|انتظر تعديل", RegexOptions.Compiled)),
        ("تزيد على (use يتجاوز)", new("تزيد شجرة|شجرة مراكز", RegexOptions.Compiled)),
    ];

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Zayra.Api.csproj"))) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}.");
    }

    private static IEnumerable<string> ArabicLines(string relative, bool mixed)
    {
        var path = Path.Combine(ResolveApiRoot(), relative);
        File.Exists(path).Should().BeTrue($"{relative} is on the cost-accounting Arabic scan list");
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.TrimStart();
            if (t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("/*")) continue;
            if (!ArabicLine.IsMatch(line)) continue;
            if (mixed && !MixedLine.IsMatch(line)) continue;
            yield return line;
        }
    }

    public static IEnumerable<object[]> Targets() =>
        WholeFiles.Select(f => new object[] { f, false }).Concat(MixedFiles.Select(f => new object[] { f, true }));

    [Theory]
    [MemberData(nameof(Targets))]
    public void No_banned_term_in_cost_accounting_server_arabic(string relative, bool mixed)
    {
        var hits = new List<string>();
        foreach (var line in ArabicLines(relative, mixed))
            foreach (var (name, re) in Banned)
                if (re.IsMatch(line)) hits.Add($"{name} :: {line.Trim()[..Math.Min(140, line.Trim().Length)]}");
        hits.Should().BeEmpty();
    }

    [Fact]
    public void The_scan_reads_real_arabic()
    {
        ArabicLines("Application/Organization/CostCentreRuleException.cs", false).Count().Should().BeGreaterThan(8);
    }
}
