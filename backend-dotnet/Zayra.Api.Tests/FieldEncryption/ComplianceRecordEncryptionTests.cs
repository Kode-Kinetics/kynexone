using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

/// <summary>
/// The identity numbers a hire form stores as compliance records (Iqama, national ID, passport, border number) were kept as plaintext in
/// <c>employee_compliance_records.field_value</c> even with field encryption on. The value column is now in the encrypted-field catalog,
/// so the save interceptor keeps a ciphertext beside it exactly as for the same numbers on the employee row.
/// </summary>
public sealed class ComplianceRecordEncryptionTests
{
    [Fact]
    public void TheComplianceRecordValueColumn_IsInTheEncryptedFieldCatalog()
    {
        var field = EncryptedFieldCatalog.Find(typeof(EmployeeComplianceRecord), nameof(EmployeeComplianceRecord.FieldValue));
        field.Should().NotBeNull("an Iqama or passport number stored as a compliance record is as sensitive as the same number on the employee");
        field!.CipherColumn.Should().Be("field_value_enc");
        field.PlainColumn.Should().Be("field_value");
    }

    [Fact]
    public void TheModelGivesTheComplianceRecordAShadowCiphertextColumn_WithTheEnvelopeShapeCheck()
    {
        using var db = new Zayra.Api.Data.ZayraDbContext(new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p").Options);
        var model = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)db).Instance
            .GetRequiredService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(EmployeeComplianceRecord))!;
        var cipher = entity.FindProperty("FieldValueEnc");
        cipher.Should().NotBeNull();
        cipher!.IsShadowProperty().Should().BeTrue("no controller or serializer may reach the ciphertext");
        cipher.GetColumnName().Should().Be("field_value_enc");
        entity.GetCheckConstraints().Select(c => c.Name).Should().Contain("ck_employee_compliance_records_field_value_enc_shape");
    }

    [Fact]
    public void TheMigrationAddsOnlyTheColumnAndAnUnscannedCheck()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api", "Migrations"))) dir = dir.Parent;
        var file = Directory.GetFiles(Path.Combine(dir!.FullName, "Zayra.Api", "Migrations"), "*_AddComplianceRecordFieldEncryption.cs").Single();
        var text = File.ReadAllText(file);
        text.Should().Contain("field_value_enc").And.Contain("NOT VALID").And.Contain("VALIDATE CONSTRAINT").And.Contain("lock_timeout");
        text.Should().NotContain("DropColumn(\n                name: \"field_value\"", "expand only: the plaintext column stays until the reader release");
    }
}
