using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Zayra.Api.Infrastructure.Documents.Letters;

public class LetterService : ILetterService
{
    static LetterService()
    {
        // Registers the embedded Arabic font and turns QuestPDF's host-font fallback OFF.
        // Before this line the payslip's Arabic labels rendered as tofu boxes in the Linux
        // container and looked fine on a developer Mac. See DocumentFonts.
        DocumentFonts.EnsureRegistered();
    }

    /// <summary>
    /// Width of every right-hand amount column on the payslip: room for "12,345,678.90" at 9pt.
    ///
    /// The amount is aligned with <c>Text(...).AlignRight()</c> (the paragraph aligns itself inside the
    /// fixed-width cell). The previous <c>ConstantItem(70).AlignRight().Text(...)</c> aligned the cell
    /// content AND let the paragraph keep its measured box, so QuestPDF 2025.1 shifted every amount right by
    /// the free space of the cell a second time: "10,000.00" ended 25pt past its column and ran into the
    /// Deductions labels, and "SAR 12,281.25" ran past the page edge and was cut off.
    /// </summary>
    private const float AmountColumnWidth = 80;

    /// <summary>Width of the NET PAY figure in its banner.</summary>
    private const float NetColumnWidth = 170;

    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default)
    {
        var b = data.Branding ?? new();
        // Three variants. "ar": Arabic labels AND a mirrored (right-to-left) layout. "bilingual": the
        // left-to-right layout with "English / Arabic" labels and both sentences. Anything else: English.
        var ar = b.Locale == "ar";
        var bi = b.Locale == "bilingual";
        var rtl = ar;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // The page's culture must never pick the digits or month names (an Arabic-culture host would print
        // Arabic-Indic digits); English months are invariant, Arabic months come from PayslipArabic.
        var monthEn = new DateTime(data.PayYear, data.PayMonth, 1).ToString("MMMM yyyy", inv);
        var monthAr = PayslipArabic.MonthYear(data.PayYear, data.PayMonth);
        var monthName = ar ? monthAr : bi ? $"{monthEn} / {monthAr}" : monthEn;

        // L: one label in the variant's language(s).
        string L(string en, string arText) => ar ? arText : bi ? $"{en} / {arText}" : en;
        string ItemText(PayslipLineItem i)
        {
            if (!ar && !bi) return i.Name;
            var arName = PayslipArabic.ItemName(i.Name, i.NameAr);
            return ar ? arName : string.Equals(arName, i.Name, StringComparison.Ordinal) ? i.Name : $"{i.Name} / {arName}";
        }

        var earnings   = data.Items.Where(i => i.Type == PayslipLineTypes.Earning).ToList();
        // Employee deductions only. Employer contributions are an employer cost: they get their own
        // section below and are never part of the deductions total, so gross − deductions = net pay.
        var deductions = data.Items.Where(i => i.Type == PayslipLineTypes.Deduction).ToList();
        var employerContributions = data.Items.Where(i => i.Type == PayslipLineTypes.EmployerContribution).ToList();
        var netItem    = data.Items.FirstOrDefault(i => i.Type == PayslipLineTypes.Net);
        var grossTotal     = earnings.Sum(e => e.Amount);
        var deductionTotal = deductions.Sum(d => d.Amount);
        var employerTotal  = employerContributions.Sum(c => c.Amount);
        var netPay = netItem?.Amount ?? grossTotal - deductionTotal;

        // Parse branding colors to QuestPDF color strings (fallback to defaults on invalid hex)
        var primaryHex = b.PrimaryColor.TrimStart('#');
        var accentHex  = b.AccentColor.TrimStart('#');
        static bool IsValidHex(string hex) => uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out _);
        var primaryColor = (string)(IsValidHex(primaryHex) ? $"#{primaryHex}" : Colors.Blue.Darken3);
        var accentColor  = (string)(IsValidHex(accentHex)  ? $"#{accentHex}"  : Colors.Blue.Lighten3);

        // Controllers load tenant-scoped logo bytes through IDocumentStorage. The PDF
        // renderer must never interpret a persisted, client-controlled key as a local path.
        var logoBytes = b.LogoBytes;

        var earningsLabel   = L("EARNINGS", "المستحقات");
        var deductionsLabel = L("DEDUCTIONS", "الاستقطاعات");
        var grossLabel      = L("Gross Total", "الإجمالي");
        var deductTotalLbl  = L("Total Deductions", "إجمالي الاستقطاعات");
        var netLabel        = L("NET PAY", "صافي الراتب");
        var noDeductLbl     = L("No deductions", "لا توجد استقطاعات");
        var employerLabel   = L("EMPLOYER CONTRIBUTIONS (NOT DEDUCTED FROM YOUR PAY)", "مساهمات صاحب العمل (لا تُخصم من راتبك)");
        var employerTotLbl  = L("Total employer contributions", "إجمالي مساهمات صاحب العمل");
        var empLabel        = L("Employee", "الموظف");
        var codeLabel       = L("Code", "الكود");
        var deptLabel       = L("Department", "القسم");
        var desigLabel      = L("Designation", "المسمى");
        var periodLabel     = L("Pay Period", "فترة الراتب");
        var currencyLabel   = L("Currency", "العملة");
        var payslipLabel    = L("Payslip", "كشف راتب");
        var noLabel         = L("No", "رقم");

        // Label and value are SEPARATE cells: in one paragraph the bidi algorithm moves the colon of an Arabic
        // label to the wrong side of the value (":الموظف").
        // Fixed-width label cell: QuestPDF 2025.1 collapses an AutoItem to nothing in a right-to-left row,
        // which made every label (and the NET PAY amount) vanish from the Arabic payslip.
        var labelWidth = ar ? 54f : 58f;
        void Field(ColumnDescriptor col, string label, string value, bool strong = false)
        {
            if (bi)
            {
                // "English / Arabic" labels are too wide for a side-by-side cell: stack label over value.
                col.Item().PaddingBottom(2).Column(c =>
                {
                    c.Item().Text(label).FontSize(7).FontColor(Colors.Grey.Darken1);
                    var v = c.Item().Text(value);
                    if (strong) v.SemiBold();
                });
                return;
            }
            col.Item().Row(r =>
            {
                var l = r.ConstantItem(labelWidth).Text($"{label}:");
                var v = r.RelativeItem().PaddingLeft(3).PaddingRight(3).Text(value);
                if (strong) { l.SemiBold(); v.SemiBold(); }
            });
        }

        var generatedOn = data.GeneratedOn ?? DateTime.UtcNow;
        string StampEn() => generatedOn.ToString("dd MMM yyyy HH:mm", inv) + " UTC";
        string StampAr() => $"{generatedOn.ToString("dd", inv)} {PayslipArabic.Month(generatedOn.Month)} {generatedOn.ToString("yyyy HH:mm", inv)} UTC";

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(35);
                if (rtl) page.ContentFromRightToLeft();
                // SHARED PAYSLIP PDF — minimal change, flagged in the stream notes. The only
                // edit is the font chain: Lato for Latin, the embedded Noto Sans Arabic for the
                // Arabic labels a few lines above, which previously had no font that could draw
                // them anywhere but a developer laptop.
                // TextLayerSafe: no ligatures. Lato draws "ti" as one ligature glyph that the embedded font
                // subset gives no Unicode mapping, so copy, search, screen readers and parsers read
                // "Designation" as "Designaon" and "Deductions" as "Deducons".
                page.DefaultTextStyle(s => s.FontSize(9).FontColor(Colors.Grey.Darken3)
                    .FontFamily(DocumentFonts.LatinFamily, DocumentFonts.ArabicFamily)
                    .TextLayerSafe());

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        // Logo (if available)
                        if (logoBytes is { Length: > 0 })
                            row.ConstantItem(60).Height(40).Image(logoBytes).FitArea();

                        row.RelativeItem().Column(c =>
                        {
                            // Arabic variant leads with the Arabic company name when there is one.
                            var primaryName = ar && !string.IsNullOrWhiteSpace(data.CompanyNameAr) ? data.CompanyNameAr : data.CompanyName;
                            var secondaryName = ar && !string.IsNullOrWhiteSpace(data.CompanyNameAr) ? data.CompanyName : data.CompanyNameAr;
                            c.Item().Text(primaryName).FontSize(14).Bold().FontColor(primaryColor);
                            if (!string.IsNullOrWhiteSpace(secondaryName))
                                c.Item().Text(secondaryName).FontSize(11).FontColor(primaryColor);
                            if (!string.IsNullOrWhiteSpace(b.HeaderTextEn))
                                c.Item().Text(b.HeaderTextEn).FontSize(8).FontColor(Colors.Grey.Darken1);
                            if ((ar || bi) && !string.IsNullOrWhiteSpace(b.HeaderTextAr))
                                c.Item().Text(b.HeaderTextAr).FontSize(8).FontColor(Colors.Grey.Darken1);
                            c.Item().Text(payslipLabel).FontSize(11).SemiBold().FontColor(Colors.Grey.Darken1);
                        });
                        if (rtl)
                        {
                            // Mirrored: the period block sits on the left; its lines start at the right edge of the
                            // block, as the English block's lines start at its left edge.
                            row.ConstantItem(120).Column(c =>
                            {
                                c.Item().Text(monthName).FontSize(11).SemiBold();
                                c.Item().Row(r =>
                                {
                                    r.ConstantItem(24).Text($"{noLabel}:").FontSize(8).FontColor(Colors.Grey.Medium);
                                    r.RelativeItem().PaddingRight(3).Text(data.PayslipNumber).FontSize(8).FontColor(Colors.Grey.Medium);
                                });
                            });
                        }
                        else
                        {
                            row.ConstantItem(bi ? 170 : 120).AlignRight().Column(c =>
                            {
                                c.Item().Text(monthName).FontSize(11).SemiBold();
                                c.Item().Text($"{noLabel}: {data.PayslipNumber}").FontSize(8).FontColor(Colors.Grey.Medium);
                            });
                        }
                    });
                    col.Item().PaddingVertical(6).LineHorizontal(1).LineColor(accentColor);
                });

                page.Content().Column(col =>
                {
                    // Employee info box
                    col.Item().Background(Colors.Blue.Lighten5).Padding(10).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            Field(c, empLabel, data.EmployeeName, strong: true);
                            Field(c, codeLabel, data.EmployeeCode);
                        });
                        row.RelativeItem().Column(c =>
                        {
                            Field(c, deptLabel, data.Department);
                            Field(c, desigLabel, data.Designation);
                        });
                        row.RelativeItem().Column(c =>
                        {
                            Field(c, periodLabel, monthName);
                            Field(c, currencyLabel, data.Currency);
                        });
                    });

                    col.Item().PaddingTop(12).Row(row =>
                    {
                        // Earnings (first child: left in English, right when mirrored)
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Background(Colors.Green.Lighten4).Padding(6).Text(earningsLabel).Bold().FontSize(8);
                            foreach (var e in earnings)
                                c.Item().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Row(r =>
                                {
                                    r.RelativeItem().Text(ItemText(e));
                                    r.ConstantItem(AmountColumnWidth).Text($"{e.Amount:N2}").AlignEnd();
                                });
                            c.Item().Background(Colors.Green.Lighten3).Padding(5).Row(r =>
                            {
                                r.RelativeItem().Text(grossLabel).SemiBold();
                                r.ConstantItem(AmountColumnWidth).Text($"{grossTotal:N2}").AlignEnd().SemiBold();
                            });
                        });

                        row.ConstantItem(24); // gutter between the two tables

                        // Deductions
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Background(Colors.Red.Lighten4).Padding(6).Text(deductionsLabel).Bold().FontSize(8);
                            if (deductions.Count == 0)
                                c.Item().Padding(4).Text(noDeductLbl).FontColor(Colors.Grey.Medium);
                            foreach (var d in deductions)
                                c.Item().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Row(r =>
                                {
                                    r.RelativeItem().Text(ItemText(d));
                                    r.ConstantItem(AmountColumnWidth).Text($"{d.Amount:N2}").AlignEnd();
                                });
                            c.Item().Background(Colors.Red.Lighten3).Padding(5).Row(r =>
                            {
                                r.RelativeItem().Text(deductTotalLbl).SemiBold();
                                r.ConstantItem(AmountColumnWidth).Text($"{deductionTotal:N2}").AlignEnd().SemiBold();
                            });
                        });
                    });

                    // Net pay banner
                    col.Item().PaddingTop(12).Background(primaryColor).Padding(10).Row(row =>
                    {
                        row.RelativeItem().Text(netLabel).FontSize(12).Bold().FontColor(Colors.White);
                        // 170pt end-aligned: room for "SAR 12,345,678.90" at 13pt bold. A fixed 120pt box clipped
                        // "SAR 12,281.25" to "SAR 12,28" (see AmountColumnWidth).
                        row.ConstantItem(NetColumnWidth).Text($"{data.Currency} {netPay:N2}").FontSize(13).Bold().FontColor(Colors.White).AlignEnd();
                    });

                    // Employer contributions — what the employer pays ON TOP of this salary (e.g. GOSI
                    // occupational hazard). Shown after net pay so it can never be read as a deduction.
                    if (employerContributions.Count > 0)
                    {
                        col.Item().PaddingTop(12).Column(c =>
                        {
                            c.Item().Background(Colors.Grey.Lighten3).Padding(6).Text(employerLabel).Bold().FontSize(8);
                            foreach (var ec in employerContributions)
                                c.Item().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Row(r =>
                                {
                                    r.RelativeItem().Text(ItemText(ec));
                                    r.ConstantItem(AmountColumnWidth).Text($"{ec.Amount:N2}").AlignEnd();
                                });
                            c.Item().Background(Colors.Grey.Lighten2).Padding(5).Row(r =>
                            {
                                r.RelativeItem().Text(employerTotLbl).SemiBold();
                                r.ConstantItem(AmountColumnWidth).Text($"{employerTotal:N2}").AlignEnd().SemiBold();
                            });
                        });
                    }

                    col.Item().PaddingTop(20).Column(c =>
                    {
                        if (!ar)
                            c.Item().Text("This is a system-generated payslip and is valid without a signature.").FontSize(7).FontColor(Colors.Grey.Medium).Italic();
                        if (ar || bi)
                            c.Item().Text(PayslipArabic.Disclaimer).FontSize(7).FontColor(Colors.Grey.Medium).DirectionFromRightToLeft();
                    });
                });

                page.Footer().Column(col =>
                {
                    string Pick(string tenant, string fallback) => !string.IsNullOrWhiteSpace(tenant) ? tenant : fallback;
                    if (!ar)
                        col.Item().AlignCenter().Text(t =>
                        {
                            t.Span($"{Pick(b.FooterTextEn, "Generated by KynexOne HRMS")} — ").FontSize(7).FontColor(Colors.Grey.Medium);
                            t.Span(StampEn()).FontSize(7).FontColor(Colors.Grey.Medium);
                        });
                    if (ar || bi)
                        col.Item().AlignCenter().Text(Pick(b.FooterTextAr, PayslipArabic.Footer)).FontSize(7).FontColor(Colors.Grey.Medium).DirectionFromRightToLeft();
                    if (ar)
                        // The date is its own line: in the same paragraph as the sentence its Latin "UTC" and digits
                        // were reordered around the Arabic month and the footer read "... — 07 UTC 09:30 2026 نوفمبر".
                        col.Item().AlignCenter().Text(StampAr()).FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        }).WithMetadata(new DocumentMetadata
        {
            Title = $"{(ar ? "كشف راتب" : "Payslip")} {(ar ? monthAr : monthEn)}",
            Creator = "KynexOne HRMS",
            Producer = "KynexOne HRMS",
        });

        return Task.FromResult(doc.GeneratePdf());
    }

    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(50);
                page.DefaultTextStyle(s => s.FontSize(10).FontColor(Colors.Grey.Darken3).LineHeight(1.5f).TextLayerSafe());

                page.Header().Column(col =>
                {
                    col.Item().Text(data.CompanyName).FontSize(16).Bold().FontColor(Colors.Blue.Darken3);
                    col.Item().Text("APPOINTMENT LETTER").FontSize(12).Bold().FontColor(Colors.Grey.Darken2);
                    col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Blue.Lighten3);
                });

                page.Content().PaddingTop(16).Column(col =>
                {
                    col.Item().Text($"Date: {data.IssuedDate:dd MMMM yyyy}").AlignRight();
                    col.Item().PaddingTop(12).Text($"Dear {data.EmployeeName},");
                    col.Item().PaddingTop(8).Text(t =>
                    {
                        t.Span("We are pleased to appoint you to the position of ");
                        t.Span(data.Designation).Bold();
                        t.Span($" in the ");
                        t.Span(data.Department).Bold();
                        t.Span($" department, effective ");
                        t.Span(data.JoiningDate.ToString("dd MMMM yyyy")).Bold();
                        t.Span(".");
                    });

                    col.Item().PaddingTop(12).Column(c =>
                    {
                        c.Item().Text("TERMS OF EMPLOYMENT").Bold().FontSize(10);
                        c.Item().PaddingTop(6).Row(r =>
                        {
                            r.ConstantItem(160).Text("Employee Code:");
                            r.RelativeItem().Text(data.EmployeeCode).SemiBold();
                        });
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(160).Text("Designation:");
                            r.RelativeItem().Text(data.Designation).SemiBold();
                        });
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(160).Text("Department:");
                            r.RelativeItem().Text(data.Department).SemiBold();
                        });
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(160).Text("Date of Joining:");
                            r.RelativeItem().Text(data.JoiningDate.ToString("dd MMMM yyyy")).SemiBold();
                        });
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(160).Text("Basic Salary:");
                            r.RelativeItem().Text($"{data.Currency} {data.BasicSalary:N2} per month").SemiBold();
                        });
                    });

                    if (!string.IsNullOrWhiteSpace(data.AdditionalNote))
                        col.Item().PaddingTop(12).Text(data.AdditionalNote);

                    col.Item().PaddingTop(12).Text("You are required to comply with all company policies and procedures.");
                    col.Item().PaddingTop(8).Text("We welcome you to our team and wish you a successful career.");
                    col.Item().PaddingTop(24).Column(c =>
                    {
                        c.Item().Text("Yours sincerely,");
                        c.Item().PaddingTop(30).Text("_________________________");
                        c.Item().Text(data.IssuedBy).SemiBold();
                        c.Item().Text("Human Resources");
                        c.Item().Text(data.CompanyName);
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span($"Confidential — {data.CompanyName} — KynexOne HRMS").FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });

        return Task.FromResult(doc.GeneratePdf());
    }

    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default)
    {
        var leavingDate = data.LeavingDate ?? DateTime.UtcNow;
        var tenure = (leavingDate - data.JoiningDate).Days / 365.0;
        var tenureText = tenure < 1
            ? $"{(leavingDate - data.JoiningDate).Days} days"
            : $"{tenure:F1} years";

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(50);
                page.DefaultTextStyle(s => s.FontSize(10).FontColor(Colors.Grey.Darken3).LineHeight(1.6f).TextLayerSafe());

                page.Header().Column(col =>
                {
                    col.Item().Text(data.CompanyName).FontSize(16).Bold().FontColor(Colors.Blue.Darken3);
                    col.Item().Text("EXPERIENCE CERTIFICATE").FontSize(12).Bold().FontColor(Colors.Grey.Darken2);
                    col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Blue.Lighten3);
                });

                page.Content().PaddingTop(16).Column(col =>
                {
                    col.Item().Text($"Date: {data.IssuedDate:dd MMMM yyyy}").AlignRight();
                    col.Item().Text($"Ref: EXP-{data.EmployeeCode}-{data.IssuedDate:yyyyMM}").AlignRight().FontSize(8).FontColor(Colors.Grey.Medium);

                    col.Item().PaddingTop(16).Text("TO WHOM IT MAY CONCERN").Bold().AlignCenter();

                    col.Item().PaddingTop(12).Text(t =>
                    {
                        t.Span("This is to certify that ");
                        t.Span(data.EmployeeName).Bold();
                        t.Span($" (Employee Code: {data.EmployeeCode}) was employed with ");
                        t.Span(data.CompanyName).Bold();
                        t.Span($" as ");
                        t.Span(data.Designation).Bold();
                        t.Span($" in the ");
                        t.Span(data.Department).Bold();
                        t.Span($" department from ");
                        t.Span(data.JoiningDate.ToString("dd MMMM yyyy")).Bold();
                        t.Span(" to ");
                        t.Span(leavingDate.ToString("dd MMMM yyyy")).Bold();
                        t.Span($", a total tenure of {tenureText}.");
                    });

                    col.Item().PaddingTop(12).Text($"During this period, {data.EmployeeName} demonstrated professionalism and dedication, and consistently met the expectations of the role.");
                    col.Item().PaddingTop(8).Text($"We wish {data.EmployeeName} all the best in future endeavours.");

                    if (!string.IsNullOrWhiteSpace(data.AdditionalNote))
                        col.Item().PaddingTop(12).Text(data.AdditionalNote);

                    col.Item().PaddingTop(30).Column(c =>
                    {
                        c.Item().Text("_________________________");
                        c.Item().Text(data.IssuedBy).SemiBold();
                        c.Item().Text("Human Resources");
                        c.Item().Text(data.CompanyName);
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span($"Confidential — {data.CompanyName} — KynexOne HRMS").FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });

        return Task.FromResult(doc.GeneratePdf());
    }

    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(50);
                page.DefaultTextStyle(s => s.FontSize(10).FontColor(Colors.Grey.Darken3).LineHeight(1.6f).TextLayerSafe());

                page.Header().Column(col =>
                {
                    col.Item().Text(data.CompanyName).FontSize(16).Bold().FontColor(Colors.Blue.Darken3);
                    col.Item().Text("OFFER LETTER").FontSize(12).Bold().FontColor(Colors.Grey.Darken2);
                    col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Blue.Lighten3);
                });

                page.Content().PaddingTop(16).Column(col =>
                {
                    col.Item().Text($"Date: {data.IssuedDate:dd MMMM yyyy}").AlignRight();
                    col.Item().PaddingTop(12).Text($"Dear {data.CandidateName},");

                    col.Item().PaddingTop(8).Text(t =>
                    {
                        t.Span("We are pleased to offer you the position of ");
                        t.Span(data.Position).Bold();
                        t.Span($" in the ");
                        t.Span(data.Department).Bold();
                        t.Span($" department, commencing on ");
                        t.Span(data.StartDate.ToString("dd MMMM yyyy")).Bold();
                        t.Span(".");
                    });

                    col.Item().PaddingTop(12).Column(c =>
                    {
                        c.Item().Text("OFFER DETAILS").Bold().FontSize(10);
                        c.Item().PaddingTop(6).Row(r =>
                        {
                            r.ConstantItem(160).Text("Position:");
                            r.RelativeItem().Text(data.Position).SemiBold();
                        });
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(160).Text("Department:");
                            r.RelativeItem().Text(data.Department).SemiBold();
                        });
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(160).Text("Start Date:");
                            r.RelativeItem().Text(data.StartDate.ToString("dd MMMM yyyy")).SemiBold();
                        });
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(160).Text("Offered Salary:");
                            r.RelativeItem().Text($"{data.Currency} {data.Salary:N2} per month").SemiBold();
                        });
                    });

                    col.Item().PaddingTop(12).Text("This offer is contingent upon satisfactory completion of background verification and submission of required documents.");
                    col.Item().PaddingTop(8).Text("Please sign and return a copy of this letter by the date indicated to confirm your acceptance.");
                    col.Item().PaddingTop(8).Text("We look forward to welcoming you to our team.");

                    col.Item().PaddingTop(24).Column(c =>
                    {
                        c.Item().Text("Accepted by: _________________________ Date: ______________").FontColor(Colors.Grey.Medium);
                        c.Item().PaddingTop(24).Text("On behalf of:");
                        c.Item().PaddingTop(30).Text("_________________________");
                        c.Item().Text(data.IssuedBy).SemiBold();
                        c.Item().Text("Human Resources");
                        c.Item().Text(data.CompanyName);
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span($"Confidential — {data.CompanyName} — KynexOne HRMS").FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });

        return Task.FromResult(doc.GeneratePdf());
    }

    /// <summary>
    /// Renders a tenant-configured letter: letterhead, merged body in one or both languages,
    /// the stored reference number, and a named signature block.
    ///
    /// <para>Three things this does that the four hard-coded letters do not: it prints a real
    /// company identity (name, Arabic name, CR number, logo) instead of falling back to the
    /// literal "KynexOne Technologies"; it prints the reference number that was allocated and
    /// stored, instead of recomputing a colliding one inline; and it names the human who issued
    /// it instead of signing "HR Department" over a row of underscores.</para>
    /// </summary>
    public Task<byte[]> GenerateTemplateLetterAsync(TemplateLetterData data, CancellationToken cancellationToken = default)
    {
        DocumentFonts.EnsureRegistered();

        var head = data.Letterhead;
        var primaryHex = head.PrimaryColorHex.TrimStart('#');
        var primary = (string)(uint.TryParse(primaryHex, System.Globalization.NumberStyles.HexNumber, null, out _)
            ? $"#{primaryHex}"
            : Colors.Blue.Darken3);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(42);
                page.DefaultTextStyle(s => s
                    .FontSize(10)
                    .LineHeight(1.5f)
                    .FontColor(Colors.Grey.Darken4)
                    .FontFamily(DocumentFonts.LatinFamily, DocumentFonts.ArabicFamily)
                    .TextLayerSafe());

                // ── Letterhead ────────────────────────────────────────────────────────
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        if (head.LogoBytes is { Length: > 0 })
                            row.ConstantItem(72).Height(48).Image(head.LogoBytes).FitArea();

                        row.RelativeItem().PaddingLeft(head.LogoBytes is { Length: > 0 } ? 10 : 0).Column(c =>
                        {
                            c.Item().Text(head.CompanyNameEn).FontSize(15).Bold().FontColor(primary);
                            if (!string.IsNullOrWhiteSpace(head.CompanyNameAr))
                                c.Item().Text(head.CompanyNameAr).FontSize(12).SemiBold().FontColor(primary);
                            if (!string.IsNullOrWhiteSpace(head.RegistrationNumber))
                                c.Item().Text($"CR / Reg. No. {head.RegistrationNumber}")
                                    .FontSize(8).FontColor(Colors.Grey.Darken1);
                        });

                        row.ConstantItem(150).AlignRight().Column(c =>
                        {
                            c.Item().Text($"Ref: {data.ReferenceNumber}").FontSize(9).SemiBold();
                            c.Item().Text($"Date: {data.IssuedOn:dd MMMM yyyy}").FontSize(9);
                            c.Item().Text($"التاريخ: {data.IssuedOn:dd/MM/yyyy}").FontSize(8)
                                .FontColor(Colors.Grey.Darken1);
                        });
                    });
                    col.Item().PaddingTop(8).LineHorizontal(1.4f).LineColor(primary);
                });

                page.Content().PaddingVertical(14).Column(col =>
                {
                    if (data.English is { } en)
                    {
                        col.Item().AlignCenter().Text(en.Title).FontSize(13).Bold().FontColor(primary);
                        foreach (var paragraph in en.Paragraphs)
                            col.Item().PaddingTop(9).Text(paragraph);
                        if (!string.IsNullOrWhiteSpace(en.Closing))
                            col.Item().PaddingTop(9).Text(en.Closing).FontSize(9).FontColor(Colors.Grey.Darken2);
                    }

                    if (data.English is not null && data.Arabic is not null)
                        col.Item().PaddingVertical(14).LineHorizontal(0.6f).LineColor(Colors.Grey.Lighten1);

                    if (data.Arabic is { } ar)
                    {
                        // ContentFromRightToLeft + DirectionFromRightToLeft give HarfBuzz the bidi
                        // base direction; without it the Arabic shapes correctly but paragraphs
                        // align and wrap from the wrong edge, which reads as broken to an Arabic
                        // speaker even though every glyph is right.
                        col.Item().ContentFromRightToLeft().Column(c =>
                        {
                            c.Item().AlignCenter().Text(ar.Title).FontSize(13).Bold().FontColor(primary);
                            foreach (var paragraph in ar.Paragraphs)
                                c.Item().PaddingTop(9).Text(paragraph).DirectionFromRightToLeft();
                            if (!string.IsNullOrWhiteSpace(ar.Closing))
                                c.Item().PaddingTop(9).Text(ar.Closing).FontSize(9)
                                    .FontColor(Colors.Grey.Darken2).DirectionFromRightToLeft();
                        });
                    }

                    // ── Signature block ───────────────────────────────────────────────
                    col.Item().PaddingTop(30).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Yours sincerely,").FontSize(9).FontColor(Colors.Grey.Darken2);
                            c.Item().PaddingTop(34).Width(200).LineHorizontal(0.8f).LineColor(Colors.Grey.Darken2);
                            c.Item().PaddingTop(3).Text(data.IssuerName).SemiBold();
                            c.Item().Text(data.IssuerTitle).FontSize(9).FontColor(Colors.Grey.Darken2);
                            c.Item().Text(head.CompanyNameEn).FontSize(9).FontColor(Colors.Grey.Darken2);
                        });

                        // Stamp area. An empty bordered box, deliberately: the product has no
                        // stamp image to print and inventing one on a document a bank relies on
                        // would be forgery. HR wet-stamps here, which is what they do today.
                        row.ConstantItem(150).Height(96).Border(0.8f)
                            .BorderColor(Colors.Grey.Lighten1).Padding(8).AlignCenter().AlignMiddle()
                            .Column(c =>
                            {
                                c.Item().AlignCenter().Text("Company stamp").FontSize(8).FontColor(Colors.Grey.Medium);
                                c.Item().AlignCenter().Text("ختم الشركة").FontSize(8).FontColor(Colors.Grey.Medium);
                            });
                    });
                });

                // Footer laid out as stacked lines rather than a Row: a long disclaimer and a
                // 19-character reference competing for one row clipped the reference, and a
                // half-printed reference is worse than none — it is the one string a bank reads
                // back over the phone.
                page.Footer().Column(col =>
                {
                    col.Item().PaddingBottom(3).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    var footer = string.IsNullOrWhiteSpace(head.FooterTextEn)
                        ? $"{head.CompanyNameEn} — this document is valid only with an authorised signature and the company stamp."
                        : head.FooterTextEn;
                    col.Item().Text(footer).FontSize(7).FontColor(Colors.Grey.Medium);
                    if (!string.IsNullOrWhiteSpace(head.FooterTextAr))
                        col.Item().AlignRight().Text(head.FooterTextAr).FontSize(7)
                            .FontColor(Colors.Grey.Medium).DirectionFromRightToLeft();
                    // The verifiable half of the letterhead: a third party holding the paper can
                    // quote this back to HR and the register will find exactly one match.
                    col.Item().Text($"Reference {data.ReferenceNumber}")
                        .FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(doc.GeneratePdf());
    }
}
