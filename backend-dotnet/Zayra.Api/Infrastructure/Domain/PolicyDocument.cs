namespace Zayra.Api.Domain.Entities;
public class PolicyDocument : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Status { get; set; } = "Processing"; // Processing, Ready, Failed
    public int ChunkCount { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? UploadedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    // Parsing readiness does not grant employee visibility. Existing records remain drafts.
    public Guid? CompanyId { get; set; }
    public string PublicationStatus { get; set; } = "Draft";
    public DateTime? EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public Guid? PublishedByUserId { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
}
