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

    public static bool IsOnCooldown(ulong userId, int cooldownMinutes, out int minutesRemaining)
    {
        minutesRemaining = 0;
        if (cooldownMinutes <= 0)
            return false;
        if (_lastTrade.TryGetValue(userId, out var last))
        {
            var elapsed = DateTime.UtcNow - last;
            var cooldown = TimeSpan.FromMinutes(cooldownMinutes);
            if (elapsed < cooldown)
            {
                minutesRemaining = Math.Max(1, (int)Math.Ceiling((cooldown - elapsed).TotalMinutes));
                return true;
            }
            // Cooldown has expired — drop the entry so memory doesn't grow.
            _lastTrade.TryRemove(userId, out _);
        }
        return false;
    }

    public static void RecordTrade(ulong userId) => _lastTrade[userId] = DateTime.UtcNow;

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

    public static Embed BuildCooldownEmbed(int minutesRemaining)
    {
        return new EmbedBuilder()
            .WithColor(new Color(255, 140, 0))
            .WithTitle("⏳ Trade Cooldown")
            .WithDescription($"Please wait **{minutesRemaining} minutes** to request another pokemon.")
            .AddField("💎 GET PREMIUM for UNLIMITED Trades!",
                "**Step 1:** [Become a Patron](https://www.patreon.com/c/pokedexmasters/membership)\n" +
                "**Step 2:** Link your Discord account to Patreon to gain the **Premium** role.\n" +
                "**Step 3:** All bots unlock automatically — trade as much as you want!")
            .WithFooter("This message will disappear in 15 seconds.")
            .Build();
    }

    // Removes entries older than 1 hour — well past any reasonable cooldown.
    private static void Sweep()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        foreach (var kvp in _lastTrade)
        {
            if (kvp.Value < cutoff)
                _lastTrade.TryRemove(kvp.Key, out _);
        }
    }
}
