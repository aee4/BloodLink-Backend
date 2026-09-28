using BloodLink.Domain.Common;

namespace BloodLink.Domain.Entities;

public sealed class RefreshSession : Entity
{
    public string UserId { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string SecurityStampHash { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}
