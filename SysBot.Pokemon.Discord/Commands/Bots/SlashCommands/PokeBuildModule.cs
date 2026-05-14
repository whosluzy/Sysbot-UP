using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using PKHeX.Core;
using SysBot.Pokemon.Discord.Commands.Bots.Autocomplete;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord.Commands.Bots.SlashCommands;

// ─── Cached game data ───────────────────────────────────────────────────────────
internal static class BuilderData
{
    private const int PageSize = 24;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<(string Display, string Value)>>
        SpeciesCache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>>
        ItemCache = new();
    private static readonly ConcurrentDictionary<(string Game, ushort Species, byte Form), IReadOnlyList<int>>
        LevelCache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<object>>
        EncounterAreasCache = new();

    public static IReadOnlyList<(string Display, string Value)> GetSpecies(string gameType)
        => SpeciesCache.GetOrAdd(gameType, BuildSpecies);

    public static IReadOnlyList<string> GetItems(string gameType)
        => ItemCache.GetOrAdd(gameType, BuildItems);

    public static IReadOnlyList<int> GetLegalLevels(string gameType, ushort species, byte form)
        => LevelCache.GetOrAdd((gameType, species, form), k => BuildLegalLevels(k.Game, k.Species, k.Form));

    public static void ClearLevelCache() => LevelCache.Clear();

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
            "la"   => EntityContext.Gen8a,
            "swsh" => EntityContext.Gen8,
            "bdsp" => EntityContext.Gen8b,
            "lgpe" => EntityContext.Gen7b,
            _      => EntityContext.Gen9,   // sv, plza
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

    // ── Legal Levels (per species, from actual encounter data) ───────────────

    private static IReadOnlyList<int> BuildLegalLevels(string gameType, ushort species, byte form)
    {
        var levels = new SortedSet<int>();
        var instFlags = BindingFlags.Public | BindingFlags.Instance;

        foreach (var area in GetEncounterAreas(gameType))
        {
            if (area.GetType().GetProperty("Slots", instFlags)?.GetValue(area)
                is not IEnumerable slots) continue;

            foreach (var slot in slots)
            {
                var t = slot.GetType();
                var rawSp = t.GetProperty("Species", instFlags)?.GetValue(slot);
                var slotSp = rawSp switch { ushort u => u, int i => (ushort)i, _ => (ushort)0 };
                if (slotSp != species) continue;

                var rawForm = t.GetProperty("Form", instFlags)?.GetValue(slot);
                var slotForm = rawForm switch { byte b => b, int i => (byte)i, _ => (byte)0 };
                if (slotForm != 0 && slotForm != form) continue;

                var rawMin = t.GetProperty("LevelMin", instFlags)?.GetValue(slot);
                var rawMax = t.GetProperty("LevelMax", instFlags)?.GetValue(slot);
                int lmin = rawMin switch { byte b => b, int i => i, _ => 0 };
                int lmax = rawMax switch { byte b => b, int i => i, _ => lmin };
                for (int l = lmin; l <= lmax; l++) levels.Add(l);
            }
        }

        if (levels.Count > 0)
            return CompressLevels(levels);

        // No wild encounters — check if the species can breed (can hatch at Lv 1)
        IPersonalTable table = gameType switch
        {
            "la"   => PersonalTable.LA,
            "plza" => PersonalTable.ZA,
            "swsh" => PersonalTable.SWSH,
            "bdsp" => PersonalTable.BDSP,
            _      => PersonalTable.SV,
        };
        var info   = table[species];
        var eg1Prop = info.GetType().GetProperty("EggGroup1", BindingFlags.Public | BindingFlags.Instance);
        var eg1Val  = eg1Prop?.GetValue(info);
        int eg1     = eg1Val switch { int i => i, byte b => b, _ => 15 };
        int minFall = eg1 != 15 ? 1 : 50;   // breedable → level 1; legend/mythic → level 50
        return Enumerable.Range(minFall, 101 - minFall)
            .Where(l => l == minFall || l % 5 == 0)
            .ToList();
    }

    // Compresses a large set of encounter levels to ≤24 dropdown options.
    private static IReadOnlyList<int> CompressLevels(SortedSet<int> levels)
    {
        if (levels.Count <= 24) return [.. levels];
        var result = new SortedSet<int> { levels.Min, levels.Max };
        // Fill with multiples-of-5 that fall inside the encounter range
        for (int l = ((levels.Min / 5) + 1) * 5; l < levels.Max; l += 5)
            result.Add(l);
        // Still too many? Widen step to 10
        if (result.Count > 24)
        {
            result.Clear();
            result.Add(levels.Min);
            result.Add(levels.Max);
            for (int l = ((levels.Min / 10) + 1) * 10; l < levels.Max; l += 10)
                result.Add(l);
        }
        return [.. result];
    }

