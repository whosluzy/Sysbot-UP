using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using PKHeX.Core;
using SysBot.Pokemon.Discord.Commands.Bots.Autocomplete;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord.Commands.Bots.SlashCommands;

// ─── Cached game data (built once per game type, held for the process lifetime) ─
internal static class BuilderData
{
    private const int PageSize = 24;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<(string Display, string Value)>>
        SpeciesCache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>>
        ItemCache = new();
    private static IReadOnlyList<string>? _moves;

    public static IReadOnlyList<(string Display, string Value)> GetSpecies(string gameType)
        => SpeciesCache.GetOrAdd(gameType, BuildSpecies);

    public static IReadOnlyList<string> GetItems(string gameType)
        => ItemCache.GetOrAdd(gameType, BuildItems);

    public static IReadOnlyList<string> GetMoves()
        => _moves ??= BuildMoves();

    private static IReadOnlyList<string> BuildMoves()
        => GameInfo.GetStrings("en").movelist
            .Select((name, idx) => (name, idx))
            .Where(x => x.idx > 0 && !string.IsNullOrEmpty(x.name))
            .OrderBy(x => x.name)
            .Select(x => x.name)
            .ToList();

    public static int TotalPages(int count) => Math.Max(1, (int)Math.Ceiling(count / (double)PageSize));

    public static IEnumerable<T> GetPage<T>(IReadOnlyList<T> list, int page)
        => list.Skip(page * PageSize).Take(PageSize);

    // ── Species ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<(string Display, string Value)> BuildSpecies(string gameType)
    {
        IPersonalTable table = gameType switch
        {
            "la"   => PersonalTable.LA,
            "plza" => PersonalTable.ZA,
            "swsh" => PersonalTable.SWSH,
            "bdsp" => PersonalTable.BDSP,
            _      => PersonalTable.SV,
        };
        var ctx     = GameContext(gameType);
        var strings = GameInfo.GetStrings("en");
        var list    = new List<(string Display, string Value)>();

        for (ushort sp = 1; sp < (ushort)Species.MAX_COUNT; sp++)
        {
            var name = ((Species)sp).ToString();
            if (name is "None" or "Egg") continue;

            var formCount = table[sp].FormCount;
            var formList  = FormConverter.GetFormList(sp, strings.Types, strings.forms,
                GameInfo.GenderSymbolASCII, ctx);

            for (byte form = 0; form < formCount; form++)
            {
                if (!table.IsPresentInGame(sp, form)) continue;
                var formName = formList.Length > form ? formList[form] : string.Empty;
                if (form > 0 && string.IsNullOrWhiteSpace(formName)) continue;
                if (ForbiddenForms.IsForbidden(sp, form, formName)) continue;

                var display = MakeDisplay(name, formName, sp, form);
                list.Add((display, $"{name}|{form}|{display}"));
            }
        }
        return list.OrderBy(x => x.Display).ToList();
    }

    private static string MakeDisplay(string baseName, string formName, ushort sp, byte form)
    {
        if (string.IsNullOrWhiteSpace(formName) ||
            formName.Equals("Normal", StringComparison.OrdinalIgnoreCase))
            return baseName;
        if (ForbiddenForms.ShouldSuppressSuffix(sp, form, formName))
            return baseName;
        if (sp == (ushort)Species.Basculin && formName.Contains("Striped", StringComparison.OrdinalIgnoreCase))
        {
            var color = formName.Replace("-Striped", "").Replace("Striped", "").Replace(" ", "");
            return $"{baseName}-{color}";
        }
        return $"{baseName}-{formName.Replace(' ', '-')}";
    }

    // ── Items ─────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildItems(string gameType)
    {
        var ctx = gameType switch
        {
            "la"           => EntityContext.Gen8a,
            "swsh" or "bdsp" => EntityContext.Gen8,
            _              => EntityContext.Gen9,
        };
        return GameInfo.GetStrings("en").Item
            .Select((name, idx) => (name, idx))
            .Where(x => x.idx > 0
                && !string.IsNullOrEmpty(x.name)
                && !x.name.StartsWith("(")
                && !x.name.Contains("???")
                && ItemRestrictions.IsHeldItemAllowed((ushort)x.idx, ctx))
            .OrderBy(x => x.name)
            .Select(x => x.name)
            .ToList();
    }

