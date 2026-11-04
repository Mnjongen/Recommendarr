using Recommendarr.Core.Models;

namespace Recommendarr.Infrastructure.Data;

public sealed class UserEntity
{
    public int Id { get; set; }

    public required string PlexUserId { get; set; }

    public required string Username { get; set; }

    public string? Email { get; set; }

    public string? ThumbUrl { get; set; }

    public bool IsAdmin { get; set; }

    /// <summary>The Seerr account requests are made as. Linked automatically by plex.tv id unless <see cref="SeerrUserSetManually"/>.</summary>
    public int? SeerrUserId { get; set; }

    /// <summary>
    /// True when an admin picked the Seerr account (e.g. a Plex Home managed user, which has no
    /// Seerr account of its own). Automatic linking never overwrites a manual choice.
    /// </summary>
    public bool SeerrUserSetManually { get; set; }

    public long? TelegramChatId { get; set; }

    public string? TelegramLinkCodeHash { get; set; }

    public DateTimeOffset? TelegramLinkCodeExpiresAt { get; set; }

    public ulong? DiscordUserId { get; set; }

    public bool DigestEnabled { get; set; }

    public DateTimeOffset? TelegramDigestSentAt { get; set; }

    public DateTimeOffset? LastHistorySync { get; set; }
}

public sealed class WatchEventEntity
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public UserEntity User { get; set; } = null!;

    /// <summary>Catalog item, once the play has been resolved to a TMDB id.</summary>
    public int? ItemId { get; set; }

    public ItemEntity? Item { get; set; }

    public MediaType MediaType { get; set; }

    public required string Title { get; set; }

    public DateTimeOffset WatchedAt { get; set; }

    public double PercentComplete { get; set; }

    public int? TmdbId { get; set; }

    public int? TvdbId { get; set; }

    public string? ImdbId { get; set; }

    /// <summary>Provider-specific row key used to de-duplicate incremental syncs.</summary>
    public string? SourceKey { get; set; }
}

/// <summary>A movie or series known to the catalog: watched titles and candidates alike.</summary>
public sealed class ItemEntity
{
    public int Id { get; set; }

    public int TmdbId { get; set; }

    public MediaType MediaType { get; set; }

    public required string Title { get; set; }

    public int? Year { get; set; }

    public string? Overview { get; set; }

    public string? PosterPath { get; set; }

    public string? OriginalLanguage { get; set; }

    public double VoteAverage { get; set; }

    public int VoteCount { get; set; }

    public double Popularity { get; set; }

    public int? TvdbId { get; set; }

    public string? ImdbId { get; set; }

    /// <summary>Pipe-separated lists, used to build the embedding document.</summary>
    public string Genres { get; set; } = "";

    public string Keywords { get; set; } = "";

    public string Cast { get; set; } = "";

    public string Creators { get; set; } = "";

    /// <summary>When TMDB metadata was last fetched; null for stubs that only have an id and title.</summary>
    public DateTimeOffset? MetadataFetchedAt { get; set; }

    /// <summary>When the vector was written; cleared when the embedding model changes.</summary>
    public DateTimeOffset? EmbeddedAt { get; set; }
}

/// <summary>"Seed recommends candidate" edges from TMDB recommendations/similar lists.</summary>
public sealed class ItemLinkEntity
{
    public int SourceItemId { get; set; }

    public int TargetItemId { get; set; }

    public ItemLinkKind Kind { get; set; }
}

public enum ItemLinkKind
{
    Recommendation,
    Similar,
}

public sealed class RecommendationEntity
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public UserEntity User { get; set; } = null!;

    public int ItemId { get; set; }

    public ItemEntity Item { get; set; } = null!;

    public int Rank { get; set; }

    public double Score { get; set; }

    public string? Reason { get; set; }

    public AvailabilityStatus? Availability { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
}

public enum FeedbackKind
{
    Like,
    Dislike,
    NotInterested,
}

public sealed class FeedbackEntity
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public int ItemId { get; set; }

    public FeedbackKind Kind { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RequestEntity
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public int ItemId { get; set; }

    public int? SeerrRequestId { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    /// <summary>Where the request came from: web, api, telegram, discord.</summary>
    public required string Origin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class JobRunEntity
{
    public long Id { get; set; }

    public required string JobName { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool Success { get; set; }

    public string? Message { get; set; }
}
