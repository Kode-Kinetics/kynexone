using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Data.Esign;

/// <summary>
/// P4 (click-to-sign acknowledgement) model additions, kept out of <c>ZayraDbContext</c> so the hot file carries one line.
/// Expand-only: every new column is nullable or defaulted, so no existing acknowledgement or document changes meaning.
/// The table-level checks only bind rows that are NOT <c>legacy_click</c>, so rows written before the migration stay valid.
/// Migration name for the integrator: <c>OnboardingP4Acknowledgements</c>; fold <see cref="AckAppendOnlySql"/> into it.
/// </summary>
public static class OnboardingP4EsignModel
{
    public const string MethodCheck = "ck_epa__method";
    public const string EvidenceCheck = "ck_epa__esign_evidence";
    public const string WitnessCheck = "ck_epa__witness";
    public const string ChainIndex = "ux_ack_chain";
    public const string StepUpJtiIndex = "ux_ack_stepup_jti";
    public const string SignPendingIndex = "ix_empdoc_sign_pending";
    public const string CertificateFk = "fk_epa__certificate_document";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmployeePolicyAcknowledgement>(entity =>
        {
            entity.ToTable("employee_policy_acknowledgements", t =>
            {
                t.HasCheckConstraint(MethodCheck, "method IN ('legacy_click','esign_ess','esign_hr_device')");
                t.HasCheckConstraint(EvidenceCheck,
                    "method = 'legacy_click' OR (document_sha256 IS NOT NULL AND typed_name IS NOT NULL AND ip_address IS NOT NULL " +
                    "AND record_hash IS NOT NULL AND chain_seq IS NOT NULL AND step_up_jti IS NOT NULL)");
                t.HasCheckConstraint(WitnessCheck, "(method = 'esign_hr_device') = (witness_user_id IS NOT NULL)");
            });
            entity.Property(x => x.Method).HasMaxLength(16).HasDefaultValue("legacy_click").IsRequired();
            entity.Property(x => x.DocumentSha256).HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.ConsentVersion).HasMaxLength(16);
            entity.Property(x => x.ConsentTextSha256).HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.TypedName).HasMaxLength(200);
            entity.Property(x => x.IpAddress).HasMaxLength(45);
            entity.Property(x => x.UserAgent).HasMaxLength(400);
            entity.Property(x => x.DeviceId).HasMaxLength(64);
            entity.Property(x => x.Language).HasMaxLength(2);
            entity.Property(x => x.AuthMethod).HasMaxLength(24);
            entity.Property(x => x.StepUpAtUtc).HasColumnName("step_up_at");
            entity.Property(x => x.PrevHash).HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.RecordHash).HasMaxLength(64).IsFixedLength();
            // One chain per tenant, no gap and no fork: a second writer taking the same sequence number loses on this index.
            entity.HasIndex(x => new { x.TenantId, x.ChainSeq }).HasDatabaseName(ChainIndex).IsUnique().HasFilter("method <> 'legacy_click'");
            // A step-up proof opens exactly one signature (the jti is the single-use ledger).
            entity.HasIndex(x => x.StepUpJti).HasDatabaseName(StepUpJtiIndex).IsUnique().HasFilter("step_up_jti IS NOT NULL");
            // Evidence must not orphan: the certificate document cannot be removed while an acknowledgement points at it.
            entity.HasOne<EmployeeDocument>().WithMany().HasForeignKey(x => x.CertificateDocumentId)
                  .HasConstraintName(CertificateFk).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        });

        modelBuilder.Entity<EmployeeDocument>(entity =>
        {
            entity.Property(x => x.SignatureRequiredAtUtc).HasColumnName("signature_required_at");
            entity.Property(x => x.IntegrityFlaggedAtUtc).HasColumnName("integrity_flagged_at");
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId }).HasDatabaseName(SignPendingIndex).HasFilter("signature_required_at IS NOT NULL");
        });
    }
}
