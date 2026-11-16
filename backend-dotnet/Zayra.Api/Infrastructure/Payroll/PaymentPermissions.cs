namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// M2 — the four narrow permission keys for the payment actions, replacing the reverted <c>payroll.export</c> widening
/// (that key also opens the bank file's IBAN lines, the bulk payslip bundle, the register export and the bank-confirmation screens).
/// Each key opens exactly one family; <c>payroll.export</c> still opens all of them.
///
/// <para><b>Who prepares, who pays (final design).</b> The seeded Payroll Manager prepares the bank file: ONLY <see cref="Batch"/>. The seeded
/// Finance Approver pays and confirms: <see cref="WpsStatus"/>, <see cref="Settle"/> and <see cref="Remit"/>, and NOT <see cref="Batch"/>, so no
/// single built-in role both prepares and pays. HR, the payroll officer, the loan operator and employees hold none of the four. At runtime a caller
/// who holds only the settle key cannot settle a batch they created or generated the file of (maker-checker, SettlePaymentBatch).</para>
/// </summary>
public static class PaymentPermissions
{
    public const string Export = "payroll.export";
    /// <summary>Create the payment batch of a locked run, validate the wage file, generate and download the bank or WPS file, read its export history.</summary>
    public const string Batch = "payroll.payment_batch.manage";
    /// <summary>Record the file's lifecycle (submitted, accepted, rejected) and attach or read the bank evidence.</summary>
    public const string WpsStatus = "payroll.wps_status.update";
    /// <summary>Post the net-pay settlement journal, record a payment made outside the bank file, and reverse either.</summary>
    public const string Settle = "payroll.payment.settle";
    /// <summary>Remit a statutory or loan liability and reverse a remittance.</summary>
    public const string Remit = "payroll.remit.manage";

    public static readonly IReadOnlyList<string> All = new[] { Batch, WpsStatus, Settle, Remit };
}
