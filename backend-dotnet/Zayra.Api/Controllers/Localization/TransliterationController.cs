using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Localization;

namespace Zayra.Api.Controllers.Localization;

/// <summary>
/// P0-6: sanctioned, in-house replacement for the removed MyMemory keystroke call.
/// Authenticated + tenant-scoped; backed by a fully offline transliteration service so employee
/// and company legal names never leave our infrastructure. The frontend calls this only on an
/// explicit user action (a "Suggest (AR)" button), not on keystroke.
/// </summary>
[ApiController]
[Route("api/localization/transliterate")]
[Authorize]
public class TransliterationController : ControllerBase
{
    private readonly ITransliterationService _svc;
    public TransliterationController(ITransliterationService svc) => _svc = svc;

    [HttpPost]
    public IActionResult Transliterate([FromBody] TransliterateRequest req)
    {
        // Authenticated + tenant-scoped (defence-in-depth; the fallback policy already requires auth).
        if (this.GetTenantId() is null) return Unauthorized();

        // Name suggestions are per-user and must never be cached by any shared proxy.
        Response.Headers["Cache-Control"] = "no-store";

        var text = req?.Text ?? string.Empty;
        var kind = string.IsNullOrWhiteSpace(req?.Kind) ? "generic" : req.Kind.Trim();
        var personName = string.Equals(kind, "person-name", StringComparison.OrdinalIgnoreCase);
        if (!personName && !string.Equals(kind, "generic", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "unsupported_transliteration_kind", message = "Choose generic or person-name suggestions." });
        if (personName)
        {
            if (text.Length > 200)
                return BadRequest(new { error = "person_name_too_long", message = "Personal names must be 200 characters or fewer." });
            var personSuggestion = _svc.ToArabicPersonName(text);
            return Ok(new { suggestion = personSuggestion, requiresManualEntry = string.IsNullOrEmpty(personSuggestion) });
        }

        if (text.Length > 200) text = text[..200]; // bound the work; names are short
        var suggestion = _svc.ToArabic(text);
        return Ok(new { suggestion, requiresManualEntry = false });
    }
}

public record TransliterateRequest(string? Text, string? Target, string? Kind = null);
