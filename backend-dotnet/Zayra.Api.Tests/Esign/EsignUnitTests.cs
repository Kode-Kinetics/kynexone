using System.Reflection;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using FluentAssertions;
using UglyToad.PdfPig;
using Zayra.Api.Application.Esign;
using Zayra.Api.Infrastructure.Documents.Esign;

namespace Zayra.Api.Tests.Esign;

/// <summary>P4 units: the refusal order, the typed-name rule, the consent catalogue, the hash chain, the certificate.</summary>
public class EsignUnitTests
{
    private static readonly Guid Employee = Guid.NewGuid();

    private static ClickSignFacts Ok(Action<Dictionary<string, object?>>? tweak = null)
    {
        var o = new Dictionary<string, object?>
        {
            ["stored"] = new string('a', 64), ["claimed"] = new string('a', 64), ["signer"] = Employee, ["employee"] = Employee, ["fresh"] = true,
            ["consent"] = ConsentCatalog.Current, ["typed"] = "Layla Hassan", ["tampered"] = false, ["already"] = false,
        };
        tweak?.Invoke(o);
        return new ClickSignFacts((string)o["stored"]!, (string?)o["claimed"], (Guid)o["signer"]!, (Guid)o["employee"]!, (bool)o["fresh"]!,
            (string?)o["consent"], (string?)o["typed"], ["Layla Hassan", "ليلى حسن"], (bool)o["tampered"]!, (bool)o["already"]!);
    }

    [Fact]
    public void ClickSign_AcceptsAWellFormedRequest() => ClickSignGuard.Evaluate(Ok()).Should().BeNull();

    [Fact]
    public void ClickSign_RejectsHashMismatch()
    {
        var r = ClickSignGuard.Evaluate(Ok(o => o["claimed"] = new string('b', 64)));
        r.Should().NotBeNull(); r!.Status.Should().Be(409); r.Code.Should().Be(EsignProblemCodes.DocumentChanged);
        ClickSignGuard.Evaluate(Ok(o => o["claimed"] = null))!.Code.Should().Be(EsignProblemCodes.DocumentChanged);
    }

    [Fact]
    public void ClickSign_RejectsSignerNotEmployee()
    {
        var r = ClickSignGuard.Evaluate(Ok(o => o["signer"] = Guid.NewGuid()));
        r!.Status.Should().Be(403); r.Code.Should().Be(EsignProblemCodes.SubjectCannotSignForEmployee);
    }

    [Fact]
    public void ClickSign_RejectsStaleStepUp()
    {
        var r = ClickSignGuard.Evaluate(Ok(o => o["fresh"] = false));
        r!.Status.Should().Be(401); r.Code.Should().Be(EsignProblemCodes.StepUpRequired);
    }

