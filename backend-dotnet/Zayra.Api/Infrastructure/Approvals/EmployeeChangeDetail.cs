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
                before = FormatPackage(await EmployeeSalaryBreakdownChanges.ReadLatestAsync(db, employee, ct), employee.Salary);
                after = ReadPackage(proposed[key]);
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

    private static string? ReadPackage(JsonElement value)
    {
        try { return FormatPackage(EmployeeSalaryBreakdownChanges.Read(value), null); }
        catch (InvalidOperationException) { return Display(value); }
    }

    private static string? FormatPackage(EmployeeSalaryBreakdownRequest? package, decimal? fallbackGross)
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
        Add("fixed deduction", package.FixedDeduction);
        var currency = string.IsNullOrWhiteSpace(package.Currency) ? string.Empty : " " + package.Currency.Trim();
        var from = package.EffectiveDate is { } date ? $", from {date:yyyy-MM-dd}" : string.Empty;
        return $"{string.Join(", ", parts)} (gross {Amount(EmployeeSalaryBreakdownChanges.Gross(package))}{currency}){from}";
    }

    private static string Amount(decimal? amount) => (amount ?? 0m).ToString("#,0.##", CultureInfo.InvariantCulture);
}
