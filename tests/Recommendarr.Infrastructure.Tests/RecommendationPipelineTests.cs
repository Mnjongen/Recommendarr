using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Sync;
using Recommendarr.Infrastructure.Vectors;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Recommendarr.Infrastructure.Tests;

public sealed class RecommendationPipelineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _time = new(T0);
    private readonly FakeEmbeddingGenerator _generator = new("Action", "Romance", "Documentary");
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await TestDatabase.CreateAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private async Task<EmbeddingRunResult> EmbedAsync(string model = "fake")
    {
        await using var db = _database.CreateContext();
        var options = MsOptions.Create(new EmbeddingOptions { Provider = EmbeddingProviderKind.Onnx, Model = model });
        return await new EmbeddingService(db, new SqliteVecStore(db), _generator, options, _time, NullLogger<EmbeddingService>.Instance).RunAsync();
    }

    private async Task<RecommendationRunResult> RecommendAsync()
    {
        await using var db = _database.CreateContext();
        var options = MsOptions.Create(new RecommendationOptions { MinVoteCount = 10, MinVoteAverage = 5 });
        return await new RecommendationService(db, new SqliteVecStore(db), options, _time, NullLogger<RecommendationService>.Instance).RunAsync();
    }

    private static ItemEntity Item(int tmdbId, string title, string genres, bool fetched = true) => new()
    {
        TmdbId = tmdbId,
        MediaType = MediaType.Movie,
        Title = title,
        Genres = genres,
        Overview = fetched ? $"{title}." : null,
        VoteAverage = 7.5,
        VoteCount = 500,
        MetadataFetchedAt = fetched ? T0 : null,
    };

    [Fact]
    public async Task Embedding_covers_items_with_metadata_and_restarts_when_the_model_changes()
    {
        await using (var db = _database.CreateContext())
        {
            db.Items.AddRange(Item(1, "Die Hard", "Action"), Item(2, "Stub", "", fetched: false));
            await db.SaveChangesAsync();
        }

        Assert.Equal(new EmbeddingRunResult(true, 1), await EmbedAsync());
        Assert.Equal(new EmbeddingRunResult(false, 0), await EmbedAsync());
        Assert.Equal(new EmbeddingRunResult(true, 1), await EmbedAsync(model: "other"));

        await using var check = _database.CreateContext();
        var vectors = await new SqliteVecStore(check).GetAsync(check.Items.Select(i => i.Id).ToList());
        Assert.Single(vectors);
        Assert.Equal(1f, vectors.Values.Single()[0]); // "Action" once
    }

    [Fact]
    public async Task Recommendations_follow_each_users_taste_and_explain_themselves()
    {
        await using (var db = _database.CreateContext())
        {
            var dieHard = Item(1, "Die Hard", "Action|Action");
            var notebook = Item(2, "The Notebook", "Romance|Romance");
            var speed = Item(3, "Speed", "Action");
            var titanic = Item(4, "Titanic", "Romance");
            var planet = Item(5, "Planet Earth", "Documentary");
            db.Items.AddRange(dieHard, notebook, speed, titanic, planet);
            var alice = new UserEntity { PlexUserId = "1", Username = "alice" };
            var bob = new UserEntity { PlexUserId = "2", Username = "bob" };
            db.Users.AddRange(alice, bob);
            db.WatchEvents.AddRange(
                new WatchEventEntity { User = alice, Item = dieHard, Title = "Die Hard", MediaType = MediaType.Movie, WatchedAt = T0.AddDays(-1), PercentComplete = 100 },
                new WatchEventEntity { User = bob, Item = notebook, Title = "The Notebook", MediaType = MediaType.Movie, WatchedAt = T0.AddDays(-1), PercentComplete = 100 });
            await db.SaveChangesAsync();
            db.ItemLinks.Add(new ItemLinkEntity { SourceItemId = dieHard.Id, TargetItemId = speed.Id, Kind = ItemLinkKind.Recommendation });
            await db.SaveChangesAsync();
        }
        await EmbedAsync();

        Assert.Equal(new RecommendationRunResult(2, 8), await RecommendAsync()); // 4 unwatched titles each
        await RecommendAsync(); // re-running replaces rather than appends

        await using var check = _database.CreateContext();
        var recs = await check.Recommendations.Include(r => r.User).Include(r => r.Item)
            .OrderBy(r => r.User.Username).ThenBy(r => r.Rank)
            .Select(r => new { r.User.Username, r.Item.Title, r.Reason })
            .ToListAsync();
        Assert.Equal(8, recs.Count);
        Assert.Equal(("Speed", "Because you watched Die Hard"), (recs[0].Title, recs[0].Reason));
        Assert.Equal("alice", recs[0].Username);
        Assert.Equal(("Titanic", "Because you watched The Notebook"), (recs[4].Title, recs[4].Reason));
        Assert.DoesNotContain(recs, r => r.Username == "alice" && r.Title == "Die Hard");
    }

    [Fact]
    public async Task Explicit_likes_shape_taste_and_are_not_recommended_again()
    {
        int likedId;
        int romanceCandidateId;
        await using (var db = _database.CreateContext())
        {
            var actionSeed = Item(1, "Film", "Action|Action");
            var likedRomance = Item(2, "Film", "Romance|Romance");
            var actionCandidate = Item(3, "Film", "Action|Action");
            var romanceCandidate = Item(4, "Film", "Romance|Romance");
            var alice = new UserEntity { PlexUserId = "1", Username = "alice" };
            db.AddRange(actionSeed, likedRomance, actionCandidate, romanceCandidate, alice);
            await db.SaveChangesAsync();
            likedId = likedRomance.Id;
            romanceCandidateId = romanceCandidate.Id;
            db.WatchEvents.Add(new WatchEventEntity
            {
                UserId = alice.Id,
                ItemId = actionSeed.Id,
                Title = actionSeed.Title,
                MediaType = MediaType.Movie,
                WatchedAt = T0.AddDays(-1),
                PercentComplete = 100,
            });
            db.Feedback.Add(new FeedbackEntity { UserId = alice.Id, ItemId = likedRomance.Id, Kind = FeedbackKind.Like, CreatedAt = T0 });
            await db.SaveChangesAsync();
        }
        await EmbedAsync();

        await RecommendAsync();

        await using var check = _database.CreateContext();
        var recs = await check.Recommendations.Include(r => r.Item).OrderBy(r => r.Rank).ToListAsync();
        Assert.Equal(romanceCandidateId, recs[0].ItemId);
        Assert.DoesNotContain(recs, r => r.ItemId == likedId);
    }
}
