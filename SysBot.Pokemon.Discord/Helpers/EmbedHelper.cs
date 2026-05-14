using Discord;
using Discord.Net;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord;

public static class EmbedHelper
{
    // Rate limiter for DM operations to prevent "opening DMs too fast" errors
    private static readonly SemaphoreSlim _dmRateLimiter = new(1, 1);
    private static readonly ConcurrentDictionary<ulong, IDMChannel> _dmChannels = new();
    private static DateTime _lastDmTime = DateTime.MinValue;
    private const int MinDmDelayMs = 2000; // Minimum 2 seconds between DMs

    private static async Task<IDMChannel?> GetOrCreateDMAsync(IUser user)
    {
        try
        {
            if (_dmChannels.TryGetValue(user.Id, out var channel))
                return channel;

            // Enforce minimum delay before creating a new DM channel to respect Discord rate limits
            var timeSinceLastDm = DateTime.Now - _lastDmTime;
            if (timeSinceLastDm.TotalMilliseconds < MinDmDelayMs)
            {
                var remainingDelay = MinDmDelayMs - (int)timeSinceLastDm.TotalMilliseconds;
                await Task.Delay(remainingDelay).ConfigureAwait(false);
            }

            var dm = await user.CreateDMChannelAsync().ConfigureAwait(false);
            _dmChannels[user.Id] = dm;
            _lastDmTime = DateTime.Now;
            return dm;
        }
        catch (HttpException ex) when (ex.DiscordCode.HasValue && ex.DiscordCode.Value == (DiscordErrorCode)40003)
        {
            LogUtil.LogError($"Opening DMs too fast when creating DM channel for user {user.Username} ({user.Id}). Waiting 5 seconds...", "GetOrCreateDMAsync");
            await Task.Delay(5000).ConfigureAwait(false);

            // Try one more time after the delay
            try
            {
                var dm = await user.CreateDMChannelAsync().ConfigureAwait(false);
                _dmChannels[user.Id] = dm;
                _lastDmTime = DateTime.Now;
                return dm;
            }
            catch (Exception retryEx)
            {
                LogUtil.LogError($"Failed to create DM channel after retry: {retryEx.Message}", "GetOrCreateDMAsync");
                return null;
            }
        }
        catch (ObjectDisposedException)
        {
            LogUtil.LogError("Discord client is disposed. Cannot create DM channel.", "GetOrCreateDMAsync");
            return null;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to create DM channel: {ex.Message}", "GetOrCreateDMAsync");
            return null;
        }
    }

    public static async Task SendNotificationEmbedAsync(IUser user, string message)
    {
        await _dmRateLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            var dm = await GetOrCreateDMAsync(user).ConfigureAwait(false);
            if (dm == null)
            {
                LogUtil.LogError($"Could not create DM channel for user {user.Username} ({user.Id}). Skipping notification.", "SendNotificationEmbedAsync");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("Notice")
                .WithDescription(message)
                .AddField("Need Help?", "If you have questions or concerns, please contact a moderator.", false)
                .WithFooter("PokedexMasterBot Notification")
                .WithTimestamp(DateTimeOffset.Now)
                .WithThumbnailUrl("https://raw.githubusercontent.com/Secludedly/ZE-FusionBot-Sprite-Images/main/dm-legalityerror.gif")
                .WithColor(Color.Orange)
                .Build();

            await dm.SendMessageAsync(embed: embed).ConfigureAwait(false);
            _lastDmTime = DateTime.Now;
        }
        catch (ObjectDisposedException)
        {
            LogUtil.LogError("Discord client disposed when sending notification embed.", "SendNotificationEmbedAsync");
        }
        catch (HttpException ex) when (ex.DiscordCode.HasValue && ex.DiscordCode.Value == (DiscordErrorCode)40003)
        {
            LogUtil.LogError($"Opening DMs too fast! User: {user.Username} ({user.Id})", "SendNotificationEmbedAsync");
            _dmChannels.TryRemove(user.Id, out _);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error sending notification embed: {ex.Message}", "SendNotificationEmbedAsync");
        }
        finally
        {
            _dmRateLimiter.Release();
        }
    }