    // ── Moves (per species, cached) ───────────────────────────────────────────

    private static readonly ConcurrentDictionary<(string Game, ushort Species, byte Form), IReadOnlyList<string>>
        MovesForSpeciesCache = new();

    public static IReadOnlyList<string> GetMovesForSpecies(string gameType, ushort species, byte form)
        => MovesForSpeciesCache.GetOrAdd((gameType, species, form), k => BuildMovesForSpecies(k.Game, k.Species, k.Form));

    private static IReadOnlyList<string> BuildMovesForSpecies(string gameType, ushort species, byte form)
    {
        var moveNames = GameInfo.GetStrings("en").movelist;
        var result    = new bool[moveNames.Length];
        var evo       = new EvoCriteria { Species = species, Form = form, LevelMax = 100, LevelMin = 1 };

        switch (gameType)
        {
            case "sv":   LearnSource9SV.Instance.GetAllMoves(result,   new PK9(), evo, MoveSourceType.All); break;
            case "la":   LearnSource8LA.Instance.GetAllMoves(result,   new PA8(), evo, MoveSourceType.All); break;
            case "plza": LearnSource9ZA.Instance.GetAllMoves(result,   new PA9(), evo, MoveSourceType.All); break;
            case "swsh": LearnSource8SWSH.Instance.GetAllMoves(result, new PK8(), evo, MoveSourceType.All); break;
            case "bdsp": LearnSource8BDSP.Instance.GetAllMoves(result, new PB8(), evo, MoveSourceType.All); break;
        }

        return result
            .Select((can, i) => (can, i))
            .Where(x => x.can && x.i > 0 && x.i < moveNames.Length && !string.IsNullOrEmpty(moveNames[x.i]))
            .OrderBy(x => moveNames[x.i])
            .Select(x => moveNames[x.i])
            .ToList();
    }

    // ── EntityContext per game ────────────────────────────────────────────────

    public static EntityContext GameContext(string gameType) => gameType switch
    {
        "la"           => EntityContext.Gen8a,
        "swsh"         => EntityContext.Gen8,
        "bdsp"         => EntityContext.Gen8b,
        _              => EntityContext.Gen9,
    };
}

// ─── Session state ─────────────────────────────────────────────────────────────
public class PokemonBuilderState
{
    public string GameType       = "sv";
    public string Species        = "";   // "EnumName|form|DisplayName"
    public string SpeciesDisplay = "";
    public int    Level          = 50;
    public string Nature         = "";
    public bool   Shiny          = false;
    public bool   Alpha          = false;
    public string Item           = "";
    public string Ball           = "";
    public string Move1 = "", Move2 = "", Move3 = "", Move4 = "";
    public string TeraType       = "";
    public ulong  MessageId      = 0;

    // Picker state
    public int SpeciesPage = 0;
    public int ItemPage    = 0;
    public int MovePage    = 0;
    public int MoveStep    = 0;   // 0-3
}

