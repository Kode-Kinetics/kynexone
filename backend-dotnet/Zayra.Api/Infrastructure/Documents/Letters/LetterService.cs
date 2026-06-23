using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Zayra.Api.Infrastructure.Documents.Letters;

public class LetterService : ILetterService
{
    static LetterService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default)
    {
        var b = data.Branding ?? new();
        var ar = b.Locale is "ar" or "bilingual";

        var monthName = new DateTime(data.PayYear, data.PayMonth, 1).ToString("MMMM yyyy");
        var earnings   = data.Items.Where(i => i.Type == "Earning").ToList();
        var deductions = data.Items.Where(i => i.Type == "Deduction").ToList();
        var netItem    = data.Items.FirstOrDefault(i => i.Type == "Net");
        var grossTotal     = earnings.Sum(e => e.Amount);
        var deductionTotal = deductions.Sum(d => d.Amount);
        var netPay = netItem?.Amount ?? grossTotal - deductionTotal;

        // Parse branding colors to QuestPDF color strings (fallback to defaults on invalid hex)
        var primaryHex = b.PrimaryColor.TrimStart('#');
        var accentHex  = b.AccentColor.TrimStart('#');
        static bool IsValidHex(string hex) => uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out _);
        var primaryColor = (string)(IsValidHex(primaryHex) ? $"#{primaryHex}" : Colors.Blue.Darken3);
        var accentColor  = (string)(IsValidHex(accentHex)  ? $"#{accentHex}"  : Colors.Blue.Lighten3);

        // Logo bytes (null if not configured or file not found)
        byte[]? logoBytes = null;
        if (!string.IsNullOrEmpty(b.LogoStorageUrl) && System.IO.File.Exists(b.LogoStorageUrl))
        {
            try { logoBytes = System.IO.File.ReadAllBytes(b.LogoStorageUrl); } catch { /* non-fatal */ }
        }

        var earningsLabel   = ar ? "المستحقات"   : "EARNINGS";
        var deductionsLabel = ar ? "الاستقطاعات" : "DEDUCTIONS";
        var grossLabel      = ar ? "الإجمالي"    : "Gross Total";
        var deductTotalLbl  = ar ? "إجمالي الاستقطاعات" : "Total Deductions";
        var netLabel        = ar ? "صافي الراتب"  : "NET PAY";
        var noDeductLbl     = ar ? "لا توجد استقطاعات" : "No deductions";
        var empLabel        = ar ? "الموظف"       : "Employee";
        var codeLabel       = ar ? "الكود"        : "Code";
        var deptLabel       = ar ? "القسم"        : "Department";
        var desigLabel      = ar ? "المسمى"       : "Designation";
        var periodLabel     = ar ? "فترة الراتب"  : "Pay Period";
        var currencyLabel   = ar ? "العملة"       : "Currency";
        var payslipLabel    = ar ? "كشف راتب"     : "Payslip";
        var noLabel         = ar ? "رقم"          : "No";

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(35);
                page.DefaultTextStyle(s => s.FontSize(9).FontColor(Colors.Grey.Darken3));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        // Logo (if available)
                        if (logoBytes is { Length: > 0 })
                            row.ConstantItem(60).Height(40).Image(logoBytes).FitArea();

                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(data.CompanyName).FontSize(14).Bold().FontColor(primaryColor);
                            if (!string.IsNullOrWhiteSpace(data.CompanyNameAr))
                                c.Item().Text(data.CompanyNameAr).FontSize(11).FontColor(primaryColor);
                            if (!string.IsNullOrWhiteSpace(b.HeaderTextEn))
                                c.Item().Text(b.HeaderTextEn).FontSize(8).FontColor(Colors.Grey.Darken1);
                            if (ar && !string.IsNullOrWhiteSpace(b.HeaderTextAr))
                                c.Item().Text(b.HeaderTextAr).FontSize(8).FontColor(Colors.Grey.Darken1);
                            c.Item().Text(payslipLabel).FontSize(11).SemiBold().FontColor(Colors.Grey.Darken1);
                        });
                        row.ConstantItem(120).AlignRight().Column(c =>
                        {
                            c.Item().Text(monthName).FontSize(11).SemiBold();
                            c.Item().Text($"{noLabel}: {data.PayslipNumber}").FontSize(8).FontColor(Colors.Grey.Medium);
                        });
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
                            c.Item().Text($"{empLabel}: {data.EmployeeName}").SemiBold();
                            c.Item().Text($"{codeLabel}: {data.EmployeeCode}");
                        });
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text($"{deptLabel}: {data.Department}");
                            c.Item().Text($"{desigLabel}: {data.Designation}");
                        });
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text($"{periodLabel}: {monthName}");
                            c.Item().Text($"{currencyLabel}: {data.Currency}");
                        });
                    });

                    col.Item().PaddingTop(12).Row(row =>
                    {
                        // Earnings
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Background(Colors.Green.Lighten4).Padding(6).Text(earningsLabel).Bold().FontSize(8);
                            foreach (var e in earnings)
                                c.Item().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Row(r =>
                                {
                                    r.RelativeItem().Text(e.Name);
                                    r.ConstantItem(70).AlignRight().Text($"{e.Amount:N2}");
                                });
                            c.Item().Background(Colors.Green.Lighten3).Padding(5).Row(r =>
                            {
                                r.RelativeItem().Text(grossLabel).SemiBold();
                                r.ConstantItem(70).AlignRight().Text($"{grossTotal:N2}").SemiBold();
                            });
                        });

                        row.ConstantItem(20);

                        // Deductions
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Background(Colors.Red.Lighten4).Padding(6).Text(deductionsLabel).Bold().FontSize(8);
                            if (deductions.Count == 0)
                                c.Item().Padding(4).Text(noDeductLbl).FontColor(Colors.Grey.Medium);
                            foreach (var d in deductions)
                                c.Item().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Row(r =>
                                {
                                    r.RelativeItem().Text(d.Name);
                                    r.ConstantItem(70).AlignRight().Text($"{d.Amount:N2}");
                                });
                            c.Item().Background(Colors.Red.Lighten3).Padding(5).Row(r =>
                            {
                                r.RelativeItem().Text(deductTotalLbl).SemiBold();
                                r.ConstantItem(70).AlignRight().Text($"{deductionTotal:N2}").SemiBold();
                            });
                        });
                    });

                    // Net pay banner
                    col.Item().PaddingTop(12).Background(primaryColor).Padding(10).Row(row =>
                    {
                        row.RelativeItem().Text(netLabel).FontSize(12).Bold().FontColor(Colors.White);
                        row.ConstantItem(120).AlignRight().Text($"{data.Currency} {netPay:N2}").FontSize(13).Bold().FontColor(Colors.White);
                    });

                    col.Item().PaddingTop(20).Text("This is a system-generated payslip and is valid without a signature.").FontSize(7).FontColor(Colors.Grey.Medium).Italic();
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    var footerText = !string.IsNullOrWhiteSpace(ar ? b.FooterTextAr : b.FooterTextEn)
                        ? (ar ? b.FooterTextAr : b.FooterTextEn)
                        : "Generated by Zayra HRMS";
                    t.Span($"{footerText} — ").FontSize(7).FontColor(Colors.Grey.Medium);
                    t.Span((data.GeneratedOn ?? DateTime.UtcNow).ToString("dd MMM yyyy HH:mm") + " UTC").FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
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
                page.DefaultTextStyle(s => s.FontSize(10).FontColor(Colors.Grey.Darken3).LineHeight(1.5f));

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
                page.DefaultTextStyle(s => s.FontSize(10).FontColor(Colors.Grey.Darken3).LineHeight(1.6f));

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
                page.DefaultTextStyle(s => s.FontSize(10).FontColor(Colors.Grey.Darken3).LineHeight(1.6f));

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
}
