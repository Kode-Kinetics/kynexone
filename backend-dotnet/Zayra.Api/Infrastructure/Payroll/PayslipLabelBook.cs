using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Arabic names for the pay items on a payslip. The payslip carries each item as its English stored name; a
/// tenant that names its pay components in Arabic (<c>PayComponent.NameAr</c>) gets that wording on the Arabic
/// payslip. Items with no Arabic name are left alone: the renderer falls back to the fixed catalogue
/// (<see cref="PayslipArabic"/>) and then to the English name, never to blank.
/// </summary>
public sealed class PayslipLabelBook
{
    private readonly Dictionary<string, string> _arabicByEnglish;

    private PayslipLabelBook(Dictionary<string, string> arabicByEnglish) => _arabicByEnglish = arabicByEnglish;

    public static async Task<PayslipLabelBook> LoadAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.PayComponents.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.NameAr != "" && c.NameEn != "")
            .Select(c => new { c.NameEn, c.NameAr })
            .ToListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) map.TryAdd(r.NameEn.Trim(), r.NameAr.Trim());
        return new PayslipLabelBook(map);
    }

    public List<PayslipLineItem> Apply(IEnumerable<PayslipLineItem> items) =>
        items.Select(i => string.IsNullOrWhiteSpace(i.NameAr) && _arabicByEnglish.TryGetValue(i.Name.Trim(), out var ar)
            ? i with { NameAr = ar }
            : i).ToList();
}
