# octoFixes — Slay the Spire 2

Один мод с исправлениями для модифицированного Slay the Spire 2, включая кооп.
Объединяет MultiEnchantment Co-op Fix 1.1.1 и Hextech Form Rune Co-op Fix 1.1.1.

**[Скачать последнюю версию](https://github.com/olegsubdiv/octoFixes/releases/latest/download/octoFixes.zip)** · [Все релизы](https://github.com/olegsubdiv/octoFixes/releases)

Эту ссылку можно сохранить и отправлять друзьям: она всегда ведёт на ZIP последнего релиза.

## Установка и обновление

1. Полностью закройте игру.
2. Скачайте ZIP, распакуйте и запустите `install.bat`.
3. Запустите игру. В списке модов должен появиться **octoFixes**.

Установщик находит игру через Steam, сохраняет старые фиксы вне папки `mods` и устанавливает
одну папку `mods/octoFixes`. Старые `MultiEnchantmentCoopFix` и `HextechFormRuneCoopFix`
одновременно с octoFixes использовать нельзя. При обновлении снова запускайте установщик.

**Всем игрокам в коопе нужна одна версия octoFixes.** Обновлять нужно между игровыми сессиями.
Резервные копии установщик сохраняет рядом с `mods`; путь показывает после установки.
Сохранения игры установщик не меняет.

Ручная установка: уберите обе старые папки фиксов из `mods`, затем положите
`octoFixes.dll` и `octoFixes.json` в `mods/octoFixes`. При необходимости сохраните
свои значения настроек из старых манифестов в новом `octoFixes.json`.

## Что исправлено

- **MultiEnchantmentMod:** порядок сетевых ID свойств больше не зависит от порядка загрузки модов;
  лишняя история одиночного зачарования и осиротевшая история после трансформаций удаляются
  из сериализованной карты. Включает случай Primal Force → Giant Rock со старой историей Spiral.
- **Проверка коопа:** различия в таблицах свойств и пулах карт, реликвий и зелий учитываются
  при подключении. Например, разные тумблеры TheHeroExpansion приводят к отказу при подключении,
  вместо рассинхрона посреди боя. При отказе сравните строки `[MECoopFix] Pool` в логах игроков.
- **HextechRunes:** Reaper/Demon/Echo/Serpent/Void Form Upgrade запускают формы последовательно
  в детерминированной точке первого хода. Формы из руки проходят штатный визуальный перенос:
  после автоплея не остаётся неиграбельных изображений карт в руке.
- **Neutralize Upgrade Rune:** разыгрывает настоящую Neutralize/Suppress серией повторов,
  поэтому Momentum накапливается на самой карте. Это изменение баланса: Echo Form расходует
  один заряд на серию, а эффекты, учитывающие первый розыгрыш серии, могут работать иначе.

Лог: `%APPDATA%/SlayTheSpire2/logs/godot.log`, строки `[octoFixes]`, `[MECoopFix]`,
`[FormRuneFix]`, `[NeutralizeRuneFix]`.

Проверено на игре **v0.111.0 PublicBeta**. Это набор исправлений известных случаев,
а не гарантия совместимости всех модов. Подозрение на повторный бонус Evolution
при клонировании пока не исправлено и не входит в релиз.

Исходные моды устанавливаются отдельно из Workshop. octoFixes не содержит их DLL,
ресурсы игры или сохранения. Hextech-патчи активируются при наличии HextechRunes.

## Настройки

Флаги находятся в `mods/octoFixes/octoFixes.json`, читаются при запуске игры.

| Флаг | По умолчанию | Назначение |
|---|---|---|
| `stripRedundantOrderData` | `true` | Очистка лишней истории зачарований |
| `canonicalPropertyTable` | `true` | Стабильные сетевые ID свойств |
| `strictPropertyTableCheck` | `true` | Проверка набора свойств при подключении |
| `strictContentPoolCheck` | `true` | Проверка состава пулов при подключении |
| `disableAutoPlayFix` | `false` | `true` возвращает исходный автоплей форм Hextech |
| `neutralizeRuneRealCard` | `true` | Серия повторов настоящей карты вместо клонов |
| `verboseLog` | `false` | Подробная диагностика |

## Сборка и публикация

Windows, .NET SDK 8 или новее, .NET 9 runtime, установленная игра и совместимая
HextechRunes DLL для проверок. Нужные DLL используются только локально.

```powershell
.\build.ps1 -GameDir 'F:\SteamLibrary\steamapps\common\Slay the Spire 2' -Zip
```

Можно задать `STS2_GAME_DIR`, `-HextechDll` и `-Net9RuntimeDir`. Результат:
`dist/octoFixes.zip` и SHA256. В архив попадают только собственная DLL, манифест,
установщик и README. Обе исходные группы проверок запускаются на объединённой DLL.

Публикация: [docs/RELEASING.md](docs/RELEASING.md). Постоянный адрес скачивания
работает благодаря одинаковому имени `octoFixes.zip` у каждого релиза.

Подробные разборы исходных исправлений:
[MultiEnchantment](docs/MultiEnchantmentCoopFix-history.md),
[Hextech](docs/HextechFormRuneCoopFix-history.md).
