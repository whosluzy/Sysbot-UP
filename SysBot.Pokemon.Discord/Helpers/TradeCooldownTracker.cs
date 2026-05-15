using System;
using System.Collections.Concurrent;

namespace SysBot.Pokemon.Discord;

public static class TradeCooldownTracker
{
    private static readonly ConcurrentDictionary<ulong, DateTime> _lastTrade = new();

    public static bool IsOnCooldown(ulong userId, int cooldownMinutes, out TimeSpan remaining)
    {
        if (_lastTrade.TryGetValue(userId, out var lastTrade))
        {
            var cooldown = TimeSpan.FromMinutes(cooldownMinutes);
            var elapsed = DateTime.UtcNow - lastTrade;
            if (elapsed < cooldown)
            {
                remaining = cooldown - elapsed;
                return true;
            }
        }
        remaining = TimeSpan.Zero;
        return false;
    }

    public static void RecordTrade(ulong userId) => _lastTrade[userId] = DateTime.UtcNow;

    public static void ClearCooldown(ulong userId) => _lastTrade.TryRemove(userId, out _);

    public static void ClearAll() => _lastTrade.Clear();
}
