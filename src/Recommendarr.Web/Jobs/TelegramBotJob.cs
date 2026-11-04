using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Seerr;
using Recommendarr.Infrastructure.Telegram;
using Recommendarr.Web.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Recommendarr.Web.Jobs;

public sealed class TelegramBotJob(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClientFactory,
    IOptions<TelegramOptions> options,
    TimeProvider time,
    ILogger<TelegramBotJob> logger) : BackgroundService
{
    public const string HttpClientName = "Telegram";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogInformation("Telegram bot is disabled because no bot token is configured");
            return;
        }

        var bot = new TelegramBotClient(token, httpClientFactory.CreateClient(HttpClientName));
        var offset = 0;
        var nextDigestCheck = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (time.GetUtcNow() >= nextDigestCheck)
                {
                    await SendDueDigestsAsync(bot, stoppingToken);
                    nextDigestCheck = time.GetUtcNow().AddMinutes(15);
                }

                var updates = await bot.GetUpdates(
                    offset: offset,
                    timeout: 30,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    try
                    {
                        await HandleAsync(bot, update, stoppingToken);
                    }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Could not process Telegram update {UpdateId}", update.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Telegram polling failed; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(bot, callback, ct);
            return;
        }

        var message = update.Message;
        if (message?.Text is not { } text || message.Chat.Type != ChatType.Private)
            return;

        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].Split('@')[0].ToLowerInvariant();
        var argument = parts.Length == 2 ? parts[1].Trim() : "";

        switch (command)
        {
            case "/start":
                await StartAsync(bot, message.Chat.Id, argument, ct);
                break;
            case "/help":
                await bot.SendMessage(message.Chat.Id,
                    "Commands:\n/start <code> - link your Recommendarr profile\n/recommendations - show your recommendations\n/digest on|off - manage daily recommendations\n/unlink - disconnect this chat",
                    cancellationToken: ct);
                break;
            case "/recommendations":
                await RecommendationsAsync(bot, message.Chat.Id, ct);
                break;
            case "/digest":
                await SetDigestAsync(bot, message.Chat.Id, argument, ct);
                break;
            case "/unlink":
                await UnlinkAsync(bot, message.Chat.Id, ct);
                break;
            default:
                await bot.SendMessage(message.Chat.Id, "Use /help to see available commands.", cancellationToken: ct);
                break;
        }
    }

    private async Task StartAsync(ITelegramBotClient bot, long chatId, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            await bot.SendMessage(chatId,
                "To link this chat, sign in to Recommendarr, open Telegram in the navigation, and generate a link code. Then send the returned /start command here. Codes expire after 10 minutes.",
                cancellationToken: ct);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<TelegramLinkService>();
        var result = await links.LinkChatAsync(code, chatId, ct);
        var response = result.Status switch
        {
            TelegramLinkStatus.Success => $"This chat is now linked to {result.Username}. Use /recommendations to get started.",
            TelegramLinkStatus.AlreadyLinked => "This profile is already linked to another Telegram chat. Unlink it there first.",
            TelegramLinkStatus.ChatAlreadyLinked => "This Telegram chat is already linked to a different profile. Use /unlink first.",
            _ => "That link code is invalid or expired. Create a new code in Recommendarr and try again.",
        };
        await bot.SendMessage(chatId, response, cancellationToken: ct);
    }

    private async Task RecommendationsAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.TelegramChatId == chatId, ct);
        if (user is null)
        {
            await bot.SendMessage(chatId, "This chat is not linked. Use /start with a link code from Recommendarr.", cancellationToken: ct);
            return;
        }

        var recommendations = await scope.ServiceProvider.GetRequiredService<RecommendationReader>()
            .GetAsync(user.PlexUserId, null, ct);
        if (recommendations is null or { Count: 0 })
        {
            await bot.SendMessage(chatId, "No recommendations yet. They will appear after Recommendarr completes a sync.", cancellationToken: ct);
            return;
        }

        var items = recommendations
            .GroupBy(item => item.MediaType)
            .SelectMany(group => group.Take(3))
            .ToList();
        await SendRecommendationItemsAsync(bot, chatId, user.Id, items, includeActions: true, ct);
    }

    private async Task SetDigestAsync(ITelegramBotClient bot, long chatId, string setting, CancellationToken ct)
    {
        bool? enabled = setting switch { "on" => true, "off" => false, _ => null };
        if (enabled is null)
        {
            await bot.SendMessage(chatId, "Use /digest on or /digest off.", cancellationToken: ct);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.TelegramChatId == chatId, ct);
        if (user is null)
        {
            await bot.SendMessage(chatId, "This chat is not linked. Use /start with a link code from Recommendarr.", cancellationToken: ct);
            return;
        }

        user.DigestEnabled = enabled.Value;
        await db.SaveChangesAsync(ct);
        await bot.SendMessage(chatId,
            enabled.Value ? $"Daily recommendations are on. Digests are sent after {Math.Clamp(options.Value.DigestHourUtc, 0, 23):00}:00 UTC." : "Daily recommendations are off.",
            cancellationToken: ct);
    }

    internal async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        if (callback.Message is not { Chat.Type: ChatType.Private } message ||
            callback.From.Id != message.Chat.Id || callback.Data is not { } data)
        {
            await bot.AnswerCallbackQuery(callback.Id, text: "This action is only available in a private chat.", showAlert: true, cancellationToken: ct);
            return;
        }

        var fields = data.Split(':');
        var isRequest = fields.Length == 4 && fields[0] == "r";
        var isFeedback = fields.Length == 5 && fields[0] == "f";
        var typeCode = isRequest ? fields[1] : isFeedback ? fields[2] : "";
        var itemIdField = isRequest ? fields.ElementAtOrDefault(2) : isFeedback ? fields.ElementAtOrDefault(3) : null;
        var profileIdField = isRequest ? fields.ElementAtOrDefault(3) : isFeedback ? fields.ElementAtOrDefault(4) : null;
        if (!TryParseMediaType(typeCode, out var mediaType) ||
            !int.TryParse(itemIdField, out var tmdbId) ||
            !int.TryParse(profileIdField, out var profileId))
        {
            await bot.AnswerCallbackQuery(callback.Id, text: "That action is no longer available.", showAlert: true, cancellationToken: ct);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.TelegramChatId == message.Chat.Id, ct);
        if (user is null)
        {
            await bot.AnswerCallbackQuery(callback.Id, text: "This chat is no longer linked.", showAlert: true, cancellationToken: ct);
            return;
        }

        if (user.Id != profileId)
        {
            await bot.AnswerCallbackQuery(callback.Id, text: "This action is no longer available for this profile.", showAlert: true, cancellationToken: ct);
            return;
        }

        if (isRequest)
        {
            var current = await scope.ServiceProvider.GetRequiredService<RecommendationReader>()
                .GetAsync(user.PlexUserId, mediaType, ct);
            var item = current?.SingleOrDefault(r => r.TmdbId == tmdbId);
            if (item is null || !item.CanRequest)
            {
                await bot.AnswerCallbackQuery(callback.Id, text: "This title is no longer requestable.", showAlert: true, cancellationToken: ct);
                return;
            }

            var outcome = await scope.ServiceProvider.GetRequiredService<SeerrService>()
                .RequestAsync(user.PlexUserId, mediaType, tmdbId, "telegram", ct);
            var response = outcome.Kind switch
            {
                RequestOutcomeKind.Created => "Request sent to Seerr.",
                RequestOutcomeKind.NotConfigured => "Seerr is not configured.",
                _ => outcome.Error ?? "Seerr could not accept this request.",
            };
            await bot.AnswerCallbackQuery(callback.Id, text: response, showAlert: outcome.Kind != RequestOutcomeKind.Created, cancellationToken: ct);
            return;
        }

        var kind = fields[1] switch
        {
            "l" => FeedbackKind.Like,
            "d" => FeedbackKind.Dislike,
            "n" => FeedbackKind.NotInterested,
            _ => (FeedbackKind?)null,
        };
        if (kind is null)
        {
            await bot.AnswerCallbackQuery(callback.Id, text: "That feedback action is not supported.", showAlert: true, cancellationToken: ct);
            return;
        }

        var feedback = await scope.ServiceProvider.GetRequiredService<RecommendationFeedbackService>()
            .RecordAsync(user.PlexUserId, mediaType, tmdbId, kind.Value, ct);
        await bot.AnswerCallbackQuery(callback.Id,
            text: feedback == FeedbackOutcome.Saved ? "Feedback saved." : "Could not save feedback.",
            showAlert: feedback != FeedbackOutcome.Saved,
            cancellationToken: ct);
    }

    private async Task SendDueDigestsAsync(ITelegramBotClient bot, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var digestHour = Math.Clamp(options.Value.DigestHourUtc, 0, 23);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var users = await db.Users
            .Where(u => u.DigestEnabled && u.TelegramChatId != null)
            .ToListAsync(ct);
        var reader = scope.ServiceProvider.GetRequiredService<RecommendationReader>();

        foreach (var user in users.Where(u => TelegramDigestSchedule.IsDue(now, digestHour, u.TelegramDigestSentAt)))
        {
            try
            {
                var recommendations = await reader.GetAsync(user.PlexUserId, null, ct);
                var items = recommendations?.GroupBy(r => r.MediaType).SelectMany(group => group.Take(3)).ToList() ?? [];
                var text = items.Count == 0
                    ? "No recommendations yet. They will appear after Recommendarr completes a sync."
                    : FormatRecommendations(items);
                await bot.SendMessage(user.TelegramChatId!.Value, text, cancellationToken: ct);
                user.TelegramDigestSentAt = now;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Could not send Telegram digest for profile {PlexUserId}", user.PlexUserId);
            }
        }
    }

    private static async Task SendRecommendationItemsAsync(
        ITelegramBotClient bot, long chatId, int profileId, IReadOnlyList<RecommendationView> items, bool includeActions, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            await bot.SendMessage(chatId, "No recommendations yet. They will appear after Recommendarr completes a sync.", cancellationToken: ct);
            return;
        }

        foreach (var item in items)
        {
            InlineKeyboardMarkup? keyboard = null;
            if (includeActions)
            {
                var rows = new List<InlineKeyboardButton[]>();
                if (item.CanRequest)
                    rows.Add([InlineKeyboardButton.WithCallbackData("Request", $"r:{TypeCode(item.MediaType)}:{item.TmdbId}:{profileId}")]);
                rows.Add([
                    InlineKeyboardButton.WithCallbackData("Like", $"f:l:{TypeCode(item.MediaType)}:{item.TmdbId}:{profileId}"),
                    InlineKeyboardButton.WithCallbackData("Dislike", $"f:d:{TypeCode(item.MediaType)}:{item.TmdbId}:{profileId}"),
                    InlineKeyboardButton.WithCallbackData("Not interested", $"f:n:{TypeCode(item.MediaType)}:{item.TmdbId}:{profileId}")
                ]);
                keyboard = new InlineKeyboardMarkup(rows);
            }

            await bot.SendMessage(chatId, FormatRecommendation(item), replyMarkup: keyboard, cancellationToken: ct);
        }
    }

    private static string FormatRecommendations(IReadOnlyList<RecommendationView> items) => string.Join("\n\n",
        items.GroupBy(item => item.MediaType).Select(group =>
            $"{group.Key}\n" + string.Join("\n", group.Select(FormatRecommendation))));

    private static string FormatRecommendation(RecommendationView item) =>
        $"{item.Title}{(item.Year is { } year ? $" ({year})" : "")}\n{item.MediaType}" +
        (string.IsNullOrWhiteSpace(item.Reason) ? "" : $"\n{item.Reason}");

    private static string TypeCode(MediaType type) => type switch
    {
        MediaType.Movie => "m",
        MediaType.Tv => "t",
        _ => "?",
    };

    private static bool TryParseMediaType(string value, out MediaType type)
    {
        type = value switch
        {
            "m" => MediaType.Movie,
            "t" => MediaType.Tv,
            _ => (MediaType)(-1),
        };
        return Enum.IsDefined(type);
    }

    private async Task UnlinkAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var removed = await scope.ServiceProvider.GetRequiredService<TelegramLinkService>().UnlinkChatAsync(chatId, ct);
        await bot.SendMessage(chatId,
            removed ? "This Telegram chat has been unlinked." : "This chat was not linked to a Recommendarr profile.",
            cancellationToken: ct);
    }
}