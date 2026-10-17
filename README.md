# octoFixes

Companion fixes for modded Slay the Spire 2, tested against v0.111.0 PublicBeta.

**[Download latest](https://github.com/olegsubdiv/octoFixes/releases/latest/download/octoFixes.zip)** · [Releases](https://github.com/olegsubdiv/octoFixes/releases)

## Install

Close the game, extract the ZIP, and run `install.bat`. The installer backs up and
replaces MultiEnchantmentCoopFix and HextechFormRuneCoopFix. All co-op players need
the same octoFixes version. Original Workshop mods are installed separately.

## Fixes

- Stable multiplayer property IDs and content-pool checks prevent known enchantment/config desyncs.
- Redundant enchantment history is removed, including orphaned Spiral data after Primal Force transforms.
- Hextech Form runes auto-play consistently; cards played from hand no longer leave ghost images.
- Neutralize Upgrade replays the real card, preserving Momentum. This changes some replay-series interactions.
- Double Vision duplicates relics selected through RelicRewardChoices using Hextech's own grant/sync logic.
  Skips and repeated selection attempts do not grant extra copies. Already-claimed rewards are not retroactively duplicated.

Evolution cloning remains an unconfirmed, unresolved issue. The new reward bridge
has binding and retry tests; an in-game co-op playtest is still needed.

Settings: `mods/octoFixes/octoFixes.json`. Logs: `%APPDATA%/SlayTheSpire2/logs/godot.log`.

## Build / release

Requires .NET SDK 8+, .NET 9 runtime, the game, HextechRunes, RelicRewardChoices and BaseLib.

```powershell
.\build.ps1 -GameDir 'F:\SteamLibrary\steamapps\common\Slay the Spire 2' -Zip
```

See [release instructions](docs/RELEASING.md). Game and Workshop DLLs are local dependencies, never included in releases.
