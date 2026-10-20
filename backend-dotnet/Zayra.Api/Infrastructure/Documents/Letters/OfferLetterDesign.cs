using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;
using Zayra.Api.Application.Recruitment;

namespace Zayra.Api.Infrastructure.Documents.Letters;

// Plain text blocks, not arbitrary HTML/CSS. The preview and issued document use this same renderer.
public sealed class OfferDesign
{
    public int SchemaVersion { get; set; } = 1;
    public string PageSize { get; set; } = "A4";
    public bool Landscape { get; set; }
    public float Margin { get; set; } = 36;
    public string Direction { get; set; } = "ltr";
    public string FontFamily { get; set; } = "Lato";
    public string HeadingFontFamily { get; set; } = "Lato";
    public string? CustomFont { get; set; }
    public string CustomFontName { get; set; } = "Custom font";
    public float FontSize { get; set; } = 11;
    public float HeadingSize { get; set; } = 25;
    public float LineHeight { get; set; } = 1.45f;
    public string TextColor { get; set; } = "#253047";
    public string AccentColor { get; set; } = "#2454D6";
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public bool PageBorder { get; set; }
    public string Header { get; set; } = "{{company_name}}";
    public string Footer { get; set; } = "Confidential • {{company_name}}";
    public bool PageNumbers { get; set; } = true;
    public string? Logo { get; set; }
    public float LogoWidth { get; set; } = 110;
    public string LogoAlign { get; set; } = "left";
    public string? Signature { get; set; }
    public string? Stamp { get; set; }
    public string Signatory { get; set; } = "Authorized signatory";
    public string SignatoryTitle { get; set; } = "Human Resources";
    public string CandidateAcceptance { get; set; } = "I accept this offer.\nCandidate signature: ____________________    Date: ______________";
    public Dictionary<string, string> CustomFields { get; set; } = new();
    public List<OfferDesignBlock> Blocks { get; set; } =
    [
        new() { Kind = "heading", Text = "Offer of employment" },
        new() { Text = "{{issue_date}}\n\nDear {{candidate_name}},\n\nWe are pleased to offer you the position of {{job_title}} at {{company_name}}. We look forward to welcoming you to our team." },
        new() { Kind = "table", Rows = [new("Department", "{{department}}"), new("Start date", "{{start_date}}"), new("Probation", "{{probation_months}} months")] },
        new() { Kind = "heading", Text = "Your compensation", FontSize = 16 },
        new() { Kind = "table", Rows = [new("Basic salary", "{{currency}} {{basic_salary}}"), new("Housing allowance", "{{currency}} {{housing_allowance}}"), new("Transport allowance", "{{currency}} {{transport_allowance}}"), new("Other allowances", "{{currency}} {{other_allowances}}"), new("Gross monthly salary", "{{currency}} {{gross_salary}}")] },
        new() { Text = "Please confirm your acceptance by {{response_deadline}}. This offer is subject to the agreed employment terms and applicable company policies." },
        new() { Kind = "signature" }
    ];
}

public sealed class OfferDesignBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Align { get; set; } = "left";
    public float? FontSize { get; set; }
    public string? Color { get; set; }
    public string? Background { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public float Spacing { get; set; } = 12;
    public string? Image { get; set; }
    public float ImageWidth { get; set; } = 180;
    public List<OfferDesignRow> Rows { get; set; } = [];
}
public sealed record OfferDesignRow(string Label, string Value);
public sealed record OfferDesignSnapshot(OfferDesign Design, Dictionary<string, string> Values, Guid? TemplateId, int? TemplateVersion,
    OfferCompensationSnapshot? Compensation = null);

