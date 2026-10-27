namespace Zayra.Api.Infrastructure.Documents.Esign;

public sealed record AckCertificateData(
    string CompanyName, string EmployeeName, string EmployeeCode, string DocumentTitle, string DocumentFileName, string DocumentSha256,
    string TypedName, DateTime SignedAtUtc, string Method, string? WitnessName, string ConsentVersion, string ConsentTextEn, string ConsentTextAr,
    string RecordHash, string VerificationCode);

public static class AckCertificateRenderer
{
    public const string StatementEn = "Electronic acknowledgement - this is not a qualified electronic signature.";
    public const string StatementAr = "إقرار إلكتروني - وهذا ليس توقيعاً إلكترونياً مؤهلاً.";
    public const string TitleEn = "Certificate of electronic acknowledgement";
    public const string TitleAr = "شهادة إقرار إلكتروني";

    public static byte[] Render(AckCertificateData d) => throw new NotImplementedException();
}
