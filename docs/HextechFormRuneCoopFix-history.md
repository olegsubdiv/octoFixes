# Hextech Form Rune Co-op Fix (Slay the Spire 2)

Мод-компаньон для **HextechRunes** (Natsuki, Workshop 3747501308). Убирает вылет из кооп-забега с ошибкой
`State divergence detected!` на чексумме `After player turn start` в первом ходу боя, который вызывают руны
**Reaper / Demon / Echo / Serpent / Void Form Upgrade** (в начале боя сами разыгрывают карты «… Form»).
С v1.1.0 также меняет руну **Neutralize Upgrade**: при сбросе Neutralize/Suppress разыгрывается **настоящая**
карта серией повторов, а не одноразовые клоны (см. раздел «Руна Neutralize Upgrade»).

**Ставить обоим игрокам, одинаковую версию.** Мод помечен `affects_gameplay: true`, поэтому игра сама откажет
в подключении, если у одного из игроков его нет или версия другая. Без HextechRunes мод ничего не делает
(одна строка в логе), зависимости в манифесте нет.

## Установка

**Просто:** распаковать zip куда угодно и запустить `install.bat` двойным кликом. Он сам найдёт игру через Steam
(реестр → `libraryfolders.vdf` → `appmanifest_2868840.acf`), создаст `<игра>\mods\HextechFormRuneCoopFix\` и
скопирует туда файлы. Если игру найти не удалось — попросит вставить путь к папке с `SlayTheSpire2.exe`.

**Вручную:** положить `HextechFormRuneCoopFix.dll` + `HextechFormRuneCoopFix.json` в
`<Steam>\steamapps\common\Slay the Spire 2\mods\HextechFormRuneCoopFix\` (папку `mods` создать, если её нет —
игра читает её рядом с exe).

Проверка: после запуска игры в `%APPDATA%\SlayTheSpire2\logs\godot.log` должны быть строки
`[HextechFormRuneCoopFix] [FormRuneFix] Initialised v1.1.0 (...)`,
`[FormRuneFix] Hextech auto-play hook disabled on 5 rune type(s): ...` и
`[NeutralizeRuneFix] Hextech clone hook replaced on NeutralizeUpgradeRune`, а в списке модов — «Hextech Form Rune Co-op Fix».

Сборка из исходников: `.\build.ps1 -Install -Zip` (нужен .NET SDK 8+, .NET 9 runtime ставится вместе с игрой).
`.\build.ps1` без ключей — сборка + самопроверка: грузит настоящий `HextechRunes.dll` из Workshop, проверяет, что
находятся ровно 5 рун с правильными картами, что хук Hextech пропатчен у всех пяти (и реально подавляется при вызове),
и что постфикс на `CombatManager.SetupPlayerTurn` садится на метод игры. Для руны Neutralize — что префикс
на `NeutralizeUpgradeRune.AfterCardDiscarded` проверен, патчи на `Hook.ModifyCardPlayCount` / `Hook.BeforeCardPlayed`
садятся, `CardPlay.Target` меняется через рефлексию, плюс табличные тесты выбора цели и подсчёта розыгрышей.

## Что именно было сломано

Полный разбор — в разделе ниже (English, можно отправить автору мода). Кратко:

- Руна реализована через хук `AfterPlayerTurnStartLate` и срабатывает «один раз за бой» по полям **экземпляра
  реликвии** (`_startedThisCombat`, `_autoPlaying`). Хост перед каждым боем пересоздаёт реликвии соседа
  (`Player.SyncWithSerializedPlayer`), а хук выполняется внутри `SetupPlayerTurn`, где пауза/ожидание выбора
  может увести последующие хуки за точку чексуммы.
- Итог в логах: три боя подряд руна не сработала ни у кого, в четвёртом — сработала **только у хоста**
  (у хоста `REAPER_FORM_POWER` и карта ушла из колоды, у клиента `CARD.REAPER_FORM` всё ещё в доборе) →
  чексуммы расходятся → клиента выкидывает.

## Что делает этот мод

| Патч | Что делает | Флаг в манифесте |
|---|---|---|
| `AutoPlayFormsAtCombatStartRuneBase<TCard>.AfterPlayerTurnStartLate` (prefix, для всех 5 рун) | **Отключает** родной автоплей руны: сразу возвращает завершённую задачу. Привязка ленивая: при старте, при загрузке сборки `HextechRunes` или при первом ходе в бою — что наступит раньше. После патча проверяется `Harmony.GetPatchInfo` у каждой из 5 рун. | `disableAutoPlayFix` (false) |
| `CombatManager.SetupPlayerTurn` (postfix) | **Детерминированная замена**: после того как игра полностью подготовила 1-й ход игрока (энергия, добор, все хуки `AfterPlayerTurnStart`), один раз на (бой, игрок) для каждой руны игрока (не расплавленной, для своего персонажа) берёт все его карты нужной формы из Добора/Руки/Сброса, по одной переносит в Play и разыгрывает (`CardCmd.AutoPlay`). `SetupPlayerTurn` вызывается на каждом пире для каждого игрока, до чексуммы `After player turn start`, поэтому результат одинаковый у всех. | `disableAutoPlayFix` (false) |
| — | `disableAutoPlayFix: true` — руны форм не трогаются, HextechRunes работает как есть (для сравнения). | |
| `NeutralizeUpgradeRune.AfterCardDiscarded` (prefix) + `Hook.ModifyCardPlayCount` (postfix) + `Hook.BeforeCardPlayed` (prefix) | Руна Neutralize Upgrade разыгрывает **настоящую** сброшенную карту одной серией повторов вместо клонов (подробно ниже). `false` — логика клонов Hextech без изменений. | `neutralizeRuneRealCard` (true) |
| — | Подробное логирование (каждая карта, каждое подавление хука, пропуски на ходах > 1). | `verboseLog` (false) |

Отличия от оригинала: карты разыгрываются последовательно (без «объединённого» эффекта Hextech для нескольких
копий одной формы) и без групповой анимации полёта карт. На итоговое состояние это не влияет: каждая копия
применяет свою силу.

### Как сравнить логи хоста и клиента

В `%APPDATA%\SlayTheSpire2\logs\godot.log` у обоих игроков в начале каждого боя с руной должны быть **одинаковые**
строки (кроме времени):

```
[FormRuneFix] player <NetId> turn 1: rune RELIC.<RUNE_ID> found <n> CARD.<FORM> card(s) in Draw/Hand/Discard -> auto-playing
```

или `... found 0 ... -> nothing to play` / `... skipped: <причина>`. Строка пишется для **каждого** игрока с руной
(и для себя, и для соседа) — у хоста и клиента их набор и числа должны совпадать. Если у кого-то строки нет,
а у другого есть, или числа `<n>` разные — прислать оба лога. Строка
`suppressed HextechRunes <Rune>.AfterPlayerTurnStartLate` подтверждает, что родной хук руны реально перехвачен.

## Руна Neutralize Upgrade (v1.1.0)

**Как было в HextechRunes.** Когда владелец руны сбрасывает Neutralize или Suppress, руна `Repeats` раз (2) по
каждому врагу, которого можно ударить, создаёт **клон** карты (бесплатный, с Exhaust), кладёт его в руку и
разыгрывает по этому врагу. Всё, что хранится в самой карте, копится на клоне и исчезает вместе с ним. Пример:
ваниллное зачарование **Momentum** при каждом розыгрыше делает `+Amount` к урону *этой* карты. С клонами настоящая
Neutralize не получала ничего.

**Как стало.** Разыгрывается **сама сброшенная карта**, один раз (`CardCmd.AutoPlay`), но с числом повторов
`врагов × Repeats × обычное_число_розыгрышей` (обычное число — то, что дала игра: 1, или больше при Replay-зачаровании,
Echo Form и т.п.). Перед каждым повтором цель переключается в том же порядке, что у Hextech: повтор 1 по врагу 1, 2, 3,
повтор 2 по врагу 1, 2, 3… Если враг уже мёртв или его нельзя ударить, берётся следующий живой по списку (по кругу).
Карта вылетает из сброса в зону розыгрыша и после серии один раз возвращается туда, куда положено (обычно в сброс).
Повторно руна от этого не срабатывает: `AfterCardDiscarded` вызывается только из `CardCmd.Discard`.

**Баланс.** Momentum теперь реально копится: `+Amount` за **каждый** повтор серии (3 врага × 2 = 6 повторов → +6×Amount
к урону этой карты до конца боя). Это заметный бафф по сравнению с оригиналом.

**Как ванилла считает серию повторов против отдельных карт** (проверено по декомпиляции 0.111.0):

- `PenNib` — счётчик `AttacksPlayed` растёт в `BeforeCardPlayed`, т.е. **за каждый повтор**; удваивается ровно тот
  повтор, на котором счётчик дошёл до 10. `Shuriken`, `Kunai`, `Nunchaku` — счёт в `AfterCardPlayed`, тоже **за каждый
  повтор**. `IsFirstInSeries` ни одна из них не проверяет — для них серия и отдельные клоны считаются одинаково.
- История боя (`CombatHistory.CardPlayStarted/Finished`) пишет запись на **каждый** повтор, поэтому счётчики вида
  «атак/карт сыграно за ход» (Pale Blue Dot, Finisher, Ftl, Normality и т.п.) видят то же количество, что и с клонами.
- **Разница есть** там, где смотрят на `IsFirstInSeries` / `CurrentPlayIndex`:
  - `EchoFormPower` считает только первые розыгрыши серии: раньше каждый клон съедал заряд Echo Form и получал
    +1 повтор; теперь заряд уходит **один** на всю серию, зато он удваивает её целиком (обычное число = 2).
  - `LethalityPower` не даёт бонус повторам с `CurrentPlayIndex > 0`.
  - `ImitationLearningPower` реагирует только на первый розыгрыш серии (для атак не важно).
- Раньше каждый клон был **отдельной картой** (свой `CardPlay`, `PlayIndex = 0`, свой exhaust), теперь это одна
  карта с `PlayCount = N`.

**Сверка логов хоста и клиента.** На каждую серию обе стороны пишут одинаковую строку:

```
[NeutralizeRuneFix] player <NetId>: CARD.NEUTRALIZE discarded -> real-card series: <n> enemies x <repeats> repeats x base <b> = <total> plays
```

(или `... -> real-card series not played (...)`, если розыгрыш заблокирован). С `verboseLog` ещё и цель каждого повтора.

---

## For the HextechRunes author (Natsuki)

**Symptom.** Co-op, game v0.111.0, HextechRunes (0.111.0 variant DLL) on both peers, Necrobinder with
*Reaper Form Upgrade Rune* on the host side. At the first checksum of combat:

```
State divergence detected! ... Context: After player turn start.
LOCAL  (host)   ... Powers: REAPER_FORM_POWER ...   (CARD.REAPER_FORM no longer in any pile)
REMOTE (client) ... no REAPER_FORM_POWER ...        Draw pile: ... CARD.REAPER_FORM ...
```

Everything else in the dumps was identical. The host ran the rune's auto-play for that player; the client did not.

**Why (analysis).**

1. `AutoPlayFormsAtCombatStartRuneBase<TCard>.AfterPlayerTurnStartLate` is gated on per-*instance* fields
   `_startedThisCombat` / `_autoPlaying` (reset in `BeforeCombatStart` / `AfterCombatEnd`). In multiplayer the host
   rebuilds every remote player's relics before each combat: `CombatStateSynchronizer` → `Player.SyncWithSerializedPlayer`
   → `RelicModel.FromSerializable` → `ToMutable()`. The relic instance the hooks are dispatched to, and the one whose
   fields were reset, are not guaranteed to be the same object on every peer, and the reset hooks may run against a
   different instance/moment than the start-of-turn hook. Relic-instance state is therefore not a reliable
   "once per combat" guard across peers.
2. The effect runs from `Hook.AfterPlayerTurnStart` *inside* `CombatManager.SetupPlayerTurn`. That task is awaited by
   the turn loop with `HookPlayerChoiceContext.WaitForPauseOrCompletionWithoutAssigningTask(...)`: if anything in the
   setup pauses (choice / wait), the turn loop continues and later hooks may complete after
   `ChecksumTracker.GenerateChecksum("After player turn start")`, differently per peer.
3. Observed in logs: the rune did **not** fire in three consecutive combats on either peer, then fired on the **host
   only** in the fourth → divergence at "After player turn start".

**Suggested fix.**

- Drive "once per combat" from combat state, not relic-instance fields: e.g. `Owner.PlayerCombatState.TurnNumber == 1`
  plus a marker keyed by the `ICombatState` object (a `ConditionalWeakTable<ICombatState, …>`), or a flag stored on the
  combat state / a power applied to the creature.
- Run the effect at a deterministic point that every peer executes for every player before the checksum — e.g. a
  postfix continuation of `CombatManager.SetupPlayerTurn` for turn 1 (what this companion does), rather than from a
  hook that can be deferred by pause semantics.
- Log the decision (player NetId, rune, number of cards found, played/skipped and why) so host/client logs can be
  diffed when something goes wrong.

This companion disables the rune's own hook and performs the same effect deterministically from a
`SetupPlayerTurn` postfix, so both players can keep playing until it is fixed upstream.

### Note on NeutralizeUpgradeRune (v1.1.0 of this companion)

`NeutralizeUpgradeRune.AfterCardDiscarded` auto-plays `card.CreateClone()` copies (Repeats x each hittable enemy).
Clones are fresh card instances, so any per-instance state of the real card is lost: enchantments that mutate
themselves on play (vanilla `Momentum`: `ExtraDamage += Amount` in `OnPlay`) accrue on the throw-away clone and the
real Neutralize/Suppress never scales. This companion instead auto-plays the real card once with its play count
multiplied to `enemies x repeats x normalPlayCount` (postfix on `Hook.ModifyCardPlayCount`) and retargets each
iteration in the same repeat-major / enemy-minor order (prefix on `Hook.BeforeCardPlayed`, setting `CardPlay.Target`).
Side effects worth knowing: effects keyed on `CardPlay.IsFirstInSeries` / `CurrentPlayIndex` (Echo Form charges,
Lethality) now see one series instead of N separate first plays; per-play counters (Pen Nib, Shuriken, Kunai,
Nunchaku, combat history) are unchanged.

## Исправление визуальных карт в руке (v1.1.1, 2026-10-17)

Формы, автоиграемые из руки, теперь проходят обычный визуальный перенос и розыгрыш.
Игра убирает их держатели из интерфейса руки; после розыгрыша не остаются неактивные
карты-призраки. Формы из добора и сброса по-прежнему играются без анимаций переноса.
Порядок карт и место вызова автоплея не меняются. Для применения нужен перезапуск
игры; в коопе обновление устанавливают все игроки.