# PokedexMasterBot — Claude Code Context

## Project Overview
This is a Discord Pokémon trading bot built on the SysBot.NET framework. Originally "FusionBot" by Secludedly, renamed to **PokedexMasterBot** by the owner. The bot runs as a WinForms exe (.NET 10, `net10.0-windows`, `win-x64`) and lets Discord users trade Pokémon via automated Nintendo Switch control.

## Repository
- **Owner's fork**: `https://github.com/whosluzy/Sysbot-UP`
- **Upstream (do not break)**: Secludedly's original ZE-FusionBot
- **Image CDN** (Secludedly's — do NOT rename): `ZE-FusionBot-Sprite-Images` and `ZE-FusionBot-HOME-Images`

## Git Workflow
- `origin` = owner's fork (`whosluzy/Sysbot-UP`) — always push here
- Always `git pull origin main --rebase` before pushing (GitHub Actions auto-commits version bumps)
- If pull fails due to unstaged changes: `git stash && git pull origin main --rebase && git stash pop && git push origin main`

## GitHub Actions (`.github/workflows/release.yml`)
- Triggers on every push to `main`
- Auto-increments patch version in `SysBot.Pokemon/Helpers/TradeBot.cs` (e.g. v1.0.1 → v1.0.2)
- Commits the version bump with `[skip ci]` to avoid infinite loops
- Builds exe with `dotnet publish` and creates a GitHub Release
- Every push = new uniquely versioned release kept forever

## Key Files & What They Do

### Branding / Version
- `SysBot.Pokemon/Helpers/TradeBot.cs` — contains `Version = "v1.0.x"` (auto-bumped by CI)

### Discord DM Embeds
- `SysBot.Pokemon.Discord/Helpers/EmbedHelper.cs` — all DM embed messages (trade code, searching, initializing, completed, canceled, notice). Has friendly emoji tone.
- `SysBot.Pokemon.Discord/Helpers/DiscordTradeNotifier.cs` — queue update DMs, "You're Up Next" embed, trade cancel reason mapping

### Trade Cancel Messages (in DiscordTradeNotifier.cs `TradeCanceled`)
- `NoTrainerFound` → "Make sure you are using online mode in game on link trade. Try Again!"
- `ExceptionInternal` → "Please contact an ADMIN in the server. This bot could be offline."

### Channel Trade Embeds
- `SysBot.Pokemon.Discord/Helpers/QueueHelper.cs` — embeds posted in the Discord channel when trade is queued. Footer shows `PokedexMasterBot vX.X.X`.
- `SysBot.Pokemon.Discord/Helpers/DetailsExtractor.cs` — extracts Pokémon data for embed fields

### Non-Native Pokémon Notice (QueueHelper.cs)
When a Pokémon is non-native, shows: "Please use a file with home tracker instead of word format if you wish to transfer it to HOME."

### Showdown Parsing / Error Messages
- `SysBot.Pokemon.Discord/Helpers/TradeModule/Helpers.cs` — processes Showdown sets
  - Has **species name autocorrect** (Levenshtein distance fuzzy matching against full PKHeX species database). If species not found, tries closest match automatically.
  - Parse error message: "Please try again, something about your format is wrong. Check spelling or if level/customizations are legally possible to exist in game also."

### Sprite Images
- `SysBot.Pokemon/Helpers/TradeExtensions.cs` — generates Pokémon sprite URLs using `ZE-FusionBot-HOME-Images` repo. **Do NOT rename this to PokedexMasterBot** — it's Secludedly's CDN.

### Update Checker
- `SysBot.Pokemon.WinForms/UpdateChecker.cs` — checks `whosluzy/Sysbot-UP` GitHub releases for updates. Compares `TradeBot.Version` string to latest release tag. Shows "Up to date" dialog when already on latest.

### UI / Theme
- `SysBot.Pokemon.WinForms/ThemeManager.cs` — Classic theme only (stripped from 30 themes). No animations, no sparkles.
- `SysBot.Pokemon.WinForms/Main.cs` — WinForms main window. All hover/pulse/sparkle animation code removed.
- `Sparkle.cs` — deleted entirely

## What Was Removed / Simplified
- All themes except Classic
- Sparkle particle system
- Hover fade animations (60fps timers)
- Pulse/glow outline effects
- Slide/fade form transitions
- CB_Themes ComboBox from UI
- `zepkm.com/pokecreator` link from all embeds
- `build.bat`

## Permissions
- `.claude/settings.local.json` is set to `"allow": ["*"]` — all tools auto-approve, no prompts

## Owner Preferences
- Push every change to GitHub immediately after making it
- Keep every GitHub release (never delete old ones)
- Version format: v1.0.1, v1.0.2, etc. (auto-incremented by CI on each push)
- Friendly, emoji-filled tone for all Discord DM messages
- Do not rename any URLs pointing to Secludedly's image CDN repos
