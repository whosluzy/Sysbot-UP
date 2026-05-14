using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using PKHeX.Core;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord.Commands.Bots.SlashCommands;

// ─── Session state ────────────────────────────────────────────────────────────
public class PokemonBuilderState
{
    public string GameType = "sv";
    public string Species  = "";
    public int    Level    = 50;
    public string Nature   = "";
    public bool   Shiny    = false;
    public bool   Alpha    = false;
    public string IVs      = "";
    public string EVs      = "";
    public string Item     = "";
    public string Ball     = "";
    public string Move1    = "", Move2 = "", Move3 = "", Move4 = "";
    public string TeraType = "";
    public ulong  MessageId  = 0;
}

// ─── Modals ───────────────────────────────────────────────────────────────────
public class PokeBuildBasicModal : IModal
{
    public string Title => "🔨 Build Your Pokémon";

    [RequiredInput(true)]
    [InputLabel("Pokémon Name")]
    [ModalTextInput("pb_species", placeholder: "e.g. Pikachu", maxLength: 50)]
    public string Species { get; set; } = "";

    [RequiredInput(false)]
    [InputLabel("Level (1-100, leave blank for 50)")]
    [ModalTextInput("pb_level", placeholder: "50", maxLength: 3)]
    public string Level { get; set; } = "";

    [RequiredInput(false)]
    [InputLabel("Nature (optional, leave blank for random)")]
    [ModalTextInput("pb_nature", placeholder: "e.g. Adamant", maxLength: 20)]
    public string Nature { get; set; } = "";

    [RequiredInput(false)]
    [InputLabel("Shiny? (type yes or no, default: no)")]
    [ModalTextInput("pb_shiny", placeholder: "no", maxLength: 3)]
    public string Shiny { get; set; } = "";
}

public class PokeBuildStatsModal : IModal
{
    public string Title => "📊 Set IVs & EVs";

    [RequiredInput(false)]
    [InputLabel("IVs — HP/Atk/Def/SpA/SpD/Spe")]
    [ModalTextInput("pb_ivs", placeholder: "31/31/31/31/31/31", maxLength: 50)]
    public string IVs { get; set; } = "";

    [RequiredInput(false)]
    [InputLabel("EVs — HP/Atk/Def/SpA/SpD/Spe")]
    [ModalTextInput("pb_evs", placeholder: "252/252/4/0/0/0", maxLength: 50)]
    public string EVs { get; set; } = "";
}

public class PokeBuildItemModal : IModal
{
    public string Title => "🎒 Item, Ball & Moves";

    [RequiredInput(false)]
    [InputLabel("Held Item (optional)")]
    [ModalTextInput("pb_item", placeholder: "e.g. Choice Band", maxLength: 50)]
    public string Item { get; set; } = "";

    [RequiredInput(false)]
    [InputLabel("Poké Ball (optional)")]
    [ModalTextInput("pb_ball", placeholder: "e.g. Master Ball", maxLength: 30)]
    public string Ball { get; set; } = "";

    [RequiredInput(false)]
    [InputLabel("Moves (one per line, up to 4)")]
    [ModalTextInput("pb_moves", TextInputStyle.Paragraph, placeholder: "Thunderbolt\nIce Beam\nFlamethrower\nSurf", maxLength: 200)]
    public string Moves { get; set; } = "";
}

