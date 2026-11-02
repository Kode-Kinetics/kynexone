using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>One protected property: the plaintext CLR property plus the shadow ciphertext / blind-index companions.</summary>
public sealed record EncryptedField(Type Entity, string Property, bool BlindIndexed)
{
    /// <summary>Shadow property (and CLR-name stem) holding the envelope bytea.</summary>
    public string CipherProperty => Property + "Enc";
    public string BlindIndexProperty => Property + "Bidx";
    public string CipherColumn => Snake(Property) + "_enc";
    public string BlindIndexColumn => Snake(Property) + "_bidx";
    public string PlainColumn => Snake(Property);

    public static string Snake(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsUpper(value[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(value[i]));
        }
        return sb.ToString();
    }
}

/// <summary>
/// THE list of encrypted fields. Adding a sensitive column means adding it here; <c>FieldEncryptionCoverageTests</c> fails
/// when an identifier-shaped string property exists on an entity that is neither listed nor explicitly exempted.
/// </summary>
public static class EncryptedFieldCatalog
{
    private static EncryptedField F<T>(string property, bool blindIndexed) => new(typeof(T), property, blindIndexed);

    public static readonly IReadOnlyList<EncryptedField> All =
    [
        // Employee: the dedup identity columns carry a blind index (it replaces the ix_employees_dup_* plaintext indexes).
        F<Employee>(nameof(Employee.BankIban), true),
        F<Employee>(nameof(Employee.WpsBankDetails), false),
        F<Employee>(nameof(Employee.PassportNumber), true),
        F<Employee>(nameof(Employee.IqamaNumber), true),
        F<Employee>(nameof(Employee.BorderNumber), true),
        F<Employee>(nameof(Employee.EmiratesId), true),
        F<Employee>(nameof(Employee.Qid), true),
        F<Employee>(nameof(Employee.CivilId), true),
        F<Employee>(nameof(Employee.IdNumber), true),
        // Draft copy of the same data (never looked up by value).
        F<EmployeeDraft>(nameof(EmployeeDraft.BankIban), false),
        F<EmployeeDraft>(nameof(EmployeeDraft.WpsBankDetails), false),
        F<EmployeeDraft>(nameof(EmployeeDraft.PassportNumber), false),
        F<EmployeeDraft>(nameof(EmployeeDraft.IqamaNumber), false),
        F<EmployeeDraft>(nameof(EmployeeDraft.BorderNumber), false),
        F<EmployeeDraft>(nameof(EmployeeDraft.EmiratesId), false),
        F<EmployeeDraft>(nameof(EmployeeDraft.Qid), false),
        F<EmployeeDraft>(nameof(EmployeeDraft.CivilId), false),
        // Payroll profile + the payment artefacts that snapshot a bank account.
        F<EmployeePayrollProfile>(nameof(EmployeePayrollProfile.Iban), true),
        F<EmployeePayrollProfile>(nameof(EmployeePayrollProfile.AccountNumber), true),
        F<EmployeePayrollProfile>(nameof(EmployeePayrollProfile.MolId), true),
        F<PayrollPaymentRecord>(nameof(PayrollPaymentRecord.Iban), false),
        F<SIFFileRecord>(nameof(SIFFileRecord.Iban), false),
        F<SIFFileRecord>(nameof(SIFFileRecord.MolId), false),
        F<LoanDisbursementLine>(nameof(LoanDisbursementLine.Iban), false),
        // Compliance registers and dependants.
        F<VisaRecord>(nameof(VisaRecord.IqamaNumber), true),
        F<VisaRecord>(nameof(VisaRecord.EmiratesIdNumber), true),
        F<PassportRecord>(nameof(PassportRecord.PassportNumber), true),
        F<EmployeeDependent>(nameof(EmployeeDependent.NationalId), false),
    ];

    private static readonly Dictionary<Type, EncryptedField[]> ByEntity =
        All.GroupBy(f => f.Entity).ToDictionary(g => g.Key, g => g.ToArray());

    public static IReadOnlyCollection<Type> Entities => ByEntity.Keys;
    public static EncryptedField[] For(Type entity) => ByEntity.TryGetValue(entity, out var f) ? f : [];
    public static EncryptedField? Find(Type entity, string property) => For(entity).FirstOrDefault(f => f.Property == property);
}