    // Directly accesses the known PKHeX encounter-area static fields by name.
    // Avoids assembly.GetTypes() which can throw ReflectionTypeLoadException.
    private static IReadOnlyList<object> GetEncounterAreas(string gameType)
        => EncounterAreasCache.GetOrAdd(gameType, LoadEncounterAreas);

    private static IReadOnlyList<object> LoadEncounterAreas(string gameType)
    {
        try
        {
            var asm  = typeof(PersonalTable).Assembly;
            var bind = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var all  = new List<object>();

            // (className, field names) — discovered via reflection probe on PKHeX 24.6.3
            (string cls, string[] fields)[] targets = gameType switch
            {
                "la"   => [("PKHeX.Core.Encounters8a", ["SlotsLA"])],
                "swsh" => [("PKHeX.Core.Encounters8",  ["SlotsSW_Symbol", "SlotsSH_Symbol",
                                                         "SlotsSW_Hidden", "SlotsSH_Hidden"])],
                "bdsp" => [("PKHeX.Core.Encounters8b",  ["SlotsBD_OW", "SlotsSP_OW",
                                                          "SlotsBD_UG", "SlotsSP_UG",
                                                          "SlotsBD", "SlotsSP"])],
                _      => [("PKHeX.Core.Encounters9",   ["Slots"])],  // sv & plza both Gen9
            };

            foreach (var (cls, fnames) in targets)
            {
                var type = asm.GetType(cls);
                if (type == null) continue;
                foreach (var fname in fnames)
                {
                    if (type.GetField(fname, bind)?.GetValue(null) is not IEnumerable areas) continue;
                    foreach (var area in areas) all.Add(area);
                }
            }
            return all;
        }
        catch { return []; }
    }

    // ── EntityContext per game ────────────────────────────────────────────────

    public static EntityContext GameContext(string gameType) => gameType switch
    {
        "la"   => EntityContext.Gen8a,
        "swsh" => EntityContext.Gen8,
        "bdsp" => EntityContext.Gen8b,
        _      => EntityContext.Gen9,
    };
}

// ─── Wizard step ───────────────────────────────────────────────────────────────
public enum BuilderStep { Species, Alpha, Shiny, Level, Item, Nature, Ball, IV, EV, Confirm }

// ─── Session state ─────────────────────────────────────────────────────────────
public class PokemonBuilderState
{
    public string       GameType       = "sv";
    public string       Species        = "";
    public string       SpeciesDisplay = "";
    public int          Level          = 50;
    public string       Nature         = "";
    public bool         Shiny          = false;
    public bool         Alpha          = false;
    public string       Item           = "";
    public string       Ball           = "";
    public string       IVs            = "31/31/31/31/31/31";
    public string       EVs            = "";
    public ulong        MessageId      = 0;
    public BuilderStep  Step           = BuilderStep.Species;
    public int          SpeciesPage    = 0;
    public int          ItemPage       = 0;
    // Stored so we can ModifyAsync / DeleteAsync the ephemeral followup
    // RestFollowupMessage overrides these to use the webhook endpoint (not channel REST)
    public IUserMessage? BuilderMessage = null;
}

