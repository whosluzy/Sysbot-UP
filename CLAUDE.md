# PokedexMasterBot — Project Guide

Fork of `kwsch/SysBot.NET` (via `Secludedly/FusionBot` and `hexbyt3/PokeBot`), branded as **PokedexMasterBot**. Lives at `whosluzy/Sysbot-UP`. Discord trade bot for Switch Pokémon games.

## Critical rules — do not violate

1. **Never modify Pokémon Showdown parsing, legality validation, or Pokémon request/generation logic.** Anything touching `AutoLegalityWrapper`, `ShowdownSet`, `ShowdownUtil`, `PKHeX`, legality, species lookup, move validation, or trade processing logic is off-limits unless the user explicitly asks.
2. **Never rename or alter CDN URLs** containing `ZE-FusionBot-Sprite-Images` or `ZE-FusionBot-HOME-Images`. Those repos belong to Secludedly and are linked from the embeds — they are not ours to rename even when rebranding "FusionBot" → "PokedexMasterBot".
3. **Never skip git hooks** (`--no-verify`, `--no-gpg-sign`, etc.) unless explicitly told to. If a hook fails, fix the real issue.
4. **After any upstream sync, always verify and restore these fork-specific values:**
   - `UpdateChecker.cs`: `RepositoryOwner = "whosluzy"`, `RepositoryName = "Sysbot-UP"`, User-Agent = `"PokedexMasterBot"` — must point to this fork, not upstream.
   - `UpdateForm.cs`: User-Agent = `"PokedexMasterBot"`.
   - `SysBot.Pokemon.WinForms.csproj`: `AssemblyName` must be `PokedexMasterBot`; the rename target must produce `PokedexMasterBot_x.x.x.exe` (not `FusionBot_x.x.x.exe`). Without this the CI release has no exe.
   - `DiscordSettings.cs`: `FreeTradeLimitMinutes` and `RolesExemptFromCooldown` properties must exist (see Free-user trade limit section).
   - `TradeCooldownTracker.cs` must exist in `SysBot.Pokemon.Discord/Helpers/`, all limit checks must be wired up, and `DiscordTradeNotifier.TradeFinished` must record the completed trade (see Free-user trade limit section).

## Workflow

- **Local build before commit**: run `dotnet build SysBot.Pokemon.WinForms/SysBot.Pokemon.WinForms.csproj -c Release --no-restore` and confirm no errors. The CI build is slow; compile errors there waste a release cycle.
- **Ask before pushing**: after the code changes are done and the local build is clean, ask the user "Do you want to make any other changes before I push?" Do not push without explicit confirmation.
- **Rebase on push reject**: CI auto-bumps the patch version in `SysBot.Pokemon/Helpers/TradeBot.cs` and commits `[skip ci]`. If `git push` is rejected, run `git pull origin main --rebase` then push again.
- **Commit messages**: short, present-tense summary; use PowerShell here-string syntax (`@'...'@`) — do NOT use Bash heredoc `$(cat <<EOF...)`. Do NOT add a `Co-Authored-By: Claude` trailer — commits go out under the owner's own name (whosluzy).

## CI / release

- Workflow: `.github/workflows/release.yml` — runs on push to `main`.
- Steps: bump patch version in `SysBot.Pokemon/Helpers/TradeBot.cs` → tag → publish via `dotnet publish` → upload exe to GitHub Release using `softprops/action-gh-release@v2` with `make_latest: true`.
- Output exe is renamed by the .csproj target to `PokedexMasterBot_<version>.exe`.
- `AssemblyName` is `PokedexMasterBot` (so the process name matches port-file lookups in `BotServer.cs`).

## Free-user trade limit

Free users (no role in `RolesExemptFromCooldown`, e.g. no Premium) get **1 completed trade per hour**. Lives in `SysBot.Pokemon.Discord/Helpers/TradeCooldownTracker.cs`. Settings in `DiscordSettings.cs`:

- **Free User Trade Limit (Minutes)** — `FreeTradeLimitMinutes` (Operation category, default 60, 0 disables). Replaced the old `TradeCooldownMinutes` (10 min, counted at queue time).
- **Roles With Unlimited Trades** — `RolesExemptFromCooldown` (Roles category, role IDs or names). Property name kept so existing configs keep their Premium role.

**A trade only counts once it concludes with the user.** Recording happens in exactly one place: `DiscordTradeNotifier.TradeFinished` → `TradeCooldownTracker.RecordCompletedTrade(Trader.Id)`. Every game's trade bot calls `TradeFinished` only after the box checksum changed (trade really happened). Dump and Seed types are skipped (nothing handed over). Never record at queue time — sent codes, cancels, timeouts and no-shows must not count.

Checks (block BEFORE sending the trade-code DM):

- `QueueHelper.AddToQueueAsync` (public, main path) — `EnforceCooldown(...)` at top.
- `QueueHelper.AddBatchContainerToQueueAsync` × 3 batch variants — same.
- `MysteryEggModule.ProcessBatchMysteryEggs` — calls `QueueHelper<T>.EnforceCooldown` directly.
- `CreatePokemonHelper` (slash command) — inline `IsExempt` + `IsOnCooldown`, ephemeral followup with `BuildCooldownEmbed`.

The limit is tracked under BOTH the Discord user ID and the trainer's in-game TID+SID (read from `tradecodes.json`, which the trade bots already fill in during each trade — no extra Switch reads). Blocked if either traded within the window, so alt Discord accounts on the same game save share one limit. A brand-new alt with no saved TID/SID can get one trade through before it is linked. Needs `StoreTradeCodes` on (default).

A batch (up to 6 Pokémon) counts as one trade. Tracking is in-memory per bot process (a restart clears it).

Limit message: friendly orange embed with the Patreon upsell + 3-step guide. Patreon URL: `https://www.patreon.com/c/pokedexmasters/membership`. Does NOT auto-delete (user dismisses).

## Key file locations

| Concern | File |
|---|---|
| Trade command modules (prefix) | `SysBot.Pokemon.Discord/Commands/Bots/TradeModule.cs` |
| Slash trade commands | `SysBot.Pokemon.Discord/Commands/Bots/SlashCommands/CreatePokemonHelper.cs` |
| Trade processing pipeline | `SysBot.Pokemon.Discord/Helpers/TradeModule/Helpers.cs` |
| Queue enqueue + embeds | `SysBot.Pokemon.Discord/Helpers/QueueHelper.cs` |
| Trade-code / searching / canceled DM embeds | `SysBot.Pokemon.Discord/Helpers/EmbedHelper.cs` |
| Discord settings (UI tab) | `SysBot.Pokemon/Settings/Integrations/DiscordSettings.cs` |
| WinForms entry / window | `SysBot.Pokemon.WinForms/Main.cs` + `Main.Designer.cs` |
| Update checker (points at this fork) | `SysBot.Pokemon.WinForms/UpdateChecker.cs` |
| Version constant | `SysBot.Pokemon/Helpers/TradeBot.cs` |
| Bot version label on embeds | `PokedexMasterBot {TradeBot.Version}` |

## Conventions

- Stick to existing patterns when adding embed messages — DM via `EmbedHelper.*` helpers, channel via `context.Channel.SendMessageAsync`.
- Slash commands can use `ephemeral: true` for true "only-you" messages; prefix commands cannot — fall back to DM.
- WinForms has no fancy UI animations (no glitter, no hover fades, no slide transitions). Don't reintroduce them.
- No emojis in code or commit messages unless the user asks. (Embeds shown to end users are fine.)
