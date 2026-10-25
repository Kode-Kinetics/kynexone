using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostExportHarness;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-D slice 1: scale-aware journal amounts (cost-accounting contract 2.6, T-062, D-1). A 3-dp currency (KWD, BHD, OMR)
/// must be rounded, balanced, emitted and reconciled at 3 dp, and the Lock-side rule (CurrencyMinorUnits) and the
/// export-side rule (JournalAmount) must be the same rule. A legacy export (null amount_scale) stays 2 dp.
/// </summary>
public class JournalExportScaleTests
{
    private static decimal D(string v) => decimal.Parse(v, CultureInfo.InvariantCulture);

    // ── The published JournalAmount API ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.2345", 3, "1.235")]
    [InlineData("-1.2345", 3, "-1.235")]
    [InlineData("1.00049", 3, "1.000")]
    [InlineData("1.005", 2, "1.01")]
    [InlineData("-1.005", 2, "-1.01")]
    [InlineData("2.5", 0, "3")]
    public void Round_IsHalfAwayFromZero_AtTheGivenScale(string value, int scale, string expected) =>
        JournalAmount.Round(D(value), scale).Should().Be(D(expected));

    [Fact]
    public void Round_AndEmit_WithoutAScale_KeepTheLegacyTwoDecimalDefault()
    {
        JournalAmount.Round(D("1.005")).Should().Be(1.01m);
        JournalAmount.Emit(D("10.5")).Should().Be("10.50");
        JournalAmount.Emit(D("10.5"), 3).Should().Be("10.500");
        JournalAmount.Emit(D("-0.5"), 3).Should().Be("-0.500");
    }

    [Theory]
    [InlineData("KWD", 3)]
    [InlineData("bhd", 3)]
    [InlineData("OMR", 3)]
    [InlineData("SAR", 2)]
    [InlineData("AED", 2)]
    [InlineData("QAR", 2)]
    [InlineData("USD", 2)]
    [InlineData("", 2)]
    public void ScaleFor_FollowsTheCurrencyMinorUnits(string currency, int scale) =>
        JournalAmount.ScaleFor(currency).Should().Be(scale);

    [Fact]
    public void LockAndExport_RoundTheSameWay_ForEveryCurrencyAndAmount()
    {
        // Contract 2.6: "Lock and export use the same rule." CurrencyMinorUnits is the Lock rule.
        var amounts = new[] { "0.0005", "1.005", "1.0005", "2.675", "99999.9995", "-1.0005", "0.0049", "-0.0005", "1234.5675" };
        foreach (var currency in new[] { "SAR", "AED", "QAR", "KWD", "BHD", "OMR", "USD", "" })
            foreach (var a in amounts.Select(D))
                JournalAmount.Round(a, JournalAmount.ScaleFor(currency))
                    .Should().Be(CurrencyMinorUnits.Round(a, currency), $"{a} in {currency}");
    }

    // ── Lines, tie-out and documents carry the scale ──────────────────────────────────────────────

    [Fact]
    public void Expand_StampsTheCurrencyScaleOnEveryLine()
    {
        var kwd = JournalExportBuilder.Expand(new[] { Row(Guid.NewGuid(), "Accrual", "5000 - Salaries", "", 10m, currency: "KWD") }, "SA01");
        var sar = JournalExportBuilder.Expand(new[] { Row(Guid.NewGuid(), "Accrual", "5000 - Salaries", "", 10m) }, "SA01");

        kwd.Should().OnlyContain(l => l.Scale == 3);
        sar.Should().OnlyContain(l => l.Scale == 2);
    }

    [Fact]
    public void Verify_IsExactAtTheLineScale()
    {
        // DR 1.004 / CR 1.000: equal at 2 dp (SAR), different at 3 dp (KWD). The gate is zero-tolerance on the EMITTED values.
        JournalTieOut Tie(string currency)
        {
            var rows = new[]
            {
                Row(Guid.NewGuid(), "Accrual", "5000 - Salaries", "", 1.004m, currency: currency),
                Row(Guid.NewGuid(), "Accrual", "", "2100 - Payable", 1.000m, currency: currency),
            };
            return JournalExportBuilder.Verify(JournalExportBuilder.Expand(rows, "SA01"), rows);
        }

        Tie("SAR").Ok.Should().BeTrue("both sides emit 1.00");
        var kwd = Tie("KWD");
        kwd.Ok.Should().BeFalse("1.004 and 1.000 are different fils");
        kwd.Failure.Should().Be("debits_credits_unbalanced");
        kwd.RoundedDebits.Should().Be(1.004m);
    }

    [Fact]
    public void PostingDocuments_NetAtTheLineScale()
    {
        JournalExportDocument DocOf(string currency) => Doc(new[]
        {
            Row(Guid.NewGuid(), "Accrual", "5000 - Salaries", "", 1.004m, currency: currency),
            Row(Guid.NewGuid(), "Accrual", "", "2100 - Payable", 1.000m, currency: currency),
        }, currency);

        DocOf("SAR").PostingDocuments().Single().Composite.Should().BeFalse("1.00 = 1.00 at two decimals");
        var kwd = DocOf("KWD").PostingDocuments().Single();
        kwd.Composite.Should().BeTrue("a 4-fil difference does not net at three decimals");
        kwd.Debits.Should().Be(1.004m);
        kwd.Credits.Should().Be(1.000m);
    }

