using Zayra.Api.Infrastructure.Documents.Letters;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Zayra.Api.Infrastructure.Documents.Invoices;

public record InvoiceData(
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string? PeriodDescription,
    decimal Amount,
    string CurrencyCode,
    string TenantName,
    string BillingEmail,
    string BillingCycle,
    string? Notes,
    IReadOnlyList<InvoiceLineItem> LineItems
);

public record InvoiceLineItem(
    string Description,
    int Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal LineTotal
);

public static class InvoicePdfService
{
    static InvoicePdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Generate(InvoiceData inv)
    {
        var sym = inv.CurrencyCode switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "AED" => "AED ",
            "SAR" => "SAR ",
            _     => inv.CurrencyCode + " "
        };

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(s => s.FontSize(9).FontColor(Colors.Grey.Darken3).TextLayerSafe());

                // ── Header ────────────────────────────────────────────────────
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("KynexOne Workforce").FontSize(18).Bold().FontColor("#1e3a5f");
                            c.Item().Text("Workforce Intelligence Platform").FontSize(9).FontColor(Colors.Grey.Medium);
                        });
                        row.ConstantItem(140).AlignRight().Column(c =>
                        {
                            c.Item().Background("#1e3a5f").Padding(8).AlignCenter()
                                .Text("TAX INVOICE").FontSize(13).Bold().FontColor(Colors.White);
                            c.Item().PaddingTop(4).Text($"# {inv.InvoiceNumber}").FontSize(10).SemiBold().AlignRight();
                        });
                    });
                    col.Item().PaddingTop(10).LineHorizontal(2).LineColor("#1e3a5f");
                });

                // ── Body ──────────────────────────────────────────────────────
                page.Content().PaddingTop(20).Column(col =>
                {
                    // Bill To + Invoice Details
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("BILL TO").FontSize(8).Bold().FontColor(Colors.Grey.Medium);
                            c.Item().PaddingTop(4).Text(inv.TenantName).FontSize(12).Bold().FontColor("#1e3a5f");
                            c.Item().Text(inv.BillingEmail).FontSize(9).FontColor(Colors.Grey.Darken2);
                            if (!string.IsNullOrWhiteSpace(inv.BillingCycle))
                                c.Item().Text($"Billing cycle: {inv.BillingCycle}").FontSize(8).FontColor(Colors.Grey.Medium);
                        });
                        row.ConstantItem(20);
                        row.ConstantItem(200).Background("#f1f5f9").Padding(10).Column(c =>
                        {
                            MetaRow(c, "Invoice Date", inv.InvoiceDate.ToString("dd MMM yyyy"));
                            MetaRow(c, "Due Date",     inv.DueDate.ToString("dd MMM yyyy"));
                            if (!string.IsNullOrWhiteSpace(inv.PeriodDescription))
                                MetaRow(c, "Period", inv.PeriodDescription);
                        });
                    });

                    col.Item().PaddingVertical(16).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

                    // Line items table header
                    col.Item().Background("#1e3a5f").Padding(8).Row(row =>
                    {
                        row.RelativeItem(3).Text("DESCRIPTION").FontSize(8).Bold().FontColor(Colors.White);
                        row.ConstantItem(30).AlignRight().Text("QTY").FontSize(8).Bold().FontColor(Colors.White);
                        row.ConstantItem(70).AlignRight().Text("UNIT PRICE").FontSize(8).Bold().FontColor(Colors.White);
                        row.ConstantItem(60).AlignRight().Text("DISCOUNT").FontSize(8).Bold().FontColor(Colors.White);
                        row.ConstantItem(55).AlignRight().Text("TAX").FontSize(8).Bold().FontColor(Colors.White);
                        row.ConstantItem(70).AlignRight().Text("TOTAL").FontSize(8).Bold().FontColor(Colors.White);
                    });

                    // Line item rows
                    var isEven = false;
                    foreach (var item in inv.LineItems)
                    {
                        isEven = !isEven;
                        col.Item().Background(isEven ? "#f8fafc" : Colors.White).Padding(8).Row(row =>
                        {
                            row.RelativeItem(3).Text(item.Description).FontSize(9);
                            row.ConstantItem(30).AlignRight().Text(item.Quantity.ToString()).FontSize(9);
                            row.ConstantItem(70).AlignRight().Text($"{sym}{item.UnitPrice:N2}").FontSize(9);
                            row.ConstantItem(60).AlignRight()
                               .Text(item.DiscountAmount > 0 ? $"-{sym}{item.DiscountAmount:N2}" : "-")
                               .FontSize(9);
                            row.ConstantItem(55).AlignRight()
                               .Text(item.TaxAmount > 0 ? $"{sym}{item.TaxAmount:N2}" : "-")
                               .FontSize(9);
                            row.ConstantItem(70).AlignRight()
                               .Text($"{sym}{item.LineTotal:N2}").FontSize(9).SemiBold();
                        });
                    }

                    // Subtotal / total
                    var subtotal     = inv.LineItems.Sum(l => l.UnitPrice * l.Quantity - l.DiscountAmount);
                    var totalDisc    = inv.LineItems.Sum(l => l.DiscountAmount);
                    var totalTax     = inv.LineItems.Sum(l => l.TaxAmount);
                    var grandTotal   = inv.LineItems.Sum(l => l.LineTotal);

                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                    col.Item().PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem();
                        row.ConstantItem(220).Column(c =>
                        {
                            if (totalDisc > 0)
                                SummaryRow(c, "DISCOUNT", $"-{sym}{totalDisc:N2}");
                            if (totalTax > 0)
                                SummaryRow(c, "TAX", $"{sym}{totalTax:N2}");
                            SummaryRow(c, "SUBTOTAL", $"{sym}{subtotal:N2}");
                            c.Item().Background("#1e3a5f").Padding(10).Row(r =>
                            {
                                r.RelativeItem().Text("TOTAL DUE").FontSize(11).Bold().FontColor(Colors.White);
                                r.ConstantItem(100).AlignRight()
                                    .Text($"{sym}{grandTotal:N2}").FontSize(11).Bold().FontColor("#60a5fa");
                            });
                        });
                    });

                    // Notes / payment instructions
                    col.Item().PaddingTop(24).Column(c =>
                    {
                        c.Item().Text("PAYMENT INSTRUCTIONS").FontSize(8).Bold().FontColor(Colors.Grey.Medium);
                        c.Item().PaddingTop(4).Text(
                            "Please arrange bank transfer or use the payment method agreed with your account manager. " +
                            "Quote the invoice number in the payment reference. " +
                            "Payment is due by the date shown above."
                        ).FontSize(8).FontColor(Colors.Grey.Darken2);

                        if (!string.IsNullOrWhiteSpace(inv.Notes))
                        {
                            c.Item().PaddingTop(10).Text("NOTES").FontSize(8).Bold().FontColor(Colors.Grey.Medium);
                            c.Item().PaddingTop(4).Text(inv.Notes).FontSize(8).FontColor(Colors.Grey.Darken2);
                        }
                    });
                });

                // ── Footer ────────────────────────────────────────────────────
                page.Footer().BorderTop(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingTop(8).Row(row =>
                {
                    row.RelativeItem().Text("KynexOne Workforce · This is a system-generated invoice.")
                        .FontSize(7).FontColor(Colors.Grey.Medium);
                    row.ConstantItem(160).AlignRight()
                        .Text($"Generated {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC")
                        .FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static void MetaRow(ColumnDescriptor c, string label, string value)
    {
        c.Item().PaddingBottom(4).Row(r =>
        {
            r.ConstantItem(80).Text(label + ":").FontSize(8).FontColor(Colors.Grey.Medium);
            r.RelativeItem().Text(value).FontSize(8).SemiBold();
        });
    }

    private static void SummaryRow(ColumnDescriptor c, string label, string value)
    {
        c.Item().Padding(8).Row(r =>
        {
            r.RelativeItem().Text(label).FontSize(9).SemiBold();
            r.ConstantItem(100).AlignRight().Text(value).FontSize(9);
        });
    }
}