    public static async Task SendTradeCanceledEmbedAsync(IUser user, string reason)
    {
        await _dmRateLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            var dm = await GetOrCreateDMAsync(user).ConfigureAwait(false);
            if (dm == null)
            {
                LogUtil.LogError($"Could not create DM channel for user {user.Username} ({user.Id}). Skipping trade canceled message.", "SendTradeCanceledEmbedAsync");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("Trade Canceled")
                .WithDescription("Unfortunately, your trade was unable to be completed.")
                .AddField("Reason", reason, false)
                .AddField("What Now?", "You can try submitting the trade command again. If the issue keeps happening, please contact a moderator.", false)
                .WithFooter("Sorry for the inconvenience!")
                .WithTimestamp(DateTimeOffset.Now)
                .WithThumbnailUrl("https://raw.githubusercontent.com/Secludedly/ZE-FusionBot-Sprite-Images/main/dm-uhoherror.gif")
                .WithColor(Color.Red)
                .Build();

            await dm.SendMessageAsync(embed: embed).ConfigureAwait(false);
            _lastDmTime = DateTime.Now;
        }
        catch (ObjectDisposedException)
        {
            LogUtil.LogError("Discord client disposed when sending trade canceled embed.", "SendTradeCanceledEmbedAsync");
        }
        catch (HttpException ex) when (ex.DiscordCode.HasValue && ex.DiscordCode.Value == (DiscordErrorCode)40003)
        {
            LogUtil.LogError($"Opening DMs too fast! User: {user.Username} ({user.Id})", "SendTradeCanceledEmbedAsync");
            _dmChannels.TryRemove(user.Id, out _);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error sending trade canceled embed: {ex.Message}", "SendTradeCanceledEmbedAsync");
        }
        finally
        {
            _dmRateLimiter.Release();
        }
    }

    public static async Task SendTradeCodeEmbedAsync(IUser user, int code)
    {
        await _dmRateLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            var dm = await GetOrCreateDMAsync(user).ConfigureAwait(false);
            if (dm == null)
            {
                LogUtil.LogError($"Could not create DM channel for user {user.Username} ({user.Id}). Skipping trade code message.", "SendTradeCodeEmbedAsync");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("Your Link Trade Code")
                .WithDescription($"Use this code to connect with the bot in your game!\n# {code:0000 0000}")
                .AddField("How to Connect",
                    "1. Open your game and go to **Link Trade**\n" +
                    "2. Select **Set Link Code** and enter the code above\n" +
                    "3. The bot will search for and find you automatically!", false)
                .WithFooter("You'll receive another DM when your trade is about to begin.")
                .WithTimestamp(DateTimeOffset.Now)
                .WithThumbnailUrl("https://raw.githubusercontent.com/Secludedly/ZE-FusionBot-Sprite-Images/main/dm-tradecode.gif")
                .WithColor(Color.Gold)
                .Build();

            await dm.SendMessageAsync(embed: embed).ConfigureAwait(false);
            _lastDmTime = DateTime.Now;
        }
        catch (ObjectDisposedException)
        {
            LogUtil.LogError("Discord client disposed when sending trade code embed.", "SendTradeCodeEmbedAsync");
        }
        catch (HttpException ex) when (ex.DiscordCode.HasValue && ex.DiscordCode.Value == (DiscordErrorCode)40003)
        {
            LogUtil.LogError($"Opening DMs too fast! User: {user.Username} ({user.Id})", "SendTradeCodeEmbedAsync");
            _dmChannels.TryRemove(user.Id, out _);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error sending trade code embed: {ex.Message}", "SendTradeCodeEmbedAsync");
        }
        finally
        {
            _dmRateLimiter.Release();
        }
    }

