using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers.Localization;
using Zayra.Api.Infrastructure.Localization;

namespace Zayra.Api.Tests;

public class PersonNameTransliterationTests
{
    private readonly TransliterationService _service = new();

    [Theory]
    [InlineData("Muhammad Umar", "محمد عمر")]
    [InlineData("Mohammed Omar", "محمد عمر")]
    [InlineData("Mohammad Umar", "محمد عمر")]
    [InlineData("  MUHAMMAD\t\nUMAR  ", "محمد عمر")]
    [InlineData("Muhammad-Umar", "محمد عمر")]
    [InlineData("Muhammad\u2011Umar", "محمد عمر")]
    [InlineData("Ahmad Ali", "أحمد علي")]
    [InlineData("Fatimah Maryam", "فاطمة مريم")]
    [InlineData("Khalid Yusuf", "خالد يوسف")]
    public void PersonModeUsesConventionalReadingsWithNormalizedSeparators(string source, string expected)
    {
        _service.ToArabicPersonName(source).Should().Be(expected);
        _service.ToArabicPersonName(source).Should().Be(expected, "the offline suggestion is deterministic");
    }

    [Theory]
    [InlineData("Abdul Rahman", "عبد الرحمن")]
    [InlineData("Abd al-Rahman Umar", "عبد الرحمن عمر")]
    [InlineData("Abdurrahman Ali", "عبد الرحمن علي")]
    [InlineData("Abdul-Rehman", "عبد الرحمن")]
    [InlineData("Abd El Aziz", "عبد العزيز")]
    [InlineData("Abdulaziz", "عبد العزيز")]
    [InlineData("Abdullah", "عبد الله")]
    [InlineData("Abd Allah", "عبد الله")]
    [InlineData("Noor-ud-Din", "نور الدين")]
    [InlineData("Salah Al Din", "صلاح الدين")]
    public void CompoundNamesRequireExplicitWholeNameAliases(string source, string expected)
        => _service.ToArabicPersonName(source).Should().Be(expected);

    [Theory]
    [InlineData("Muhammad Unknown")]
    [InlineData("Unknown Umar")]
    [InlineData("Muhammad Xyz Umar")]
    [InlineData("Md Umar")]
    [InlineData("M Umar")]
    [InlineData("M. Umar")]
    [InlineData("Abdul")]
    [InlineData("Abd")]
    [InlineData("Al")]
    [InlineData("Bin")]
    [InlineData("Abdul Unknown")]
    [InlineData("Abdul Rahman Unknown")]
    [InlineData("Sana")]
    [InlineData("Hana")]
    [InlineData("Amina")]
    [InlineData("Said")]
    [InlineData("Salim")]
    [InlineData("Rashid")]
    [InlineData("Hassan")]
    public void UnsupportedOrAmbiguousPartRefusesTheEntireName(string source)
        => _service.ToArabicPersonName(source).Should().BeEmpty("a partial name or guessed expansion is not a safe suggestion");

    [Theory]
    [InlineData("محمد عمر", "محمد عمر")]
    [InlineData("مُحَمَّد عُمَر", "مُحَمَّد عُمَر")]
    [InlineData("Muhammad عمر", "محمد عمر")]
    [InlineData("محمد Umar", "محمد عمر")]
    [InlineData("عبد الرحمن", "عبد الرحمن")]
    public void SuppliedArabicWordsArePreservedAlongsideSupportedLatinWords(string source, string expected)
        => _service.ToArabicPersonName(source).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("123")]
    [InlineData("محمد ١٢٣")]
    [InlineData("Muhammad, Umar")]
    [InlineData("O'Connor")]
    [InlineData("Muhammad/عمر")]
    [InlineData("Muحammad Umar")]
    [InlineData("محمد Unknown")]
    [InlineData("محمد\u202eعمر")]
    [InlineData("محمد\u061cعمر")]
    [InlineData("محمد\u200dعمر")]
    [InlineData("<script>Ali</script>")]
    [InlineData("محمد 😀")]
    [InlineData("َ")]
    [InlineData("ـ")]
    [InlineData("،")]
    [InlineData("-Muhammad")]
    [InlineData("Muhammad-")]
    [InlineData("Muhammad--Umar")]
    [InlineData("Muhammad - - Umar")]
    public void UnsupportedCharactersAndMalformedSeparatorsRequireManualEntry(string source)
        => _service.ToArabicPersonName(source).Should().BeEmpty();

    [Fact]
    public void ExistingGenericPhoneticBehaviorIsUnchanged()
    {
        _service.ToArabic("Ali 42").Should().Be("الي 42");
        _service.ToArabic("Muhammad Umar").Should().NotBe(_service.ToArabicPersonName("Muhammad Umar"));
        _service.ToArabic("Sh Kh").Should().Be("ش خ");
    }

    private static TransliterationController Controller(bool withTenant = true)
    {
        var http = new DefaultHttpContext();
        if (withTenant)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", Guid.NewGuid().ToString())], "Test"));
        return new TransliterationController(new TransliterationService())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [Theory]
    [InlineData("Muhammad Umar", "محمد عمر", false)]
    [InlineData("Muhammad Unknown", "", true)]
    [InlineData("Md Umar", "", true)]
    [InlineData("", "", true)]
    public void PersonModeReturnsExplicitManualEntrySignal(string source, string expected, bool requiresManualEntry)
    {
        var controller = Controller();
        var result = controller.Transliterate(new TransliterateRequest(source, "ar", "person-name"))
            .Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(result.Value);
        body.GetProperty("suggestion").GetString().Should().Be(expected);
        body.GetProperty("requiresManualEntry").GetBoolean().Should().Be(requiresManualEntry);
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("generic")]
    public void GenericCallersRetainTheirBehaviorAndExistingLengthBound(string? kind)
    {
        var source = "Ali 42".PadRight(201, ' ');
        var controller = Controller();
        var result = controller.Transliterate(new TransliterateRequest(source, "ar", kind))
            .Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(result.Value);
        body.GetProperty("suggestion").GetString().Should().Be(_service.ToArabic(source[..200]));
        body.GetProperty("requiresManualEntry").GetBoolean().Should().BeFalse();
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public void OverlongPersonNameIsRejectedWithoutSuggestingATruncatedPrefix()
    {
        var atLimit = string.Join(' ', Enumerable.Repeat("Ali", 50)) + " ";
        atLimit.Length.Should().Be(200);
        Controller().Transliterate(new TransliterateRequest(atLimit, "ar", "person-name")).Should().BeOfType<OkObjectResult>();

        var source = atLimit + "X";
        _service.ToArabicPersonName(source).Should().BeEmpty();
        var controller = Controller();
        var result = controller.Transliterate(new TransliterateRequest(source, "ar", "person-name"))
            .Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString().Should().Be("person_name_too_long");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public void UnknownKindDoesNotFallBackToGenericNameGuessing()
    {
        var controller = Controller();
        var result = controller.Transliterate(new TransliterateRequest("Muhammad Umar", "ar", "person"))
            .Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString().Should().Be("unsupported_transliteration_kind");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("person-name")]
    public void BothModesRetainAuthenticatedTenantBoundary(string? kind)
    {
        typeof(TransliterationController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Should().ContainSingle();
        typeof(TransliterationController).GetCustomAttributes(typeof(AllowAnonymousAttribute), true).Should().BeEmpty();
        Controller(withTenant: false).Transliterate(new TransliterateRequest("Muhammad Umar", "ar", kind))
            .Should().BeOfType<UnauthorizedResult>();
    }
}
