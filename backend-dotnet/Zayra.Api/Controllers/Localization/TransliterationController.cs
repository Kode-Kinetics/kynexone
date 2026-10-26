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
    [Zayra.Api.Infrastructure.Http.TenantUserRateLimit("transliterate")]
    public IActionResult Transliterate([FromBody] TransliterateRequest req)
    {
        // Authenticated + tenant-scoped (defence-in-depth; the fallback policy already requires auth).
        if (this.GetTenantId() is null) return Unauthorized();

        // Name suggestions are per-user and must never be cached by any shared proxy.
        Response.Headers["Cache-Control"] = "no-store";

        var text = req?.Text ?? string.Empty;
        var kind = string.IsNullOrWhiteSpace(req?.Kind) ? "generic" : req.Kind.Trim();
        var personName = string.Equals(kind, "person-name", StringComparison.OrdinalIgnoreCase);
        var organizationLabel = string.Equals(kind, "organization-label", StringComparison.OrdinalIgnoreCase);
        if (!personName && !organizationLabel && !string.Equals(kind, "generic", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "unsupported_transliteration_kind", message = "Choose generic, person-name, or organization-label suggestions." });
        if (personName)
        {
            if (text.Length > 200)
                return BadRequest(new { error = "person_name_too_long", message = "Personal names must be 200 characters or fewer." });
            var personSuggestion = _svc.ToArabicPersonName(text);
            return Ok(new { suggestion = personSuggestion, requiresManualEntry = string.IsNullOrEmpty(personSuggestion) });
        }
        if (organizationLabel)
        {
            if (text.Length > 200)
                return BadRequest(new { error = "organization_label_too_long", message = "Organization labels must be 200 characters or fewer." });
            var organizationSuggestion = _svc.ToArabicOrganizationLabel(text);
            return Ok(new { suggestion = organizationSuggestion, requiresManualEntry = string.IsNullOrEmpty(organizationSuggestion) });
        }

        if (text.Length > 200) text = text[..200]; // bound the work; names are short
        var suggestion = _svc.ToArabic(text);
        return Ok(new { suggestion, requiresManualEntry = false });
    }

    /// <summary>
    /// Organisation labels only (branch, department, title, grade names), for a wizard section that offers
    /// "Fill missing Arabic names (N)": one request answers every row. Same curated wording as the single call;
    /// "" means no confident suggestion. Person names are never batched.
    /// </summary>
    [HttpPost("batch")]
    [Zayra.Api.Infrastructure.Http.TenantUserRateLimit("transliterate-batch")]
    public IActionResult TransliterateBatch([FromBody] TransliterateBatchRequest req)
    {
        if (this.GetTenantId() is null) return Unauthorized();
        Response.Headers["Cache-Control"] = "no-store";
        if (!string.Equals(req?.Kind?.Trim(), "organization-label", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "unsupported_transliteration_kind", message = "Only organization-label suggestions can be requested together." });
        var texts = req!.Texts ?? [];
        if (texts.Count > MaxBatch)
            return BadRequest(new { error = "too_many_labels", message = $"Ask for at most {MaxBatch} labels at a time." });
        var suggestions = texts.Select(t =>
        {
            var text = t ?? string.Empty;
            var suggestion = text.Length > 200 ? string.Empty : _svc.ToArabicOrganizationLabel(text);
            return new { text, suggestion, requiresManualEntry = string.IsNullOrEmpty(suggestion) };
        }).ToList();
        return Ok(new { suggestions });
    }

    public const int MaxBatch = 100;
}

public record TransliterateRequest(string? Text, string? Target, string? Kind = null);
public record TransliterateBatchRequest(List<string?>? Texts, string? Target, string? Kind = null);