// ─── Module ───────────────────────────────────────────────────────────────────
public class PokeBuildModule : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly ConcurrentDictionary<ulong, PokemonBuilderState> Sessions = new();

    private static readonly string[] TeraTypes =
    [
        "Normal", "Fire", "Water", "Electric", "Grass", "Ice",
        "Fighting", "Poison", "Ground", "Flying", "Psychic", "Bug",
        "Rock", "Ghost", "Dragon", "Dark", "Steel", "Fairy", "Stellar",
    ];

    // ─── Slash commands ───────────────────────────────────────────────────────

    [SlashCommand("pokebuild-setup", "Post the permanent Pokémon builder panel in this channel (Admin only)")]
    public async Task PokeBuildSetupAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync("❌ This command can only be used in a server.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        var member = Context.User as SocketGuildUser;
        if (member == null || (!member.GuildPermissions.ManageGuild && !member.GuildPermissions.Administrator))
        {
            await RespondAsync("❌ You need **Manage Server** permission to post the builder panel.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        await Context.Channel.SendMessageAsync(embed: BuildPanelEmbed(), components: BuildPanelComponents()).ConfigureAwait(false);
        await FollowupAsync("✅ Builder panel posted! Pin it so it stays at the top.", ephemeral: true).ConfigureAwait(false);
    }

    // Kept as fallback — slash commands still work alongside the panel
    [SlashCommand("pokebuild", "Build a Scarlet/Violet Pokémon step by step")]
    public async Task PokeBuildSVAsync()
        => await StartBuilderAsync("sv").ConfigureAwait(false);

    [SlashCommand("pokebuild-la", "Build a Legends: Arceus Pokémon step by step")]
    public async Task PokeBuildLAAsync()
        => await StartBuilderAsync("la").ConfigureAwait(false);

    [SlashCommand("pokebuild-plza", "Build a Legends: Z-A Pokémon step by step")]
    public async Task PokeBuildPLZAAsync()
        => await StartBuilderAsync("plza").ConfigureAwait(false);

    [SlashCommand("pokebuild-swsh", "Build a Sword/Shield Pokémon step by step")]
    public async Task PokeBuildSWSHAsync()
        => await StartBuilderAsync("swsh").ConfigureAwait(false);

    [SlashCommand("pokebuild-bdsp", "Build a BDSP Pokémon step by step")]
    public async Task PokeBuildBDSPAsync()
        => await StartBuilderAsync("bdsp").ConfigureAwait(false);

    // ─── Panel button → open builder ─────────────────────────────────────────

    [ComponentInteraction("pb_start_*")]
    public async Task OnPanelStartAsync(string gameType)
    {
        if (Context.Guild == null)
        {
            await RespondAsync("❌ This can only be used in a server.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await RespondWithModalAsync<PokeBuildBasicModal>($"pb_basic_{gameType}").ConfigureAwait(false);
    }

    private async Task StartBuilderAsync(string gameType)
    {
        if (Context.Guild == null)
        {
            await RespondAsync("❌ This command can only be used in a server.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await RespondWithModalAsync<PokeBuildBasicModal>($"pb_basic_{gameType}").ConfigureAwait(false);
    }

    // ─── Modal handlers ───────────────────────────────────────────────────────

    [ModalInteraction("pb_basic_*")]
    public async Task OnBasicModalAsync(string gameType, PokeBuildBasicModal modal)
    {
        await DeferAsync(ephemeral: false).ConfigureAwait(false);
        var userId = Context.User.Id;

        var session = Sessions.GetOrAdd(userId, _ => new PokemonBuilderState());
        session.GameType = gameType;
        session.Species  = modal.Species.Trim();
        session.Level    = int.TryParse(modal.Level.Trim(), out int lvl) ? Math.Clamp(lvl, 1, 100) : 50;
        session.Nature   = modal.Nature.Trim();
        session.Shiny    = modal.Shiny.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);

        var msg = await FollowupAsync(
            embed: BuildEmbed(session),
            components: BuildComponents(session, userId)
        ).ConfigureAwait(false);

        session.MessageId = msg.Id;
    }

    [ModalInteraction("pb_statsmodal_*")]
    public async Task OnStatsModalAsync(string userIdStr, PokeBuildStatsModal modal)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !Sessions.TryGetValue(userId, out var session))
        {
            await RespondAsync("❌ Session expired. Please use `/pokebuild` again.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync().ConfigureAwait(false);
        session.IVs = modal.IVs.Trim();
        session.EVs = modal.EVs.Trim();
        await UpdateBuilderMessageAsync(session, userId).ConfigureAwait(false);
    }

    [ModalInteraction("pb_itemmodal_*")]
    public async Task OnItemModalAsync(string userIdStr, PokeBuildItemModal modal)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !Sessions.TryGetValue(userId, out var session))
        {
            await RespondAsync("❌ Session expired. Please use `/pokebuild` again.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync().ConfigureAwait(false);

        if (session.GameType != "la")
            session.Item = modal.Item.Trim();

        session.Ball = modal.Ball.Trim();

        var lines = modal.Moves.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        session.Move1 = lines.ElementAtOrDefault(0) ?? "";
        session.Move2 = lines.ElementAtOrDefault(1) ?? "";
        session.Move3 = lines.ElementAtOrDefault(2) ?? "";
        session.Move4 = lines.ElementAtOrDefault(3) ?? "";

        await UpdateBuilderMessageAsync(session, userId).ConfigureAwait(false);
    }

    // ─── Button handlers ──────────────────────────────────────────────────────

    [ComponentInteraction("pb_stats_*")]
    public async Task OnStatsButtonAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !Sessions.ContainsKey(userId))
        {
            await RespondAsync("❌ Session expired. Please use `/pokebuild` again.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await RespondWithModalAsync<PokeBuildStatsModal>($"pb_statsmodal_{userId}").ConfigureAwait(false);
    }

    [ComponentInteraction("pb_item_*")]
    public async Task OnItemButtonAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !Sessions.ContainsKey(userId))
        {
            await RespondAsync("❌ Session expired. Please use `/pokebuild` again.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await RespondWithModalAsync<PokeBuildItemModal>($"pb_itemmodal_{userId}").ConfigureAwait(false);
    }

    [ComponentInteraction("pb_shiny_*")]
    public async Task OnShinyToggleAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !Sessions.TryGetValue(userId, out var session))
        {
            await RespondAsync("❌ Session expired.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync().ConfigureAwait(false);
        session.Shiny = !session.Shiny;
        await UpdateBuilderMessageAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_alpha_*")]
    public async Task OnAlphaToggleAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !Sessions.TryGetValue(userId, out var session))
        {
            await RespondAsync("❌ Session expired.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync().ConfigureAwait(false);
        session.Alpha = !session.Alpha;
        await UpdateBuilderMessageAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_tera_*")]
    public async Task OnTeraSelectAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !Sessions.TryGetValue(userId, out var session))
        {
            await RespondAsync("❌ Session expired.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        var component = (SocketMessageComponent)Context.Interaction;
        session.TeraType = component.Data.Values.FirstOrDefault() ?? "";
        await DeferAsync().ConfigureAwait(false);
        await UpdateBuilderMessageAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_submit_*")]
    public async Task OnSubmitAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId))
        {
            await RespondAsync("❌ Invalid session.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (!Sessions.TryRemove(userId, out var session))
        {
            await RespondAsync("❌ Session expired. Please use `/pokebuild` again.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        await DeferAsync(ephemeral: false).ConfigureAwait(false);
        await DeleteBuilderMessageAsync(session).ConfigureAwait(false);
        await DispatchSubmitAsync(session).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_cancel_*")]
    public async Task OnCancelAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId))
        {
            await RespondAsync("❌ Invalid session.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (!Sessions.TryRemove(userId, out var session))
        {
            await RespondAsync("❌ No active builder session.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        await DeleteBuilderMessageAsync(session).ConfigureAwait(false);
        await FollowupAsync("❌ Builder canceled.", ephemeral: true).ConfigureAwait(false);
    }

    // ─── Submit dispatch ──────────────────────────────────────────────────────

    private async Task DispatchSubmitAsync(PokemonBuilderState s)
    {
        var item   = string.IsNullOrWhiteSpace(s.Item)   ? null : s.Item;
        var ball   = string.IsNullOrWhiteSpace(s.Ball)   ? null : s.Ball;
        var nature = string.IsNullOrWhiteSpace(s.Nature) ? null : s.Nature;
        var ivs    = string.IsNullOrWhiteSpace(s.IVs)    ? null : s.IVs;
        var evs    = string.IsNullOrWhiteSpace(s.EVs)    ? null : s.EVs;
        bool hasMoves = !string.IsNullOrWhiteSpace(s.Move1) || !string.IsNullOrWhiteSpace(s.Move2)
                     || !string.IsNullOrWhiteSpace(s.Move3) || !string.IsNullOrWhiteSpace(s.Move4);

        switch (s.GameType)
        {
            case "sv":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PK9>(
                    Context, s.Species, s.Shiny, item, ball, s.Level, nature, ivs, evs,
                    string.IsNullOrWhiteSpace(s.TeraType) ? "" : $"Tera Type: {s.TeraType}",
                    hasMoves ? BuildMovePostProcess<PK9>(s) : null
                ).ConfigureAwait(false);
                break;

            case "la":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PA8>(
                    Context, s.Species, s.Shiny, null, ball, s.Level, nature, ivs, evs,
                    s.Alpha ? "Alpha: Yes" : "",
                    hasMoves ? BuildMovePostProcess<PA8>(s) : null
                ).ConfigureAwait(false);
                break;

            case "plza":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PA9>(
                    Context, s.Species, s.Shiny, item, ball, s.Level, nature, ivs, evs,
                    s.Alpha ? "Alpha: Yes" : "",
                    hasMoves ? BuildMovePostProcess<PA9>(s) : null
                ).ConfigureAwait(false);
                break;

            case "swsh":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PK8>(
                    Context, s.Species, s.Shiny, item, ball, s.Level, nature, ivs, evs, "",
                    hasMoves ? BuildMovePostProcess<PK8>(s) : null
                ).ConfigureAwait(false);
                break;

            case "bdsp":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PB8>(
                    Context, s.Species, s.Shiny, item, ball, s.Level, nature, ivs, evs, "",
                    hasMoves ? BuildMovePostProcess<PB8>(s) : null
                ).ConfigureAwait(false);
                break;

            default:
                await FollowupAsync("❌ Unknown game type. Please start a new builder.", ephemeral: true).ConfigureAwait(false);
                break;
        }
    }

    private static Action<T> BuildMovePostProcess<T>(PokemonBuilderState s) where T : PKM, new()
    {
        return pk =>
        {
            var moveNames = GameInfo.GetStrings("en").movelist;
            var ids = new[] { s.Move1, s.Move2, s.Move3, s.Move4 }
                .Select(m => FindMoveId(moveNames, m))
                .ToArray();
            if (ids[0] > 0) pk.Move1 = ids[0];
            if (ids[1] > 0) pk.Move2 = ids[1];
            if (ids[2] > 0) pk.Move3 = ids[2];
            if (ids[3] > 0) pk.Move4 = ids[3];
            pk.HealPP();
        };
    }

    private static ushort FindMoveId(string[] moveNames, string moveName)
    {
        if (string.IsNullOrWhiteSpace(moveName)) return 0;
        var idx = Array.FindIndex(moveNames, 1, mn =>
            mn.Equals(moveName.Trim(), StringComparison.OrdinalIgnoreCase));
        return idx > 0 ? (ushort)idx : (ushort)0;
    }

    // ─── Embed builder ────────────────────────────────────────────────────────

    private static Embed BuildEmbed(PokemonBuilderState s)
    {
        var gameLabel = s.GameType switch
        {
            "sv"   => "Scarlet/Violet 🟣",
            "la"   => "Legends: Arceus 🏔️",
            "plza" => "Legends: Z-A 🗼",
            "swsh" => "Sword/Shield ⚔️",
            "bdsp" => "BDSP 💎",
            _      => s.GameType,
        };

        var eb = new EmbedBuilder()
            .WithTitle("🔨 Building Your Pokémon")
            .WithColor(Color.Blue)
            .WithDescription($"**Game:** {gameLabel}\nCustomize your Pokémon below, then hit **✅ Submit Trade**!")
            .AddField("🐾 Species", string.IsNullOrWhiteSpace(s.Species) ? "*Not set*" : s.Species, inline: true)
            .AddField("📊 Level",   s.Level.ToString(), inline: true)
            .AddField("✨ Shiny",   s.Shiny ? "Yes ⭐" : "No", inline: true)
            .AddField("🌿 Nature",  string.IsNullOrWhiteSpace(s.Nature) ? "*Random*" : s.Nature, inline: true)
            .AddField("🏆 IVs",     string.IsNullOrWhiteSpace(s.IVs) ? "31 all (default)" : s.IVs, inline: true)
            .AddField("💪 EVs",     string.IsNullOrWhiteSpace(s.EVs) ? "None" : s.EVs, inline: true)
            .AddField("💎 Item",    string.IsNullOrWhiteSpace(s.Item) ? "None" : s.Item, inline: true)
            .AddField("⚾ Ball",    string.IsNullOrWhiteSpace(s.Ball) ? "Default" : s.Ball, inline: true);

        if (s.GameType == "sv")
            eb.AddField("🧬 Tera Type", string.IsNullOrWhiteSpace(s.TeraType) ? "Default" : s.TeraType, inline: true);
        else if (s.GameType is "la" or "plza")
            eb.AddField("⭐ Alpha", s.Alpha ? "Yes" : "No", inline: true);

        var moveList = new[] { s.Move1, s.Move2, s.Move3, s.Move4 }
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToList();
        if (moveList.Count > 0)
            eb.AddField("⚔️ Moves", string.Join("\n", moveList.Select((m, i) => $"{i + 1}. {m}")));

        eb.WithFooter($"PokedexMasterBot {TradeBot.Version} • Fill in details and press Submit Trade!");

        return eb.Build();
    }

    // ─── Component builder ────────────────────────────────────────────────────

    private static MessageComponent BuildComponents(PokemonBuilderState s, ulong userId)
    {
        var cb = new ComponentBuilder();

        // Row 0: Stats + Item/Ball/Moves
        cb.WithButton("📊 IVs & EVs",        $"pb_stats_{userId}", ButtonStyle.Secondary, row: 0);
        cb.WithButton("🎒 Item, Ball & Moves", $"pb_item_{userId}",  ButtonStyle.Secondary, row: 0);

        // Row 1: Shiny toggle + game-specific toggle
        cb.WithButton(
            s.Shiny ? "✨ Shiny: ON" : "✨ Shiny: OFF",
            $"pb_shiny_{userId}",
            s.Shiny ? ButtonStyle.Success : ButtonStyle.Secondary,
            row: 1
        );

        if (s.GameType is "la" or "plza")
        {
            cb.WithButton(
                s.Alpha ? "⭐ Alpha: ON" : "⭐ Alpha: OFF",
                $"pb_alpha_{userId}",
                s.Alpha ? ButtonStyle.Success : ButtonStyle.Secondary,
                row: 1
            );
        }

        // Row 2 (SV only): Tera Type select menu
        if (s.GameType == "sv")
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId($"pb_tera_{userId}")
                .WithPlaceholder("🎮 Select Tera Type...");
            foreach (var t in TeraTypes)
                menu.AddOption(t, t, isDefault: t.Equals(s.TeraType, StringComparison.OrdinalIgnoreCase));
            cb.WithSelectMenu(menu, row: 2);
        }

        // Final row: Submit + Cancel
        int actionRow = s.GameType == "sv" ? 3 : 2;
        cb.WithButton("✅ Submit Trade", $"pb_submit_{userId}", ButtonStyle.Success, row: actionRow);
        cb.WithButton("❌ Cancel",       $"pb_cancel_{userId}", ButtonStyle.Danger,   row: actionRow);

        return cb.Build();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task UpdateBuilderMessageAsync(PokemonBuilderState session, ulong userId)
    {
        if (session.MessageId == 0) return;
        try
        {
            if (await Context.Channel.GetMessageAsync(session.MessageId).ConfigureAwait(false) is IUserMessage msg)
            {
                await msg.ModifyAsync(m =>
                {
                    m.Embed      = BuildEmbed(session);
                    m.Components = BuildComponents(session, userId);
                }).ConfigureAwait(false);
            }
        }
        catch { /* message may have been deleted */ }
    }

    private async Task DeleteBuilderMessageAsync(PokemonBuilderState session)
    {
        if (session.MessageId == 0) return;
        try
        {
            if (await Context.Channel.GetMessageAsync(session.MessageId).ConfigureAwait(false) is IUserMessage msg)
                await msg.DeleteAsync().ConfigureAwait(false);
        }
        catch { /* already deleted */ }
    }

    // ─── Panel embed & components ─────────────────────────────────────────────

    private static Embed BuildPanelEmbed()
    {
        return new EmbedBuilder()
            .WithTitle("🔨 Pokémon Builder")
            .WithColor(Color.Gold)
            .WithDescription(
                "Want a Pokémon? Click the button for your game below!\n\n" +
                "You'll be guided step by step — no commands needed. " +
                "Fill in the name, level, IVs, moves, and more, then hit **Submit Trade** to get in the queue!\n​")
            .AddField("🐾 What you can set",
                "Species • Level • Nature • Shiny ✨\n" +
                "IVs & EVs • Held Item • Poké Ball • Moves ⚔️\n" +
                "Tera Type 🧬 (SV) • Alpha ⭐ (LA/PLZA)", inline: false)
            .WithFooter($"PokedexMasterBot {TradeBot.Version} • Tap a button to start building!")
            .Build();
    }

    private static MessageComponent BuildPanelComponents()
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
