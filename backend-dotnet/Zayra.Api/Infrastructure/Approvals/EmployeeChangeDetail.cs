using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Approvals;

/// <summary>
/// What an employee change will do, field by field: the value on file now and the value it proposes. Built for
/// the person deciding it (the Approval Center card and the mobile approvals sheet), who until this saw only
/// the field NAMES ("IBAN, date of birth") and had to approve a value they could not see.
///
/// <para><b>Masking.</b> Identity and banking numbers (IBAN, passport, Iqama, national ID, GOSI reference…) show
/// their last 4 characters only, on both sides, for every viewer — enough to match against the document the
/// employee supplied. Pay (salary, the salary package) is shown only to a viewer holding employees.sensitive or
/// payroll.read, else masked. Medical and disciplinary text needs employees.sensitive. Everything else (dates,
/// names, codes) is shown as is.</para>
///
/// <para><b>"Current" is the value on file when the detail is read</b>, which is exactly what approving would
/// overwrite. It is not sealed at submission: what matters to the decision is the value being replaced, and the
/// change's own fields can only move in between through a direct edit by someone holding that access.</para>
/// </summary>
public static class EmployeeChangeDetail
{
    /// <summary>Medical and disciplinary text: shown only to employees.sensitive holders.</summary>
    private static readonly HashSet<string> RestrictedKeys = new(StringComparer.OrdinalIgnoreCase)
        { "medicalInformation", "disciplinaryRecords" };

    /// <summary>Social-insurance enrolment numbers the name classifier does not recognise on its own.</summary>
    private static readonly HashSet<string> ExtraIdentifierKeys = new(StringComparer.OrdinalIgnoreCase)
        { "socialInsuranceReference", "gosiReference", "qiwaContractNumber" };

    public const string MaskedPay = "Hidden — needs pay access";
    public const string MaskedRestricted = "Hidden — needs sensitive-data access";

    public static bool IsIdentifier(string key) =>
        ExtraIdentifierKeys.Contains(key) || SensitiveFieldClassifier.IsIdentifierName(key);

    public static bool IsPay(string key) =>
        string.Equals(key, EmployeeSalaryBreakdownChanges.Key, StringComparison.OrdinalIgnoreCase) || SensitiveFieldClassifier.IsSalaryName(key);

    public static async Task<EmployeeChangeDetailDto?> BuildAsync(ZayraDbContext db, ApprovalRequest approval, EmployeeChangeRequest change,
        IReadOnlyCollection<string> viewerPermissions, CancellationToken ct)
    {
        // Company-filtered on purpose: an employee outside the viewer's legal-entity scope yields nothing.
        var employee = await db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == change.TenantId && e.Id == change.EmployeeId && !e.IsDeleted, ct);
        if (employee is null) return null;

