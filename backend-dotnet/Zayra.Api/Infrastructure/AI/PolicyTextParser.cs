using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace Zayra.Api.Infrastructure.AI;

public sealed record ParsedPolicyText(string Text, string ContentSha256);

/// <summary>Bounded native text extraction. No OCR and no external provider calls.</summary>
public static class PolicyTextParser
{
    public const int MaxFileBytes = 20 * 1024 * 1024;
    public const int MaxTextCharacters = 200_000;

    public static ParsedPolicyText Extract(Stream content, string fileName, string mimeType, CancellationToken ct = default)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (bytes.Length + read > MaxFileBytes) throw new InvalidDataException("File size exceeds 20 MB.");
            bytes.Write(buffer, 0, read);
        }
        bytes.Position = 0;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var text = new StringBuilder();
        void Append(string value)
        {
            ct.ThrowIfCancellationRequested();
            if (text.Length + value.Length + 1 > MaxTextCharacters)
                throw new InvalidDataException("Policy text exceeds 200,000 characters. Split the document into smaller policies.");
            text.AppendLine(value);
        }
        if (ext == ".pdf")
        {
            using var pdf = PdfDocument.Open(bytes);
            if (pdf.NumberOfPages > 500) throw new InvalidDataException("PDF exceeds 500 pages.");
            foreach (var page in pdf.GetPages()) Append(page.Text);
        }
        else if (ext == ".docx")
        {
            // Reject zip bombs before Open XML expands document parts.
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Read, true))
            {
                if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 40L * 1024 * 1024)
                    throw new InvalidDataException("Word document expands beyond the supported size.");
            }
            bytes.Position = 0;
            using var word = WordprocessingDocument.Open(bytes, false);
            foreach (var paragraph in word.MainDocumentPart?.Document?.Body?
                         .Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>() ?? [])
                Append(paragraph.InnerText);
        }
        else if (ext == ".txt")
        {
            using var reader = new StreamReader(bytes, new UTF8Encoding(false, true), true);
            var chars = new char[4096];
            while ((read = reader.Read(chars, 0, chars.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (text.Length + read > MaxTextCharacters) throw new InvalidDataException("Policy text exceeds 200,000 characters.");
                text.Append(chars, 0, read);
            }
        }
        else throw new InvalidDataException("Upload PDF, DOCX, or TXT. Legacy DOC files are not supported.");
        var normalized = text.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Contains('\0')) throw new InvalidDataException("The document contains invalid text.");
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidDataException("No readable text was found. For scanned documents, use OCR or upload a text-based PDF, DOCX, or TXT file.");
        return new(normalized, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant());
    }
}
