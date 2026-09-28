using BloodLink.Domain.Entities;
using BloodLink.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BloodLink.Infrastructure.Data.Configurations;

public sealed class RefreshSessionConfiguration : IEntityTypeConfiguration<RefreshSession>
{
    public void Configure(EntityTypeBuilder<RefreshSession> builder)
    {
        builder.ToTable("RefreshSessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.UserId).HasMaxLength(450).IsRequired();
        builder.Property(session => session.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(session => session.ReplacedByTokenHash).HasMaxLength(64);
        builder.Property(session => session.SecurityStampHash).HasMaxLength(64).IsRequired();
        builder.Property(session => session.RowVersion).IsRowVersion();
        builder.HasIndex(session => session.TokenHash).IsUnique();
        builder.HasIndex(session => new { session.UserId, session.FamilyId, session.RevokedAtUtc });
        builder.HasIndex(session => session.ExpiresAtUtc);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
