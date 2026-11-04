using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Seerr;
using Recommendarr.Web.Jobs;
using Recommendarr.Web.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Recommendarr.Web.Tests;

public sealed class TelegramBotJobTests
{
    [Theory]
    [InlineData(ChatType.Group, 123, "This action is only available in a private chat.")]
    [InlineData(ChatType.Private, 456, "This action is only available in a private chat.")]
    public async Task Callback_from_group_or_foreign_user_is_rejected(ChatType chatType, long fromId, string expectedMessage)
    {
        await using var harness = await CallbackHarness.CreateAsync();

        await harness.Job.HandleCallbackAsync(harness.Bot, Callback(chatType, 123, fromId, "r:m:603:1"), default);

        Assert.Equal(expectedMessage, harness.Api.LastRequestText);
        Assert.Empty(harness.Seerr.Requests);
    }

    [Fact]
    public async Task Callback_from_unlinked_chat_is_rejected()
    {
        await using var harness = await CallbackHarness.CreateAsync();

        await harness.Job.HandleCallbackAsync(harness.Bot, Callback(ChatType.Private, 456, 456, "r:m:603:1"), default);

        Assert.Equal("This chat is no longer linked.", harness.Api.LastRequestText);
        Assert.Empty(harness.Seerr.Requests);
    }

    [Fact]
    public async Task Callback_for_a_different_profile_is_rejected_after_relinking()
    {
        await using var harness = await CallbackHarness.CreateAsync();
        await harness.AddSecondLinkedProfileAsync();

        await harness.Job.HandleCallbackAsync(harness.Bot, Callback(ChatType.Private, 456, 456, "r:m:603:1"), default);

        Assert.Equal("This action is no longer available for this profile.", harness.Api.LastRequestText);
        Assert.Empty(harness.Seerr.Requests);
    }

    [Fact]
    public async Task Request_button_for_a_title_no_longer_recommended_is_rejected()
    {
        await using var harness = await CallbackHarness.CreateAsync(includeRecommendation: false);

        await harness.Job.HandleCallbackAsync(harness.Bot, Callback(ChatType.Private, 123, 123, "r:m:603:1"), default);

        Assert.Equal("This title is no longer requestable.", harness.Api.LastRequestText);
        Assert.Empty(harness.Seerr.Requests);
    }

    [Fact]
    public async Task Valid_request_is_made_as_the_linked_profile()
    {
        await using var harness = await CallbackHarness.CreateAsync();

        await harness.Job.HandleCallbackAsync(harness.Bot, Callback(ChatType.Private, 123, 123, "r:m:603:1"), default);

        Assert.Equal("Request sent to Seerr.", harness.Api.LastRequestText);
        Assert.Equal([(MediaType.Movie, 603, 7)], harness.Seerr.Requests);
        await using var db = harness.CreateContext();
        var request = await db.Requests.SingleAsync();
        Assert.Equal("telegram", request.Origin);
        Assert.Equal(harness.LinkedUserId, request.UserId);
    }

