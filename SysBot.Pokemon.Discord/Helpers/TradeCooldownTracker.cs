using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace SysBot.Pokemon.Discord;

public static class TradeCooldownTracker
{
    private static readonly ConcurrentDictionary<ulong, DateTime> _lastTrade = new();

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

    public static string BuildCooldownMessage(int minutesRemaining)
        => $"Please wait {minutesRemaining} minutes to request another pokemon or GET PREMIUM for UNLIMITED trades.";
}
