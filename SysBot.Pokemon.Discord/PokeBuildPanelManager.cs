using Discord;
using Discord.WebSocket;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord;

/// <summary>
/// Keeps the Pokémon builder panel at the bottom of its channel.
/// Whenever any message appears in a tracked channel the old panel is
/// deleted and a fresh one is posted as the newest message.
/// </summary>
public static class PokeBuildPanelManager
{
    private static readonly string FilePath = "pokebuild_panels.json";

    // channelId → current panel messageId
    private static readonly ConcurrentDictionary<ulong, ulong> Panels = new();
    // per-channel timestamp of last repost (rate-limit guard)
    private static readonly ConcurrentDictionary<ulong, DateTime> LastRepost = new();

    private const int CooldownSeconds = 10;

    private static readonly string[] TeraTypes =
    [
        "Normal", "Fire", "Water", "Electric", "Grass", "Ice",
        "Fighting", "Poison", "Ground", "Flying", "Psychic", "Bug",
        "Rock", "Ghost", "Dragon", "Dark", "Steel", "Fairy", "Stellar",
    ];

    // ─── Persistence ──────────────────────────────────────────────────────────

    public static void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var json = File.ReadAllText(FilePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, ulong>>(json);
            if (dict == null) return;
            foreach (var (k, v) in dict)
                if (ulong.TryParse(k, out var channelId))
                    Panels[channelId] = v;
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            var dict = Panels.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dict));
        }
        catch { }
    }

    // ─── Registration (called by /pokebuild-setup) ────────────────────────────

    public static void Register(ulong channelId, ulong messageId)
    {
        Panels[channelId] = messageId;
        Save();
    }

    // ─── On-ready restore ─────────────────────────────────────────────────────

    public static async Task RestorePanelsAsync(DiscordSocketClient client)
    {
        foreach (var channelId in Panels.Keys.ToList())
        {
            try
            {
                var channel = client.GetChannel(channelId) as ITextChannel
                    ?? await client.Rest.GetChannelAsync(channelId) as ITextChannel;
                if (channel == null) continue;

                // Delete old panel (may already be gone — that's fine)
                try { await channel.DeleteMessageAsync(Panels[channelId]); } catch { }

                var msg = await channel.SendMessageAsync(embed: CreatePanelEmbed(), components: CreatePanelComponents());
                Panels[channelId] = msg.Id;
            }
            catch { }
        }
        Save();
    }

    // ─── Sticky behaviour (called from SysCord.HandleMessageAsync) ────────────

    public static async Task OnMessageReceivedAsync(SocketMessage message)
    {
        // Ignore system messages (e.g. "pinned a message" notices) to avoid loops
        if (message.Type != MessageType.Default && message.Type != MessageType.Reply)
            return;

        if (!Panels.TryGetValue(message.Channel.Id, out var panelMsgId))
            return;

        // Don't react to the panel message being reposted
        if (message.Id == panelMsgId)
            return;

        // Per-channel cooldown so a burst of messages doesn't spam Discord
        if (LastRepost.TryGetValue(message.Channel.Id, out var last)
            && (DateTime.UtcNow - last).TotalSeconds < CooldownSeconds)
            return;

        LastRepost[message.Channel.Id] = DateTime.UtcNow;

        try { await message.Channel.DeleteMessageAsync(panelMsgId); } catch { }

        if (message.Channel is not ITextChannel textChannel)
            return;

        var newMsg = await textChannel.SendMessageAsync(embed: CreatePanelEmbed(), components: CreatePanelComponents());
        Panels[message.Channel.Id] = newMsg.Id;
        Save();
    }

    // ─── Embed & components ───────────────────────────────────────────────────

    public static Embed CreatePanelEmbed()
    {
        return new EmbedBuilder()
            .WithTitle("🔨 Pokémon Builder")
            .WithColor(Color.Gold)
            .WithDescription(
                "Want a Pokémon? **Click the button for your game below!**\n\n" +
                "You'll be guided step-by-step — no commands needed. " +
                "Fill in the name, level, IVs, moves, and more, then hit **✅ Submit Trade** to join the queue!\n​")
            .AddField("🐾 What you can set",
                "Species • Level • Nature • Shiny ✨\n" +
                "IVs & EVs • Held Item • Poké Ball • Moves ⚔️\n" +
                "Tera Type 🧬 (SV) • Alpha ⭐ (LA / PLZA)", inline: false)
            .WithFooter($"PokedexMasterBot {TradeBot.Version} • Tap a button to start building!")
            .Build();
    }

    public static MessageComponent CreatePanelComponents()
    {
        return new ComponentBuilder()
            .WithButton("🟣 Scarlet / Violet",  "pb_start_sv",   ButtonStyle.Primary,   row: 0)
            .WithButton("🏔️ Legends: Arceus",   "pb_start_la",   ButtonStyle.Primary,   row: 0)
            .WithButton("🗼 Legends: Z-A",       "pb_start_plza", ButtonStyle.Primary,   row: 0)
            .WithButton("⚔️ Sword / Shield",     "pb_start_swsh", ButtonStyle.Secondary, row: 1)
            .WithButton("💎 BDSP",               "pb_start_bdsp", ButtonStyle.Secondary, row: 1)
            .Build();
    }
}
