# PokedexMasterBot — Project Guide

Fork of `kwsch/SysBot.NET` (via `Secludedly/FusionBot` and `hexbyt3/PokeBot`), branded as **PokedexMasterBot**. Lives at `whosluzy/Sysbot-UP`. Discord trade bot for Switch Pokémon games.

## Critical rules — do not violate

1. **Never modify Pokémon Showdown parsing, legality validation, or Pokémon request/generation logic.** Anything touching `AutoLegalityWrapper`, `ShowdownSet`, `ShowdownUtil`, `PKHeX`, legality, species lookup, move validation, or trade processing logic is off-limits unless the user explicitly asks.
2. **Never rename or alter CDN URLs** containing `ZE-FusionBot-Sprite-Images` or `ZE-FusionBot-HOME-Images`. Those repos belong to Secludedly and are linked from the embeds — they are not ours to rename even when rebranding "FusionBot" → "PokedexMasterBot".
3. **Never skip git hooks** (`--no-verify`, `--no-gpg-sign`, etc.) unless explicitly told to. If a hook fails, fix the real issue.

## Workflow

- **Local build before commit**: run `dotnet build SysBot.Pokemon.WinForms/SysBot.Pokemon.WinForms.csproj -c Release --no-restore` and confirm no errors. The CI build is slow; compile errors there waste a release cycle.
- **Ask before pushing**: after the code changes are done and the local build is clean, ask the user "Do you want to make any other changes before I push?" Do not push without explicit confirmation.
- **Rebase on push reject**: CI auto-bumps the patch version in `SysBot.Pokemon/Helpers/TradeBot.cs` and commits `[skip ci]`. If `git push` is rejected, run `git pull origin main --rebase` then push again.
- **Commit messages**: short, present-tense summary; use PowerShell here-string syntax (`@'...'@`) — do NOT use Bash heredoc `$(cat <<EOF...)`. Include `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>` on the last line.

## CI / release

- Workflow: `.github/workflows/release.yml` — runs on push to `main`.
- Steps: bump patch version in `SysBot.Pokemon/Helpers/TradeBot.cs` → tag → publish via `dotnet publish` → upload exe to GitHub Release using `softprops/action-gh-release@v2` with `make_latest: true`.
- Output exe is renamed by the .csproj target to `PokedexMasterBot_<version>.exe`.
- `AssemblyName` is `PokedexMasterBot` (so the process name matches port-file lookups in `BotServer.cs`).

## Trade cooldown system

Centralized 10-minute cooldown (configurable). Lives in `SysBot.Pokemon.Discord/Helpers/TradeCooldownTracker.cs`. Settings exposed in `DiscordSettings.cs`:

- **Trade Cooldown (Minutes)** (Operation category, default 10, 0 disables)
- **Roles Exempt From Cooldown** (Roles category, list of role IDs or names)

Enforcement points (must check BEFORE sending trade-code DM, and record only on `QueueResultAdd.Added`):

- `QueueHelper.AddToQueueAsync` (public, main path) — check at top via `EnforceCooldown(...)`, record inside private `AddToTradeQueue` via `RecordCooldownIfApplicable(...)`.
- `QueueHelper.AddBatchContainerToQueueAsync` × 3 batch variants — same pattern.
- `MysteryEggModule.ProcessBatchMysteryEggs` — calls `QueueHelper<T>.EnforceCooldown` directly.
- `CreatePokemonHelper` (slash command) — inline check using `TradeCooldownTracker.IsExempt` + `IsOnCooldown`, sends ephemeral followup with `BuildCooldownEmbed`.

Cooldown message: friendly orange embed with the Patreon upsell + 3-step guide. Patreon URL: `https://www.patreon.com/c/pokedexmasters/membership`. Does NOT auto-delete (user dismisses).

Cleanup: `IsOnCooldown` removes expired entries lazily; a 30-minute background `Timer` sweeps anything older than 1 hour.

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
