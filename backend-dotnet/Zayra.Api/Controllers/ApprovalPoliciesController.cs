using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/approval-policies")]
[Authorize(Roles = "Admin,HR Manager")]
public class ApprovalPoliciesController : ControllerBase
{
    private readonly ZayraDbContext _db;

    private static readonly string[] PolicyCsvHeaders =
        { "Code", "Name", "WorkflowType", "IsDefault", "IsActive" };

    private static readonly string[] StepCsvHeaders =
        { "PolicyCode", "StepOrder", "StepName", "ApproverType", "SpecificEmployeeCode", "IsFinalStep" };

    public ApprovalPoliciesController(ZayraDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var policies = await _db.ApprovalPolicies
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId.Value && p.IsActive && !p.IsDeleted)
            .OrderBy(p => p.WorkflowType).ThenBy(p => p.Name)
            .ToListAsync(ct);
        return Ok(policies);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var policy = await _db.ApprovalPolicies
            .AsNoTracking()
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId.Value && !p.IsDeleted, ct);
        return policy is null ? NotFound() : Ok(policy);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ApprovalPolicyCreateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.WorkflowType)) return BadRequest(new { message = "WorkflowType is required" });
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Name is required" });

        var policy = new ApprovalPolicy
        {
            TenantId = tenantId.Value,
            WorkflowType = req.WorkflowType.Trim(),
            Name = req.Name.Trim(),
            IsDefault = req.IsDefault,
            IsActive = req.IsActive
        };

        foreach (var step in req.Steps.OrderBy(s => s.StepOrder))
        {
            policy.Steps.Add(new ApprovalPolicyStep
            {
                TenantId = tenantId.Value,
                StepOrder = step.StepOrder,
                StepName = step.StepName.Trim(),
                ApproverType = step.ApproverType.Trim(),
                SpecificEmployeeId = step.SpecificEmployeeId,
                ApproverRole = step.ApproverRole?.Trim(),
                EscalationAfterHours = step.EscalationAfterHours,
                IsFinalStep = step.IsFinalStep
            });
        }

        _db.ApprovalPolicies.Add(policy);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = policy.Id }, policy);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ApprovalPolicyCreateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var policy = await _db.ApprovalPolicies
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId.Value && !p.IsDeleted, ct);
        if (policy is null) return NotFound();

        policy.WorkflowType = req.WorkflowType?.Trim() ?? policy.WorkflowType;
        policy.Name = req.Name?.Trim() ?? policy.Name;
        policy.IsDefault = req.IsDefault;
        policy.IsActive = req.IsActive;
        policy.UpdatedAtUtc = DateTime.UtcNow;

        // Replace steps
        _db.ApprovalPolicySteps.RemoveRange(policy.Steps);
        policy.Steps.Clear();
        foreach (var step in req.Steps.OrderBy(s => s.StepOrder))
        {
            policy.Steps.Add(new ApprovalPolicyStep
            {
                TenantId = tenantId.Value,
                PolicyId = policy.Id,
                StepOrder = step.StepOrder,
                StepName = step.StepName.Trim(),
                ApproverType = step.ApproverType.Trim(),
                SpecificEmployeeId = step.SpecificEmployeeId,
                ApproverRole = step.ApproverRole?.Trim(),
                EscalationAfterHours = step.EscalationAfterHours,
                IsFinalStep = step.IsFinalStep
            });
        }

        await _db.SaveChangesAsync(ct);
        return Ok(policy);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var policy = await _db.ApprovalPolicies.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId.Value && !p.IsDeleted, ct);
        if (policy is null) return NotFound();
        policy.IsActive = false;
        policy.IsDeleted = true;
        policy.DeletedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var policies = await _db.ApprovalPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId.Value && !p.IsDeleted)
            .Include(p => p.Steps)
            .OrderBy(p => p.WorkflowType).ToListAsync(ct);

        // Use a compound CSV: first policy rows, then step rows (with PolicyCode join key)
        var sb = new StringBuilder();
        sb.Append(string.Join(",", PolicyCsvHeaders)).Append('\n');
        foreach (var p in policies)
            sb.Append($"{Escape(p.Id.ToString())},{Escape(p.Name)},{Escape(p.WorkflowType)},{p.IsDefault},{p.IsActive}\n");

        sb.Append('\n');
        sb.Append(string.Join(",", StepCsvHeaders)).Append('\n');
        foreach (var p in policies)
            foreach (var s in p.Steps.OrderBy(x => x.StepOrder))
                sb.Append($"{Escape(p.Id.ToString())},{s.StepOrder},{Escape(s.StepName)},{Escape(s.ApproverType)},,{s.IsFinalStep}\n");

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"approval_policies_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    public IActionResult ImportTemplate()
    {
        var sb = new StringBuilder();
        sb.Append("# Policies\n");
        sb.Append(string.Join(",", PolicyCsvHeaders)).Append('\n');
        sb.Append("POL-001,Example Policy,Leave,true,true\n");
        sb.Append("\n# Steps (use PolicyCode from above)\n");
        sb.Append(string.Join(",", StepCsvHeaders)).Append('\n');
        sb.Append("POL-001,1,Example Step,Manager,,true\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "approval_policies_import_template.csv");
    }

    [HttpPost("import-preview")]
    public async Task<IActionResult> ImportPreview([FromBody] ApprovalPolicyImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunPreviewAsync(tenantId.Value, req.Csv, ct));
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ApprovalPolicyImportRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await RunCommitAsync(tenantId.Value, req.Csv, ct));
    }

    // The import CSV is structured: policy rows come first, then step rows
    // Steps are joined to policies by PolicyCode column
    private (List<Dictionary<string, string>> policyRows, List<Dictionary<string, string>> stepRows) SplitSections(string csv)
    {
        var lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var policyRows = new List<Dictionary<string, string>>();
        var stepRows = new List<Dictionary<string, string>>();

        // Find header lines — look for PolicyCsvHeaders and StepCsvHeaders
        int policyHeaderIdx = -1, stepHeaderIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim().ToUpperInvariant();
            if (trimmed.StartsWith("CODE,NAME,WORKFLOWTYPE")) policyHeaderIdx = i;
            if (trimmed.StartsWith("POLICYCODE,STEPORDER,STEPNAME")) stepHeaderIdx = i;
        }

        if (policyHeaderIdx >= 0)
        {
            var pHeaders = ParseCsvLine(lines[policyHeaderIdx]);
            for (int i = policyHeaderIdx + 1; i < lines.Count; i++)
            {
                if (i == stepHeaderIdx) break;
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var cells = ParseCsvLine(line);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < pHeaders.Count; c++) map[pHeaders[c]] = c < cells.Count ? cells[c] : string.Empty;
                policyRows.Add(map);
            }
        }

        if (stepHeaderIdx >= 0)
        {
            var sHeaders = ParseCsvLine(lines[stepHeaderIdx]);
            for (int i = stepHeaderIdx + 1; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var cells = ParseCsvLine(line);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < sHeaders.Count; c++) map[sHeaders[c]] = c < cells.Count ? cells[c] : string.Empty;
                stepRows.Add(map);
            }
        }

        // Fallback: if only one section, treat all as policy rows (simple single-section CSV)
        if (policyHeaderIdx < 0 && stepHeaderIdx < 0)
        {
            // Try parsing as policy CSV using Csv.Parse
            var parsed = Csv.Parse(csv);
            policyRows.AddRange(parsed);
        }

        return (policyRows, stepRows);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (ch == '"') inQuotes = false; else sb.Append(ch); }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { result.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString().Trim());
        return result;
    }

    private async Task<ImportPreviewResult> RunPreviewAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var (policyRows, stepRows) = SplitSections(csv);
        var existingByCode = await _db.ApprovalPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Name.ToUpperInvariant(), ct);

        var rowResults = new List<ImportRowResult>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int wouldCreate = 0, wouldUpdate = 0, wouldSkip = 0;

        for (int i = 0; i < policyRows.Count; i++)
        {
            var row = policyRows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            var wfType = row.GetValueOrDefault("WorkflowType", string.Empty).Trim();
            var errors = new List<string>(); var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required");
            if (string.IsNullOrWhiteSpace(wfType)) errors.Add("WorkflowType is required");
            if (!string.IsNullOrWhiteSpace(code) && seenCodes.Contains(code)) errors.Add($"Duplicate Code '{code}' in batch");
            if (errors.Count > 0) { wouldSkip++; rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Error, errors, warnings)); continue; }
            bool exists = !string.IsNullOrWhiteSpace(name) && existingByCode.ContainsKey(name.ToUpperInvariant());
            if (exists) wouldUpdate++; else wouldCreate++;
            seenCodes.Add(code);
            rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Ok, errors, warnings));
        }

        return new ImportPreviewResult(policyRows.Count, wouldCreate, wouldUpdate, wouldSkip, rowResults);
    }

    private async Task<ImportCommitResult> RunCommitAsync(Guid tenantId, string csv, CancellationToken ct)
    {
        var (policyRows, stepRows) = SplitSections(csv);

        var existingByName = await _db.ApprovalPolicies
            .Include(p => p.Steps)
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Name.ToUpperInvariant(), ct);

        var empByCode = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .ToDictionaryAsync(e => e.EmployeeCode.ToUpperInvariant(), e => e.Id, ct);

        var rowResults = new List<ImportRowResult>();
        var codeToPolicy = new Dictionary<string, ApprovalPolicy>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < policyRows.Count; i++)
        {
            var row = policyRows[i]; var rowNum = i + 2;
            var code = row.GetValueOrDefault("Code", string.Empty).Trim();
            var name = row.GetValueOrDefault("Name", string.Empty).Trim();
            var wfType = row.GetValueOrDefault("WorkflowType", string.Empty).Trim();
            var errors = new List<string>(); var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
            if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required");
            if (string.IsNullOrWhiteSpace(wfType)) errors.Add("WorkflowType is required");
            if (errors.Count > 0) { skipped++; rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Error, errors, warnings)); continue; }

            bool isDefault = string.Equals(row.GetValueOrDefault("IsDefault", "false"), "true", StringComparison.OrdinalIgnoreCase);
            bool isActive = !row.TryGetValue("IsActive", out var av) || !string.Equals(av, "false", StringComparison.OrdinalIgnoreCase);

            ApprovalPolicy policy;
            if (existingByName.TryGetValue(name.ToUpperInvariant(), out var existing))
            {
                existing.WorkflowType = wfType; existing.IsDefault = isDefault; existing.IsActive = isActive; existing.UpdatedAtUtc = DateTime.UtcNow;
                _db.ApprovalPolicySteps.RemoveRange(existing.Steps); existing.Steps.Clear();
                policy = existing; updated++;
            }
            else
            {
                policy = new ApprovalPolicy { TenantId = tenantId, WorkflowType = wfType, Name = name, IsDefault = isDefault, IsActive = isActive };
                _db.ApprovalPolicies.Add(policy); created++;
            }
            codeToPolicy[code] = policy;
            rowResults.Add(new ImportRowResult(rowNum, code, name, ImportRowStatus.Ok, errors, warnings));
        }

        // Attach steps
        foreach (var sRow in stepRows)
        {
            var pCode = sRow.GetValueOrDefault("PolicyCode", string.Empty).Trim();
            if (!codeToPolicy.TryGetValue(pCode, out var pol)) continue;
            int.TryParse(sRow.GetValueOrDefault("StepOrder", "1"), out var stepOrder);
            var approverType = sRow.GetValueOrDefault("ApproverType", "Manager").Trim();
            var empCode = sRow.GetValueOrDefault("SpecificEmployeeCode", string.Empty).Trim();
            int? specificEmpId = null;
            if (!string.IsNullOrWhiteSpace(empCode) && empByCode.TryGetValue(empCode.ToUpperInvariant(), out var eid))
                specificEmpId = eid;
            bool isFinal = string.Equals(sRow.GetValueOrDefault("IsFinalStep", "false"), "true", StringComparison.OrdinalIgnoreCase);
            pol.Steps.Add(new ApprovalPolicyStep
            {
                TenantId = tenantId, PolicyId = pol.Id, StepOrder = stepOrder,
                StepName = sRow.GetValueOrDefault("StepName", $"Step {stepOrder}").Trim(),
                ApproverType = approverType, SpecificEmployeeId = specificEmpId, IsFinalStep = isFinal
            });
        }

        await _db.SaveChangesAsync(ct);
        return new ImportCommitResult(policyRows.Count, created, updated, skipped, rowResults, Array.Empty<string>());
    }

    private static string Escape(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}

public record ApprovalPolicyStepRequest(
    int StepOrder,
    [Required] string StepName,
    [Required] string ApproverType,
    int? SpecificEmployeeId,
    string? ApproverRole,
    int? EscalationAfterHours,
    bool IsFinalStep = false);

public record ApprovalPolicyCreateRequest(
    string? WorkflowType,
    string? Name,
    bool IsDefault = false,
    bool IsActive = true,
    IReadOnlyList<ApprovalPolicyStepRequest>? Steps = null)
{
    public IReadOnlyList<ApprovalPolicyStepRequest> Steps { get; init; } = Steps ?? Array.Empty<ApprovalPolicyStepRequest>();
}

public record ApprovalPolicyImportRequest(string Csv);
