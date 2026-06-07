using PKHeX.Core;
using PKHeX.Core.AutoMod;
using System;
using System.Linq;

namespace SysBot.Pokemon;

/// <summary>
/// Shared egg helpers used by every front-end (Discord, Twitch, etc.).
/// </summary>
public static class EggBallHelper
{
    /// <summary>
    /// Applies a user-specified "Ball:" line to a generated egg. ALM's GenerateEgg does not
    /// honor ForceSpecifiedBall (it leaves the species' matching/default ball), so the regular
    /// trade path's ball selection never runs for eggs. This restores it for the egg paths.
    /// </summary>
    public static void ApplySpecifiedBall(PKM? pkm, string content)
    {
        if (pkm == null || !APILegality.ForceSpecifiedBall || string.IsNullOrEmpty(content))
            return;

        var ballLine = content.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("Ball:", StringComparison.OrdinalIgnoreCase));
        if (ballLine == null)
            return;

        var ballName = ballLine.Split(':')[1].Trim();
        static string Norm(string s) => s.Replace("é", "e").Replace("-", " ").Replace(" ", "").Trim();
        var ballId = Array.FindIndex(GameInfo.GetStrings("en").balllist,
            b => Norm(b).Equals(Norm(ballName), StringComparison.OrdinalIgnoreCase));
        if (ballId <= 0)
            return;

        pkm.Ball = (byte)ballId;
        pkm.RefreshChecksum();
    }
}