    public static async Task SendTradeFinishedEmbedAsync<T>(IUser user, string message, T pk, bool isMysteryEgg)
        where T : PKM, new()
    {
        try
        {
            string thumbnailUrl;
            string speciesName;

            if (isMysteryEgg)
            {
                thumbnailUrl = "https://raw.githubusercontent.com/Secludedly/ZE-FusionBot-Sprite-Images/main/mysteryegg3.png";
                speciesName = "Mystery Egg";
            }
            else
            {
                thumbnailUrl = TradeExtensions<T>.PokeImg(pk, false, true, null);
                speciesName = SpeciesName.GetSpeciesName(pk.Species, 2);
            }

            var embedBuilder = new EmbedBuilder()
                .WithTitle("Trade Complete!")
                .WithDescription(message)
                .WithTimestamp(DateTimeOffset.Now)
                .WithThumbnailUrl(thumbnailUrl)
                .WithColor(Color.Teal);

            if (!isMysteryEgg && pk.Species != 0)
            {
                var shinyTag = pk.IsShiny ? " ✨" : "";
                embedBuilder.AddField("Pokémon Received", $"**{speciesName}{shinyTag}** — Level {pk.CurrentLevel}", true);

                if (!string.IsNullOrEmpty(pk.Nickname) && pk.Nickname != speciesName)
                    embedBuilder.AddField("Nickname", pk.Nickname, true);
            }

            embedBuilder
                .AddField("Thank You!", "Hope you enjoy your new Pokémon! Feel free to use the trade command again anytime.", false)
                .WithFooter("Thanks for using the bot!");

            await user.SendMessageAsync(embed: embedBuilder.Build()).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            LogUtil.LogError("Discord client disposed when sending trade finished embed.", "SendTradeFinishedEmbedAsync");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error sending trade finished embed: {ex.Message}", "SendTradeFinishedEmbedAsync");
        }
    }

    public static async Task SendBatchProgressEmbedAsync<T>(IUser user, int currentTrade, int totalTrades, T pk, bool isMysteryEgg)
        where T : PKM, new()
    {
        try
        {
            var speciesName = isMysteryEgg ? "Mystery Egg" : SpeciesName.GetSpeciesName(pk.Species, 2);
            var thumbnailUrl = isMysteryEgg
                ? "https://raw.githubusercontent.com/Secludedly/ZE-FusionBot-Sprite-Images/main/mysteryegg3.png"
                : TradeExtensions<T>.PokeImg(pk, false, true, null);

            var remaining = totalTrades - currentTrade;

            var embed = new EmbedBuilder()
                .WithTitle($"Trade {currentTrade}/{totalTrades} Complete!")
                .WithDescription($"**{speciesName}** was traded successfully!")
                .AddField("Batch Progress", $"{currentTrade} of {totalTrades} trades done.", true)
                .AddField("Up Next", $"Preparing trade {currentTrade + 1}/{totalTrades}...", true)
                .AddField("⚠️ Stay in the Trade!", "The next Pokémon will be sent automatically — **do not exit the trade!**", false)
                .WithFooter($"{remaining} trade{(remaining == 1 ? "" : "s")} remaining in this batch.")
                .WithTimestamp(DateTimeOffset.Now)
                .WithThumbnailUrl(thumbnailUrl)
                .WithColor(Color.Blue)
                .Build();

            await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            LogUtil.LogError("Discord client disposed when sending batch progress embed.", "SendBatchProgressEmbedAsync");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error sending batch progress embed: {ex.Message}", "SendBatchProgressEmbedAsync");
        }
    }

