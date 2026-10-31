using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Seerr;

namespace Recommendarr.Infrastructure.Tests;

public sealed class SeerrServiceTests : IAsyncLifetime
{
    private readonly FakeSeerrClient _seerr = new();
    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        var alice = new UserEntity { PlexUserId = "111", Username = "alice" };
        var bob = new UserEntity { PlexUserId = "222", Username = "bob" };
        var matrix = new ItemEntity { TmdbId = 603, MediaType = MediaType.Movie, Title = "The Matrix" };
        var breakingBad = new ItemEntity { TmdbId = 1396, MediaType = MediaType.Tv, Title = "Breaking Bad" };
        db.AddRange(alice, bob, matrix, breakingBad);
        db.Recommendations.AddRange(
            new RecommendationEntity { User = alice, Item = matrix, Rank = 1 },
            new RecommendationEntity { User = bob, Item = matrix, Rank = 1 },
            new RecommendationEntity { User = bob, Item = breakingBad, Rank = 1 });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private SeerrService CreateService(RecommendarrDbContext db, bool configured = true) => new(
        db,
        _seerr,
        Microsoft.Extensions.Options.Options.Create(configured ? new SeerrOptions { BaseUrl = "http://seerr", ApiKey = "key" } : new SeerrOptions()),
        NullLogger<SeerrService>.Instance);

    [Fact]
    public async Task Refresh_links_users_by_plex_id_and_updates_every_recommendation_row()
    {
        _seerr.Users.Add(new SeerrUser(7, "alice", "111", null));
        _seerr.Statuses[(MediaType.Movie, 603)] = AvailabilityStatus.Available;

        await using (var db = _database.CreateContext())
            Assert.Equal(new AvailabilityRunResult(false, 1, 2), await CreateService(db).RefreshAvailabilityAsync());

        await using var check = _database.CreateContext();
        Assert.Equal([7, null], await check.Users.OrderBy(u => u.PlexUserId).Select(u => u.SeerrUserId).ToListAsync());
        var availability = await check.Recommendations.Select(r => new { r.Item.TmdbId, r.Availability }).ToListAsync();
        Assert.All(availability.Where(a => a.TmdbId == 603), a => Assert.Equal(AvailabilityStatus.Available, a.Availability));
        Assert.Equal(AvailabilityStatus.NotRequested, availability.Single(a => a.TmdbId == 1396).Availability);
    }

    [Fact]
    public async Task Refresh_is_skipped_without_configuration()
    {
        await using var db = _database.CreateContext();
        Assert.True((await CreateService(db, configured: false).RefreshAvailabilityAsync()).Skipped);
    }

    [Fact]
    public async Task Request_links_the_user_on_demand_and_records_the_outcome()
    {
        _seerr.Users.Add(new SeerrUser(8, "bob", "222", null));
        _seerr.OnRequest = (_, tmdbId, _) =>
        {
            _seerr.Statuses[(MediaType.Tv, tmdbId)] = AvailabilityStatus.Processing; // auto-approved
            return new SeerrRequestResult(true, 42, null);
        };

        await using (var db = _database.CreateContext())
        {
            var outcome = await CreateService(db).RequestAsync("222", MediaType.Tv, 1396, "api");
            Assert.Equal(new RequestOutcome(RequestOutcomeKind.Created, null, 42, AvailabilityStatus.Processing), outcome);
        }

        Assert.Equal([(MediaType.Tv, 1396, 8)], _seerr.Requests);
        await using var check = _database.CreateContext();
        var request = await check.Requests.SingleAsync();
        Assert.True(request.Success);
        Assert.Equal(("api", 42), (request.Origin, request.SeerrRequestId));
        Assert.Equal(AvailabilityStatus.Processing, (await check.Recommendations.SingleAsync(r => r.Item.TmdbId == 1396)).Availability);
    }

    [Fact]
    public async Task Request_reports_rejections_and_users_missing_from_seerr()
    {
        _seerr.Users.Add(new SeerrUser(7, "alice", "111", null));
        _seerr.OnRequest = (_, _, _) => new SeerrRequestResult(false, null, "Movie Quota exceeded.");

        await using var db = _database.CreateContext();
        var service = CreateService(db);

        Assert.Equal(new RequestOutcome(RequestOutcomeKind.Rejected, "Movie Quota exceeded."), await service.RequestAsync("111", MediaType.Movie, 603, "api"));
        Assert.Equal(RequestOutcomeKind.UserNotInSeerr, (await service.RequestAsync("222", MediaType.Movie, 603, "api")).Kind);
        Assert.Equal(RequestOutcomeKind.UserNotFound, (await service.RequestAsync("999", MediaType.Movie, 603, "api")).Kind);
        Assert.Equal(RequestOutcomeKind.ItemNotFound, (await service.RequestAsync("111", MediaType.Movie, 1, "api")).Kind);
        var recorded = await db.Requests.AsNoTracking().SingleAsync();
        Assert.Equal((false, "Movie Quota exceeded."), (recorded.Success, recorded.Error));
    }

    private sealed class FakeSeerrClient : ISeerrClient
    {
        public List<SeerrUser> Users { get; } = [];

        public Dictionary<(MediaType, int), AvailabilityStatus> Statuses { get; } = [];

        public List<(MediaType, int, int)> Requests { get; } = [];

        public Func<MediaType, int, int, SeerrRequestResult> OnRequest { get; set; } = (_, _, _) => new SeerrRequestResult(true, 1, null);

        public Task<AvailabilityStatus> GetStatusAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
            Task.FromResult(Statuses.GetValueOrDefault((type, tmdbId), AvailabilityStatus.NotRequested));

        public Task<IReadOnlyList<SeerrUser>> GetUsersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SeerrUser>>(Users);

        public Task<SeerrRequestResult> RequestAsync(MediaType type, int tmdbId, int seerrUserId, IReadOnlyList<int>? seasons = null, CancellationToken ct = default)
        {
            Requests.Add((type, tmdbId, seerrUserId));
            return Task.FromResult(OnRequest(type, tmdbId, seerrUserId));
        }
    }
}
