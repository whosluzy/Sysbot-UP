using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace SysBot.Pokemon.Discord;

public static class TradeCooldownTracker
{
    // Last completed trade per Discord account.
    private static readonly ConcurrentDictionary<ulong, DateTime> _lastTrade = new();

    // Last completed trade per in-game trainer (TID + SID), so a second Discord account
    // used with the same game save shares the same limit. TID/SID come from tradecodes.json,
    // which the trade bots already fill in during every trade — nothing extra is read from the Switch.
    private static readonly ConcurrentDictionary<long, DateTime> _lastTradeByTrainer = new();

    // Periodic sweep so entries from users who never come back don't sit in memory forever.
    private static readonly Timer _sweepTimer = new(_ => Sweep(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

    public static bool IsOnCooldown(ulong userId, int cooldownMinutes, out int minutesRemaining)
    {
        minutesRemaining = 0;
        if (cooldownMinutes <= 0)
            return false;
        var cooldown = TimeSpan.FromMinutes(cooldownMinutes);

        var left = Remaining(_lastTrade, userId, cooldown);
        if (TryGetTrainerKey(userId, out var trainerKey))
        {
            var trainerLeft = Remaining(_lastTradeByTrainer, trainerKey, cooldown);
            if (trainerLeft > left)
                left = trainerLeft;
        }

        if (left <= TimeSpan.Zero)
            return false;
        minutesRemaining = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        return true;
    }

    /// <summary>
    /// Records a trade that actually concluded with this user — called only from DiscordTradeNotifier.TradeFinished,
    /// right before "Trade finished. Enjoy!" is sent. Never at queue time, so sent codes, cancels and no-shows don't count.
    /// Recorded under both the Discord ID and the trainer's saved TID/SID.
    /// </summary>
    public static void RecordCompletedTrade(ulong userId)
    {
        var now = DateTime.UtcNow;
        _lastTrade[userId] = now;
        if (TryGetTrainerKey(userId, out var trainerKey))
            _lastTradeByTrainer[trainerKey] = now;
    }

    private static TimeSpan Remaining<TKey>(ConcurrentDictionary<TKey, DateTime> map, TKey key, TimeSpan cooldown) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var last))
            return TimeSpan.Zero;
        var left = cooldown - (DateTime.UtcNow - last);
        if (left <= TimeSpan.Zero)
            map.TryRemove(key, out _); // expired — drop it so memory doesn't grow
        return left;
    }

    // Trainer key = TID and SID packed together. Same TID with a different SID is a different trainer.
    private static bool TryGetTrainerKey(ulong userId, out long key)
    {
        key = 0;
        try
        {
            var details = new TradeCodeStorage().GetTradeDetails(userId);
            if (details is null || (details.TID == 0 && details.SID == 0))
                return false;
            key = ((long)details.TID << 32) | (uint)details.SID;
            return true;
        }
        catch
        {
            return false; // unreadable tradecodes.json — fall back to the Discord ID only
        }
    }

    public static void ClearCooldown(ulong userId) => _lastTrade.TryRemove(userId, out _);

    public static void ClearAll() { _lastTrade.Clear(); _lastTradeByTrainer.Clear(); }

    public static bool IsExempt(SocketUser user, DiscordSettings cfg)
    {
        if (user is not SocketGuildUser gu)
            return false;
        var list = cfg.RolesExemptFromCooldown;
        if (list.List.Count == 0)
            return false;
        return gu.Roles.Any(r => list.Contains(r.Id) || list.Contains(r.Name));
    }

    public static Embed BuildCooldownEmbed(int minutesRemaining, int limitMinutes)
    {
        var window = limitMinutes == 60 ? "hour"
            : limitMinutes % 60 == 0 ? $"{limitMinutes / 60} hours"
            : $"{limitMinutes} minutes";
        var wait = minutesRemaining == 1 ? "1 minute" : $"{minutesRemaining} minutes";
        return new EmbedBuilder()
            .WithColor(new Color(255, 140, 0))
            .WithTitle("⏳ Free Trade Limit Reached")
            .WithDescription($"Free members get **1 completed trade per {window}** — and you've already had yours! 🎉\n" +
                             $"Your next free trade unlocks in **{wait}**.")
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
        foreach (var kvp in _lastTradeByTrainer)
        {
            if (kvp.Value < cutoff)
                _lastTradeByTrainer.TryRemove(kvp.Key, out _);
        }
    }
}
