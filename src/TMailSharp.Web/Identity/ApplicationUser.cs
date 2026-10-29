using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TMailSharp.Identity;

public sealed class ApplicationUser : IdentityUser;

public sealed class IdentityDatabase(DbContextOptions<IdentityDatabase> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ApiKeyRecord>(key =>
        {
            key.HasKey(x => x.Id);
            key.Property(x => x.Name).HasMaxLength(80);
            key.Property(x => x.Hash).HasMaxLength(64);
            key.Property(x => x.Prefix).HasMaxLength(12);
            key.HasIndex(x => x.Hash).IsUnique();
            key.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class ApiKeyRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}
