using Microsoft.Extensions.Logging.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Evaluation;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Sync;
using Recommendarr.Infrastructure.Vectors;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Recommendarr.Infrastructure.Tests;

public sealed class RecommendationQualityServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _time = new(T0);
    private readonly RecommendationOptions _options = new() { MinVoteCount = 10, MinVoteAverage = 5 };
    private TestDatabase _database = null!;
    private readonly Dictionary<string, int> _ids = [];

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        string[] titles = ["Die Hard", "Speed", "Heat", "Ronin", "Point Break", "Titanic"];
        var items = titles.Select((t, i) => new ItemEntity
        {
            TmdbId = i + 1,
            MediaType = MediaType.Movie,
            Title = t,
            Genres = t == "Titanic" ? "Romance" : "Action",
            Overview = $"{t}.",
            VoteAverage = 7.5,
            VoteCount = t == "Point Break" ? 2 : 500, // too obscure for the vote filter
            MetadataFetchedAt = T0,
        }).ToList();
        var alice = new UserEntity { PlexUserId = "1", Username = "alice" };
        db.AddRange(items);
        db.Users.Add(alice);
        await db.SaveChangesAsync();
        foreach (var item in items)
            _ids[item.Title] = item.Id;

        // Alice watches four action films in order, then Point Break.
        string[] watched = ["Die Hard", "Speed", "Heat", "Ronin", "Point Break"];
        db.WatchEvents.AddRange(watched.Select((t, day) => new WatchEventEntity
        {
            UserId = alice.Id, ItemId = _ids[t], Title = t, MediaType = MediaType.Movie,
            WatchedAt = T0.AddDays(day - 10), PercentComplete = 100, SourceKey = t,
        }));
        db.ItemLinks.Add(new ItemLinkEntity { SourceItemId = _ids["Die Hard"], TargetItemId = _ids["Ronin"], Kind = ItemLinkKind.Recommendation });
        await db.SaveChangesAsync();

        var embedding = new EmbeddingService(db, new SqliteVecStore(db), new FakeEmbeddingGenerator("Action", "Romance"),
            MsOptions.Create(new EmbeddingOptions { Provider = EmbeddingProviderKind.Onnx, Model = "fake" }), _time, NullLogger<EmbeddingService>.Instance);
        await embedding.RunAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private RecommendationQualityService Create(RecommendarrDbContext db) =>
        new(db, new SqliteVecStore(db), MsOptions.Create(_options), _time, NullLogger<RecommendationQualityService>.Instance);

    [Fact]
    public async Task Evaluation_reports_coverage_and_is_stored_for_the_history()
    {
        await using var db = _database.CreateContext();
        var quality = Create(db);

        var report = await quality.EvaluateAsync([new EvaluationExperiment(EvaluationExperiments.BaselineName, _options)]);

        var result = Assert.Single(report.Results).Result;
        Assert.Equal([_ids["Ronin"], _ids["Point Break"]], result.Cases.Select(c => c.ItemId));
        Assert.Equal([(true, true), (false, false)], result.Cases.Select(c => (c.Eligible, c.Linked)));
        Assert.Equal("alice", report.Usernames["1"]);

        var run = Assert.Single(await quality.GetHistoryAsync());
        Assert.Equal((2, 6, 1, 0.5, 0.5), (run.Cases, run.CatalogTitles, run.Users, run.EligibleShare, run.LinkedShare));
        Assert.Contains("\"MinVoteCount\":10", run.Settings);
    }

    [Fact]
    public async Task Explanation_scores_the_pool_and_says_why_other_titles_are_missing()
    {
        await using var db = _database.CreateContext();
        var quality = Create(db);
        db.Feedback.Add(new FeedbackEntity { UserId = db.Users.Single().Id, ItemId = _ids["Titanic"], Kind = FeedbackKind.NotInterested, CreatedAt = T0 });
        await db.SaveChangesAsync();
        var catalog = await quality.LoadCatalogAsync();

        var explanation = await quality.ExplainAsync(catalog, "1");

        Assert.NotNull(explanation);
        Assert.Empty(explanation.Explanation.Candidates); // everything is watched, obscure or rejected
        Assert.Equal(5, explanation.SeedWeights.Count); // vote filters apply to candidates, not to taste
        Assert.Contains("Already watched", explanation.WhyNotScored(_ids["Heat"]));
        Assert.Contains("NotInterested", explanation.WhyNotScored(_ids["Titanic"]));
        Assert.Null(await quality.ExplainAsync(catalog, "missing"));
    }
}
