using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace SysBot.Pokemon.Discord;

public static class TradeCooldownTracker
{
    private static readonly ConcurrentDictionary<ulong, DateTime> _lastTrade = new();

    // Periodic sweep so entries from users who never come back don't sit in memory forever.
    private static readonly Timer _sweepTimer = new(_ => Sweep(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

    public static bool IsOnCooldown(ulong userId, int cooldownMinutes, out DateTime unlocksAtUtc)
    {
        unlocksAtUtc = default;
        if (cooldownMinutes <= 0)
            return false;
        if (_lastTrade.TryGetValue(userId, out var last))
        {
            var unlock = last + TimeSpan.FromMinutes(cooldownMinutes);
            if (DateTime.UtcNow < unlock)
            {
                unlocksAtUtc = unlock;
                return true;
            }
            // Cooldown has expired — drop the entry so memory doesn't grow.
            _lastTrade.TryRemove(userId, out _);
        }
        return false;
    }

    /// <summary>
    /// Records a trade that actually concluded with this user. Called only from DiscordTradeNotifier.TradeFinished —
    /// never at queue time, so sent codes, cancels and no-shows don't use up a free user's trade.
    /// </summary>
    public static void RecordCompletedTrade(ulong userId) => _lastTrade[userId] = DateTime.UtcNow;

    public static void ClearCooldown(ulong userId) => _lastTrade.TryRemove(userId, out _);

    public static void ClearAll() => _lastTrade.Clear();

    public static bool IsExempt(SocketUser user, DiscordSettings cfg)
    {
        if (user is not SocketGuildUser gu)
            return false;
        var list = cfg.RolesExemptFromCooldown;
        if (list.List.Count == 0)
            return false;
        return gu.Roles.Any(r => list.Contains(r.Id) || list.Contains(r.Name));
    }

    public static Embed BuildCooldownEmbed(DateTime unlocksAtUtc, int limitMinutes)
    {
        var window = limitMinutes == 60 ? "hour"
            : limitMinutes % 60 == 0 ? $"{limitMinutes / 60} hours"
            : $"{limitMinutes} minutes";
        // Discord timestamps render in each user's own timezone and update live:
        // <t:X:R> = countdown ("in 45 minutes"), <t:X:t> = clock time ("4:32 PM").
        var unix = new DateTimeOffset(DateTime.SpecifyKind(unlocksAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return new EmbedBuilder()
            .WithColor(new Color(255, 140, 0))
            .WithTitle("⏳ Free Trade Limit Reached")
            .WithDescription($"Free members get **1 completed trade per {window}** — and you've already had yours! 🎉\n\n" +
                             $"⏰ Your next free trade unlocks **<t:{unix}:R>** (at <t:{unix}:t>).")
            .AddField("💎 GET PREMIUM for UNLIMITED Trades!",
                "**Step 1:** [Become a Patron](https://www.patreon.com/c/pokedexmasters/membership)\n" +
                "**Step 2:** [Link your Discord account to Patreon](https://www.patreon.com/settings/apps/discord) to gain the **Premium** role.\n" +
                "**Step 3:** All bots unlock automatically — trade as much as you want!")
            .Build();
    }

    // Removes entries older than 1 day — well past any reasonable trade-limit window.
    private static void Sweep()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        foreach (var kvp in _lastTrade)
        {
            if (kvp.Value < cutoff)
                _lastTrade.TryRemove(kvp.Key, out _);
        }
    }
}