    public static async Task SendTradeInitializingEmbedAsync(IUser user, string speciesName, int code, bool isMysteryEgg, string? message = null)
    {
        await _dmRateLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            var dm = await GetOrCreateDMAsync(user).ConfigureAwait(false);
            if (dm == null)
            {
                LogUtil.LogError($"Could not create DM channel for user {user.Username} ({user.Id}). Skipping trade initializing message.", "SendTradeInitializingEmbedAsync");
                return;
            }

            if (isMysteryEgg)
                speciesName = "Mystery Egg";

            var embed = new EmbedBuilder()
                .WithTitle("Opening the Trade Menu...")
                .WithDescription("Almost there! The bot is loading up the trade interface.")
                .AddField("Pokémon", speciesName, true)
                .AddField("Trade Code", $"`{code:0000 0000}`", true)
                .AddField("What to Do",
                    "Head to **Link Trade** in your game and enter your trade code if you haven't already!", false);

            if (!string.IsNullOrEmpty(message))
                embed.AddField("Additional Info", message, false);

            embed
                .WithFooter("The bot will begin searching for you shortly...")
                .WithTimestamp(DateTimeOffset.Now)
                .WithThumbnailUrl("https://raw.githubusercontent.com/Secludedly/ZE-FusionBot-Sprite-Images/main/dm-initializingbot.gif")
                .WithColor(Color.Green);

            await dm.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);
            _lastDmTime = DateTime.Now;
        }
        catch (ObjectDisposedException)
        {
            LogUtil.LogError("Discord client disposed when sending trade initializing embed.", "SendTradeInitializingEmbedAsync");
        }
        catch (HttpException ex) when (ex.DiscordCode.HasValue && ex.DiscordCode.Value == (DiscordErrorCode)40003)
        {
            LogUtil.LogError($"Opening DMs too fast! User: {user.Username} ({user.Id})", "SendTradeInitializingEmbedAsync");
            _dmChannels.TryRemove(user.Id, out _);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error sending trade initializing embed: {ex.Message}", "SendTradeInitializingEmbedAsync");
        }
        finally
        {
            _dmRateLimiter.Release();
        }
    }

    public static async Task SendTradeSearchingEmbedAsync(IUser user, string trainerName, string inGameName, string? message = null)
    {
        await _dmRateLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            var dm = await GetOrCreateDMAsync(user).ConfigureAwait(false);
            if (dm == null)
            {
                LogUtil.LogError($"Could not create DM channel for user {user.Username} ({user.Id}). Skipping trade searching message.", "SendTradeSearchingEmbedAsync");
                return;
            }

            var displayTrainer = string.IsNullOrWhiteSpace(trainerName) ? "Unknown" : trainerName.Trim();

            var embed = new EmbedBuilder()
                .WithTitle("Searching For You...")
                .WithDescription("The bot is in the Link Trade lobby and waiting for you to connect!")
                .AddField("Bot's In-Game Name", $"**{inGameName}**", true)
                .AddField("Your Trainer", displayTrainer, true)
                .AddField("How to Connect",
                    "1. Open **Link Trade** in your game\n" +
                    "2. Enter your trade code to search\n" +
                    $"3. Find and select **{inGameName}** when they appear\n" +
                    "4. Choose your Pokémon and **confirm the trade!**", false);

            if (!string.IsNullOrEmpty(message))
                embed.AddField("Batch Info", message, false);

            embed
                .WithFooter("Accept the trade when the bot finds you!")
                .WithTimestamp(DateTimeOffset.Now)
                .WithThumbnailUrl("https://raw.githubusercontent.com/Secludedly/ZE-FusionBot-Sprite-Images/main/dm-nowsearching.gif")
                .WithColor(Color.DarkGreen);

            await dm.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);
            _lastDmTime = DateTime.Now;
        }
        catch (ObjectDisposedException)
        {
            LogUtil.LogError("Discord client disposed when sending trade searching embed.", "SendTradeSearchingEmbedAsync");
        }
        catch (HttpException ex) when (ex.DiscordCode.HasValue && ex.DiscordCode.Value == (DiscordErrorCode)40003)
        {
            LogUtil.LogError($"Opening DMs too fast! User: {user.Username} ({user.Id})", "SendTradeSearchingEmbedAsync");
            _dmChannels.TryRemove(user.Id, out _);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error sending trade searching embed: {ex.Message}", "SendTradeSearchingEmbedAsync");
        }
        finally
        {
            _dmRateLimiter.Release();
        }
    }
}
