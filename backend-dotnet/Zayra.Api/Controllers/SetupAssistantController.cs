using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Setup;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/setup-assistant")]
[Authorize(Roles = "Admin,HR Manager")]
public class SetupAssistantController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ISetupAssistantService _assistant;
    private readonly ILlmClient _llm;
    private readonly AiOptions _aiOptions;

    public SetupAssistantController(ZayraDbContext db, ISetupAssistantService assistant, ILlmClient llm, AiOptions aiOptions)
    {
        _db = db;
        _assistant = assistant;
        _llm = llm;
        _aiOptions = aiOptions;
    }

    /// <summary>
    /// Live diagnostics for the AI provider. Performs a tiny real completion and returns the
    /// actual outcome (provider, model, latency, and the raw upstream error on failure) so an
    /// admin can tell *why* the assistant fell back to the deterministic template — e.g. provider
    /// not configured, bad model name (404), auth failure (401), or a timeout. Never leaks the API key.
    /// </summary>
    [HttpGet("diagnostics")]
    public async Task<IActionResult> Diagnostics(CancellationToken ct)
    {
        var provider = _aiOptions.EffectiveProvider;
        var model = ResolveDiagnosticsModel(provider);
        var baseUrl = provider == "ollama" ? (string.IsNullOrWhiteSpace(_aiOptions.OllamaBaseUrl) ? "http://localhost:11434" : _aiOptions.OllamaBaseUrl) : null;

        if (!_aiOptions.IsLiveProviderConfigured)
        {
            return Ok(new
            {
                configured = false,
                provider,
                model,
                baseUrl,
                success = false,
                message = "No live AI provider is configured. Set AI_PROVIDER and the matching credentials " +
                          "(ANTHROPIC_API_KEY / OPENAI_API_KEY, or OLLAMA_BASE_URL + OLLAMA_API_KEY) in the backend environment.",
            });
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var req = new LlmRequest(provider, model,
                "You are a connectivity health check.", "Reply with the single word: OK.", 16);
            var res = await _llm.CompleteAsync(req, ct);
            sw.Stop();
            return Ok(new
            {
                configured = true,
                provider,
                model,
                baseUrl,
                success = res.Success,
                elapsedMs = sw.ElapsedMilliseconds,
                error = res.Success ? null : Truncate(res.Error, 800),
                sample = res.Success ? Truncate(res.Text, 120) : null,
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            // A TaskCanceledException here usually means the HttpClient timeout was hit.
            var hint = ex is TaskCanceledException or OperationCanceledException
                ? "Request timed out — the model may be slow/cold. Raise AI_HTTP_TIMEOUT_SECONDS or use a smaller model."
                : null;
            return Ok(new
            {
                configured = true,
                provider,
                model,
                baseUrl,
                success = false,
                elapsedMs = sw.ElapsedMilliseconds,
                error = Truncate(ex.Message, 800),
                hint,
            });
        }
    }

    private string ResolveDiagnosticsModel(string provider)
    {
        if (!string.IsNullOrWhiteSpace(_aiOptions.Model)) return _aiOptions.Model;
        return provider switch
        {
            "anthropic" => "claude-sonnet-4-20250514",
            "openai" => "gpt-5",
            "ollama" => "llama3.1",
            _ => string.Empty,
        };
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    /// <summary>Generate a proposed starter configuration — does NOT write anything.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] CompanyProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.CountryCode))
            return BadRequest(new { message = "Country is required." });
        var result = await _assistant.GenerateAsync(profile, ct);
        return Ok(result);
    }

    /// <summary>Persist an approved draft. Idempotent — existing codes are skipped, not duplicated.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplySetupRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var d = req.Draft;
        var counts = new Dictionary<string, int>();
        void Bump(string k, int n) => counts[k] = counts.GetValueOrDefault(k) + n;

        // ── Org: departments → grades → designations ─────────────────────────
        var existingDept = await _db.Departments.Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.Code.ToUpper(), x => x.Id, ct);
        foreach (var dep in d.Departments)
        {
            if (existingDept.ContainsKey(dep.Code.ToUpper())) continue;
            var entity = new Department { TenantId = tenantId, Code = dep.Code, NameEn = dep.NameEn, IsActive = true };
            _db.Departments.Add(entity);
            existingDept[dep.Code.ToUpper()] = entity.Id;
            Bump("departments", 1);
        }

        var existingGrade = await _db.Grades.Where(x => x.TenantId == tenantId)
            .Select(x => x.Code.ToUpper()).ToListAsync(ct);
        var gradeSet = existingGrade.ToHashSet();
        foreach (var g in d.Grades)
        {
            if (!gradeSet.Add(g.Code.ToUpper())) continue;
            _db.Grades.Add(new Grade { TenantId = tenantId, Code = g.Code, Name = g.Name, Band = g.Band, Level = g.Level, IsActive = true });
            Bump("grades", 1);
        }

        var existingDesig = (await _db.Designations.Where(x => x.TenantId == tenantId)
            .Select(x => x.Code).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
        foreach (var ds in d.Designations)
        {
            if (!existingDesig.Add(ds.Code.ToUpper())) continue;
            Guid? deptId = !string.IsNullOrWhiteSpace(ds.DepartmentCode) && existingDept.TryGetValue(ds.DepartmentCode.ToUpper(), out var id) ? id : null;
            _db.Designations.Add(new Designation
            {
                TenantId = tenantId, Code = ds.Code, TitleEn = ds.TitleEn, DepartmentId = deptId,
                JobLevel = ds.JobLevel, IsManagerRole = ds.IsManagerRole, LevelRank = ds.LevelRank, IsActive = true,
            });
            Bump("designations", 1);
        }

        // ── Leave types ──────────────────────────────────────────────────────
        var existingLeave = (await _db.LeaveTypes.Where(x => x.TenantId == tenantId)
            .Select(x => x.Code).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
        foreach (var lt in d.LeaveTypes)
        {
            if (!existingLeave.Add(lt.Code.ToUpper())) continue;
            _db.LeaveTypes.Add(new LeaveType
            {
                TenantId = tenantId, Code = lt.Code, NameEn = lt.NameEn, Category = lt.Category, IsPaid = lt.IsPaid,
                MaxConsecutiveDays = lt.MaxConsecutiveDays, RequiresAttachment = lt.RequiresAttachment, ColorCode = lt.ColorCode, IsActive = true,
            });
            Bump("leaveTypes", 1);
        }

        // ── Shifts ───────────────────────────────────────────────────────────
        var existingShift = (await _db.ShiftDefinitions.Where(x => x.TenantId == tenantId)
            .Select(x => x.Code).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
        foreach (var sh in d.Shifts)
        {
            if (!existingShift.Add(sh.Code.ToUpper())) continue;
            if (!TimeOnly.TryParse(sh.Start, out var start) || !TimeOnly.TryParse(sh.End, out var end)) continue;
            _db.ShiftDefinitions.Add(new ShiftDefinition
            {
                TenantId = tenantId, Code = sh.Code, Name = sh.Name, StartTime = start, EndTime = end,
                BreakMinutes = sh.BreakMinutes, Color = sh.Color, IsActive = true,
            });
            Bump("shifts", 1);
        }

        // ── Working week (localization upsert) ───────────────────────────────
        if (d.WorkingWeek is not null)
        {
            var loc = await _db.TenantLocalizationSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (loc is null) { loc = new TenantLocalizationSetting { TenantId = tenantId }; _db.TenantLocalizationSettings.Add(loc); }
            loc.WorkWeek = d.WorkingWeek.WorkWeek;
            loc.WeekStartDay = d.WorkingWeek.WeekStartDay;
            if (!string.IsNullOrWhiteSpace(req.CurrencyCode)) loc.CurrencyCode = req.CurrencyCode;
            Bump("workingWeek", 1);
        }

        // ── Pay components (under a default salary structure) ─────────────────
        if (d.PayComponents.Count > 0)
        {
            var structure = await _db.SalaryStructures.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == "DEFAULT", ct);
            if (structure is null)
            {
                structure = new SalaryStructure
                {
                    TenantId = tenantId, Code = "DEFAULT", Name = "Default Structure",
                    Currency = string.IsNullOrWhiteSpace(req.CurrencyCode) ? "AED" : req.CurrencyCode, IsActive = true,
                };
                _db.SalaryStructures.Add(structure);
            }
            var existingComp = (await _db.SalaryComponents.Where(x => x.TenantId == tenantId && x.SalaryStructureId == structure.Id)
                .Select(x => x.Code).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
            foreach (var pc in d.PayComponents)
            {
                if (!existingComp.Add(pc.Code.ToUpper())) continue;
                _db.SalaryComponents.Add(new SalaryComponent
                {
                    TenantId = tenantId, SalaryStructureId = structure.Id, Code = pc.Code, Name = pc.Name,
                    ComponentType = pc.ComponentType, CalculationType = pc.CalculationType,
                    Amount = pc.Amount, Percentage = pc.Percentage, IsTaxable = pc.IsTaxable, IsActive = true,
                });
                Bump("payComponents", 1);
            }
        }

        // ── Statutory rules ──────────────────────────────────────────────────
        if (d.StatutoryRules.Count > 0)
        {
            var country = (req.CountryCode ?? "").Trim().ToUpperInvariant();
            var existingRules = (await _db.StatutoryRules.Where(x => x.TenantId == tenantId)
                .Select(x => x.RuleKey).ToListAsync(ct)).Select(c => c.ToUpper()).ToHashSet();
            foreach (var r in d.StatutoryRules)
            {
                if (!existingRules.Add(r.RuleKey.ToUpper())) continue;
                _db.StatutoryRules.Add(new StatutoryRule
                {
                    TenantId = tenantId, CountryCode = country, Jurisdiction = $"{country}-default",
                    RuleKey = r.RuleKey, RuleValue = r.RuleValue, DataType = r.DataType, Description = r.Description,
                    EffectiveFrom = DateTime.UtcNow,
                });
                Bump("statutoryRules", 1);
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { applied = counts, total = counts.Values.Sum() });
    }

    private Guid GetTenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);
}

public record ApplySetupRequest(SetupDraft Draft, string CountryCode, string CurrencyCode);