    [Fact]
    public void EveryFormatter_EmitsAmountsAtTheLineScale()
    {
        foreach (var formatter in JournalExportFormatterGoldenTests.AllFormatters())
        {
            string TextOf(string currency)
            {
                var doc = Doc(new[]
                {
                    Row(Guid.NewGuid(), "Accrual", "5000 - Salaries", "", 10.5m, currency: currency),
                    Row(Guid.NewGuid(), "Accrual", "", "2100 - Payable", 10.5m, currency: currency),
                }, currency);
                return Encoding.Latin1.GetString(formatter.Format(doc));
            }

            TextOf("KWD").Should().Contain("10.500", $"{formatter.Key} must carry fils for a 3-dp currency");
            var sar = TextOf("SAR");
            sar.Should().Contain("10.50").And.NotContain("10.500", $"{formatter.Key} stays 2 dp for SAR");
        }
    }

    // ── Persist / regenerate (T-062) ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("KWD", 3, "100.124")]
    [InlineData("SAR", 2, "100.12")]
    public async Task Persist_FreezesTheCurrencyScale_AndTheFileRegeneratesIdentically(string currency, int scale, string rounded)
    {
        var (db, tenantId) = await TenantAsync(currency);
        db.FinanceGlEntries.AddRange(
            Row(tenantId, "Accrual", "5000 - Salaries", "", 100.1235m, currency: currency),
            Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 100.1235m, currency: currency));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var created = await CreateAsync(db, maker, "generic-csv", currency);

        StatusOf(created).Should().Be(201);
        var export = await db.GlJournalExports.AsNoTracking().SingleAsync();
        export.AmountScale.Should().Be((short)scale);
        export.TotalDebits.Should().Be(D(rounded));
        var first = await DownloadAsync(db, tenantId, export.Id);
        var second = await DownloadAsync(db, tenantId, export.Id);
        first.Should().Equal(second);
        Utf8(first).Should().Contain(rounded);
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first)).Should().BeEquivalentTo(export.FileHash);
    }

    [Fact]
    public async Task ALegacyExport_WithNoAmountScale_RegeneratesAtTwoDecimals()
    {
        var (db, tenantId) = await TenantAsync();
        db.FinanceGlEntries.AddRange(
            Row(tenantId, "Accrual", "5000 - Salaries", "", 100.1235m),
            Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 100.1235m));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var id = IdOf(await CreateAsync(db, maker));
        var before = await DownloadAsync(db, tenantId, id);
        var export = await db.GlJournalExports.SingleAsync(x => x.Id == id);
        export.AmountScale = null;                       // exactly what a pre-scale row holds
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var after = await DownloadAsync(db, tenantId, id);

        after.Should().Equal(before, "null means legacy, which is 2 dp, and this export was 2 dp");
    }

    [Fact]
    public void ToDocument_ReadsTheFrozenScale_NullIsTwo()
    {
        GlJournalExportLine Stored() => new()
        {
            LineNo = 1, Side = "DR", AccountCode = "5000", Amount = 100.1235m, Currency = "KWD", Period = Period,
            JournalRef = "J", CompanyCode = "SA01",
        };
        GlJournalExport Export(short? scale) => new()
        {
            Id = Guid.NewGuid(), CompanyCode = "SA01", Currency = "KWD", FormatVersion = JournalExportService.CurrentFormatVersion,
            AmountScale = scale,
        };

        JournalExportService.ToDocument(Export(3), new[] { Stored() }).Lines.Should().OnlyContain(l => l.Scale == 3);
        JournalExportService.ToDocument(Export(null), new[] { Stored() }).Lines.Should().OnlyContain(l => l.Scale == 2);
        JournalExportService.ToDocument(Export(2), new[] { Stored() }).Lines.Should().OnlyContain(l => l.Scale == 2);
    }

    // ── Reconciler (PeriodHandoffReconciler) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Reconciler_TiesOutAKuwaitiPeriod_AtThreeDecimals()
    {
        var (db, tenantId) = await TenantAsync("KWD");
        db.FinanceGlEntries.AddRange(
            Row(tenantId, "Accrual", "5000 - Salaries", "", 10.005m, currency: "KWD"),
            Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10.005m, currency: "KWD"));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        StatusOf(await CreateAsync(db, maker, "generic-csv", "KWD")).Should().Be(201);

        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var report = JsonSerializer.SerializeToElement(await new PeriodHandoffReconciler(db, exports)
            .ReconcileAsync(tenantId, new JournalExportFilter(Period), groupScopedCaller: true, default));

        report.GetProperty("posted").GetProperty("totalDebits").GetDecimal().Should().Be(10.005m, "10.005 KWD is not 10.01");
        report.GetProperty("posted").GetProperty("byCurrency")[0].GetProperty("total").GetDecimal().Should().Be(20.010m);
        report.GetProperty("exported").GetProperty("exportedTotalDebits").GetDecimal().Should().Be(10.005m);
        report.GetProperty("deltas").GetProperty("postedMinusExported").GetDecimal().Should().Be(0m);
    }
}