        Dictionary<string, JsonElement> proposed;
        try { proposed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson) ?? new(); }
        catch (JsonException) { proposed = new(); }

        var keys = proposed.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var profile = await db.EmployeePayrollProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == change.TenantId && x.EmployeeId == employee.Id && !x.IsDeleted, ct);
        var current = await EmployeeChangeBaseline.CaptureAsync(db, employee, profile,
            keys.Where(k => k != EmployeeSalaryBreakdownChanges.Key && EmployeeChangeBaseline.CanRead(k)), ct);

        var canSeePay = Has(viewerPermissions, "employees.sensitive") || Has(viewerPermissions, "payroll.read");
        var canSeeRestricted = Has(viewerPermissions, "employees.sensitive");
        var fields = new List<EmployeeChangeFieldDto>(keys.Count);
        foreach (var key in keys)
        {
            string? before;
            string? after;
            if (string.Equals(key, EmployeeSalaryBreakdownChanges.Key, StringComparison.OrdinalIgnoreCase))
            {
                // The package as a person approving it must see it: the six slots AND the customer-defined cash allowance lines, with a gross that counts
                // them, on both sides. (Masked below under the same rule as every other pay field, so the lines are as hidden as the slots.)
                var latest = await EmployeeSalaryBreakdownChanges.ReadLatestAsync(db, employee, ct);
                (before, after) = await FormatPackagesAsync(db, change.TenantId, employee, latest, proposed[key], ct);
            }
            else
            {
                before = EmployeeChangeBaseline.CanRead(key) ? EmployeeChangeBaseline.ValueOf(current, key) : null;
                after = Display(proposed[key]);
            }

            var masked = false;
            if (IsIdentifier(key))
            {
                before = MaskOrEmpty(before);
                after = MaskOrEmpty(after);
                masked = true;
            }
            else if (IsPay(key) && !canSeePay)
            {
                before = string.IsNullOrEmpty(before) ? before : MaskedPay;
                after = string.IsNullOrEmpty(after) ? after : MaskedPay;
                masked = true;
            }
            else if (RestrictedKeys.Contains(key) && !canSeeRestricted)
            {
                before = string.IsNullOrEmpty(before) ? before : MaskedRestricted;
                after = string.IsNullOrEmpty(after) ? after : MaskedRestricted;
                masked = true;
            }

            fields.Add(new EmployeeChangeFieldDto(key, Label(key), NullIfEmpty(before), NullIfEmpty(after), masked));
        }

        return new EmployeeChangeDetailDto(approval.Id, change.Id, employee.Id, employee.EmployeeCode, employee.FullName,
            change.EffectiveDate, change.Status, fields);
    }

    /// <summary>"bankIban" → "IBAN", "dateOfBirth" → "Date of birth" (the same words as the queue's summary).</summary>
    public static string Label(string key)
    {
        if (string.Equals(key, EmployeeSalaryBreakdownChanges.Key, StringComparison.OrdinalIgnoreCase)) return "Salary package";
        var label = Zayra.Api.Controllers.DashboardController.FormatChangedFields(key) ?? key;
        return label.Length == 0 ? key : char.ToUpperInvariant(label[0]) + label[1..];
    }

    private static string MaskOrEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : SensitiveValueMask.MaskId(value);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool Has(IReadOnlyCollection<string> permissions, string key) =>
        permissions.Any(p => p.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static string? Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        _ => value.GetRawText(),
    };

    /// <summary>A line as the card prints it: its names (when the definition is known), basis, rate and the amount it comes to.</summary>
    private sealed record CardLine(string Code, string? NameEn, string? NameAr, string Basis, decimal? Rate, decimal Amount);

    /// <summary>
    /// The "on file" and "proposed" texts of a salary package. A proposal that states <c>allowances</c> shows exactly those lines. A proposal that does
    /// not mention them (a merit-style package) keeps the lines on the salary record in force on its date, so the card says they are carried forward
    /// and shows a percentage line re-worked on the proposed basic, with the gross that follows. An unreadable proposal is shown as it was sent.
    /// </summary>
    private static async Task<(string? Before, string? After)> FormatPackagesAsync(
        ZayraDbContext db, Guid tenantId, Employee employee, EmployeeSalaryBreakdownRequest? latest, JsonElement proposedValue, CancellationToken ct)
    {
        EmployeeSalaryBreakdownRequest? package = null;
        try { package = EmployeeSalaryBreakdownChanges.Read(proposedValue); }
        catch (InvalidOperationException) { /* shown as sent, below */ }

        var carried = false;
        List<(string? Code, string? Basis, decimal? Rate, decimal? Amount)> proposedLines = [];
        if (package is not null)
        {
            if (package.Allowances is not null)
                proposedLines = package.Allowances.Where(l => l is not null).Select(l => (l.Code, l.Basis, l.Rate, l.Amount)).ToList();
            else if (package.EffectiveDate is { } date)
            {
                var (_, inForce) = await Zayra.Api.Infrastructure.Payroll.SalaryAllowanceLines.InForceAsync(db, tenantId, employee.Id, date, ct);
                carried = inForce.Count > 0;
                proposedLines = inForce.Select(l => ((string?)l.Code, (string?)l.Basis, l.Rate, (decimal?)l.Amount)).ToList();
            }
        }
        var onFileLines = (latest?.Allowances ?? []).Where(l => l is not null).Select(l => (l.Code, l.Basis, l.Rate, l.Amount)).ToList();

        var codes = onFileLines.Concat(proposedLines).Select(l => (l.Code ?? string.Empty).Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        var named = codes.Count == 0 ? new Dictionary<string, (string En, string Ar)>(StringComparer.Ordinal)
            : (await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.PayComponents, tenantId, "The approval card names each allowance line, removed definitions included.")
                    .AsNoTracking().Where(c => c.CompanyId == null && c.ComponentType == PayComponentTypes.Earning && codes.Contains(c.Code))
                    .Select(c => new { c.Code, c.NameEn, c.NameAr }).ToListAsync(ct))
                .GroupBy(c => c.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (En: g.First().NameEn, Ar: g.First().NameAr), StringComparer.Ordinal);

        List<CardLine> Card(IEnumerable<(string? Code, string? Basis, decimal? Rate, decimal? Amount)> lines, decimal basic) => lines
            .Select(l =>
            {
                var code = (l.Code ?? string.Empty).Trim().ToUpperInvariant();
                var percent = string.Equals(l.Basis?.Trim(), Zayra.Api.Models.AllowanceBases.PercentOfBasic, StringComparison.OrdinalIgnoreCase);
                var amount = percent ? Zayra.Api.Infrastructure.Payroll.SalaryAllowanceLines.PercentAmount(l.Rate.GetValueOrDefault(), basic) : l.Amount.GetValueOrDefault();
                named.TryGetValue(code, out var n);
                return new CardLine(code.Length == 0 ? "?" : code, n.En, n.Ar, percent ? Zayra.Api.Models.AllowanceBases.PercentOfBasic : Zayra.Api.Models.AllowanceBases.Amount, percent ? l.Rate : null, amount);
            })
            .OrderBy(l => l.Code, StringComparer.Ordinal).ToList();

        var before = FormatPackage(latest, employee.Salary, Card(onFileLines, latest?.BasicSalary.GetValueOrDefault() ?? 0m), carried: false);
        var proposedCard = package is null ? [] : Card(proposedLines, package.BasicSalary.GetValueOrDefault());
        var after = package is null
            ? Display(proposedValue)
            : FormatPackage(package, null, proposedCard, carried);
        // The approver must see when this lowers the employee's pay, and the written agreement it cites.
        if (package is not null && after is not null && package.EffectiveDate is { } effective && package.BasicSalary is { } basic)
        {
            var reduction = await Zayra.Api.Infrastructure.Payroll.SalaryReductions.EvaluateAsync(db, tenantId, employee.Id, effective,
                new Zayra.Api.Infrastructure.Payroll.PaySlots(basic, package.HousingAllowance.GetValueOrDefault(), package.TransportAllowance.GetValueOrDefault(),
                    package.FoodAllowance.GetValueOrDefault(), package.MobileAllowance.GetValueOrDefault(), package.OtherAllowance.GetValueOrDefault()),
                proposedCard.Select(l => new Zayra.Api.Infrastructure.Payroll.ResolvedAllowanceLine(l.Code, l.Basis, l.Rate, l.Amount)).ToList(), ct);
            if (reduction.IsReduction)
            {
                var reference = Zayra.Api.Infrastructure.Payroll.SalaryReductions.CleanReference(package.ReductionConsentReference);
                var down = string.Join(", ", reduction.Items.Select(i => i.To == 0m ? $"{i.Item} removed" : $"{i.Item} {Amount(i.From)} to {Amount(i.To)}"));
                after += $" [lowers the employee's pay: {down}; written agreement: {(reference is null ? "none given" : reference)}]";
            }
        }
        return (before, after);
    }

    private static string? FormatPackage(EmployeeSalaryBreakdownRequest? package, decimal? fallbackGross, IReadOnlyList<CardLine>? lines = null, bool carried = false)
    {
        if (package is null)
            return fallbackGross is > 0m ? $"Gross {Amount(fallbackGross.Value)}" : null;
        var parts = new List<string> { $"Basic {Amount(package.BasicSalary)}" };
        void Add(string name, decimal? amount) { if (amount is > 0m) parts.Add($"{name} {Amount(amount)}"); }
        Add("housing", package.HousingAllowance);
        Add("transport", package.TransportAllowance);
        Add("food", package.FoodAllowance);
        Add("mobile", package.MobileAllowance);
        Add("other", package.OtherAllowance);
        foreach (var line in lines ?? [])
            parts.Add($"allowance {LineName(line)} {(line.Basis == Zayra.Api.Models.AllowanceBases.PercentOfBasic ? $"{(line.Rate.GetValueOrDefault() * 100m).ToString("0.##", CultureInfo.InvariantCulture)}% of basic = " : string.Empty)}{Amount(line.Amount)}");
        Add("fixed deduction", package.FixedDeduction);
        var currency = string.IsNullOrWhiteSpace(package.Currency) ? string.Empty : " " + package.Currency.Trim();
        var from = package.EffectiveDate is { } date ? $", from {date:yyyy-MM-dd}" : string.Empty;
        var note = carried && lines is { Count: > 0 } ? " [the current allowances are carried forward; a percentage is worked out on the new basic]" : string.Empty;
        // The gross is the whole pay: the slots and every allowance line.
        var gross = EmployeeSalaryBreakdownChanges.Gross(package) + (lines?.Sum(l => l.Amount) ?? 0m);
        return $"{string.Join(", ", parts)}{note} (gross {Amount(gross)}{currency}){from}";
    }

    /// <summary>"Car allowance / سيارة (ALW_CAR)": the English name, the Arabic name when there is one that differs, and the code (the only thing a removed definition leaves).</summary>
    private static string LineName(CardLine line)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(line.NameEn)) names.Add(line.NameEn.Trim());
        if (!string.IsNullOrWhiteSpace(line.NameAr) && !string.Equals(line.NameAr.Trim(), line.NameEn?.Trim(), StringComparison.Ordinal)) names.Add(line.NameAr.Trim());
        return names.Count == 0 ? line.Code : $"{string.Join(" / ", names)} ({line.Code})";
    }

    private static string Amount(decimal? amount) => (amount ?? 0m).ToString("#,0.##", CultureInfo.InvariantCulture);
}