    [Fact]
    public void Telegram_poller_is_not_registered_in_testing()
    {
        using var factory = new AppFactory();

        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), service => service is TelegramBotJob);
    }

    private static CallbackQuery Callback(ChatType chatType, long chatId, long fromId, string data) => new()
    {
        Id = "callback",
        From = new User { Id = fromId, FirstName = "Tester" },
        Message = new Message
        {
            Date = DateTime.UtcNow,
            Chat = new Chat { Id = chatId, Type = chatType },
        },
        Data = data,
    };

    private sealed class CallbackHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        private CallbackHarness(SqliteConnection connection, ServiceProvider services, TelegramBotJob job, TelegramBotClient bot, CapturingTelegramApi api, FakeSeerrClient seerr, int linkedUserId)
        {
            _connection = connection;
            _services = services;
            Job = job;
            Bot = bot;
            Api = api;
            Seerr = seerr;
            LinkedUserId = linkedUserId;
        }

        public TelegramBotJob Job { get; }
        public TelegramBotClient Bot { get; }
        public CapturingTelegramApi Api { get; }
        public FakeSeerrClient Seerr { get; }
        public int LinkedUserId { get; }

        public static async Task<CallbackHarness> CreateAsync(bool includeRecommendation = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<RecommendarrDbContext>(options => options.UseSqlite(connection));
            var seerr = new FakeSeerrClient();
            services.AddScoped<RecommendationReader>();
            services.AddScoped(sp => new SeerrService(
                sp.GetRequiredService<RecommendarrDbContext>(),
                seerr,
                Options.Create(new SeerrOptions { BaseUrl = "http://seerr", ApiKey = "key" }),
                NullLogger<SeerrService>.Instance));
            services.AddScoped(sp => new RecommendationFeedbackService(
                sp.GetRequiredService<RecommendarrDbContext>(),
                new FakeMetadataSource(),
                TimeProvider.System));
            var root = services.BuildServiceProvider();
            var scopeFactory = root.GetRequiredService<IServiceScopeFactory>();
            var userId = 0;
            await using (var setupScope = root.CreateAsyncScope())
            {
                var db = setupScope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
                await db.Database.EnsureCreatedAsync();
                var user = new UserEntity { PlexUserId = "111", Username = "Alice", TelegramChatId = 123, SeerrUserId = 7 };
                var item = new ItemEntity { TmdbId = 603, MediaType = MediaType.Movie, Title = "The Matrix" };
                db.Users.Add(user);
                db.Items.Add(item);
                if (includeRecommendation)
                    db.Recommendations.Add(new RecommendationEntity { User = user, Item = item, Rank = 1 });
                await db.SaveChangesAsync();
                userId = user.Id;
            }

            var api = new CapturingTelegramApi();
            var http = new HttpClient(api);
            var bot = new TelegramBotClient("123456:TestToken", http);
            var job = new TelegramBotJob(
                scopeFactory,
                new TestHttpClientFactory(http),
                Options.Create(new TelegramOptions()),
                TimeProvider.System,
                NullLogger<TelegramBotJob>.Instance);
            return new CallbackHarness(connection, root, job, bot, api, seerr, userId);
        }

        public async Task AddSecondLinkedProfileAsync()
        {
            await using var db = CreateContext();
            db.Users.Add(new UserEntity { PlexUserId = "222", Username = "Bob", TelegramChatId = 456, SeerrUserId = 8 });
            await db.SaveChangesAsync();
        }

        public RecommendarrDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<RecommendarrDbContext>().UseSqlite(_connection).Options);

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CapturingTelegramApi : HttpMessageHandler
    {
        public string? LastRequestText { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            LastRequestText = document.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeSeerrClient : ISeerrClient
    {
        public List<(MediaType Type, int TmdbId, int SeerrUserId)> Requests { get; } = [];

        public Task<AvailabilityStatus> GetStatusAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
            Task.FromResult(AvailabilityStatus.NotRequested);

        public Task<IReadOnlyList<SeerrUser>> GetUsersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SeerrUser>>([]);

        public Task<SeerrRequestResult> RequestAsync(MediaType type, int tmdbId, int seerrUserId, IReadOnlyList<int>? seasons = null, CancellationToken ct = default)
        {
            Requests.Add((type, tmdbId, seerrUserId));
            return Task.FromResult(new SeerrRequestResult(true, 1, null));
        }
    }

    private sealed class FakeMetadataSource : IMetadataSource
    {
        public Task<MediaMetadata?> GetDetailsAsync(MediaType type, int tmdbId, CancellationToken ct = default) => Task.FromResult<MediaMetadata?>(null);
        public Task<IReadOnlyList<MediaReference>> GetRecommendationsAsync(MediaType type, int tmdbId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaReference>>([]);
        public Task<IReadOnlyList<MediaReference>> GetSimilarAsync(MediaType type, int tmdbId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaReference>>([]);
        public Task<IReadOnlyList<MediaReference>> GetTrendingAsync(MediaType type, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaReference>>([]);
        public Task<int?> FindTmdbIdAsync(MediaType type, int? tvdbId, string? imdbId, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<int?> SearchTmdbIdAsync(MediaType type, string title, int? year, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaSearchResult>>([]);
    }
}
