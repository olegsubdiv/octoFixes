# Changelog

## 1.0.4 — 2026-10-22

Fix Dark Magician creating bonus orbs on a background thread. Await the original channel on the game context; failed or cancelled channels no longer create a bonus.

## 1.0.3 — 2026-10-22

Fix Smokestack desync caused by a null reference in orb focus visuals. Isolate that visual callback failure so power application and card resolution can finish.

## 1.0.2 — 2026-10-17

Fix rune auto-played Void Form ending the turn. Restore Hextech's scoped end-turn suppression; manual play stays unchanged.

## 1.0.1 — 2026-10-17

Fix Double Vision skipping RelicRewardChoices rewards. The selected relic is copied through Hextech's own grant/sync logic, with guards against skipped and repeated claims.

## 1.0.0 — 2026-10-17

Combine the enchantment co-op and Hextech companion fixes into one mod. Include Form hand-visual fixes, a migration installer and a permanent GitHub release download.
