# MultiEnchantment Co-op Fix (Slay the Spire 2)

Мод-компаньон для **MultiEnchantmentMod** (Snowline, Workshop 3747561525). Убирает вылет клиента из кооп-забега
с ошибкой `State divergence detected!`, который возникает после **любого ваниллного зачарования карт**
(Тезкатара «Nutritious Soup», Mystic Lighter, ивенты и т.п.) при включённом MultiEnchantmentMod.

**Ставить обоим игрокам, одинаковую версию.** Мод помечен `affects_gameplay: true`, поэтому игра сама откажет
в подключении, если у одного из игроков его нет или версия другая.

## Установка

**Просто:** распаковать zip куда угодно и запустить `install.bat` двойным кликом. Он сам найдёт игру через Steam
(реестр → `libraryfolders.vdf` → `appmanifest_2868840.acf`), создаст `<игра>\mods\MultiEnchantmentCoopFix\` и
скопирует туда файлы. Если игру найти не удалось — попросит вставить путь к папке с `SlayTheSpire2.exe`.

**Вручную:** положить `MultiEnchantmentCoopFix.dll` + `MultiEnchantmentCoopFix.json` в
`<Steam>\steamapps\common\Slay the Spire 2\mods\MultiEnchantmentCoopFix\` (папку `mods` создать, если её нет —
игра читает её рядом с exe).

Проверка: после запуска игры в `%APPDATA%\SlayTheSpire2\logs\godot.log` должна быть строка
`[MultiEnchantmentCoopFix] [MECoopFix] Initialised v1.1.1.0 (...)`, а в списке модов — «MultiEnchantment Co-op Fix».

Сборка из исходников: `.\build.ps1 -Install -Zip` (нужен .NET SDK 8+, .NET 9 runtime ставится вместе с игрой).

## Что именно было сломано

Полный разбор — в разделе ниже (English, можно отправить автору мода). Кратко:

1. MultiEnchantmentMod пишет в `SerializableCard.Props` служебное свойство `MultiEnchantmentOrderData`
   (JSON-список ID зачарований в порядке наложения) **даже когда зачарование одно** и порядок тривиален.
   `SerializableCard` — это не только формат сейва: перед каждым боем пиры обмениваются им
   (`CombatStateSynchronizer` → `Player.SyncWithSerializedPlayer` пересобирает колоду соседа), и его же хеширует
   `ChecksumTracker`. Если свойство есть у одного пира и нет у другого — кик.
2. Имена SavedProperty по сети не передаются, идёт только их **индекс** в таблице
   `ModelIdSerializationCache._netIdToPropertyNameMap`. Таблица заполняется в порядке регистрации, а
   MultiEnchantmentMod (7 имён) и EndlessMode (5 имён: `IsMelted, IsWax, SavedEffectPercent,
   SavedFloorsBeforeLoop, SavedStackCount`) регистрируют свои имена **из инициализатора мода**
   (`CacheSavedPropertiesForTypeDebug`), т.е. в порядке загрузки модов, ещё до базовой игры. У хоста
   EndlessMode грузился раньше MultiEnchantmentMod (таблица `[Endless×5, ME×7, база…]`), у клиента — позже
   (`[ME×7, Endless×5, база…]`), индексы ME сдвинуты ровно на 5. Хеш хендшейка покрывает только имена, найденные
   самой игрой в `Init()`, поэтому оба прошли хендшейк. В дампе рассинхрона это видно буквально: значение
   `MultiEnchantmentOrderData` клиента (индекс 5 у него) у хоста декодировано под именем с индексом 5 у хоста —
   `MultiEnchantmentCardInstanceId`. Из-за этого хост, пересобирая колоду клиента перед боем, не находит порядок,
   и его копия карт сериализуется без свойств → чексуммы не сходятся.

## Что делает этот мод

С **v1.1.1** также удаляется осиротевший `MultiEnchantmentOrderData`, когда у карты нет ни основного,
ни дополнительных зачарований. Это исправляет обнаруженный 2026-10-08 рассинхрон после **Primal Force**:
MultiEnchantmentMod не переносил несовместимый **Spiral** на **Giant Rock**, но копировал историю его наложения.
У клиента Giant Rock сохранял `[SPIRAL]`, у хоста поле отсутствовало; остальной текст дампов совпадал.
Очистка действует и для других трансформаций с таким состоянием. Настоящие дополнительные зачарования,
другие свойства карты и непарсируемая история сохраняются. После обновления оба игрока должны полностью
перезапустить игру; уже загруженная DLL не обновляется в работающем процессе.

| Патч | Что делает | Флаг в манифесте |
|---|---|---|
| `ModelIdSerializationCache.Init` (postfix) | **Канонизирует таблицу имён**: сортирует её и пересобирает словарь имя→индекс. Индексы становятся функцией *набора* имён и больше не зависят от порядка загрузки модов. Ничего ещё не сериализовано на этот момент (старт игры), поэтому это безопасно. | `canonicalPropertyTable` (true) |
| там же | Пишет в лог всю таблицу с индексами (чтобы сравнить логи двух игроков) и подмешивает её в хеш хендшейка: если *набор* имён всё-таки разный (у одного есть мод, регистрирующий имена, у другого нет) — отказ в подключении сразу, а не кик посреди забега. | `strictPropertyTableCheck` (true) |
| `CardModel.ToSerializable` (postfix после MultiEnchantmentMod) | Удаляет `MultiEnchantmentOrderData`, если у карты одно зачарование и нет доп. зачарований (`MultiEnchantmentData` отсутствует), а порядок равен `[это зачарование]` или пуст. Пустые списки Props схлопывает в `null`, как у ванили. Карты с настоящим мульти-зачарованием не трогаются. Защита в глубину: ваниллно-зачарованные карты идут по сети байт-в-байт как у ванили. | `stripRedundantOrderData` (true) |
| — | Подробное логирование каждого удаления. | `verboseLog` (false) |
| там же (после таблицы имён) | **Хеширует состав пулов**: для каждого пула карт/реликвий/зелий берёт отсортированные ID участников (для карт — ещё флаг `CanBeGeneratedInCombat`), пишет в лог количество, хеш и модовые карты каждого пула и подмешивает всё в хеш хендшейка. Если у игроков пулы разные (обычно из-за per-machine конфига мода, см. «Случай 2» ниже) — отказ в подключении сразу. | `strictContentPoolCheck` (true) |

MultiEnchantmentMod сам достраивает порядок наложения, если его нет (`SeedMissingApplicationOrder` вызывается
перед каждым `AppendApplicationOrder`), поэтому удаление тривиального порядка ничего не ломает.

Если после установки игра отказывает в подключении с «hash mismatch» — значит *наборы* имён у вас разные:
сравните строки `[MECoopFix] SavedProperty net-ID table after Init` в двух логах
(`%APPDATA%\SlayTheSpire2\logs\godot.log`) и найдите мод/версию, которые есть только у одного.

---

## For the mod author (English)

**Symptom.** Co-op, host on v0.111.0 PublicBeta, MultiEnchantmentMod v2.5.4 on both peers. Right after the
Tezcatara ancient where the client picked *Nutritious Soup* (enchant all Strikes with Tezcatara's Ember),
first checksum of the next combat fails:

```
State divergence detected! Checksum with ID 631 ... Context: After player turn start.
LOCAL  (host)   CARD.STRIKE_SILENT ... Enchantment: ENCHANTMENT.TEZCATARAS_EMBER ... Props:
REMOTE (client) CARD.STRIKE_SILENT ... Enchantment: ENCHANTMENT.TEZCATARAS_EMBER ... Props: MultiEnchantmentCardInstanceId=[{"Category":"ENCHANTMENT","Entry":"TEZCATARAS_EMBER"}]
```

Everything else in the two dumps (HP, gold, relics, RNG counters, every other card) is identical.

**Why.**

1. `SerializeApplicationOrder` writes `MultiEnchantmentOrderData` for every enchanted card, including cards
   with a single enchantment where the order is trivially `[card.Enchantment.Id]`. `SerializableCard.Props` is
   part of the multiplayer state: `CombatStateSynchronizer` sends `Player.ToSerializable()` before every combat and
   the other peers rebuild the deck through `RunState.LoadCard` → `CardModel.FromSerializable`;
   `ChecksumTracker` xxhashes `NetFullCombatState`, which contains `card.ToSerializable()` for every pile card.
   Any property present on one peer and absent on the other is a guaranteed divergence.
2. Property names are transmitted as indices into `ModelIdSerializationCache._netIdToPropertyNameMap`.
   `MultiEnchantmentSupport.Initialize()` calls `CacheSavedPropertiesForTypeDebug(typeof(MultiEnchantmentSaveCarrier))`
   from the mod initializer, i.e. *before* `ModelIdSerializationCache.Init()`, so the mod's 7 names take the
   first slots **in mod load order** — and EndlessMode does the same for 5 names (`IsMelted, IsWax,
   SavedEffectPercent, SavedFloorsBeforeLoop, SavedStackCount`). The game's handshake hash does not cover names
   registered this way. Host loaded EndlessMode before MultiEnchantmentMod (table `[Endless×5, ME×7, base…]`),
   the client the other way round (`[ME×7, Endless×5, base…]`): every MultiEnchantment index is off by 5, and
   both peers pass the handshake. That is exactly why the client's `MultiEnchantmentOrderData` value (index 5
   there) shows up on the host under the host's index-5 name `MultiEnchantmentCardInstanceId`. On the host
   `DeserializeApplicationOrder` then finds no `MultiEnchantmentOrderData`, the rebuilt card has no
   `CardStates` entry, `ToSerializable` emits no props → checksums differ.
3. Secondary: `MultiEnchantmentInstanceId` / `MultiEnchantmentCardInstanceId` are `Guid.NewGuid()` values, and
   the save sidecar (`multi_enchantment_save_sidecar*.json`) is per-machine. Anything that puts these into a
   `SerializableRun`/`SerializableCard` that later crosses the wire (SaveRun prefix mutates the run in place)
   is inherently non-deterministic across peers.

**Suggested fixes in the mod.**

- Don't serialise derivable state: skip `MultiEnchantmentOrderData` when there are no extra enchantments and the
  order equals `[primary]` (you already re-seed via `SeedMissingApplicationOrder`). Same idea for any other
  property whose value is the default.
- Make the wire encoding independent of the property-name table, e.g. BaseLib's
  `ExtendedSaveHandlers<CardModel, SerializableCard>.RegisterSave<T>(id, getter, setter, packetWriter, packetReader)`
  — it serialises registered entries in a fixed (id-sorted) order and does not go through
  `GetNetIdForPropertyName`. Or at minimum: don't register names from the mod initializer; do it in a
  `ModelIdSerializationCache.Init` postfix, sort the table there (that is what this companion does) and fold it
  into `ModelIdSerializationCache.Hash` so mismatched peers are refused at connect time. (Ideally MegaCrit would
  sort the table and hash it in full — this is a game-level footgun for every mod that registers names early.)
- Never let per-machine GUIDs or sidecar lookups reach anything that is sent to a peer; keep the sidecar
  strictly for the local disk save, or send the payload inline in multiplayer.

This companion mod implements the first and the "fold into hash" parts from the outside so both players can keep
playing until it is fixed upstream.

---

## Случай 2 (2026-09-19): пулы карт различаются из-за конфига TheHeroExpansion

**Симптом.** Клиент (Сайлент) пьёт Skill Potion → `State divergence detected!` сразу после `UsePotionAction`.
В дампе всё совпадает, кроме сгенерированной карты (хост `TACTICIAN`, клиент `UP_MY_SLEEVE`) и счётчика
`RNG counter CombatCardGeneration: 187` у хоста против `188` у клиента.

**Причина.** Skill Potion берёт весь пул скиллов персонажа и делает `UnstableShuffle` (Фишер-Йетс: один вызов RNG на
каждый элемент пула), потом первые три. Разница счётчика ровно в 1 = у клиента в пуле на одну карту больше.
TheHeroExpansion (BaseLib) даёт тумблер на каждую свою карту в `%APPDATA%\SlayTheSpire2\mod_configs\TheHeroExpansion.cfg`;
выключенная карта создаётся через `CustomCardModel(..., autoAdd: false)` — остаётся в `ModelDb` (поэтому хеш ID
в хендшейке совпадает), но **не добавляется в пул**. У хоста `Repurpose=False` (скилл Сайлент) и
`Groundwork=False` (атака Сайлент), у клиента — значения по умолчанию (`True`). Конфиг лежит на каждой машине
свой и по сети никак не сверяется. Ломает всё, что тянет карту из пула: Skill/Attack Potion, Discovery,
награды за бой, магазин, трансформации.

**Лечение для текущего забега.** Сделать конфиг одинаковым у обоих (любой вариант, но одинаковый) и перезапустить
игру у обоих (тумблер читается при создании моделей, т.е. на старте). Проще всего второму игроку выставить
у себя `"Repurpose": "False"` и `"Groundwork": "False"` в `TheHeroExpansion.cfg` (или через меню конфигов модов
BaseLib в главном меню → TheHeroExpansion → Silent).

**Защита на будущее** — `strictContentPoolCheck` в этом моде (v1.1.0): состав всех пулов подмешивается в хеш
хендшейка, и при несовпадении игра откажет в подключении сразу. В логе для каждого пула есть строка
`[MECoopFix] Pool CARD_POOL.SILENT: N members, hash XXXXXXXX; modded: ...` — при отказе сравнить эти строки в двух логах.

### For the TheHeroExpansion / BaseLib authors (English)

Per-card enable toggles live in a per-machine config and are applied as `CustomCardModel(..., autoAdd: false)`,
i.e. the card is registered in `ModelDb` (so the game's handshake ID hash still matches) but never reaches the
pool. Any pool-based roll then consumes a different number of RNG calls on the two peers (`UnstableShuffle` is
Fisher–Yates over the whole pool), and the run desyncs at the first Skill/Attack Potion, Discovery, card reward or
shop. Suggested fix: either fold the toggles into `ModelIdSerializationCache.Hash` (or refuse to join with
non-default toggles), or sync the host's toggles to clients at lobby time. This companion folds the pool
composition into the handshake hash from the outside.