    [Fact]
    public void ClickSign_RejectsClientTime()
    {
        // The request type has no time property at all: nothing the browser sends can become the signing time.
        typeof(EsignSignBody).GetProperties().Select(p => p.Name.ToLowerInvariant()).Should().NotContain(n => n.Contains("time") || n.Contains("date") || n.Contains("signedat"));
        typeof(EsignSignBody).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["DocumentSha256", "ConsentVersion", "TypedName", "Language", "DeviceId"]);
    }

    [Fact]
    public void ClickSign_RefusalOrder_TamperedBeatsEverything_AndOutsiderNeverSeesAHashAnswer()
    {
        ClickSignGuard.Evaluate(Ok(o => { o["tampered"] = true; o["already"] = true; o["signer"] = Guid.NewGuid(); }))!.Code.Should().Be(EsignProblemCodes.DocumentTampered);
        ClickSignGuard.Evaluate(Ok(o => { o["already"] = true; o["signer"] = Guid.NewGuid(); }))!.Code.Should().Be(EsignProblemCodes.AlreadySigned);
        ClickSignGuard.Evaluate(Ok(o => { o["signer"] = Guid.NewGuid(); o["claimed"] = "x"; }))!.Code.Should().Be(EsignProblemCodes.SubjectCannotSignForEmployee);
        ClickSignGuard.Evaluate(Ok(o => o["consent"] = "ack-0"))!.Code.Should().Be(EsignProblemCodes.ConsentVersionStale);
    }

    [Theory]
    [InlineData("layla hassan", true)]
    [InlineData("  LAYLA   Hassan ", true)]
    [InlineData("Layla Hasan", false)]
    [InlineData("ليلى حسن", true)]
    [InlineData("لَيْلَى حَسَن", true)]          // harakat
    [InlineData("ليـــلى حســن", true)]            // tatweel
    [InlineData("ليلي  حسن", true)]               // yeh/alef maqsura + doubled space
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void TypedName_Normalisation_ArabicDiacriticsTatweelCaseSpaces(string typed, bool expected) =>
        TypedNameMatcher.Matches(typed, "Layla Hassan", "ليلى حسن").Should().Be(expected);

    [Fact]
    public void TypedName_Mismatch_Refused()
    {
        TypedNameMatcher.Matches("Someone Else", "Layla Hassan", "ليلى حسن").Should().BeFalse();
        TypedNameMatcher.Matches("Layla", "Layla Hassan", null).Should().BeFalse("a first name alone is not the record");
        TypedNameMatcher.Matches("anything", null, "").Should().BeFalse("no record name means nothing can match");
        var r = ClickSignGuard.Evaluate(Ok(o => o["typed"] = "Someone Else"));
        r!.Status.Should().Be(422); r.Code.Should().Be(EsignProblemCodes.TypedNameMismatch);
    }

    [Fact]
    public void ConsentCatalog_Ack1_StaysReadableAndUnchanged_ButIsNoLongerCurrent()
    {
        ConsentCatalog.Text("ack-1", "en").Should().Contain("not a qualified electronic signature");
        // Pinned: a wording change must bump the version, not silently change what an old signature agreed to.
        ConsentCatalog.TextSha256("ack-1").Should().Be("65d574df245e40a56d9d8deab51e71992b865f13be6b79f35ab18952c26b6b41");
        ConsentCatalog.IsKnown("ack-1").Should().BeFalse("only the current version can be signed");
    }

    [Fact]
    public void ConsentCatalog_Ack2_DisclosesIpAndDevice_AndNeverCallsTheAcknowledgementAQualifiedSignatureInArabic()  // CD64
    {
        ConsentCatalog.Current.Should().Be("ack-2");
        var en = ConsentCatalog.Text("ack-2", "en"); var ar = ConsentCatalog.Text("ack-2", "ar");
        en.Should().Contain("IP address").And.Contain("device").And.Contain("browser").And.Contain("acknowledgement");
        en.Should().Contain("not a qualified electronic signature", "the disclaimer is protective and stays");
        ar.Should().Contain("عنوان IP").And.Contain("المتصفح").And.Contain("إقرار").And.Contain("توقيعًا إلكترونيًا معتمدًا");
        ar.Should().NotContain("مؤهل");
        ConsentCatalog.TextSha256("ack-2").Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe(ConsentCatalog.TextSha256("ack-1"));
        ConsentCatalog.TextSha256("ack-2").Should().Be(ConsentCatalog.TextSha256("ack-2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsentCatalog.Text("ack-3", "en"));
        ConsentCatalog.Order("ar").Should().Equal("ar", "en");
        ConsentCatalog.Order("en").Should().Equal("en", "ar");
        ConsentCatalog.Order(null).Should().Equal("en", "ar");
    }

    [Fact]
    public void Certificate_StatesTheAcknowledgementInArabicWithoutTheEuTerm()  // CD64
    {
        AckCertificateRenderer.StatementAr.Should().Contain("معتمدًا").And.NotContain("مؤهل");
        AckCertificateRenderer.StatementEn.Should().Contain("acknowledgement");
    }

    private static AckEvidence Evidence(string typed = "Layla Hassan", Guid? witness = null) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), 42, Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('c', 64), typed,
        new DateTime(2026, 10, 27, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(7), "10.0.0.5", "UA/1", Employee, witness, new string('d', 64), EsignConstants.MethodEss);

    [Fact]
    public void ClickSign_RecordHashChainDetectsTamper()
    {
        var first = SignatureChain.ComputeHash(EsignConstants.ZeroHash, Evidence());
        first.Should().MatchRegex("^[0-9a-f]{64}$");
        SignatureChain.IsRecordValid(EsignConstants.ZeroHash, first, Evidence()).Should().BeTrue();
        // every covered field matters
        SignatureChain.IsRecordValid(EsignConstants.ZeroHash, first, Evidence("Layla Hassan.")).Should().BeFalse();
        SignatureChain.IsRecordValid(EsignConstants.ZeroHash, first, Evidence(witness: Guid.NewGuid())).Should().BeFalse();
        SignatureChain.IsRecordValid(EsignConstants.ZeroHash, first, Evidence() with { DocumentSha256 = new string('e', 64) }).Should().BeFalse();
        SignatureChain.IsRecordValid(EsignConstants.ZeroHash, first, Evidence() with { IpAddress = "10.0.0.6" }).Should().BeFalse();
        SignatureChain.IsRecordValid(EsignConstants.ZeroHash, first, Evidence() with { SignedAtUtc = Evidence().SignedAtUtc.AddSeconds(1) }).Should().BeFalse();
        SignatureChain.IsRecordValid(EsignConstants.ZeroHash, first, Evidence() with { Method = EsignConstants.MethodHrDevice }).Should().BeFalse();
        // the previous link matters: a different history gives a different hash
        SignatureChain.ComputeHash(new string('9', 64), Evidence()).Should().NotBe(first);
        // sub-microsecond ticks are not part of the evidence (the database cannot store them)
        SignatureChain.ComputeHash(EsignConstants.ZeroHash, Evidence() with { SignedAtUtc = Evidence().SignedAtUtc.AddTicks(-7) }).Should().Be(first);
        // length-prefixing: moving a character across a field boundary changes the hash
        SignatureChain.ComputeHash(EsignConstants.ZeroHash, Evidence() with { TypedName = "ab", IpAddress = "c" })
            .Should().NotBe(SignatureChain.ComputeHash(EsignConstants.ZeroHash, Evidence() with { TypedName = "a", IpAddress = "bc" }));
    }

    [Fact]
    public void VerificationCode_DerivedFromRecordHash()
    {
        var h = SignatureChain.ComputeHash(EsignConstants.ZeroHash, Evidence());
        var code = VerificationCode.From(h);
        code.Should().MatchRegex("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$");
        VerificationCode.From(h).Should().Be(code);
        VerificationCode.From(SignatureChain.ComputeHash(EsignConstants.ZeroHash, Evidence("Other Name"))).Should().NotBe(code);
    }

    [Fact]
    public void Certificate_Bilingual_ContainsHashNameTimeCode_AndNotQualifiedStatement()
    {
        var hash = SignatureChain.ComputeHash(EsignConstants.ZeroHash, Evidence());
        var data = new AckCertificateData("Saudi Co", "Layla Hassan", "E1", "Employment contract", "contract.pdf", new string('c', 64), "Layla Hassan",
            new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), EsignConstants.MethodEss, null, ConsentCatalog.Current,
            ConsentCatalog.Text(ConsentCatalog.Current, "en"), ConsentCatalog.Text(ConsentCatalog.Current, "ar"), hash, VerificationCode.From(hash));
        var pdf = AckCertificateRenderer.Render(data);
        pdf.Take(5).Should().Equal("%PDF-"u8.ToArray());
        using var doc = PdfDocument.Open(pdf);
        var text = string.Join("\n", doc.GetPages().Select(p => p.Text));
        var flat = Regex.Replace(text.Normalize(System.Text.NormalizationForm.FormKC), @"\s+", "");   // the PDF font ligates fi/fl
        flat.Should().Contain(new string('c', 64)).And.Contain(hash).And.Contain("LaylaHassan").And.Contain("2026-10-27").And.Contain(VerificationCode.From(hash));
        flat.Should().Contain("notaqualifiedelectronicsignature");
        text.Should().MatchRegex(@"[؀-ۿﭐ-﷿ﹰ-﻿]", "the Arabic half of the page is on it");
        // a second render of the same record is the same evidence
        PdfDocument.Open(AckCertificateRenderer.Render(data)).GetPages().Sum(p => p.Text.Length).Should().Be(doc.GetPages().Sum(p => p.Text.Length));
    }

    [Fact]
    public void AllP4ProblemTitleKeys_HaveEnAndAr()
    {
        var file = FindUp("frontend/src/i18n/onboardingP4.ts");
        var text = File.ReadAllText(file);
        var split = text.IndexOf("export const onboardingP4Ar", StringComparison.Ordinal);
        split.Should().BeGreaterThan(0, "the file exports an English and an Arabic block");
        var en = text[..split]; var ar = text[split..];
        foreach (var key in EsignProblemCodes.All.Select(c => EsignConstants.ProblemKeyPrefix + c))
        {
            en.Should().Contain($"'{key}'", $"EN: {key}");
            var m = Regex.Match(ar, "'" + Regex.Escape(key) + @"':\s*'([^']*)'");
            m.Success.Should().BeTrue($"AR: {key}");
            m.Groups[1].Value.Should().MatchRegex(@"\p{IsArabic}", $"AR text for {key} is Arabic");
        }
    }

    internal static string FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void EmployeeDocuments_KeepTheGeneralTenantEmployeeIndex_AlongsideTheSignPendingOne()
    {
        using var db = new Zayra.Api.Data.ZayraDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var indexes = db.Model.FindEntityType(typeof(Zayra.Api.Models.EmployeeDocument))!.GetIndexes().ToList();
        // Two indexes starting (tenant, employee): the unfiltered lookup index and the filtered sign-pending one. They must
        // never collapse into one, or the migration drops the lookup index every employee-documents query relies on.
        indexes.Where(i => i.Properties.Count >= 2 && i.Properties[0].Name == "TenantId" && i.Properties[1].Name == "EmployeeId" && i.GetFilter() is null)
            .Should().ContainSingle(i => i.Properties.Count == 2);
        indexes.Should().Contain(i => i.GetDatabaseName() == "ix_empdoc_sign_pending" && i.GetFilter() != null);
    }

    [Theory]
    [InlineData("CreateFunction")]
    [InlineData("CreateTrigger")]
    [InlineData("Drop")]
    [InlineData("RevokeFromAppRole")]
    [InlineData("GrantToAppRole")]
    public void P4Migration_CarriesTheSameAppendOnlySqlAsTheSharedConstant(string name)
    {
        var migration = typeof(Zayra.Api.Data.ZayraDbContext).Assembly.GetTypes().Single(x => x.Name == "OnboardingP4Acknowledgements");
        var inMigration = (string)migration.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var shared = (string)typeof(Zayra.Api.Data.Esign.AckAppendOnlySql).GetField(name)!.GetRawConstantValue()!;
        shared.Should().NotBeNullOrWhiteSpace();
        inMigration.Should().Be(shared);
    }
}
