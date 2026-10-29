using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

public class FieldEncryptionWritePathTests
{
    private static readonly EncryptedField Iqama = EncryptedFieldCatalog.Find(typeof(Employee), nameof(Employee.IqamaNumber))!;
    private static readonly EncryptedField Iban = EncryptedFieldCatalog.Find(typeof(Employee), nameof(Employee.BankIban))!;

    private static byte[]? Enc(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry e, string prop) => (byte[]?)e.Property(prop).CurrentValue;

    [Fact]
    public async Task DualWrite_Insert_WritesCiphertextAndBlindIndex_AndLeavesPlaintextUntouched()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using var db = kit.Db();
        var e = FeKit.NewEmployee(t, "E1", iqama: "2123456789", iban: "SA0380000000608010167519");
        db.Employees.Add(e);
        await db.SaveChangesAsync();

        e.IqamaNumber.Should().Be("2123456789", "DualWrite keeps the plaintext column exactly as the app set it");
        var entry = db.Entry(e);
        var ring = await kit.Keys.GetRingAsync(t, false, default);
        FieldCipher.Decrypt(ring, "employees", Iqama, "", Enc(entry, "IqamaNumberEnc")!).Should().Be("2123456789");
        FieldCipher.Decrypt(ring, "employees", Iban, "", Enc(entry, "BankIbanEnc")!).Should().Be("SA0380000000608010167519");
        Enc(entry, "IqamaNumberBidx").Should().Equal(BlindIndex.Compute(ring.BlindIndexRoot, "employees", "iqama_number", "2123456789"));
        Enc(entry, "PassportNumberEnc").Should().BeNull("blank plaintext has nothing to protect");
        Enc(entry, "PassportNumberBidx").Should().BeNull();
    }

    [Fact]
    public async Task ShadowColumns_ArePersisted_NotJustSetInMemory()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using (var db = kit.Db()) { db.Employees.Add(FeKit.NewEmployee(t, "E1", iqama: "2123456789")); await db.SaveChangesAsync(); }
        await using var read = kit.Db(interceptors: false);
        var e = await read.Employees.SingleAsync();
        read.Entry(e).Property("IqamaNumberEnc").CurrentValue.Should().NotBeNull();
    }

    [Fact]
    public async Task ModeOff_WritesNothing_AndNeverCreatesAKey()
    {
        var kit = new FeKit(FieldEncryptionMode.Off); var t = Guid.NewGuid();
        await using var db = kit.Db();
        var e = FeKit.NewEmployee(t, "E1", iqama: "2123456789");
        db.Employees.Add(e); await db.SaveChangesAsync();
        Enc(db.Entry(e), "IqamaNumberEnc").Should().BeNull();
        kit.Store.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Update_ReEncryptsOnlyTheFieldThatChanged()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using var db = kit.Db();
        var e = FeKit.NewEmployee(t, "E1", iqama: "2123456789", iban: "SA0380000000608010167519");
        db.Employees.Add(e); await db.SaveChangesAsync();
        var iqamaBefore = Enc(db.Entry(e), "IqamaNumberEnc")!.ToArray();
        var ibanBefore = Enc(db.Entry(e), "BankIbanEnc")!.ToArray();

        e.FullName = "Renamed"; await db.SaveChangesAsync();
        Enc(db.Entry(e), "IqamaNumberEnc").Should().Equal(iqamaBefore, "an unrelated save does no crypto");

        e.IqamaNumber = "2987654321"; await db.SaveChangesAsync();
        Enc(db.Entry(e), "BankIbanEnc").Should().Equal(ibanBefore);
        var ring = await kit.Keys.GetRingAsync(t, false, default);
        FieldCipher.Decrypt(ring, "employees", Iqama, "", Enc(db.Entry(e), "IqamaNumberEnc")!).Should().Be("2987654321");
        Enc(db.Entry(e), "IqamaNumberBidx").Should().Equal(BlindIndex.Compute(ring.BlindIndexRoot, "employees", "iqama_number", "2987654321"));
    }

    [Fact]
    public async Task ClearingAField_ClearsItsCiphertextAndIndex()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using var db = kit.Db();
        var e = FeKit.NewEmployee(t, "E1", iqama: "2123456789");
        db.Employees.Add(e); await db.SaveChangesAsync();
        e.IqamaNumber = ""; await db.SaveChangesAsync();
        Enc(db.Entry(e), "IqamaNumberEnc").Should().BeNull();
        Enc(db.Entry(e), "IqamaNumberBidx").Should().BeNull();
    }

    [Fact]
    public async Task TwoTenants_SameValue_DifferentCiphertextAndIndex()
    {
        var kit = new FeKit(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await using var db = kit.Db();
        var ea = FeKit.NewEmployee(a, "E1", iqama: "2123456789"); var eb = FeKit.NewEmployee(b, "E1", iqama: "2123456789");
        db.Employees.AddRange(ea, eb); await db.SaveChangesAsync();
        Enc(db.Entry(ea), "IqamaNumberBidx").Should().NotEqual(Enc(db.Entry(eb), "IqamaNumberBidx"));
        Enc(db.Entry(ea), "IqamaNumberEnc").Should().NotEqual(Enc(db.Entry(eb), "IqamaNumberEnc"));
        kit.Store.Snapshot().Select(r => r.TenantId).Should().BeEquivalentTo([a, b]);
    }

    [Fact]
    public async Task SyncSaveChanges_EncryptsToo()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using var db = kit.Db();
        var e = FeKit.NewEmployee(t, "E1", iqama: "2123456789");
        db.Employees.Add(e); db.SaveChanges();
        Enc(db.Entry(e), "IqamaNumberEnc").Should().NotBeNull();
    }

    [Fact]
    public async Task RevokedTenant_CannotWriteAProtectedField_ButCanSaveUnrelatedChanges()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using var db = kit.Db();
        var e = FeKit.NewEmployee(t, "E1", iqama: "2123456789");
        db.Employees.Add(e); await db.SaveChangesAsync();
        await kit.Keys.ExportRecoveryAsync(t, x => RecoveryCopy.SealWithPassphrase(t, x, "correct horse battery staple 42"), default);
        await kit.RevokeNowAsync(t);

        e.FullName = "Still editable"; await db.SaveChangesAsync();
        e.IqamaNumber = "2000000000";
        Func<Task> act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<FieldEncryptionRevokedException>();
    }

    [Fact]
    public async Task DraftsAndPayrollProfiles_AreCoveredToo_WithoutABlindIndexWhereNoneIsDeclared()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using var db = kit.Db();
        var draft = new EmployeeDraft { TenantId = t, PassportNumber = "P1234567" };
        var profile = new EmployeePayrollProfile { TenantId = t, EmployeeId = 1, Iban = "SA0380000000608010167519", AccountNumber = "6080101", MolId = "MOL1" };
        db.AddRange(draft, profile); await db.SaveChangesAsync();
        Enc(db.Entry(draft), "PassportNumberEnc").Should().NotBeNull();
        db.Entry(draft).Metadata.FindProperty("PassportNumberBidx").Should().BeNull();
        Enc(db.Entry(profile), "IbanEnc").Should().NotBeNull();
        Enc(db.Entry(profile), "IbanBidx").Should().NotBeNull();
        Enc(db.Entry(profile), "AccountNumberEnc").Should().NotBeNull();
        Enc(db.Entry(profile), "MolIdEnc").Should().NotBeNull();
    }

    [Fact]
    public async Task WhereBlindIndex_FindsTheRow_AcrossFormatVariants_AndNeverAcrossTenants()
    {
        var kit = new FeKit(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await using (var db = kit.Db())
        {
            db.Employees.AddRange(FeKit.NewEmployee(a, "A1", iqama: "2123456789"), FeKit.NewEmployee(a, "A2", iqama: "2999999999"), FeKit.NewEmployee(b, "B1", iqama: "2123456789"));
            await db.SaveChangesAsync();
        }
        await using var q = kit.Db();
        var ringA = await kit.Keys.GetRingAsync(a, false, default);
        var hit = await q.Employees.WhereBlindIndex(q, ringA, x => x.IqamaNumber, " 2123456789 ").ToListAsync();
        hit.Select(x => x.EmployeeCode).Should().Equal("A1");
        (await q.Employees.WhereBlindIndex(q, ringA, x => x.IqamaNumber, "").ToListAsync()).Should().BeEmpty();
        Func<IQueryable<Employee>> noIndex = () => q.Employees.WhereBlindIndex(q, ringA, x => x.WpsBankDetails, "x");
        noIndex.Should().Throw<ArgumentException>();
    }
}