// ─── Module ────────────────────────────────────────────────────────────────────
public class PokeBuildModule : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly ConcurrentDictionary<ulong, PokemonBuilderState> Sessions = new();

    // ── IV / EV presets ───────────────────────────────────────────────────────

    private static readonly (string Label, string Desc, string Value)[] IVPresets =
    [
        ("⭐ Perfect",       "HP 31 / Atk 31 / Def 31 / SpA 31 / SpD 31 / Spe 31", "31/31/31/31/31/31"),
        ("🔵 Sp. Attacker",  "No Atk IVs — ideal for special attackers",            "31/0/31/31/31/31"),
        ("🔴 Physical",      "No SpA IVs — ideal for physical attackers",            "31/31/31/0/31/31"),
        ("⏰ TR Physical",   "No SpA, no Spe — Trick Room physical",                 "31/31/31/0/31/0"),
        ("⏰ TR Special",    "No Atk, no Spe — Trick Room special",                  "31/0/31/31/31/0"),
        ("⭕ Min All",       "All 0 IVs",                                            "0/0/0/0/0/0"),
    ];

    private static readonly (string Label, string Desc, string Value)[] EVPresets =
    [
        ("⛔ None",          "No EVs",                                              ""),
        ("⚡ Atk + Spe",     "252 Atk / 252 Spe / 4 HP — physical sweeper",        "4/252/0/0/0/252"),
        ("🌀 SpA + Spe",     "252 SpA / 252 Spe / 4 HP — special sweeper",         "4/0/0/252/0/252"),
        ("🛡️ HP + Def",      "252 HP / 252 Def / 4 SpD — physical wall",            "252/0/252/0/4/0"),
        ("✨ HP + SpD",      "252 HP / 252 SpD / 4 Def — special wall",             "252/0/4/0/252/0"),
        ("💪 HP + Atk",      "252 HP / 252 Atk / 4 Def — bulky physical",           "252/252/4/0/0/0"),
        ("🔮 HP + SpA",      "252 HP / 252 SpA / 4 SpD — bulky special",            "252/0/0/252/4/0"),
        ("⏰ TR Atk + HP",   "252 HP / 252 Atk / 4 SpD — Trick Room physical",      "252/252/0/0/4/0"),
        ("⏰ TR SpA + HP",   "252 HP / 252 SpA / 4 Def — Trick Room special",       "252/0/4/252/0/0"),
    ];

    // ── Step navigation ───────────────────────────────────────────────────────

    private static BuilderStep NextStep(BuilderStep current, string gameType) => current switch
    {
        BuilderStep.Species => gameType is "la" or "plza" ? BuilderStep.Alpha : BuilderStep.Shiny,
        BuilderStep.Alpha   => BuilderStep.Shiny,
        BuilderStep.Shiny   => BuilderStep.Level,
        BuilderStep.Level   => BuilderStep.Item,
        BuilderStep.Item    => BuilderStep.Nature,
        BuilderStep.Nature  => BuilderStep.Ball,
        BuilderStep.Ball    => BuilderStep.IV,
        BuilderStep.IV      => BuilderStep.EV,
        BuilderStep.EV      => BuilderStep.Confirm,
        _                   => BuilderStep.Confirm,
    };

    private static BuilderStep PrevStep(BuilderStep current, string gameType) => current switch
    {
        BuilderStep.Alpha   => BuilderStep.Species,
        BuilderStep.Shiny   => gameType is "la" or "plza" ? BuilderStep.Alpha : BuilderStep.Species,
        BuilderStep.Level   => BuilderStep.Shiny,
        BuilderStep.Item    => BuilderStep.Level,
        BuilderStep.Nature  => BuilderStep.Item,
        BuilderStep.Ball    => BuilderStep.Nature,
        BuilderStep.IV      => BuilderStep.Ball,
        BuilderStep.EV      => BuilderStep.IV,
        BuilderStep.Confirm => BuilderStep.EV,
        _                   => BuilderStep.Species,
    };

    private static int StepNumber(BuilderStep step, string gameType)
    {
        bool a = gameType is "la" or "plza";
        return (step, a) switch
        {
            (BuilderStep.Species, _)    => 1,
            (BuilderStep.Alpha,   true) => 2,
            (BuilderStep.Shiny,   true) => 3,
            (BuilderStep.Shiny,  false) => 2,
            (BuilderStep.Level,   true) => 4,
            (BuilderStep.Level,  false) => 3,
            (BuilderStep.Item,    true) => 5,
            (BuilderStep.Item,   false) => 4,
            (BuilderStep.Nature,  true) => 6,
            (BuilderStep.Nature, false) => 5,
            (BuilderStep.Ball,    true) => 7,
            (BuilderStep.Ball,   false) => 6,
            (BuilderStep.IV,      true) => 8,
            (BuilderStep.IV,     false) => 7,
            (BuilderStep.EV,      true) => 9,
            (BuilderStep.EV,     false) => 8,
            (BuilderStep.Confirm, true) => 10,
            _                           => 9,
        };
    }

    private static int TotalSteps(string gameType) => gameType is "la" or "plza" ? 10 : 9;

    private static string Header(PokemonBuilderState s) =>
        $"{PokeBuildPanelManager.GetGameLabel()} · Step {StepNumber(s.Step, s.GameType)}/{TotalSteps(s.GameType)}";

    // ── Session check ─────────────────────────────────────────────────────────

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

    // ── Panel button ──────────────────────────────────────────────────────────

    [ComponentInteraction("pb_start")]
    public async Task OnPanelStartAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync("❌ Server only.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        // Role-based cooldown check
        if (Context.User is SocketGuildUser guildUser)
        {
            var (onCooldown, remaining) = PokeBuildPanelManager.CheckCooldown(guildUser);
            if (onCooldown)
            {
                var mins = (int)Math.Ceiling(remaining.TotalMinutes);
                await RespondAsync(
                    $"⏰ You can use the builder again in **{mins} minute{(mins == 1 ? "" : "s")}**.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }
        }

        await DeferAsync(ephemeral: true).ConfigureAwait(false);

        var session = new PokemonBuilderState
        {
            GameType = PokeBuildPanelManager.CurrentGameType,
            Step     = BuilderStep.Species,
        };
        Sessions[Context.User.Id] = session;

        var msg = await FollowupAsync(
            embed: StepEmbed(session),
            components: StepComponents(session, Context.User.Id),
            ephemeral: true
        ).ConfigureAwait(false);
        session.MessageId      = msg.Id;
        session.BuilderMessage = msg;

        PokeBuildPanelManager.MarkUsed(Context.User.Id);
    }

    // ── Species ───────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_spec_sel_*")]
    public async Task OnSpeciesSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;

        var value = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        var parts = value.Split('|');
        session.Species        = value;
        session.SpeciesDisplay = parts.ElementAtOrDefault(2) ?? parts.ElementAtOrDefault(0) ?? value;

        var (sp, form) = ParseSpeciesForm(value);
        if (sp > 0)
        {
            var legal = BuilderData.GetLegalLevels(session.GameType, sp, form);
            if (legal.Count > 0 && !legal.Contains(session.Level))
                session.Level = legal[0];
        }

        session.Step = NextStep(BuilderStep.Species, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_spec_prev_*")]
    public async Task OnSpeciesPrevAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.SpeciesPage = Math.Max(0, session.SpeciesPage - 1);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_spec_next_*")]
    public async Task OnSpeciesNextAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        var total = BuilderData.TotalPages(BuilderData.GetSpecies(session.GameType).Count);
        session.SpeciesPage = Math.Min(total - 1, session.SpeciesPage + 1);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── Alpha ─────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_alpha_yes_*")]
    public async Task OnAlphaYesAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Alpha = true;
        session.Step  = NextStep(BuilderStep.Alpha, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_alpha_no_*")]
    public async Task OnAlphaNoAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Alpha = false;
        session.Step  = NextStep(BuilderStep.Alpha, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── Shiny ─────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_shiny_yes_*")]
    public async Task OnShinyYesAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Shiny = true;
        session.Step  = NextStep(BuilderStep.Shiny, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_shiny_no_*")]
    public async Task OnShinyNoAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Shiny = false;
        session.Step  = NextStep(BuilderStep.Shiny, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── Level ─────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_level_*")]
    public async Task OnLevelSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        var value = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault();
        if (int.TryParse(value, out var lvl))
            session.Level = Math.Clamp(lvl, 1, 100);
        session.Step = NextStep(BuilderStep.Level, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── Item ──────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_isel_*")]
    public async Task OnItemSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Item = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        session.Step = NextStep(BuilderStep.Item, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_iclear_*")]
    public async Task OnItemClearAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Item = "";
        session.Step = NextStep(BuilderStep.Item, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_iprev_*")]
    public async Task OnItemPrevAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.ItemPage = Math.Max(0, session.ItemPage - 1);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    [ComponentInteraction("pb_inext_*")]
    public async Task OnItemNextAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        var total = BuilderData.TotalPages(BuilderData.GetItems(session.GameType).Count);
        session.ItemPage = Math.Min(total - 1, session.ItemPage + 1);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── Nature ────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_nature_*")]
    public async Task OnNatureSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Nature = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        session.Step   = NextStep(BuilderStep.Nature, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── Ball ──────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_ball_*")]
    public async Task OnBallSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Ball = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "";
        session.Step = NextStep(BuilderStep.Ball, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── IVs ───────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_iv_*")]
    public async Task OnIVSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.IVs  = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "31/31/31/31/31/31";
        session.Step = NextStep(BuilderStep.IV, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── EVs ───────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_ev_*")]
    public async Task OnEVSelectAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        var raw      = ((SocketMessageComponent)Context.Interaction).Data.Values.FirstOrDefault() ?? "_none_";
        session.EVs  = raw == "_none_" ? "" : raw;
        session.Step = NextStep(BuilderStep.EV, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── Back ──────────────────────────────────────────────────────────────────

    [ComponentInteraction("pb_back_*")]
    public async Task OnBackAsync(string userIdStr)
    {
        var (ok, userId, session) = await CheckAsync(userIdStr).ConfigureAwait(false);
        if (!ok) return;
        session.Step = PrevStep(session.Step, session.GameType);
        await UpdateAsync(session, userId).ConfigureAwait(false);
    }

    // ── Submit / Cancel ───────────────────────────────────────────────────────

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
            Sessions[userId] = session;
            return;
        }
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
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
        // Update the builder in-place to a "cancelled" state (type 7 — no separate message)
        if (Context.Interaction is SocketMessageComponent comp)
        {
            await comp.UpdateAsync(m =>
            {
                m.Embed      = new EmbedBuilder().WithTitle("❌ Builder Cancelled").WithColor(Color.Red)
                                   .WithDescription("Your builder has been cancelled.").Build();
                m.Components = new ComponentBuilder().Build();
            }).ConfigureAwait(false);
        }
    }

    // ── Admin command ─────────────────────────────────────────────────────────

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

    [SlashCommand("pokebuild-cooldown", "Toggle a role's 10-minute builder cooldown (Admin only)")]
    public async Task PokeBuildCooldownAsync(IRole role)
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

        var added = PokeBuildPanelManager.ToggleCooldownRole(role.Id);
        var action = added ? "✅ Added" : "🗑️ Removed";
        var prep   = added ? "to" : "from";

        var ids      = PokeBuildPanelManager.GetCooldownRoleIds();
        var roleList = ids.Count == 0 ? "*(none)*" : string.Join("\n", ids.Select(id => $"<@&{id}>"));

        await RespondAsync(
            $"{action} {role.Mention} {prep} the 10-minute cooldown list.\n\n" +
            $"**Roles with cooldown:**\n{roleList}",
            ephemeral: true).ConfigureAwait(false);
    }

    // ── Step router ───────────────────────────────────────────────────────────

    private static Embed StepEmbed(PokemonBuilderState s) => s.Step switch
    {
        BuilderStep.Alpha   => AlphaEmbed(s),
        BuilderStep.Shiny   => ShinyEmbed(s),
        BuilderStep.Level   => LevelEmbed(s),
        BuilderStep.Item    => ItemEmbed(s),
        BuilderStep.Nature  => NatureEmbed(s),
        BuilderStep.Ball    => BallEmbed(s),
        BuilderStep.IV      => IVEmbed(s),
        BuilderStep.EV      => EVEmbed(s),
        BuilderStep.Confirm => ConfirmEmbed(s),
        _                   => SpeciesEmbed(s),
    };

    private static MessageComponent StepComponents(PokemonBuilderState s, ulong userId) => s.Step switch
    {
        BuilderStep.Alpha   => AlphaComponents(s, userId),
        BuilderStep.Shiny   => ShinyComponents(s, userId),
        BuilderStep.Level   => LevelComponents(s, userId),
        BuilderStep.Item    => ItemComponents(s, userId),
        BuilderStep.Nature  => NatureComponents(s, userId),
        BuilderStep.Ball    => BallComponents(s, userId),
        BuilderStep.IV      => IVComponents(s, userId),
        BuilderStep.EV      => EVComponents(s, userId),
        BuilderStep.Confirm => ConfirmComponents(s, userId),
        _                   => SpeciesComponents(s, userId),
    };

    // ── Species step ──────────────────────────────────────────────────────────

    private static Embed SpeciesEmbed(PokemonBuilderState s)
    {
        var list  = BuilderData.GetSpecies(s.GameType);
        var total = BuilderData.TotalPages(list.Count);
        return new EmbedBuilder()
            .WithTitle("🐾 Choose a Pokémon")
            .WithColor(Color.Blue)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                $"{list.Count} Pokémon available · Page **{s.SpeciesPage + 1} / {total}**\n" +
                "Pick from the dropdown, or use ◀ ▶ to browse alphabetically.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();
    }

    private static MessageComponent SpeciesComponents(PokemonBuilderState s, ulong userId)
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

    // ── Alpha step ────────────────────────────────────────────────────────────

    private static Embed AlphaEmbed(PokemonBuilderState s)
        => new EmbedBuilder()
            .WithTitle("⭐ Alpha Pokémon?")
            .WithColor(Color.Orange)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                $"**{s.SpeciesDisplay}** — Should this be an Alpha Pokémon?\n\n" +
                "Alpha Pokémon are larger and have stronger moves.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();

    private static MessageComponent AlphaComponents(PokemonBuilderState s, ulong userId)
        => new ComponentBuilder()
            .WithButton("⭐ Yes, Alpha!",  $"pb_alpha_yes_{userId}", ButtonStyle.Success,   row: 0)
            .WithButton("✖️ No",           $"pb_alpha_no_{userId}",  ButtonStyle.Secondary, row: 0)
            .WithButton("◀ Back",          $"pb_back_{userId}",       ButtonStyle.Secondary, row: 1)
            .Build();

    // ── Shiny step ────────────────────────────────────────────────────────────

    private static Embed ShinyEmbed(PokemonBuilderState s)
    {
        var extra = s.GameType is "la" or "plza"
            ? $"Alpha: **{(s.Alpha ? "Yes ⭐" : "No")}**\n"
            : "";
        return new EmbedBuilder()
            .WithTitle("✨ Shiny Pokémon?")
            .WithColor(Color.Gold)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                $"**{s.SpeciesDisplay}**\n{extra}\n" +
                "Should this Pokémon be Shiny?")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();
    }

    private static MessageComponent ShinyComponents(PokemonBuilderState s, ulong userId)
        => new ComponentBuilder()
            .WithButton("✨ Yes, Shiny!",  $"pb_shiny_yes_{userId}", ButtonStyle.Success,   row: 0)
            .WithButton("✖️ No",           $"pb_shiny_no_{userId}",  ButtonStyle.Secondary, row: 0)
            .WithButton("◀ Back",          $"pb_back_{userId}",       ButtonStyle.Secondary, row: 1)
            .Build();

    // ── Level step ────────────────────────────────────────────────────────────

    private static Embed LevelEmbed(PokemonBuilderState s)
    {
        var (sp, form) = ParseSpeciesForm(s.Species);
        var legal = sp > 0 ? BuilderData.GetLegalLevels(s.GameType, sp, form) : null;
        var rangeText = legal is { Count: > 0 }
            ? $"Legal levels for **{s.SpeciesDisplay}**: {legal[0]}–100"
            : "Choose any level";
        return new EmbedBuilder()
            .WithTitle("📊 Choose a Level")
            .WithColor(Color.Green)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                $"{rangeText}\n\n" +
                "Pick from the dropdown below.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();
    }

    private static MessageComponent LevelComponents(PokemonBuilderState s, ulong userId)
    {
        var (sp, form) = ParseSpeciesForm(s.Species);
        var legalLevels = sp > 0
            ? BuilderData.GetLegalLevels(s.GameType, sp, form)
            : (IReadOnlyList<int>)Enumerable.Range(1, 100).Where(l => l == 1 || l % 5 == 0).ToList();

        var levelMenu = new SelectMenuBuilder()
            .WithCustomId($"pb_level_{userId}")
            .WithPlaceholder($"📊 Level — currently {s.Level}");
        foreach (var lvl in legalLevels)
            levelMenu.AddOption($"Level {lvl}", lvl.ToString());

        return new ComponentBuilder()
            .WithSelectMenu(levelMenu, row: 0)
            .WithButton("◀ Back", $"pb_back_{userId}", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    // ── Item step ─────────────────────────────────────────────────────────────

    private static Embed ItemEmbed(PokemonBuilderState s)
    {
        var items = BuilderData.GetItems(s.GameType);
        var total = BuilderData.TotalPages(items.Count);
        return new EmbedBuilder()
            .WithTitle("🎒 Choose a Held Item")
            .WithColor(Color.DarkGreen)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                $"Page **{s.ItemPage + 1} / {total}** · {items.Count} items available\n" +
                "Pick from the list, or press **🚫 No Item** to skip.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();
    }

    private static MessageComponent ItemComponents(PokemonBuilderState s, ulong userId)
    {
        var items = BuilderData.GetItems(s.GameType);
        var total = BuilderData.TotalPages(items.Count);
        var page  = s.ItemPage;
        var slice = BuilderData.GetPage(items, page).ToList();

        var menu = new SelectMenuBuilder()
            .WithCustomId($"pb_isel_{userId}")
            .WithPlaceholder("🎒 Select held item...");
        foreach (var item in slice)
            menu.AddOption(item, item);

        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("◀", $"pb_iprev_{userId}", ButtonStyle.Secondary, disabled: page == 0, row: 1)
            .WithButton($"{page + 1} / {total}", "pb_item_label", ButtonStyle.Secondary, disabled: true, row: 1)
            .WithButton("▶", $"pb_inext_{userId}", ButtonStyle.Secondary, disabled: page >= total - 1, row: 1)
            .WithButton("🚫 No Item", $"pb_iclear_{userId}", ButtonStyle.Danger,     row: 2)
            .WithButton("◀ Back",     $"pb_back_{userId}",   ButtonStyle.Secondary,  row: 2)
            .Build();
    }

    // ── Nature step ───────────────────────────────────────────────────────────

    private static Embed NatureEmbed(PokemonBuilderState s)
        => new EmbedBuilder()
            .WithTitle("🌿 Choose a Nature")
            .WithColor(Color.Teal)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                "Nature affects which two stats grow faster or slower.\n" +
                "Pick from the dropdown below.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();

    private static MessageComponent NatureComponents(PokemonBuilderState s, ulong userId)
    {
        var natureMenu = new SelectMenuBuilder()
            .WithCustomId($"pb_nature_{userId}")
            .WithPlaceholder("🌿 Nature — pick one...");
        foreach (var (name, effect) in PokeBuildPanelManager.Natures)
            natureMenu.AddOption(name, name, effect);

        return new ComponentBuilder()
            .WithSelectMenu(natureMenu, row: 0)
            .WithButton("◀ Back", $"pb_back_{userId}", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    // ── Ball step ─────────────────────────────────────────────────────────────

    private static Embed BallEmbed(PokemonBuilderState s)
        => new EmbedBuilder()
            .WithTitle("⚾ Choose a Poké Ball")
            .WithColor(Color.Red)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                "Pick the ball this Pokémon was caught in.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();

    private static MessageComponent BallComponents(PokemonBuilderState s, ulong userId)
    {
        var ballMenu = new SelectMenuBuilder()
            .WithCustomId($"pb_ball_{userId}")
            .WithPlaceholder("⚾ Poké Ball — pick one...");
        foreach (var ball in PokeBuildPanelManager.GetBalls())
            ballMenu.AddOption(ball, ball);

        return new ComponentBuilder()
            .WithSelectMenu(ballMenu, row: 0)
            .WithButton("◀ Back", $"pb_back_{userId}", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    // ── IV step ───────────────────────────────────────────────────────────────

    private static Embed IVEmbed(PokemonBuilderState s)
        => new EmbedBuilder()
            .WithTitle("🏆 Choose an IV Spread")
            .WithColor(Color.Purple)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                "IVs determine how high each stat can go.\n" +
                "Pick a preset below.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();

    private static MessageComponent IVComponents(PokemonBuilderState s, ulong userId)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"pb_iv_{userId}")
            .WithPlaceholder("🏆 IV Spread...");
        foreach (var (label, desc, value) in IVPresets)
            menu.AddOption(label, value, desc);

        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("◀ Back", $"pb_back_{userId}", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    // ── EV step ───────────────────────────────────────────────────────────────

    private static Embed EVEmbed(PokemonBuilderState s)
        => new EmbedBuilder()
            .WithTitle("💪 Choose an EV Spread")
            .WithColor(Color.DarkPurple)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                "EVs give bonus stats. Pick a common competitive spread.")
            .WithFooter($"PokedexMasterBot {TradeBot.Version}")
            .Build();

    private static MessageComponent EVComponents(PokemonBuilderState s, ulong userId)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"pb_ev_{userId}")
            .WithPlaceholder("💪 EV Spread...");
        foreach (var (label, desc, value) in EVPresets)
        {
            var optValue = value.Length == 0 ? "_none_" : value;
            menu.AddOption(label, optValue, desc);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("◀ Back", $"pb_back_{userId}", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    // ── Confirm step ──────────────────────────────────────────────────────────

    private static Embed ConfirmEmbed(PokemonBuilderState s)
    {
        var eb = new EmbedBuilder()
            .WithTitle("✅ Confirm Your Pokémon")
            .WithColor(Color.Green)
            .WithDescription(
                $"**{Header(s)}**\n\n" +
                "Review your choices, then hit **✅ Request Trade**!")
            .AddField("🐾 Pokémon",  s.SpeciesDisplay,                             inline: true)
            .AddField("📊 Level",    $"Lv. {s.Level}",                             inline: true)
            .AddField("✨ Shiny",    s.Shiny ? "Yes ⭐" : "No",                    inline: true)
            .AddField("🌿 Nature",   string.IsNullOrWhiteSpace(s.Nature) ? "*Random*"  : s.Nature, inline: true)
            .AddField("⚾ Ball",     string.IsNullOrWhiteSpace(s.Ball)   ? "*Default*" : s.Ball,   inline: true)
            .AddField("💎 Item",     string.IsNullOrWhiteSpace(s.Item)   ? "*None*"    : s.Item,   inline: true)
            .AddField("🏆 IVs",     FormatIVs(s.IVs),                             inline: true)
            .AddField("💪 EVs",     FormatEVs(s.EVs),                             inline: true);

        if (s.GameType is "la" or "plza")
            eb.AddField("⭐ Alpha", s.Alpha ? "Yes" : "No", inline: true);

        eb.WithFooter($"PokedexMasterBot {TradeBot.Version}");
        return eb.Build();
    }

    private static MessageComponent ConfirmComponents(PokemonBuilderState s, ulong userId)
        => new ComponentBuilder()
            .WithButton("✅ Request Trade", $"pb_submit_{userId}", ButtonStyle.Success,   row: 0)
            .WithButton("◀ Back",           $"pb_back_{userId}",   ButtonStyle.Secondary, row: 0)
            .WithButton("❌ Cancel",         $"pb_cancel_{userId}", ButtonStyle.Danger,    row: 0)
            .Build();

    // ── Update / Delete ───────────────────────────────────────────────────────

    // Uses the CURRENT component interaction to update the source message in-place (type 7).
    // This correctly handles ephemeral messages without needing the stored old-token reference.
    private async Task UpdateAsync(PokemonBuilderState session, ulong userId)
    {
        if (Context.Interaction is SocketMessageComponent comp)
        {
            await comp.UpdateAsync(m =>
            {
                m.Embed      = StepEmbed(session);
                m.Components = StepComponents(session, userId);
            }).ConfigureAwait(false);
        }
    }

    private async Task DeleteBuilderAsync(PokemonBuilderState session)
    {
        if (session.BuilderMessage is not null)
        {
            try { await session.BuilderMessage.DeleteAsync().ConfigureAwait(false); return; }
            catch { }
        }
        if (session.MessageId == 0) return;
        try
        {
            if (await Context.Channel.GetMessageAsync(session.MessageId).ConfigureAwait(false)
                is IUserMessage msg)
                await msg.DeleteAsync().ConfigureAwait(false);
        }
        catch { }
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    private async Task DispatchAsync(PokemonBuilderState s)
    {
        var item   = string.IsNullOrWhiteSpace(s.Item)   ? null : s.Item;
        var ball   = string.IsNullOrWhiteSpace(s.Ball)   ? null : s.Ball;
        var nature = string.IsNullOrWhiteSpace(s.Nature) ? null : s.Nature;
        var evs    = string.IsNullOrWhiteSpace(s.EVs)    ? null : s.EVs;

        switch (s.GameType)
        {
            case "sv":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PK9>(Context, s.Species,
                    s.Shiny, item, ball, s.Level, nature, s.IVs, evs, "", null)
                    .ConfigureAwait(false);
                break;
            case "la":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PA8>(Context, s.Species,
                    s.Shiny, null, ball, s.Level, nature, s.IVs, evs,
                    s.Alpha ? "Alpha: Yes" : "", null)
                    .ConfigureAwait(false);
                break;
            case "plza":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PA9>(Context, s.Species,
                    s.Shiny, item, ball, s.Level, nature, s.IVs, evs,
                    s.Alpha ? "Alpha: Yes" : "", null)
                    .ConfigureAwait(false);
                break;
            case "swsh":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PK8>(Context, s.Species,
                    s.Shiny, item, ball, s.Level, nature, s.IVs, evs, "", null)
                    .ConfigureAwait(false);
                break;
            case "bdsp":
                await CreatePokemonHelper.ExecuteCreatePokemonAsync<PB8>(Context, s.Species,
                    s.Shiny, item, ball, s.Level, nature, s.IVs, evs, "", null)
                    .ConfigureAwait(false);
                break;
            default:
                await FollowupAsync("❌ Unknown game.", ephemeral: true).ConfigureAwait(false);
                break;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static string FormatIVs(string ivs)
    {
        if (string.IsNullOrWhiteSpace(ivs)) return "31 all (Perfect)";
        var parts = ivs.Split('/');
        if (parts.Length != 6) return ivs;
        string[] stats = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
        return string.Join(" / ", parts.Zip(stats, (v, s) => $"{v.Trim()} {s}"));
    }

    private static string FormatEVs(string evs)
    {
        if (string.IsNullOrWhiteSpace(evs) || evs == "_none_") return "None";
        var parts = evs.Split('/');
        if (parts.Length != 6) return evs;
        string[] stats = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
        var nonZero = parts.Zip(stats, (v, s) => (v.Trim(), s))
            .Where(x => x.Item1 is not "0" and not "")
            .Select(x => $"{x.Item1} {x.s}");
        var joined = string.Join(" / ", nonZero);
        return string.IsNullOrEmpty(joined) ? "None" : joined;
    }
}
