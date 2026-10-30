using System.Text.RegularExpressions;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Visibility.Data;

public enum TagStatus
{
    Active,
    Retired,
}

/// <summary>
/// A visibility tag of the tenant (TF-241 rule 1, ADR-0057): its code, which records and people carry for good, its name,
/// a description and its status. A retired tag stays on everything that carries it, so nothing becomes visible by
/// surprise, and is put on nothing new. Tracked, so its history shows every change (ADR-0051).
/// </summary>
public sealed partial class VisibilityTag : AuditedEntity, ITenantWide, ITrackedChanges
{
    public const string RecordType = "visibility_tag";
    public const int CodeMaxLength = 32;
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    private VisibilityTag()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    public VisibilityTag(string code, string name, string? description)
    {
        if (!IsCode(code))
        {
            throw new ArgumentException($"'{code}' is not a tag code.", nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code;
        Name = name.Trim();
        Description = Describe(description);
        Status = TagStatus.Active;
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public TagStatus Status { get; private set; }

    public bool IsActive => Status == TagStatus.Active;

    /// <summary>Capitals, digits, hyphens and underscores, at most 32, starting with a capital or a digit: <c>APAC</c>, <c>PROJECT-7</c>.</summary>
    public static bool IsCode(string? code) => code is not null && CodePattern().IsMatch(code);

    public void Change(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Description = Describe(description);
    }

    public void Retire() => Status = TagStatus.Retired;

    public void Reactivate() => Status = TagStatus.Active;

    private static string? Describe(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>
/// One tag a principal holds (TF-241 rule 3): a person or a service account. A principal who holds none sees every
/// record their roles and units allow. Its changes are recorded on the principal's own history, not as rows of their own.
/// </summary>
public sealed class TagHolding : AuditedEntity, IOwnAccessRule
{
    private TagHolding()
    {
    }

    public TagHolding(Guid principalId, Guid tagId)
    {
        PrincipalId = principalId;
        TagId = tagId;
    }

    public Guid PrincipalId { get; private set; }

    public Guid TagId { get; private set; }
}

/// <summary>The <c>kernel_visibility</c> schema: the tenant's visibility tags and who holds them. Records carry their own (ADR-0057).</summary>
[Schema("kernel_visibility")]
public sealed class VisibilityDbContext(DbContextOptions<VisibilityDbContext> options) : ModuleDbContext(options)
{
    public DbSet<VisibilityTag> Tags => Set<VisibilityTag>();

    public DbSet<TagHolding> Holdings => Set<TagHolding>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VisibilityTag>(tag =>
        {
            tag.ToTable("tags");
            tag.Property(t => t.Code).HasMaxLength(VisibilityTag.CodeMaxLength);
            tag.Property(t => t.Name).HasMaxLength(VisibilityTag.NameMaxLength);
            tag.Property(t => t.Description).HasMaxLength(VisibilityTag.DescriptionMaxLength);
            tag.Property(t => t.Status).HasConversion<string>().HasMaxLength(10);
            tag.HasIndex(t => t.Code).IsUnique();
        });

        modelBuilder.Entity<TagHolding>(holding =>
        {
            holding.ToTable("holdings");
            holding.HasIndex(h => new { h.PrincipalId, h.TagId }).IsUnique();
            holding.HasIndex(h => h.TagId);
            holding.HasOne<VisibilityTag>().WithMany().HasForeignKey(h => h.TagId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class VisibilityDbContextFactory : IDesignTimeDbContextFactory<VisibilityDbContext>
{
    public VisibilityDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<VisibilityDbContext>());
}
