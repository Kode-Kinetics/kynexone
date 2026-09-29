using System.Text.Json.Serialization;
namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

// HTTP contract for api/payroll/bank-exports (IMPLEMENTATION-CONTRACT.md §12–14). Positional records
// serialise camelCase through the default web JSON options. These are the ONLY shapes the endpoints
// return — never a raw entity.

public sealed record SaudiBankExportFormatDto(
    string Id, string Name, string Bank, string Channel, string SourceUrl, string ReviewedOn, string AcceptanceStatus);

/// <summary>Per-company employer facts for the bank-instruction header. These are customer
/// account/establishment facts, NOT banking credentials — nothing here authorises a transfer.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SaudiBankExportSettingsDto
{
    public string FormatId { get; set; } = SaudiBankExportFormats.AnbConnectCsvV1;
    public string MolEstablishmentId { get; set; } = string.Empty;
    public string MainAccountNumber { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string OrganizationAddress1 { get; set; } = string.Empty;
    public string OrganizationAddress2 { get; set; } = string.Empty;
    public string OrganizationAddress3 { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Narrative { get; set; } = string.Empty;
    public string BatchType { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SaudiBankExportBatchRequest
{
    /// <summary>ANB batchNumber: 1..20 ASCII digits, unique across this company's exported batches.</summary>
    public string? BatchReference { get; set; }
    /// <summary>Credit value date, exactly <c>YYYY-MM-DD</c>.</summary>
    public string? PaymentDate { get; set; }
}

public sealed record SaudiBankExportIssueDto(string Code, string Message, int? EmployeeId = null, string? Field = null);

public sealed record SaudiBankExportWarningDto(string Code, string Message);

public sealed record SaudiBankExportFileDto(string Name, string Sha256);

public sealed record SaudiBankExistingExportDto(
    Guid Id, string FormatId, IReadOnlyList<SaudiBankExportFileDto> Files, string BatchReference,
    string PaymentDate, int EmployeeCount, decimal TotalAmount);

public sealed record SaudiBankExportContextDto(
    Guid CompanyId, string CompanyName, string CountryCode, string RunStatus, SaudiBankExistingExportDto? ExistingExport);

public sealed record SaudiBankExportValidationDto(
    bool CanExport, IReadOnlyList<SaudiBankExportIssueDto> Errors, IReadOnlyList<SaudiBankExportWarningDto> Warnings,
    int EmployeeCount, decimal TotalAmount, string Currency, string FormatId);

public sealed record SaudiBankExportGeneratedDto(
    Guid Id, string FormatId, IReadOnlyList<SaudiBankExportFileDto> Files, string BatchReference,
    string PaymentDate, int EmployeeCount, decimal TotalAmount, string DownloadUrl);

/// <summary>
/// The shared, versioned registry of bank/channel formats. One adapter per bank + channel, reusable by
/// every tenant. Lookup is ORDINAL and exact: an unknown or differently-cased id is unsupported and the
/// caller fails closed — there is deliberately no generic/XML fallback.
/// </summary>
public static class SaudiBankExportFormats
{
    public const string AnbConnectCsvV1 = "anb-connect-csv-v1";
    public const string AcceptanceStatus = "specification-implemented; bank-acceptance-not-verified";

    public static readonly IReadOnlyList<SaudiBankExportFormatDto> Supported = new[]
    {
        new SaudiBankExportFormatDto(
            AnbConnectCsvV1,
            "ANB Connect payroll payment — header.csv + body.csv",
            "Arab National Bank (ANB)",
            "ANB Connect API channel (payroll-payment). Not the ANB corporate-portal WPY upload.",
            "https://connect.anb.com.sa/apis/api/payroll-payment",
            "2026-09-26",
            AcceptanceStatus),
    };

    public static SaudiBankExportFormatDto? Find(string? id) =>
        id is null ? null : Supported.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
}

public sealed class SaudiBankExportIntegrityException : Exception
{
    public SaudiBankExportIntegrityException() : base("Stored bank-export artifact integrity could not be verified.") { }
}
