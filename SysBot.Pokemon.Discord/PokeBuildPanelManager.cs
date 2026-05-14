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

public static class PokeBuildPanelManager
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "pokebuild_panels.json");

    // channelId → current panel messageId
    private static readonly ConcurrentDictionary<ulong, ulong> Panels = new();
    private static readonly ConcurrentDictionary<ulong, DateTime> LastRepost = new();
    private const int CooldownSeconds = 12;

    // ─── Game detection ───────────────────────────────────────────────────────

    public static string CurrentGameType { get; private set; } = "sv";

    public static void SetGameType(Type pkType)
    {
        CurrentGameType = pkType.Name switch
        {
            "PK9" => "sv",
            "PA8" => "la",
            "PA9" => "plza",
            "PK8" => "swsh",
            "PB8" => "bdsp",
            "PB7" => "lgpe",
            _     => "sv",
        };
    }

    public static string GetGameLabel() => CurrentGameType switch
    {
        "sv"   => "Scarlet / Violet 🟣",
        "la"   => "Legends: Arceus 🏔️",
        "plza" => "Legends: Z-A 🗼",
        "swsh" => "Sword / Shield ⚔️",
        "bdsp" => "BDSP 💎",
        "lgpe" => "Let's Go 🎮",
        _      => "Unknown",
    };

    // ─── Nature data ──────────────────────────────────────────────────────────

    public static readonly (string Name, string Effect)[] Natures =
    [
        ("Hardy",   "Neutral"),
        ("Lonely",  "+Atk / -Def"),
        ("Brave",   "+Atk / -Spe"),
        ("Adamant", "+Atk / -SpA"),
        ("Naughty", "+Atk / -SpD"),
        ("Bold",    "+Def / -Atk"),
        ("Docile",  "Neutral"),
        ("Relaxed", "+Def / -Spe"),
        ("Impish",  "+Def / -SpA"),
        ("Lax",     "+Def / -SpD"),
        ("Timid",   "+Spe / -Atk"),
        ("Hasty",   "+Spe / -Def"),
        ("Serious", "Neutral"),
        ("Jolly",   "+Spe / -SpA"),
        ("Naive",   "+Spe / -SpD"),
        ("Modest",  "+SpA / -Atk"),
        ("Mild",    "+SpA / -Def"),
        ("Quiet",   "+SpA / -Spe"),
        ("Bashful", "Neutral"),
        ("Rash",    "+SpA / -SpD"),
        ("Calm",    "+SpD / -Atk"),
        ("Gentle",  "+SpD / -Def"),
        ("Sassy",   "+SpD / -Spe"),
        ("Careful", "+SpD / -SpA"),
        ("Quirky",  "Neutral"),
    ];

    // ─── Ball data per game ───────────────────────────────────────────────────

    private static readonly string[] BallsSV =
    [
        "Poké Ball", "Great Ball", "Ultra Ball", "Master Ball",
        "Net Ball", "Dive Ball", "Nest Ball", "Repeat Ball", "Timer Ball",
        "Luxury Ball", "Premier Ball", "Dusk Ball", "Heal Ball", "Quick Ball",
        "Level Ball", "Lure Ball", "Moon Ball", "Friend Ball", "Love Ball",
        "Heavy Ball", "Fast Ball", "Safari Ball", "Sport Ball",
        "Beast Ball", "Dream Ball",
    ];

    private static readonly string[] BallsLA =
    [
        "Poké Ball", "Great Ball", "Ultra Ball",
        "Leaden Ball", "Gigaton Ball", "Heavy Ball",
        "Feather Ball", "Wing Ball", "Jet Ball",
    ];

    private static readonly string[] BallsSWSH =
    [
        "Poké Ball", "Great Ball", "Ultra Ball", "Master Ball",
        "Net Ball", "Dive Ball", "Nest Ball", "Repeat Ball", "Timer Ball",
        "Luxury Ball", "Premier Ball", "Dusk Ball", "Heal Ball", "Quick Ball",
        "Level Ball", "Lure Ball", "Moon Ball", "Friend Ball", "Love Ball",
        "Heavy Ball", "Fast Ball", "Safari Ball", "Sport Ball",
        "Beast Ball", "Dream Ball",
    ];

    private static readonly string[] BallsBDSP =
    [
        "Poké Ball", "Great Ball", "Ultra Ball", "Master Ball",
        "Net Ball", "Dive Ball", "Nest Ball", "Repeat Ball", "Timer Ball",
        "Luxury Ball", "Premier Ball", "Dusk Ball", "Heal Ball", "Quick Ball",
        "Safari Ball", "Sport Ball",
    ];

    public static string[] GetBalls() => CurrentGameType switch
    {
        "la"   => BallsLA,
        "swsh" => BallsSWSH,
        "bdsp" => BallsBDSP,
        _      => BallsSV,
    };

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
                if (ulong.TryParse(k, out var id))
                    Panels[id] = v;
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

    // ─── Registration ─────────────────────────────────────────────────────────

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

                try { await channel.DeleteMessageAsync(Panels[channelId]); } catch { }

                var msg = await channel.SendMessageAsync(
                    embed: CreatePanelEmbed(),
                    components: CreatePanelComponents()
                );
                Panels[channelId] = msg.Id;
            }
            catch { }
        }
        Save();
    }

    // ─── Sticky (called from SysCord.HandleMessageAsync) ─────────────────────

    public static async Task OnMessageReceivedAsync(SocketMessage message)
    {
        // Skip system messages (e.g. "pinned a message" notice) to avoid loops
        if (message.Type != MessageType.Default && message.Type != MessageType.Reply)
            return;

        if (!Panels.TryGetValue(message.Channel.Id, out var panelMsgId))
            return;

        // Don't react to the panel being freshly posted
        if (message.Id == panelMsgId)
            return;

        // Per-channel cooldown
        if (LastRepost.TryGetValue(message.Channel.Id, out var last) &&
            (DateTime.UtcNow - last).TotalSeconds < CooldownSeconds)
            return;

        LastRepost[message.Channel.Id] = DateTime.UtcNow;

        try { await message.Channel.DeleteMessageAsync(panelMsgId); } catch { }

        if (message.Channel is not ITextChannel textChannel) return;

        var newMsg = await textChannel.SendMessageAsync(
            embed: CreatePanelEmbed(),
            components: CreatePanelComponents()
        );
        Panels[message.Channel.Id] = newMsg.Id;
        Save();
    }

    // ─── Panel embed & components ─────────────────────────────────────────────

    public static Embed CreatePanelEmbed()
    {
        var extras = CurrentGameType switch
        {
            "sv"           => "Tera Type 🧬",
            "la" or "plza" => "Alpha ⭐",
            _              => "",
        };
        var featuresField = "Species • Level • Nature • Shiny ✨\nIVs & EVs • Held Item • Poké Ball • Moves ⚔️"
            + (string.IsNullOrEmpty(extras) ? "" : $"\n{extras}");

        return new EmbedBuilder()
            .WithTitle("🔨 Pokémon Builder")
            .WithColor(Color.Gold)
            .WithDescription(
                $"**Game: {GetGameLabel()}**\n\n" +
                "Click **Build a Pokémon** below!\n" +
                "Pick your options from the dropdowns — no typing required.\n​")
            .AddField("🐾 What you can set", featuresField)
            .WithFooter($"PokedexMasterBot {TradeBot.Version} • Tap the button to start!")
            .Build();
    }

    public static MessageComponent CreatePanelComponents()
    {
        return new ComponentBuilder()
            .WithButton("🔨  Build a Pokémon", "pb_start", ButtonStyle.Success)
            .Build();
    }
}