// ─── Module ────────────────────────────────────────────────────────────────────
public class PokeBuildModule : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly ConcurrentDictionary<ulong, PokemonBuilderState> Sessions = new();

    private static readonly string[] TeraTypes =
    [
        "Normal","Fire","Water","Electric","Grass","Ice",
        "Fighting","Poison","Ground","Flying","Psychic","Bug",
        "Rock","Ghost","Dragon","Dark","Steel","Fairy","Stellar",
    ];

    private static readonly (string Label, string Val)[] Levels =
        Enumerable.Range(1, 100)
            .Where(l => l == 1 || l % 5 == 0)
            .Select(l => ($"Level {l}", l.ToString()))
            .ToArray();

    private enum PickerMode { Builder, Species, Item, Move }

    // ─── Session check helper ─────────────────────────────────────────────────

    private async Task<(bool ok, ulong userId, PokemonBuilderState session)> CheckAsync(string userIdStr)
    {
        var bad = (false, 0UL, (PokemonBuilderState)null!);
        if (!ulong.TryParse(userIdStr, out var userId))
        {
            await RespondAsync("❌ Invalid session.", ephemeral: true).ConfigureAwait(false);
            return bad;
        }
        if (userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return bad;
        }
        if (!Sessions.TryGetValue(userId, out var session))
        {
            await RespondAsync("❌ Session expired — please start again.", ephemeral: true).ConfigureAwait(false);
            return bad;
        }
        return (true, userId, session);
    }

    // ─── Panel button ─────────────────────────────────────────────────────────

    [ComponentInteraction("pb_start")]
    public async Task OnPanelStartAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync("❌ Server only.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync(ephemeral: false).ConfigureAwait(false);

        var userId   = Context.User.Id;
        var gameType = PokeBuildPanelManager.CurrentGameType;
        var session  = new PokemonBuilderState { GameType = gameType };
        Sessions[userId] = session;

        var msg = await FollowupAsync(
            embed: SpeciesPickerEmbed(session),
            components: SpeciesPickerComponents(session, userId)
        ).ConfigureAwait(false);
        session.MessageId = msg.Id;
    }

    // ─── Species picker ───────────────────────────────────────────────────────

    [ComponentInteraction("pb_spec_sel_*")]
    public async Task OnSpeciesSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;

        var value = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        var parts = value.Split('|');
        session.Species        = value;
        session.SpeciesDisplay = parts.ElementAtOrDefault(2) ?? parts.ElementAtOrDefault(0) ?? value;

        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_spec_prev_*")]
    public async Task OnSpeciesPrevAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        session.SpeciesPage = Math.Max(0, session.SpeciesPage - 1);
        await UpdateAsync(session, userId, PickerMode.Species).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_spec_next_*")]
    public async Task OnSpeciesNextAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        var total = BuilderData.TotalPages(BuilderData.GetSpecies(session.GameType).Count);
        session.SpeciesPage = Math.Min(total - 1, session.SpeciesPage + 1);
        await UpdateAsync(session, userId, PickerMode.Species).ConfigureAwait(false);
    }

    // ─── Item picker ──────────────────────────────────────────────────────────

    [ComponentInteraction("pb_item_*")]
    public async Task OnItemButtonAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        session.ItemPage = 0;
        await UpdateAsync(session, userId, PickerMode.Item).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_isel_*")]
    public async Task OnItemSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        var value = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        session.Item = value;
        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_iclear_*")]
    public async Task OnItemClearAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Item = "";
        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_iprev_*")]
    public async Task OnItemPrevAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        session.ItemPage = Math.Max(0, session.ItemPage - 1);
        await UpdateAsync(session, userId, PickerMode.Item).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_inext_*")]
    public async Task OnItemNextAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        var total = BuilderData.TotalPages(BuilderData.GetItems(session.GameType).Count);
        session.ItemPage = Math.Min(total - 1, session.ItemPage + 1);
        await UpdateAsync(session, userId, PickerMode.Item).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_iback_*")]
    public async Task OnItemBackAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    // ─── Move picker ──────────────────────────────────────────────────────────

    [ComponentInteraction("pb_moves_*")]
    public async Task OnMovesButtonAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        session.MoveStep = 0;
        session.MovePage = 0;
        await UpdateAsync(session, userId, PickerMode.Move).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_msel_*")]
    public async Task OnMoveSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        var value = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        SetMove(session, session.MoveStep, value);
        await DeferAsync().ConfigureAwait(false);
        session.MoveStep++;
        session.MovePage = 0;
        var mode = session.MoveStep >= 4 ? PickerMode.Builder : PickerMode.Move;
        await UpdateAsync(session, userId, mode).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_mprev_*")]
    public async Task OnMovePrevAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        session.MovePage = Math.Max(0, session.MovePage - 1);
        await UpdateAsync(session, userId, PickerMode.Move).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_mnext_*")]
    public async Task OnMoveNextAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        var total = BuilderData.TotalPages(GetSessionMoves(session).Count);
        session.MovePage = Math.Min(total - 1, session.MovePage + 1);
        await UpdateAsync(session, userId, PickerMode.Move).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_mskip_*")]
    public async Task OnMoveSkipAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        SetMove(session, session.MoveStep, "");
        session.MoveStep++;
        session.MovePage = 0;
        var mode = session.MoveStep >= 4 ? PickerMode.Builder : PickerMode.Move;
        await UpdateAsync(session, userId, mode).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_mback_*")]
    public async Task OnMoveBackAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    // ─── Builder dropdowns ────────────────────────────────────────────────────

    [ComponentInteraction("pb_level_*")]
    public async Task OnLevelSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        var value = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault();
        if (int.TryParse(value, out var lvl))
            session.Level = Math.Clamp(lvl, 1, 100);
        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_nature_*")]
    public async Task OnNatureSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Nature = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_ball_*")]
    public async Task OnBallSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Ball = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_tera_*")]
    public async Task OnTeraSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.TeraType = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        await DeferAsync().ConfigureAwait(false);
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    // ─── Builder toggle buttons ───────────────────────────────────────────────

    [ComponentInteraction("pb_shiny_*")]
    public async Task OnShinyToggleAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        session.Shiny = !session.Shiny;
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_alpha_*")]
    public async Task OnAlphaToggleAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        await DeferAsync().ConfigureAwait(false);
        session.Alpha = !session.Alpha;
        await UpdateAsync(session, userId, PickerMode.Builder).ConfigureAwait(false);
    }

    // ─── Submit / Cancel ──────────────────────────────────────────────────────

    [ComponentInteraction("pb_submit_*")]
    public async Task OnSubmitAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (!Sessions.TryRemove(userId, out var session))
        {
            await RespondAsync("❌ Session expired — please start again.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(session.Species))
        {
            await RespondAsync("❌ No Pokémon selected.", ephemeral: true).ConfigureAwait(false);
            Sessions[userId] = session; // put it back
            return;
        }
        await DeferAsync(ephemeral: false).ConfigureAwait(false);
        await DeleteBuilderAsync(session).ConfigureAwait(false);
        await DispatchAsync(session).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_cancel_*")]
    public async Task OnCancelAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || userId != Context.User.Id)
        {
            await RespondAsync("❌ This builder belongs to another user.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (!Sessions.TryRemove(userId, out var session))
        {
            await RespondAsync("❌ No active builder.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        await DeleteBuilderAsync(session).ConfigureAwait(false);
        await FollowupAsync("Builder canceled.", ephemeral: true).ConfigureAwait(false);
    }

    // ─── Admin slash command ──────────────────────────────────────────────────

    [SlashCommand("pokebuild-setup", "Post the Pokémon builder panel in this channel (Admin only)")]
    public async Task PokeBuildSetupAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync("❌ Server only.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        var member = Context.User as SocketGuildUser;
        if (member == null || (!member.GuildPermissions.ManageGuild && !member.GuildPermissions.Administrator))
        {
            await RespondAsync("❌ Requires **Manage Server** permission.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var msg = await Context.Channel.SendMessageAsync(
            embed: PokeBuildPanelManager.CreatePanelEmbed(),
            components: PokeBuildPanelManager.CreatePanelComponents()
        ).ConfigureAwait(false);
        PokeBuildPanelManager.Register(Context.Channel.Id, msg.Id);
        await FollowupAsync("✅ Builder panel posted!", ephemeral: true).ConfigureAwait(false);
    }

    // ─── Embed & component builders ───────────────────────────────────────────

    private static Embed SpeciesPickerEmbed(PokemonBuilderState s)
    {
        var list  = BuilderData.GetSpecies(s.GameType);
        var total = BuilderData.TotalPages(list.Count);
        var slice = BuilderData.GetPage(list, s.SpeciesPage).ToList();
        return new EmbedBuilder()
            .WithTitle("🔨 Build a Pokémon — Choose a Species")
            .WithColor(Color.Blue)
            .WithDescription(
                $"**{PokeBuildPanelManager.GetGameLabel()}** — {list.Count} available\n\n" +
                $"Page **{s.SpeciesPage + 1} / {total}** · showing {slice.Count} Pokémon\n" +
                "Pick from the dropdown, or use ◀ ▶ to browse alphabetically.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();
    }

    private static MessageComponent SpeciesPickerComponents(PokemonBuilderState s, ulong userId)
    {
        var list  = BuilderData.GetSpecies(s.GameType);
        var total = BuilderData.TotalPages(list.Count);
        var page  = s.SpeciesPage;
        var slice = BuilderData.GetPage(list, page).ToList();

        var menu = new SelectMenuBuilder()
            .WithCustomId($"pb_spec_sel_{userId}")
            .WithPlaceholder("🐾 Select a Pokémon...");
        foreach (var (display, value) in slice)
            menu.AddOption(display, value);

        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("◀", $"pb_spec_prev_{userId}", ButtonStyle.Secondary, disabled: page == 0, row: 1)
            .WithButton($"{page + 1} / {total}", "pb_spec_label", ButtonStyle.Secondary, disabled: true, row: 1)
            .WithButton("▶", $"pb_spec_next_{userId}", ButtonStyle.Secondary, disabled: page >= total - 1, row: 1)
            .Build();
    }

    private static Embed BuilderEmbed(PokemonBuilderState s)
    {
        var moves = new[] { s.Move1, s.Move2, s.Move3, s.Move4 }
            .Select((m, i) => string.IsNullOrWhiteSpace(m) ? null : $"{i + 1}. {m}")
            .Where(m => m != null);

        var eb = new EmbedBuilder()
            .WithTitle("🔨 Building Your Pokémon")
            .WithColor(Color.Gold)
            .WithDescription(
                $"**{PokeBuildPanelManager.GetGameLabel()}** · " +
                "Use the dropdowns & buttons below, then hit **✅ Submit**!")
            .AddField("🐾 Pokémon",
                string.IsNullOrWhiteSpace(s.SpeciesDisplay) ? "*Not chosen*" : s.SpeciesDisplay, inline: true)
            .AddField("📊 Level", $"Lv. {s.Level}", inline: true)
            .AddField("✨ Shiny", s.Shiny ? "Yes ⭐" : "No", inline: true)
            .AddField("🌿 Nature",
                string.IsNullOrWhiteSpace(s.Nature) ? "*Random*" : s.Nature, inline: true)
            .AddField("⚾ Ball",
                string.IsNullOrWhiteSpace(s.Ball) ? "*Default*" : s.Ball, inline: true)
            .AddField("💎 Item",
                string.IsNullOrWhiteSpace(s.Item) ? "*None*" : s.Item, inline: true);

        if (s.GameType == "sv")
            eb.AddField("🧬 Tera",
                string.IsNullOrWhiteSpace(s.TeraType) ? "*Default*" : s.TeraType, inline: true);
        else if (s.GameType is "la" or "plza")
            eb.AddField("⭐ Alpha", s.Alpha ? "Yes" : "No", inline: true);

        var moveText = string.Join("\n", moves);
        eb.AddField("⚔️ Moves",
            string.IsNullOrWhiteSpace(moveText) ? "*None set — click Moves*" : moveText);

        eb.WithFooter($"PokedexMasterBot {TradeBot.Version} · IVs: 31 all by default");
        return eb.Build();
    }

    private static MessageComponent BuilderComponents(PokemonBuilderState s, ulong userId)
    {
        var cb = new ComponentBuilder();

        // Row 0 — Level
        var levelMenu = new SelectMenuBuilder()
            .WithCustomId($"pb_level_{userId}")
            .WithPlaceholder($"📊 Level — currently {s.Level}");
        foreach (var (label, val) in Levels)
            levelMenu.AddOption(label, val, isDefault: s.Level.ToString() == val);
        cb.WithSelectMenu(levelMenu, row: 0);

        // Row 1 — Nature
        var natureMenu = new SelectMenuBuilder()
            .WithCustomId($"pb_nature_{userId}")
            .WithPlaceholder("🌿 Nature — pick one...");
        foreach (var (name, effect) in PokeBuildPanelManager.Natures)
            natureMenu.AddOption(name, name, effect,
                isDefault: name.Equals(s.Nature, StringComparison.OrdinalIgnoreCase));
        cb.WithSelectMenu(natureMenu, row: 1);

        // Row 2 — Ball
        var ballMenu = new SelectMenuBuilder()
            .WithCustomId($"pb_ball_{userId}")
            .WithPlaceholder("⚾ Poké Ball — pick one...");
        foreach (var ball in PokeBuildPanelManager.GetBalls())
            ballMenu.AddOption(ball, ball,
                isDefault: ball.Equals(s.Ball, StringComparison.OrdinalIgnoreCase));
        cb.WithSelectMenu(ballMenu, row: 2);

        if (s.GameType == "sv")
        {
            // Row 3 — Tera Type
            var teraMenu = new SelectMenuBuilder()
                .WithCustomId($"pb_tera_{userId}")
                .WithPlaceholder("🧬 Tera Type — pick one...");
            foreach (var t in TeraTypes)
                teraMenu.AddOption(t, t,
                    isDefault: t.Equals(s.TeraType, StringComparison.OrdinalIgnoreCase));
            cb.WithSelectMenu(teraMenu, row: 3);

            // Row 4 — buttons
            cb.WithButton(s.Shiny ? "✨ Shiny: ON" : "✨ Shiny: OFF",
                $"pb_shiny_{userId}", s.Shiny ? ButtonStyle.Success : ButtonStyle.Secondary, row: 4);
            cb.WithButton("🎒 Item",  $"pb_item_{userId}",  ButtonStyle.Primary,   row: 4);
            cb.WithButton("⚔️ Moves", $"pb_moves_{userId}", ButtonStyle.Primary,   row: 4);
            cb.WithButton("✅ Submit", $"pb_submit_{userId}", ButtonStyle.Success,  row: 4);
            cb.WithButton("❌ Cancel", $"pb_cancel_{userId}", ButtonStyle.Danger,   row: 4);
        }
        else
        {
            // Row 3 — action buttons
            cb.WithButton(s.Shiny ? "✨ Shiny: ON" : "✨ Shiny: OFF",
                $"pb_shiny_{userId}", s.Shiny ? ButtonStyle.Success : ButtonStyle.Secondary, row: 3);
            if (s.GameType is "la" or "plza")
                cb.WithButton(s.Alpha ? "⭐ Alpha: ON" : "⭐ Alpha: OFF",
                    $"pb_alpha_{userId}", s.Alpha ? ButtonStyle.Success : ButtonStyle.Secondary, row: 3);
            cb.WithButton("🎒 Item",  $"pb_item_{userId}",  ButtonStyle.Primary, row: 3);
            cb.WithButton("⚔️ Moves", $"pb_moves_{userId}", ButtonStyle.Primary, row: 3);

            // Row 4 — submit / cancel
            cb.WithButton("✅ Submit Trade", $"pb_submit_{userId}", ButtonStyle.Success, row: 4);
            cb.WithButton("❌ Cancel",       $"pb_cancel_{userId}", ButtonStyle.Danger,  row: 4);
        }

        return cb.Build();
    }

    private static Embed ItemPickerEmbed(PokemonBuilderState s)
    {
        var items = BuilderData.GetItems(s.GameType);
        var total = BuilderData.TotalPages(items.Count);
        return new EmbedBuilder()
            .WithTitle("🎒 Choose a Held Item")
            .WithColor(Color.DarkGreen)
            .WithDescription(
                $"Currently: **{(string.IsNullOrWhiteSpace(s.Item) ? "None" : s.Item)}**\n" +
                $"Page **{s.ItemPage + 1} / {total}** · {items.Count} items available\n" +
                "Pick from the list, or ◀ ▶ to browse.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();
    }

    private static MessageComponent ItemPickerComponents(PokemonBuilderState s, ulong userId)
    {
        var items = BuilderData.GetItems(s.GameType);
        var total = BuilderData.TotalPages(items.Count);
        var page  = s.ItemPage;
        var slice = BuilderData.GetPage(items, page).ToList();

        var menu = new SelectMenuBuilder()
            .WithCustomId($"pb_isel_{userId}")
            .WithPlaceholder("🎒 Select held item...");
        foreach (var item in slice)
            menu.AddOption(item, item,
                isDefault: item.Equals(s.Item, StringComparison.OrdinalIgnoreCase));

        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("◀", $"pb_iprev_{userId}", ButtonStyle.Secondary, disabled: page == 0, row: 1)
            .WithButton($"{page + 1} / {total}", "pb_item_label", ButtonStyle.Secondary, disabled: true, row: 1)
            .WithButton("▶", $"pb_inext_{userId}", ButtonStyle.Secondary, disabled: page >= total - 1, row: 1)
            .WithButton("🚫 No Item",          $"pb_iclear_{userId}", ButtonStyle.Danger,     row: 2)
            .WithButton("🔙 Back to Builder",  $"pb_iback_{userId}",  ButtonStyle.Secondary,  row: 2)
            .Build();
    }

    private static Embed MovePickerEmbed(PokemonBuilderState s, IReadOnlyList<string> moves)
    {
        var total = BuilderData.TotalPages(moves.Count);
        var set   = new[] { s.Move1, s.Move2, s.Move3, s.Move4 }
            .Select((m, i) => string.IsNullOrWhiteSpace(m) ? $"Move {i + 1}: *empty*" : $"Move {i + 1}: {m}");
        return new EmbedBuilder()
            .WithTitle($"⚔️ Pick Move {s.MoveStep + 1} of 4")
            .WithColor(Color.Red)
            .WithDescription(
                string.Join("\n", set) + "\n\n" +
                $"Page **{s.MovePage + 1} / {total}** · {moves.Count} legal moves for {s.SpeciesDisplay}\n" +
                "Pick from the list, browse with ◀ ▶, or skip this slot.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();
    }

    private static MessageComponent MovePickerComponents(PokemonBuilderState s, ulong userId, IReadOnlyList<string> moves)
    {
        var total = BuilderData.TotalPages(moves.Count);
        var page  = s.MovePage;
        var slice = BuilderData.GetPage(moves, page).ToList();

        var menu = new SelectMenuBuilder()
            .WithCustomId($"pb_msel_{userId}")
            .WithPlaceholder($"⚔️ Move {s.MoveStep + 1} for {s.SpeciesDisplay}...");
        foreach (var move in slice)
            menu.AddOption(move, move);

        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("◀", $"pb_mprev_{userId}", ButtonStyle.Secondary, disabled: page == 0, row: 1)
            .WithButton($"{page + 1} / {total}", "pb_move_label", ButtonStyle.Secondary, disabled: true, row: 1)
            .WithButton("▶", $"pb_mnext_{userId}", ButtonStyle.Secondary, disabled: page >= total - 1, row: 1)
            .WithButton($"⏭ Skip Move {s.MoveStep + 1}", $"pb_mskip_{userId}", ButtonStyle.Secondary, row: 2)
            .WithButton("🔙 Back to Builder",             $"pb_mback_{userId}", ButtonStyle.Secondary, row: 2)
            .Build();
    }

    // ─── Central message updater ──────────────────────────────────────────────

    private async Task UpdateAsync(PokemonBuilderState session, ulong userId, PickerMode mode)
    {
        if (session.MessageId == 0) return;

        Embed embed;
        MessageComponent components;

        switch (mode)
        {
            case PickerMode.Species:
                embed      = SpeciesPickerEmbed(session);
                components = SpeciesPickerComponents(session, userId);
                break;
            case PickerMode.Item:
                embed      = ItemPickerEmbed(session);
                components = ItemPickerComponents(session, userId);
                break;
            case PickerMode.Move:
                var movesForPick = GetSessionMoves(session);
                embed      = MovePickerEmbed(session, movesForPick);
                components = MovePickerComponents(session, userId, movesForPick);
                break;
            default:
                // Builder — but if species not yet set, fall back to species picker
                if (string.IsNullOrWhiteSpace(session.Species))
                {
                    embed      = SpeciesPickerEmbed(session);
                    components = SpeciesPickerComponents(session, userId);
                }
                else
                {
                    embed      = BuilderEmbed(session);
                    components = BuilderComponents(session, userId);
                }
                break;
        }

        try
        {
            if (await Context.Channel.GetMessageAsync(session.MessageId).ConfigureAwait(false)
                is IUserMessage msg)
            {
                await msg.ModifyAsync(m =>
                {
                    m.Embed      = embed;
                    m.Components = components;
                }).ConfigureAwait(false);
            }
        }
        catch { }
    }

    private async Task DeleteBuilderAsync(PokemonBuilderState session)
    {
        if (session.MessageId == 0) return;
        try
        {
            if (await Context.Channel.GetMessageAsync(session.MessageId).ConfigureAwait(false)
                is IUserMessage msg)
                await msg.DeleteAsync().ConfigureAwait(false);
        }
        catch { }
    }

    // ─── Submit dispatch ──────────────────────────────────────────────────────

    private async Task DispatchAsync(PokemonBuilderState s)
    {
        var item   = string.IsNullOrWhiteSpace(s.Item)   ? null : s.Item;
        var ball   = string.IsNullOrWhiteSpace(s.Ball)   ? null : s.Ball;
        var nature = string.IsNullOrWhiteSpace(s.Nature) ? null : s.Nature;
        bool hasMoves = !string.IsNullOrWhiteSpace(s.Move1) || !string.IsNullOrWhiteSpace(s.Move2)
                     || !string.IsNullOrWhiteSpace(s.Move3) || !string.IsNullOrWhiteSpace(s.Move4);
        Action<T>? Moves<T>() where T : PKM, new() => hasMoves ? MovePostProcess<T>(s) : null;

        switch (s.GameType)
        {
            case "sv":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PK9>(Context, s.Species,
                    s.Shiny, item, ball, s.Level, nature, null, null,
                    string.IsNullOrWhiteSpace(s.TeraType) ? "" : $"Tera Type: {s.TeraType}",
                    Moves<PK9>()).ConfigureAwait(false);
                break;
            case "la":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PA8>(Context, s.Species,
                    s.Shiny, null, ball, s.Level, nature, null, null,
                    s.Alpha ? "Alpha: Yes" : "", Moves<PA8>()).ConfigureAwait(false);
                break;
            case "plza":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PA9>(Context, s.Species,
                    s.Shiny, item, ball, s.Level, nature, null, null,
                    s.Alpha ? "Alpha: Yes" : "", Moves<PA9>()).ConfigureAwait(false);
                break;
            case "swsh":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PK8>(Context, s.Species,
                    s.Shiny, item, ball, s.Level, nature, null, null, "", Moves<PK8>())
                    .ConfigureAwait(false);
                break;
            case "bdsp":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PB8>(Context, s.Species,
                    s.Shiny, item, ball, s.Level, nature, null, null, "", Moves<PB8>())
                    .ConfigureAwait(false);
                break;
            default:
                await FollowupAsync("❌ Unknown game.", ephemeral: true).ConfigureAwait(false);
                break;
        }
    }

    private static Action<T> MovePostProcess<T>(PokemonBuilderState s) where T : PKM, new()
    {
        return pk =>
        {
            var names = GameInfo.GetStrings("en").movelist;
            var ids   = new[] { s.Move1, s.Move2, s.Move3, s.Move4 }
                .Select(m => FindMoveId(names, m)).ToArray();
            if (ids[0] > 0) pk.Move1 = ids[0];
            if (ids[1] > 0) pk.Move2 = ids[1];
            if (ids[2] > 0) pk.Move3 = ids[2];
            if (ids[3] > 0) pk.Move4 = ids[3];
            pk.HealPP();
        };
    }

    private static ushort FindMoveId(string[] names, string moveName)
    {
        if (string.IsNullOrWhiteSpace(moveName)) return 0;
        var idx = Array.FindIndex(names, 1,
            n => n.Equals(moveName.Trim(), StringComparison.OrdinalIgnoreCase));
        return idx > 0 ? (ushort)idx : (ushort)0;
    }

    private static void SetMove(PokemonBuilderState s, int step, string value)
    {
        switch (step)
        {
            case 0: s.Move1 = value; break;
            case 1: s.Move2 = value; break;
            case 2: s.Move3 = value; break;
            case 3: s.Move4 = value; break;
        }
    }

    // Parses "EnumName|form|DisplayName" stored in session.Species
    private static (ushort Species, byte Form) ParseSpeciesForm(string value)
    {
        var parts = value.Split('|');
        if (parts.Length >= 2
            && Enum.TryParse<Species>(parts[0], true, out var sp)
            && byte.TryParse(parts[1], out var form))
            return ((ushort)sp, form);
        if (Enum.TryParse<Species>(parts[0], true, out var sp2))
            return ((ushort)sp2, 0);
        return (0, 0);
    }

    // Returns the move list for the species already chosen in the session,
    // falling back to all moves if none chosen yet.
    private static IReadOnlyList<string> GetSessionMoves(PokemonBuilderState s)
    {
        if (string.IsNullOrWhiteSpace(s.Species)) return BuilderData.GetMoves();
        var (sp, form) = ParseSpeciesForm(s.Species);
        return sp > 0
            ? BuilderData.GetMovesForSpecies(s.GameType, sp, form)
            : BuilderData.GetMoves();
    }
}
