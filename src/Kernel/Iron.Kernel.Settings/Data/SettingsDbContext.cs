using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Settings.Data;

/// <summary>The <c>kernel_settings</c> schema: the values set for the tenant, its units and its people, and every change of them.</summary>
[Schema("kernel_settings")]
public sealed class SettingsDbContext(DbContextOptions<SettingsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<SettingValue> Values => Set<SettingValue>();

    public DbSet<SettingChange> History => Set<SettingChange>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SettingValue>(value =>
        {
            value.ToTable("setting_values");
            value.Property(v => v.Key).HasMaxLength(SettingValue.KeyMaxLength);
            value.Property(v => v.Level).HasConversion<string>().HasMaxLength(10);
            value.Property(v => v.ValueJson).HasColumnName("value").HasColumnType("jsonb");
            value.HasIndex(v => new { v.Key, v.Level, v.TargetId }).IsUnique();
        });

        modelBuilder.Entity<SettingChange>(change =>
        {
            change.ToTable("setting_history");
            change.Property(c => c.Key).HasMaxLength(SettingValue.KeyMaxLength);
            change.Property(c => c.Level).HasConversion<string>().HasMaxLength(10);
            change.Property(c => c.Kind).HasMaxLength(10);
            change.Property(c => c.Before).HasColumnType("jsonb");
            change.Property(c => c.After).HasColumnType("jsonb");
            change.HasIndex(c => new { c.Key, c.Level, c.TargetId, c.CreatedAt });
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class SettingsDbContextFactory : IDesignTimeDbContextFactory<SettingsDbContext>
{
    public SettingsDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<SettingsDbContext>());
}