public static class OfferLetterDesign
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Typed so an Employee can never be persisted as an offer design snapshot.
    public static string SerializeSnapshot(OfferDesignSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);
    public static readonly string[] Tokens = ["company_name", "candidate_name", "job_title", "department", "start_date", "issue_date", "response_deadline", "basic_salary", "housing_allowance", "transport_allowance", "other_allowances", "gross_salary", "currency", "probation_months", "offer_reference"];
    private static readonly Regex TokenPattern = new(@"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}", RegexOptions.Compiled);
    private static readonly object FontGate = new();
    private static readonly HashSet<string> RegisteredFonts = [];

    public static OfferDesign DynamicDefault()
    {
        var design = new OfferDesign { SchemaVersion = 2 };
        design.Blocks[4] = new OfferDesignBlock { Kind = "compensation" };
        return design;
    }

    // Used exclusively for clearly-labelled template samples, never offer snapshots.
    public static OfferCompensationSnapshot SampleCompensation() => new(1, "SAR", 2,
        [new("BASIC", "Basic salary (sample)", null, "Cash", 12000, null, "Monthly", true, null, null, 0, true),
         new("HOUSING", "Housing allowance (sample)", null, "Cash", 3000, null, "Monthly", true, null, null, 1, false),
         new("TRANSPORT", "Transport allowance (sample)", null, "Cash", 1000, null, "Monthly", true, null, null, 2, false)],
        16000, 192000, 0, 12000, 3000, 1000, 0);

    public static Dictionary<string, string> SampleValues() => new()
    {
        ["company_name"] = "Your organization", ["candidate_name"] = "Alex Morgan (sample)", ["job_title"] = "Finance Manager",
        ["department"] = "Finance", ["start_date"] = "01 January 2027", ["issue_date"] = "19 October 2026", ["response_deadline"] = "26 October 2026",
        ["basic_salary"] = "12,000.00", ["housing_allowance"] = "3,000.00", ["transport_allowance"] = "1,000.00", ["other_allowances"] = "0.00",
        ["gross_salary"] = "16,000.00", ["currency"] = "SAR", ["probation_months"] = "3", ["offer_reference"] = "SAMPLE-001"
    };

    public static string Merge(string text, IReadOnlyDictionary<string, string> values) => TokenPattern.Replace(text, m =>
        values.TryGetValue(m.Groups[1].Value, out var value) ? value : throw new ArgumentException($"Unknown merge field: {m.Groups[1].Value}"));

    public static void Validate(OfferDesign d)
    {
        static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string message) { if (!valid) throw new ArgumentException(message); }
        static bool Color(string? v) => v is not null && Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$");
        static bool Size(float v, float min, float max) => float.IsFinite(v) && v >= min && v <= max;
        Check(d.SchemaVersion is 1 or 2, "Unsupported design version.");
        Check(d.PageSize is "A4" or "Letter" or "Legal", "Choose A4, Letter or Legal paper.");
        Check(d.Direction is "ltr" or "rtl", "Choose left-to-right or right-to-left.");
        Check(Size(d.Margin, 18, 90) && Size(d.FontSize, 8, 24) && Size(d.HeadingSize, 12, 48) && Size(d.LineHeight, 1, 2.5f), "Typography or page margins are outside the supported range.");
        Check(Color(d.TextColor) && Color(d.AccentColor) && Color(d.BackgroundColor), "Colors must use six-digit hex values.");
        Check(d.FontFamily is "Lato" or "Noto Sans Arabic" or "Custom" && d.HeadingFontFamily is "Lato" or "Noto Sans Arabic" or "Custom", "Choose an included font or upload your font.");
        Check(Size(d.LogoWidth, 24, 240) && d.LogoAlign is "left" or "center" or "right", "Logo size or alignment is invalid.");
        Check(d.Blocks is { Count: > 0 and <= 50 } && d.Blocks.All(b => b is not null), "Use between 1 and 50 blocks.");
        Check(d.CustomFields is { Count: <= 30 }, "Use at most 30 custom merge fields.");
        foreach (var (key, value) in d.CustomFields)
            Check(Regex.IsMatch(key, "^[a-z][a-z0-9_]{0,49}$") && !Tokens.Contains(key) && value is { Length: <= 2000 }, "Custom fields need unique lowercase keys and cannot replace built-in offer fields.");
        var values = SampleValues();
        foreach (var pair in d.CustomFields) values.Add(pair.Key, pair.Value);
        var total = 0;
        void Text(string? text, int max = 10000)
        {
            Check(text is not null && text.Length <= max, $"Text exceeds {max} characters.");
            total += text!.Length;
            Merge(text, values);
            Check(!TokenPattern.Replace(text, "").Contains("{{"), "Incomplete merge field. Use {{field_name}}.");
        }
        Text(d.Header, 200); Text(d.Footer, 300); Text(d.Signatory, 200); Text(d.SignatoryTitle, 200); Text(d.CandidateAcceptance, 1000);
        foreach (var b in d.Blocks)
        {
            Check(b.Kind is "heading" or "text" or "table" or "image" or "signature" or "divider" or "spacer" or "pageBreak" || (d.SchemaVersion == 2 && b.Kind == "compensation"), "Unsupported block type.");
            Check(b.Align is "left" or "center" or "right" or "justify", "Invalid text alignment.");
            Check(b.FontSize is null || Size(b.FontSize.Value, 8, 48), "Block font size must be 8–48 pt.");
            Check((b.Color is null || Color(b.Color)) && (b.Background is null || Color(b.Background)), "Invalid block color.");
            Check(Size(b.Spacing, 0, 60) && Size(b.ImageWidth, 24, 300), "Invalid block spacing or image width.");
            Text(b.Text);
            Check(b.Rows is { Count: <= 30 }, "A table may have up to 30 rows.");
            foreach (var row in b.Rows) { Check(row is not null, "Invalid table row."); Text(row.Label, 250); Text(row.Value, 1000); }
            DecodeImage(b.Image);
        }
        Check(total <= 60000, "Letter text exceeds 60,000 characters.");
        DecodeImage(d.Logo); DecodeImage(d.Signature); DecodeImage(d.Stamp);
        Check(JsonSerializer.Serialize(d, Json).Length <= 6_000_000, "Design assets exceed 6 MB. Use smaller images.");
        if (d.CustomFont is not null)
        {
            byte[] font;
            try { font = Convert.FromBase64String(d.CustomFont); } catch (FormatException) { throw new ArgumentException("Upload a valid TTF or OTF font."); }
            Check(font.Length is > 0 and <= 1_500_000, "Font must be smaller than 1.5 MB.");
            using var data = SKData.CreateCopy(font);
            using var face = SKTypeface.FromData(data);
            Check(face is not null, "The uploaded font could not be read.");
        }
        Check(d.CustomFont is not null || (d.FontFamily != "Custom" && d.HeadingFontFamily != "Custom"), "Upload a font before selecting Custom.");
    }

    public static byte[]? DecodeImage(string? image)
    {
        if (string.IsNullOrEmpty(image)) return null;
        if (image.Length > 700_000 || !(image.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || image.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)))
            throw new ArgumentException("Images must be PNG or JPEG, up to 500 KB. External URLs and SVG are not accepted.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image[(image.IndexOf(',') + 1)..]); } catch (FormatException) { throw new ArgumentException("Invalid image encoding."); }
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width > 4000 || codec.Info.Height > 4000 || (long)codec.Info.Width * codec.Info.Height > 8_000_000 || bytes.Length > 512_000)
            throw new ArgumentException("Image is invalid or exceeds 4000 px / 8 megapixels / 500 KB.");
        return bytes;
    }

    public static async Task<OfferDesignSnapshot> SnapshotAsync(ZayraDbContext db, OfferLetter offer, OfferDesign design, Guid? templateId, int? version, CancellationToken ct)
    {
        Validate(design);
        var values = new Dictionary<string, string>(design.CustomFields);
        var company = offer.CompanyId is { } cid ? await db.Companies.Where(x => x.Id == cid && x.TenantId == offer.TenantId).Select(x => x.LegalNameEn).FirstOrDefaultAsync(ct) : null;
        company ??= await db.Tenants.Where(x => x.Id == offer.TenantId).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "";
        string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
        values["company_name"] = company; values["candidate_name"] = offer.CandidateName;
        values["job_title"] = offer.OfferedJobTitle; values["department"] = offer.OfferedDepartment;
        values["start_date"] = offer.StartDate.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);
        values["issue_date"] = offer.GeneratedAtUtc.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);
        values["response_deadline"] = offer.ResponseDeadline?.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture) ?? "the agreed response date";
        values["basic_salary"] = Money(offer.BasicSalary); values["housing_allowance"] = Money(offer.HousingAllowance);
        values["transport_allowance"] = Money(offer.TransportAllowance); values["other_allowances"] = Money(offer.OtherAllowances); values["gross_salary"] = Money(offer.GrossSalary);
        values["currency"] = await OfferRules.ResolveCurrencyAsync(db, offer.TenantId, offer.CompanyId, ct);
        values["probation_months"] = offer.ProbationMonths.ToString(CultureInfo.InvariantCulture); values["offer_reference"] = offer.Id.ToString();
        var compensation = offer.CompensationSnapshotJson is null ? null
            : JsonSerializer.Deserialize<OfferCompensationSnapshot>(offer.CompensationSnapshotJson, OfferCompensation.Json);
        return new(design, values, templateId, version, compensation);
    }

    public static async Task ApplyTemplateAsync(ZayraDbContext db, OfferLetter offer, Guid? templateId, CancellationToken ct)
    {
        if (templateId is null) return; // Preserve legacy documents and existing integrations.
        var template = await db.OfferLetterTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.TenantId == offer.TenantId && !t.Archived && (t.CompanyId == null || t.CompanyId == offer.CompanyId), ct)
            ?? throw new ArgumentException("The selected template is unavailable for this offer's company.");
        var design = JsonSerializer.Deserialize<OfferDesign>(template.DesignJson, Json) ?? throw new ArgumentException("Invalid template.");
        var snapshot = await SnapshotAsync(db, offer, design, template.Id, template.Version, ct);
        Render(snapshot); // Reject non-renderable layouts before persisting an offer.
        offer.DesignSnapshotJson = SerializeSnapshot(snapshot);
    }

    public static byte[] Render(OfferDesignSnapshot snapshot)
    {
        var d = snapshot.Design;
        Validate(d);
        DocumentFonts.EnsureRegistered();
        var customFamily = "Lato";
        if (d.CustomFont is not null && (d.FontFamily == "Custom" || d.HeadingFontFamily == "Custom"))
        {
            var bytes = Convert.FromBase64String(d.CustomFont);
            customFamily = "offer-" + Convert.ToHexString(SHA256.HashData(bytes));
            lock (FontGate)
            {
                if (!RegisteredFonts.Contains(customFamily))
                {
                    if (RegisteredFonts.Count >= 128) throw new ArgumentException("Custom font cache is full. Contact your administrator or choose an included font.");
                    using var stream = new MemoryStream(bytes);
                    FontManager.RegisterFontWithCustomName(customFamily, stream);
                    RegisteredFonts.Add(customFamily);
                }
            }
        }
        string Font(string family) => family == "Custom" ? customFamily : family;
        string T(string value) => Merge(value, snapshot.Values);
        static IContainer Align(IContainer c, string align) => align switch { "right" => c.AlignRight(), "center" => c.AlignCenter(), _ => c.AlignLeft() };
        static void AlignText(TextBlockDescriptor text, string align)
        {
            if (align == "right") text.AlignRight(); else if (align == "center") text.AlignCenter(); else if (align == "justify") text.Justify(); else text.AlignLeft();
        }
        var pdf = Document.Create(doc => doc.Page(page =>
        {
            var size = d.PageSize switch { "Letter" => PageSizes.Letter, "Legal" => PageSizes.Legal, _ => PageSizes.A4 };
            page.Size(d.Landscape ? size.Landscape() : size);
            page.Margin(d.Margin); page.PageColor(d.BackgroundColor);
            if (d.Direction == "rtl") page.ContentFromRightToLeft();
            page.DefaultTextStyle(t => t.FontFamily(Font(d.FontFamily), DocumentFonts.ArabicFamily).FontSize(d.FontSize).FontColor(d.TextColor).LineHeight(d.LineHeight));
            if (d.PageBorder) page.Background().Padding(12).Border(1).BorderColor(d.AccentColor);
            page.Header().PaddingBottom(18).Column(c =>
            {
                if (DecodeImage(d.Logo) is { } logo) Align(c.Item(), d.LogoAlign).Width(d.LogoWidth).MaxHeight(80).Image(logo).FitArea();
                if (d.Header.Length > 0) c.Item().PaddingTop(6).Text(T(d.Header)).FontColor(d.AccentColor).SemiBold();
            });
            page.Content().Column(c =>
            {
                foreach (var b in d.Blocks.Where(x => x.Enabled))
                {
                    if (b.Kind == "pageBreak") { c.Item().PageBreak(); continue; }
                    var item = c.Item().PaddingBottom(b.Spacing);
                    if (b.Background is not null) item = item.Background(b.Background).Padding(8);
                    if (b.Kind == "spacer") { item.Height(8); continue; }
                    if (b.Kind == "divider") { item.LineHorizontal(1).LineColor(b.Color ?? d.AccentColor); continue; }
                    if (b.Kind == "image") { if (DecodeImage(b.Image) is { } img) Align(item, b.Align).Width(b.ImageWidth).MaxHeight(200).Image(img).FitArea(); continue; }
                    if (b.Kind == "compensation")
                    {
                        var compensation = snapshot.Compensation ?? throw new ArgumentException("Save authoritative compensation before previewing this block.");
                        string Money(decimal value) => $"{compensation.Currency} {value.ToString($"N{compensation.Precision}", CultureInfo.InvariantCulture)}";
                        item.Table(table =>
                        {
                            table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); });
                            void Row(string label, string value)
                            {
                                table.Cell().BorderBottom(0.5f).BorderColor("#D8DEE8").Padding(6).Text(label).SemiBold();
                                table.Cell().BorderBottom(0.5f).BorderColor("#D8DEE8").Padding(6).Text(value);
                            }
                            foreach (var row in compensation.Components.OrderBy(r => r.Order))
                            {
                                var label = d.Direction == "rtl" && !string.IsNullOrWhiteSpace(row.LabelAr) ? row.LabelAr : row.LabelEn;
                                var value = !row.Included ? $"Excluded: {row.ExclusionReason}"
                                    : row.Kind == "Benefit" ? row.Description ?? ""
                                    : $"{Money(row.Amount ?? 0)} · {row.Frequency}";
                                Row(label, value);
                            }
                            Row("Monthly cash", Money(compensation.MonthlyCash));
                            Row("Annual recurring cash", Money(compensation.AnnualRecurringCash));
                            Row("One-time cash", Money(compensation.OneTimeCash));
                        });
                        continue;
                    }
                    if (b.Kind == "signature")
                    {
                        item.Column(sign =>
                        {
                            if (DecodeImage(d.Signature) is { } signature) sign.Item().Width(130).Height(48).Image(signature).FitArea();
                            sign.Item().Text(T(d.Signatory)).Bold(); sign.Item().Text(T(d.SignatoryTitle));
                            if (DecodeImage(d.Stamp) is { } stamp) sign.Item().Width(70).Height(60).Image(stamp).FitArea();
                            sign.Item().PaddingTop(18).Text(T(d.CandidateAcceptance));
                        });
                        continue;
                    }
                    var style = TextStyle.Default.FontSize(b.FontSize ?? (b.Kind == "heading" ? d.HeadingSize : d.FontSize)).FontColor(b.Color ?? (b.Kind == "heading" ? d.AccentColor : d.TextColor));
                    if (b.Kind == "heading") style = style.FontFamily(Font(d.HeadingFontFamily), DocumentFonts.ArabicFamily).SemiBold();
                    if (b.Bold) style = style.Bold(); if (b.Italic) style = style.Italic(); if (b.Underline) style = style.Underline();
                    if (b.Kind == "table")
                    {
                        item.DefaultTextStyle(style).Table(table =>
                        {
                            table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); });
                            foreach (var row in b.Rows)
                            {
                                AlignText(table.Cell().BorderBottom(0.5f).BorderColor("#D8DEE8").PaddingVertical(6).PaddingHorizontal(4).Text(T(row.Label)).SemiBold(), b.Align);
                                AlignText(table.Cell().BorderBottom(0.5f).BorderColor("#D8DEE8").PaddingVertical(6).PaddingHorizontal(4).Text(T(row.Value)), b.Align);
                            }
                        });
                        continue;
                    }
                    var text = item.DefaultTextStyle(style).Text(T(b.Text));
                    AlignText(text, b.Align);
                }
            });
            page.Footer().PaddingTop(12).Row(row =>
            {
                row.RelativeItem().Text(T(d.Footer)).FontSize(8);
                if (d.PageNumbers) row.ConstantItem(55).AlignRight().Text(t => { t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
            });
        })).GeneratePdf();
        return pdf;
    }
}
