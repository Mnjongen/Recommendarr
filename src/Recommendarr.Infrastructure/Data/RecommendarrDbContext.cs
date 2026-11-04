using Microsoft.EntityFrameworkCore;

namespace Recommendarr.Infrastructure.Data;

/// <summary>
/// Relational app data. Embeddings are not mapped here: they live in the sqlite-vec
/// <c>item_vectors</c> virtual table managed by <see cref="Vectors.SqliteVecStore"/>, because its
/// column dimension depends on the configured embedding model.
/// </summary>
public sealed class RecommendarrDbContext(DbContextOptions<RecommendarrDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<WatchEventEntity> WatchEvents => Set<WatchEventEntity>();

    public DbSet<ItemEntity> Items => Set<ItemEntity>();

    public DbSet<ItemLinkEntity> ItemLinks => Set<ItemLinkEntity>();

    public DbSet<RecommendationEntity> Recommendations => Set<RecommendationEntity>();

    public DbSet<FeedbackEntity> Feedback => Set<FeedbackEntity>();

    public DbSet<RequestEntity> Requests => Set<RequestEntity>();

    public DbSet<JobRunEntity> JobRuns => Set<JobRunEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserEntity>(e =>
        {
            e.ToTable("Users");
            e.HasIndex(x => x.PlexUserId).IsUnique();
            e.HasIndex(x => x.TelegramChatId).IsUnique().HasFilter("\"TelegramChatId\" IS NOT NULL");
        });

        b.Entity<WatchEventEntity>(e =>
        {
            e.ToTable("WatchEvents");
            e.HasIndex(x => new { x.UserId, x.SourceKey }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.WatchedAt });
            e.HasOne(x => x.Item).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ItemEntity>(e =>
        {
            e.ToTable("Items");
            e.HasIndex(x => new { x.MediaType, x.TmdbId }).IsUnique();
        });

        b.Entity<ItemLinkEntity>(e =>
        {
            e.ToTable("ItemLinks");
            e.HasKey(x => new { x.SourceItemId, x.TargetItemId, x.Kind });
            e.HasIndex(x => x.TargetItemId);
        });

        b.Entity<RecommendationEntity>(e =>
        {
            e.ToTable("Recommendations");
            e.HasIndex(x => new { x.UserId, x.Rank });
        });

        b.Entity<FeedbackEntity>(e =>
        {
            e.ToTable("Feedback");
            e.HasIndex(x => new { x.UserId, x.ItemId }).IsUnique();
        });

        b.Entity<RequestEntity>().ToTable("Requests");

        b.Entity<JobRunEntity>(e =>
        {
            e.ToTable("JobRuns");
            e.HasIndex(x => new { x.JobName, x.StartedAt });
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY or compare DateTimeOffset natively; store as UTC ticks.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToTicksConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    private sealed class DateTimeOffsetToTicksConverter()
        : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
}
