using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.FieldEncryption;

namespace Zayra.Api.Tests.FieldEncryption;

/// <summary>
/// Coverage ratchet. A new identifier-shaped string column on a mapped entity must either join the catalog (and so be
/// encrypted) or be listed here with the reason it is not sensitive. Without this, "we encrypt IBANs" quietly stops being
/// true the day someone adds <c>Contractor.Iban</c>.
/// </summary>
public class FieldEncryptionCoverageRatchetTests
{
    private static readonly Regex Shape = new(
        @"(Iban|AccountNumber|AccountNo|BankAccount|Iqama|Passport|NationalId|IdNumber|Qid|CivilId|EmiratesId|MolId|BorderNumber|WpsBankDetails|VisaNumber|NationalIdNumber|IdentityNumber|SocialSecurity)",
        RegexOptions.IgnoreCase);

    /// <summary>Entity.Property -> why it is not encrypted. Every entry is a decision; none is a TODO.</summary>
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["VisaRecord.VisaNumber"] = "Visa register copy of Employee.VisaNumber; same reasoning.",
        ["EmployeeDraft.VisaNumber"] = "Draft copy of Employee.VisaNumber; same reasoning.",
        ["Employee.VisaNumber"] = "A visa/permit document reference, not a bank or identity credential, and outside the owner-approved field list " +
                                  "(IBAN, account, iqama/national ID, MOL id, passport, Emirates ID/QID/Civil ID, border number, WPS bank details). Revisit with the reader release.",
    };

    private static ZayraDbContextProbe Probe() => new();

    private sealed class ZayraDbContextProbe : IDisposable
    {
        // Npgsql provider, never connected: gives the full relational model (column types, check constraints, filtered indexes).
        public Zayra.Api.Data.ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p").Options);
        public Microsoft.EntityFrameworkCore.Metadata.IModel Model => ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)Db).Instance
            .GetRequiredService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void EveryCatalogEntry_IsAStringProperty_WithShadowCiphertextAndIndexColumns_InTheModel()
    {
        using var p = Probe();
        foreach (var f in EncryptedFieldCatalog.All)
        {
            var et = p.Model.FindEntityType(f.Entity);
            et.Should().NotBeNull($"{f.Entity.Name} must be mapped");
            var plain = et!.FindProperty(f.Property);
            plain.Should().NotBeNull(f.Property);
            plain!.ClrType.Should().Be(typeof(string), f.Property);
            var enc = et.FindProperty(f.CipherProperty);
            enc.Should().NotBeNull($"{f.Entity.Name}.{f.CipherProperty}");
            enc!.IsShadowProperty().Should().BeTrue();
            enc.GetColumnName().Should().Be(f.CipherColumn);
            enc.GetColumnType().Should().Be("bytea");
            enc.IsNullable.Should().BeTrue();
            var idx = et.FindProperty(f.BlindIndexProperty);
            if (f.BlindIndexed)
            {
                idx.Should().NotBeNull(); idx!.IsShadowProperty().Should().BeTrue(); idx.GetColumnName().Should().Be(f.BlindIndexColumn);
            }
            else idx.Should().BeNull();
            et.FindProperty("TenantId").Should().NotBeNull($"{f.Entity.Name} needs a tenant to pick the key");
        }
    }

    [Fact]
    public void EveryCatalogEntry_HasShapeChecks_AndPlaintextColumnNameMatches()
    {
        using var p = Probe();
        foreach (var f in EncryptedFieldCatalog.All)
        {
            var et = p.Model.FindEntityType(f.Entity)!;
            et.FindProperty(f.Property)!.GetColumnName().Should().Be(f.PlainColumn, "AAD and blind-index derivation use the plaintext column name");
            var checks = et.GetCheckConstraints().Select(c => c.Name).ToList();
            checks.Should().Contain($"ck_{et.GetTableName()}_{f.CipherColumn}_shape");
            if (f.BlindIndexed) checks.Should().Contain($"ck_{et.GetTableName()}_{f.BlindIndexColumn}_pair");
        }
    }

    [Fact]
    public void NoSensitiveShapedStringColumn_IsLeftUncovered()
    {
        using var p = Probe();
        var missing = new List<string>();
        foreach (var et in p.Model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            foreach (var prop in et.GetProperties().Where(x => x.ClrType == typeof(string) && !x.IsShadowProperty() && Shape.IsMatch(x.Name)))
            {
                var key = $"{et.ClrType.Name}.{prop.Name}";
                if (EncryptedFieldCatalog.Find(et.ClrType, prop.Name) is null && !Exempt.ContainsKey(key)) missing.Add(key);
            }
        }
        missing.Should().BeEmpty("each of these looks like a bank/identity number: add it to EncryptedFieldCatalog (and a migration) or exempt it with a reason");
    }

    [Fact]
    public void ExemptionList_HasNoStaleOrReasonlessEntries()
    {
        using var p = Probe();
        foreach (var (key, reason) in Exempt)
        {
            reason.Should().NotBeNullOrWhiteSpace(key);
            var parts = key.Split('.');
            p.Model.GetEntityTypes().Any(e => e.ClrType.Name == parts[0] && e.FindProperty(parts[1]) is not null).Should().BeTrue($"{key} no longer exists - delete the exemption");
        }
    }

    [Fact]
    public void KeyTable_IsTenantScoped_WithOneActiveKeyPerTenantAndNoPlaintextColumns()
    {
        using var p = Probe();
        var et = p.Model.FindEntityType(typeof(Zayra.Api.Models.FieldEncryptionKey))!;
        et.GetTableName().Should().Be("field_encryption_keys");
        et.GetIndexes().Should().Contain(i => i.IsUnique && i.GetFilter() == "status = 'Active'");
        et.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Select(x => x.Name).SequenceEqual(new[] { "TenantId", "KeyVersion" }));
        et.GetProperties().Select(x => x.GetColumnName()).Should().NotContain(c => c.Contains("plain"));
    }
}
