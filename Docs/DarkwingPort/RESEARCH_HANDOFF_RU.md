# Работа с проектом

Проект `D:\Art\SW Code Zero Unity`, установленный Unity 6000.6.5f1. Перед изменениями читать `AGENTS.md` и [бриф](IMPLEMENTATION_BRIEF_RU.md). Текущие результаты — только в [приёмке](ACCEPTANCE_REPORT.md); очередь — [MILESTONES](MILESTONES.md), вопросы/адреса — `Research/DarkwingDuck/GAPS.md` и `FACTS.md`.

База последней проверки — `901cc0150460018a1ac159f1a8e91e82499d1a99` + локальные изменения интеграции и инструментов. В Assets исправлены GameFlow/Launcher/nametable/CHR/text; см. приёмку. Исходные правки `UniversalRP.asset`, IET Settings.json и untracked-исследования сохранять; коммиты не создавались. Build через Editor добавляет InputSystem_Actions в preloadedAssets. История текущих документов доступна в Git, например `git show 901cc015:Research/DarkwingDuck/GAPS.md`.

## Воспроизведение проверок (PowerShell)

```powershell
Set-Location -LiteralPath 'D:\Art\SW Code Zero Unity'
unity status --project-path 'D:\Art\SW Code Zero Unity' --format json
python Tools\DarkwingResearch\run_check.py --suite --out Temp\next_suite
$LASTEXITCODE
python Tools\DarkwingResearch\run_check.py --entry PhysCompare.EnemyVerifyTrace --out Temp\next_enemy
python -m unittest discover -s Tools\DarkwingResearch -p 'test_*.py' -v
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\DarkwingResearch\test_player_smoke.ps1
python Tools\DarkwingResearch\run_check.py --entry PhysCompare.AuditSelfTests --out Temp\next_tests
```

Для `--suite`: exit0 = все выбранные STRICT совпали; 1 = расхождение; 2 = ошибка/неполный или устаревший отчёт. Последний запуск — exit1. Wrapper проверяет внутренний success CLI и даёт 240000ms; Suite занимает около 44с. Без `--suite` exit0 подтверждает только исполнение: читать результат, это не приёмочный PASS. После разбора временный вывод не хранить как второй постоянный отчёт.

Шаблон: `python Tools\DarkwingResearch\run_check.py --file AgentScripts/TitleVerify.cs --entry TitleVerify.Run --out Temp\next_title`.

| Файл / класс | Действующие entry |
|---|---|
| AgentScripts/PhysCompare.cs | EnemySweep, BossFightSeed с `--args '[29]'`, BossFightWolfDefeat, PauseHold, PauseResume, HudOam, M6RouteTrace (префикс PhysCompare.) |
| AgentScripts/TitleVerify.cs / IntroVerify.cs / MapOamVerify.cs | TitleVerify.Run / IntroVerify.Run / MapOamVerify.Run |
| AgentScripts/MissionCardVerify.cs / GameOverVerify.cs | MissionCardVerify.Suite / GameOverVerify.Run |
| AgentScripts/FlowVerify.cs | FlowVerify.Check — принудительные победы, не прохождение |
| AgentScripts/BootFlowProbe.cs / SceneAudit.cs | BootFlowProbe.Run — fixed-step снимки без ROM alignment; SceneAudit.Inspect — чтение Editor API |

Экспорт звука и диагностическое сравнение (CSV/WAV сначала генерируются, постоянные копии не требуются):

```powershell
python Tools\DarkwingResearch\run_check.py --file Tools/DarkwingResearch/AudioPortRender.cs --entry AudioPortRender.RenderTo --args '["D:/Art/SW Code Zero Unity/Temp/next_audio"]' --out Temp\next_audio_run
python Tools\DarkwingResearch\compare_audio_port.py --port-state Temp\next_audio\audio_port_song1_state.csv --port-wav Temp\next_audio\audio_port_song1.wav --out Temp\next_audio_comparison.json
```

Для SFX использовать `AudioPortSfxCompare.RenderTo` из `Tools/DarkwingResearch/AudioPortSfxCompare.cs` тем же способом. Exit0 аудиосравнения означает выполнение диагностики, не идентичность сигнала. Нельзя заменять original goldens выходом порта.

## Обычный вход: Editor и standalone

Editor этого проекта должен быть ready. Все CLI-команды явно адресуют проект, runtime Pipeline включать не нужно. Кнопочный replay существует только для Editor/Development, запускается до первого игрового кадра и не допускает seed RAM/phase. В обычном запуске Enter=Start, X=B, Z=A, стрелки=движение; карту/страницы подтверждают B/A по подсказкам.

```powershell
unity command editor_stop --project-path 'D:\Art\SW Code Zero Unity' --caller plugin --skill unity-cli --format json
python Tools\DarkwingResearch\run_check.py --file AgentScripts/BootFlowProbe.cs --entry BootFlowProbe.EditorRoute --out Temp\editor_route_command
# Progress должен показать finished=True; результат — Temp/editor_route/route.json.
python Tools\DarkwingResearch\run_check.py --file AgentScripts/BootFlowProbe.cs --entry BootFlowProbe.Progress --out Temp\editor_progress
unity command editor_stop --project-path 'D:\Art\SW Code Zero Unity' --caller plugin --skill unity-cli --format json
python Tools\DarkwingResearch\build_player.py --out Build\Audit_20261018\DarkwingDuck.exe --evidence Temp\integration_build --development
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\DarkwingResearch\verify_player.ps1 -Exe 'D:\Art\SW Code Zero Unity\Build\Audit_20261018\DarkwingDuck.exe' -PngA 'Temp\player_route\window_a.png' -PngB 'Temp\player_route\window_b.png' -FirstWaitSec 50 -SecondWaitSec 1 -Replay 'D:\Art\SW Code Zero Unity\Temp\route_input_player.json'
python Tools\DarkwingResearch\run_check.py --file AgentScripts/BootFlowProbe.cs --entry BootFlowProbe.Verify --out Temp\route_verify
python Tools\DarkwingResearch\run_check.py --file AgentScripts/BootFlowProbe.cs --entry BootFlowProbe.IdleTitle --out Temp\idle_title
python Tools\DarkwingResearch\run_check.py --entry PhysCompare.EnemyFixtureRegression --out Temp\enemy_regression
```

Build wrapper требует Succeeded/0 errors и проверяет source hashes до/после. Verify требует одинаковые 14 состояний Editor/Player, обязательные фазы, движение/прыжок/газ. Smoke сам по себе проверяет только запуск/окно/штатный выход. Данные в Temp перезаписывать при повторении, промежуточные коллекции не сохранять.

Оригинальный маршрут от reset (выходная папка должна отсутствовать; load-state/patch не применять):

```powershell
python Tools\DarkwingResearch\probe_run.py --out Temp\ordinary_original_route --frames 4200 --compact --screenshot-every 100 --vram-every 20 --timeout 260 --input '0:start@900-903' --input '0:start@1100-1103' --input '0:start@1300-1303' --input '0:start@1500-1503' --input '0:b@1700-1703' --input '0:a@2000-2003' --input '0:a@2200-2203' --input '0:a@2400-2403' --input '0:a@2600-2603' --input '0:a@2800-2803' --input '0:b@3620-3623' --input '0:right@3700-3770' --input '0:a@3790-3805' --input '0:b@3850-3853'
python Tools\DarkwingResearch\compare_frames.py --route Temp\ordinary_original_route Temp\editor_route\route.json Temp\player_route\route.json
```

Последняя команда сохраняет точные различия и даёт exit1 при несовпадении любого сравниваемого PNG. Текущий ordinary-entry проходит как маршрут; **полная пиксельная эквивалентность меню не принята**. Mesen CLI relative f попадает в Lua capture f+1; port replay нумеруется с 1. Поэтому управляющие состояния сравниваются с offset921, а устойчивые экраны — по явно указанным парам. Это не одинаковая длительность переходов. Канонические исходные goldens не переписывать. Постоянные минимальные доказательства — только существующие файлы `evidence/audit_20261018`.

Ниже исходные исследовательские сведения и адреса. Это данные подготовки, а не текущие вердикты готовности.

# Справочник уже собранных сведений

Это материалы подготовки задания, а не готовый порт. Не требуется повторять весь поиск. Проверять конкретное утверждение тогда, когда от него зависит текущая реализация. При конфликте статей и поведения ROM приоритет имеют воспроизводимые наблюдения оригинала.

## 1. ROM

| Свойство | Подтверждённое значение |
|---|---|
| Файл | `Art/Darkwing Duck (USA).nes` |
| Размер | 262160 байт / `0x40010` |
| SHA-256 всего файла | `1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00` |
| SHA-1 всего файла | `c7b81ee2ff158a58582bc1bf508ffb1213e7a53a` |
| MD5 всего файла | `da86da29043284e236b5ece1c293c5ee` |
| CRC32 всего файла | `a157e56c` |
| SHA-256 без 16-байтного заголовка | `f97f05a4f8747d81dc8adaf07df70d1f87366632ae3e93103d387043ef5ec508` |
| Заголовок | `4e 45 53 1a 08 10 10 08 00 00 00 00 00 00 00 01` |
| Формат заголовка | NES 2.0: `(byte7 & 0x0C) == 0x08` |
| Mapper / submapper | 1 / 0, MMC1 |
| Trainer / battery flag | Нет / нет |
| PRG | `0x10..0x2000F`, 128 КиБ, восемь банков по 16 КиБ |
| CHR | `0x20010..0x4000F`, 128 КиБ, 32 банка по 4 КиБ |
| Регион по заголовку | byte12 = 0, NTSC |
| Векторы NMI / RESET / IRQ | `$C000` / `$FFE0` / `$FFE0` |

Имя файла не является доказательством редакции. У этого файла NES 2.0 заголовок; при сравнении с чужой базой hash различать весь файл и PRG+CHR. Для инструмента, понимающего только iNES, разрешается отдельная производная копия с документированным преобразованием заголовка и неизменным payload. Оригинал не переписывать.

Все следующие `0x...` смещения — **файловые, с 16 байтами заголовка**, если явно не сказано CPU/PRG-relative. При переводе CPU `$8000–$BFFF` в смещение формула `0x10 + bank16k*0x4000 + (cpu-0x8000)` верна только когда этот банк реально отображён в нижнее 16-КиБ окно. MMC1 может менять отображение; не применять формулу вне подтверждённого режима.

## 2. Таблицы уровней из CadEditor

Источник — конфигурация и код [CadEditor](https://github.com/spiiin/CadEditor/tree/b28316ddd9be4e79ac5e6adf89b7751df609d94e), MIT. Локальные копии использованных файлов находятся в `Docs/DarkwingPort/evidence/`, имена начинаются с `CadEditor_`.

| ID набора | Имя в конфигурации | Массив типов объектов | Записей | Layout | Ширина × высота | Базовая палитра-кандидат | Первый экран-кандидат |
|---|---|---:|---:|---:|---:|---:|---:|
| 0 | Bridge (в исходнике `Brigde`) | `0x10315` | 51 | `0x1DFA0` | 17×4 | 0 | `0x01` |
| 1 | City | `0x10438` | 60 | `0x1DFE4` | 17×4 | 2 | `0x21` |
| 2 | Sewers | `0x10584` | 68 | `0x1E028` | 17×4 | 4 | `0x41` |
| 3 | Tower | `0x106A0` | 54 | `0x1E06C` | 10×12 | 6 | `0x61` |
| 4 | Forest | `0x10816` | 80 | `0x1E0E4` | 19×3 | 10 | `0x81` |
| 5 | Warehouse | `0x10962` | 63 | `0x1E11D` | 19×3 | 12 | `0xA1` |
| 6 | FOWL's Fortress | `0x10A89` | 58 | `0x1E156` | 19×3 | 9 | `0x101` |

Это внутренние группы редактора, не подтверждённые номера миссий меню и не порядок прохождения.

Структура размещений — пять раздельных массивов по N байт. Если `A` — начало массива типов:

- `A-4N`: screen X;
- `A-3N`: local X;
- `A-2N`: screen Y;
- `A-N`: local Y;
- `A`: type ID.

Всего в этих диапазонах 434 записи, из них 7 имеют type `0xFF`. Обнаружено 100 различных ID без `0xFF`. Это не утверждение о 100 врагах: набор включает предметы, триггеры и другие объекты. Семантику пустых/служебных записей подтвердить. Позиции не объединять в world Y через 240 без исследования — размер экранной структуры и видимой области различается.

Остальные ориентиры:

| Данные | Адрес/формат |
|---|---|
| Экраны-кандидаты | `0x10 + screenId*0x40`, 64 индекса макротайлов, сетка 8×8 |
| Диапазон редактора | 300 слотов; это не 300 доказанно используемых комнат |
| Макротайлы 32×32 | `0x36F0 + set*0x4000`, 256 записей по четыре индекса метатайлов |
| Метатайлы 16×16 | `0x3AF0 + set*0x4000`, четыре массива tile ID по 256 байт |
| Атрибуты метатайлов | Ещё 256 байт сразу после четырёх массивов; palette = `raw & 3`, type = `raw >> 4`; сохранять весь raw |
| Названия type nibble в редакторе | 0 background, 1 hook, 2 platform, 3 block, 4 spikes, 5 door; поведение подтвердить |
| Палитры-кандидаты | `0x1C36D + id*16`, конфигурация объявляет 32 записи |
| Background CHR | `0x30010 + set*0x1000`; для основных групп set 0–6 |
| Sprite CHR | Конфигурация начинает с `0x20010`, но банк зависит от runtime |
| Scroll/door массив | Соответствующий layout address + 508 (`0x1FC`), такой же размер |

Не извлекать автоматически восьмой набор метатайлов лишь потому, что общий OffsetRec имеет count=8: сначала доказать назначение диапазона. Семь основных групп проверены структурным разбором.

**Особое место:** CadEditor прибавляет 256 ко всем байтам layout финального уровня. В ячейке с линейным индексом 12 лежит `0x98`, что даёт `0x198` / 408 — вне 300 слотов. Нельзя читать её как обычный экран или заменять пустым. Установить смысл по исполняемому коду/переходам. Нулевые ячейки и служебные ссылки также требуют семантического разбора.

## 3. Анимации и метаспрайты

Указатели этого набора относятся к PRG bank 5, отображённому в нижнее окно. Таблицы старших/младших байтов раздельные; это не обычный interleaved массив little-endian слов.

| Таблица | Число | CPU адрес low | CPU адрес high | Файловые low / high |
|---|---:|---:|---:|---:|
| Анимации | 199 | `$B429` | `$B4F0` | `0x17439` / `0x17500` |
| Составные кадры | 314 | `$9B0B` | `$9C45` | `0x15B1B` / `0x15C55` |
| Наборы координат | 208 | `$AE53` | `$AF23` | `0x16E63` / `0x16F33` |

Формат, используемый редактором:

- Анимация: header byte, timer byte, список индексов кадров. Редактор считает `count=(header+1)%128`, а при `header+1 >= 128` прибавляет к индексам 256. Сохранить raw и проверить крайние значения/смысл таймера в runtime.
- Статический кадр: первый байт + 1 = число частей; второй = индекс координат; затем пары tile ID / attribute.
- Координаты частей: пары Y, X; оба значения знаковые 8-битные.
- Атрибуты включают palette low bits, flips `0x40/0x80`; остальные биты нельзя выбрасывать без проверки.

При фактическом разборе обнаружены **311 статических и 3 RAM-кадра**:

| Frame ID, десятичный | CPU-указатель |
|---:|---:|
| 31 | `$05B0` |
| 156 | `$05C2` |
| 251 | `$05E8` |

Эти три записи не переводятся в ROM по формуле банка 5. Нужно найти писателей этих областей и алгоритм динамического формирования. Это одна из причин, почему нельзя считать экспорт таблиц готовым комплектом всех финальных спрайтов.

## 4. UI и RAM-ориентиры

CadEditor описывает две карты интерфейса с потоками по `0x80B1` и `0x83FC`. Команда: PPU address в **big-endian**, один байт количества, затем буквальные байты; `0xFF` в начале следующей команды завершает поток. Это конкретный формат этих двух карт, не универсальная компрессия Capcom. Необновлённые области могут зависеть от предыдущего содержимого VRAM. Контексты палитры/CHR из конфигурации ещё нуждаются в сверке.

Следующие RAM-поля — ориентиры из исследований автора CadEditor. До использования в приёмочных тестах подтвердить экспериментом и указать слот/область применимости:

| RAM | Предполагаемое назначение |
|---|---|
| `$0400 + slot` | флаги объекта |
| `$0410 + slot` | кадр анимации |
| `$0420 + slot` | номер анимации |
| `$0430 + slot` | таймер анимации |
| `$04D0 + slot` | стадия/состояние объекта |
| `$04E0 + slot` | индекс функции поведения |
| `$0580 + slot` | направление |
| `$05A0 + slot` | здоровье/специальное значение |
| `$0070` | индекс активного объекта по исследованию |
| `$0050/$0051/$0058` | состояния/параметры игрока из экспериментального Lua |
| `$006D`, `$007C` | локация, таймер неуязвимости из того же Lua |
| `$0550/$0560 + slot` | компоненты вертикального движения из того же Lua |

Файл `evidence/spiiin_dwd.lua` — **экспериментальный инъекционный скрипт, меняющий игру**, в том числе гравитацию. Не запускать его как пассивный измеритель и не переносить его константы как достоверную физику оригинала. Он полезен только как подсказка для поиска адресов.

## 5. Уже созданные подготовительные материалы

| Путь относительно проекта | Содержание и пределы |
|---|---|
| `Tools/DarkwingResearch/extract_rom.py` | Python stdlib, привязан к указанному SHA-256; разбор CHR, основных таблиц, анимаций, координат и двух UI-потоков |
| `Research/DarkwingDuck/extracted/manifest.json` | Идентификация ROM, counts, проверки roundtrip, исключения |
| `Research/DarkwingDuck/extracted/` | RAW PRG/CHR, JSON таблицы, grayscale-превью; это ещё не Unity-ready ассеты |
| `Tools/DarkwingResearch/vendor/mesen-agent/` | Локальный Windows x64 mesen-agent v0.1.0 с matching MesenCore.dll, лицензией, zip и checksum |
| `Tools/DarkwingResearch/record_reference.py` | Ограниченная запись эксперимента; создаёт новый output-каталог, сохраняет команду/hash и проверяет базовый результат |
| `Tools/DarkwingResearch/oracle_capture.lua.in` | Снимки RAM/palette/OAM по endFrame, журнал APU writes, опциональные PNG |
| `Tools/DarkwingResearch/capture_checkpoints.lua` | Ранняя ограниченная диагностическая запись кадров |
| `Tools/DarkwingResearch/runs/` | Ранние эксперименты оригинала, PNG/WAV/snapshots/логи |
| `Docs/DarkwingPort/evidence/mesen_help.txt` | Справка фактически запущенного бинарника |
| `Docs/DarkwingPort/evidence/shared_dialogue_messages.md` | Извлечённые сообщения предыдущего диалога; порядок блоков не гарантирован, старые выводы перепроверять по первоисточникам |

Пройденные подготовительные проверки: пересборка header+PRG+CHR; CHR encode/decode; обратное кодирование таблиц метатайлов, статических кадров/координат и анимационных записей. Структурный roundtrip доказывает сохранность данных, но **не** корректную интерпретацию их игрового смысла.

Эмулятор действительно загрузил этот ROM и записал изображения, звук и состояния. Просмотрены вступление, заставка и экран выбора/обсуждения миссии. Полное прохождение, игровые параметры, детерминизм всей записи и корректность портированной реализации не проверялись. Unity-игра ещё не реализована. Каталог с именем `enter_level` не является доказательством входа в уровень: смотреть фактический кадр/состояние.

В одном инструментированном фрагменте зафиксированы записи APU с callback PC `$80FD`, `$80EB`, `$8215`, `$8218`, `$821D`; PRG-relative offsets `$C0FD`, `$C0EB`, `$C215`, `$C218`, `$C21D`. Это ориентир на bank 3, а не готовые имена функций. PC из callback может указывать на положение после выборки операндов — найти фактическую инструкцию по соседней дизассемблированной области. PRG-relative offset не включает заголовок; для file offset добавить `0x10`.

Записанный WAV был PCM 48 кГц, stereo, 16 bit. Это конфигурация наблюдавшегося инструмента, не формат хранения музыки в ROM.

`endFrame` в наблюдённой трассе приходился на scanline 240, cycle 0. `summary.final_frame` и последний записанный `ppu.frameCount` различались на единицу из-за границы остановки. Не исправлять индексы произвольным сдвигом: формально определить фазу. Захват PCM и callback PNG тоже требуют проверки границ перед строгим сравнением.

Документация некоторых инструментов скачана с HEAD, а бинарник mesen-agent — релиз v0.1.0. При конфликте сверяться со справкой и исходниками **соответствующего релиза**. Нельзя считать совместимость всех API уже доказанной.

Команды для исполнителя, когда начинается реализация:

```powershell
Set-Location -LiteralPath 'D:\Art\SW Code Zero Unity'
python Tools/DarkwingResearch/extract_rom.py
& '.\Tools\DarkwingResearch\vendor\mesen-agent\mesen-agent.exe' --help
```

Пример уже использованного интерфейса записи; output-каталог должен быть новым:

```powershell
python Tools/DarkwingResearch/record_reference.py --out Research/DarkwingDuck/reference/boot_probe_001 --frames 300 --screenshots-every 100 --timeout 45
```

Это инструментальный пробный сценарий, не приёмочный тест прохождения. Для игровых сценариев исполнитель должен создать корректные input schedules и проверяемые конечные условия. Большие выводы и PNG просматривать выборочно по задаче.

## 6. Инструментарий: основной путь и запасные варианты

| Инструмент | Использование | Ограничение |
|---|---|---|
| [mesen-agent](https://github.com/atonamy/mesen-agent) | Основной кандидат: headless execution, ввод, RAM, snapshots, кадры, звук, breakpoints, trace/CDL, Lua | Бинарник уже есть; точную семантику API/фазы зафиксировать |
| [CadEditor](https://github.com/spiiin/CadEditor) | Проверяемые форматы карт/объектов/анимаций, источник ориентиров | Не содержит полностью перенесённый gameplay; конфигурация не равна спецификации всех особых случаев |
| [GhidraNes](https://github.com/kylewlacy/GhidraNes) | Статический анализ 6502/MMC1, именование процедур/таблиц | Сам автоматически не восстанавливает весь control flow через bank switching; совместимость Ghidra/Java/заголовка проверяет исполнитель |
| [NESRecomp](https://github.com/mstan/nesrecomp) | Вспомогательная статическая рекомпиляция, анализ/дополнительная проверка | Поддержка MMC1 не доказывает готовность Darkwing Duck; сгенерированная библиотека не является полноценным портом |
| [romdev](https://github.com/monteslu/romdev) | Запасной MCP/CLI инструментарий анализа и экспериментов | Дополнительная инфраструктура; использовать при конкретной пользе, не устанавливать автоматически |
| [re-skill](https://github.com/vgrichina/re-skill) | Справочный опыт организации реверса | Его веб-направление не заменяет цель Unity; не требуется установка для исполнения этого задания |

Известные исследованные revision: CadEditor `b28316ddd9be4e79ac5e6adf89b7751df609d94e`; NESRecomp `2b4fcdb98c9e702be3fb365cfc645003c50b5fc1`; GhidraNes `4c419644c51c650d2507920e2f083d288056c6f0`; mesen-agent HEAD при чтении `dbc2c744ad52e1c89a8570d22d7fa92507ad4816` — **не считать его автоматически commit релизного бинарника**.

Публичный пригодный к использованию полный дизассемблированный проект Darkwing Duck в ходе подготовки не найден. Есть сообщение CaH4e3 о его наличии и видео процесса; это не основание строить план на доступности такого исходника. Старый диалог содержал гипотезы и примеры по другим играм; они не доказывают работоспособность на этом ROM.

## 7. Первоисточники для целевого чтения

- [Предыдущий диалог](https://chatgpt.com/share/6acb9982-6480-83ed-999d-cb33ee452ae5): контекст выбора между рекомпиляцией и восстановлением понятной архитектуры.
- [Конфигурация Darkwing Duck](https://github.com/spiiin/CadEditor/blob/b28316ddd9be4e79ac5e6adf89b7751df609d94e/CadEditor/settings_nes/darkwing_duck/Settings_DarkwingDuck-All.cs), [CapcomBase](https://github.com/spiiin/CadEditor/blob/b28316ddd9be4e79ac5e6adf89b7751df609d94e/CadEditor/shared_settings/CapcomBase.cs), [Utils](https://github.com/spiiin/CadEditor/blob/b28316ddd9be4e79ac5e6adf89b7751df609d94e/CadEditor/Utils.cs), [AnimEditor](https://github.com/spiiin/CadEditor/blob/b28316ddd9be4e79ac5e6adf89b7751df609d94e/CadEnemyEditor/AnimEditor.cs), [MapUtils](https://github.com/spiiin/CadEditor/blob/b28316ddd9be4e79ac5e6adf89b7751df609d94e/PluginMapEditor/MapUtils.cs): исходный код форматов.
- [Разбор уровней автором редактора](https://spiiin.github.io/blog/2258302958/), [разбор объектов и анимации](https://spiiin.github.io/blog/3874274906/): исторические ориентиры; некоторые адресные подписи менее точны, чем код и наблюдения ROM.
- [Lua API Mesen fork](https://github.com/atonamy/mesen-agent/blob/v0.1.0/UI/Debugger/Documentation/LuaDocumentation.json): сверять с используемым релизом.
- [GhidraNes: bank switching](https://github.com/kylewlacy/GhidraNes#bank-switching): ограничения статического анализа банков.
- [NES 2.0](https://www.nesdev.org/wiki/NES_2.0), [MMC1](https://www.nesdev.org/wiki/MMC1), [тайминги](https://www.nesdev.org/wiki/Cycle_reference_chart): аппаратные форматы и время.
- [PPU rendering](https://www.nesdev.org/wiki/PPU_rendering), [sprite priority](https://www.nesdev.org/wiki/PPU_sprite_priority), [OAM](https://www.nesdev.org/wiki/PPU_OAM): точное представление.
- [APU](https://www.nesdev.org/wiki/NES_APU), [APU Mixer](https://www.nesdev.org/wiki/APU_Mixer): звук и смешивание.
- [Руководство Capcom](https://www.nesfiles.com/NES/Darkwing_Duck/Darkwing_Duck.pdf): перечень механик и интерфейс; противоречия кнопок разрешать экспериментом.
- [Unity OnAudioFilterRead](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/MonoBehaviour.OnAudioFilterRead.html): требования аудиопотока. По графике в первую очередь смотреть локальные исходники URP 17.6.0 и API установленного Editor.
- [ZCode Goal Mode](https://zcode.z.ai/en/docs/goal), [ZCode project instructions](https://zcode.z.ai/en/docs/agents): `/goal` задаётся в чате; workspace `AGENTS.md` служит постоянной инструкцией. Не полагаться на автоматическое чтение вложенных документов без явного указания открыть их.

## 8. Стенд ручного прохождения (Tools/DarkwingResearch/stand)

Запуск: `Tools\DarkwingResearch\stand\Start-Playthrough.ps1` (то же самое `python Tools\DarkwingResearch\stand\stand.py record`).
Открывается штатный Mesen GUI, сам ROM загружается, и с этого же момента работает **штатный же рекордер Movies**: стенд передаёт Mesen опцию `--recordMovie=<сессия>\movie.mmo`, поэтому от человека не требуется ни одного пункта меню. Клавиши игрока 1: стрелки — D-pad, `Z`=A, `X`=B, `Enter`=Start, `LeftShift`=Select; в текущей конфигурации `AllowBackgroundInput=false`, то есть клавиши слышны только когда окно Mesen в фокусе (один клик по нему). Запись заканчивается закрытием окна.

Формат захвата (Mesen 2.1.1, проверено на релизном бинарнике и его исходниках): `.mmo` — это zip из `Input.txt` и `GameSettings.txt`; `MovieRecorder::RecordInput` пишет на каждый кадр эмуляции `"|" + device->GetTextState()` по всем устройствам консоли, поэтому строка выглядит как `|..|........|........` (2-символьное сервисное устройство, порт 1, порт 2). Геймпад — 8 символов в порядке собственного перечисления кнопок (`Core/NES/Input/NesController.h`: `Up = 0, Down, Left, Right, Start, Select, B, A`, литерал `"UDLRSsBA"`), `.` — кнопка не нажата. `input.bin` — это колонка порта 1, пересобранная в байт порядка `NesButtons.ToByte`; ни одного смещения по времени wall-clock в цепочке нет. `GameSettings.txt` внутри `.mmo` дополнительно записывает конфигурацию консоли, с которой шла запись, и она детерминирована: `nes.ramPowerOnState = AllZeros`, `randomizeMapperPowerOnState = false`, `randomizeCpuPpuAlignment = false`, `runAheadFrames = 0`. У mesen-agent нет флага `ramPowerOnState`, он при включении рандомизирует RAM, поэтому первые кадры и остаются непоказуемыми (`powerOnWindowLines`).

Конфигурация GUI стендом **не переписывается**: Mesen на выходе перезаписывает `settings.json` своим (проверено — правки `Preferences.ShowFrameCounter` и `Nes.Port2.Type`, внесённые снаружи, исчезают), поэтому `record` только читает и печатает фактические значения (клавиши, пауза в фоне, `EnableRewind`, `RecordFrom`). Файл лежит рядом с исполняемым: `Tools\DarkwingResearch\vendor\Mesen2\settings.json`; первое окно конфигурации на этой машине уже подтверждено.

Записи лежат в `Playthroughs\<UTC-метка>/` (в `.gitignore`, автоматически не удаляются): `meta.json` (хэши ROM и эмулятора, клавиши, соглашение о кадрах), `movie.mmo` (запись Mesen), `movie.json` (что из неё прочитано: кадры, поля, `nonPadPresses`), `input.bin` (по одному байту NES-геймпада на кадр эмулятора), `trace.csv` и `replayN/` (оригинал), `unity/` (порт), `diff/`.

```
python Tools\DarkwingResearch\stand\stand.py record                # живая сессия в Mesen GUI
python Tools\DarkwingResearch\stand\stand.py movie <file.mmo>      # конвертировать запись, сделанную через меню Movies
python Tools\DarkwingResearch\stand\stand.py replay <сессия> [--state file.mss] [--png 100,200]
python Tools\DarkwingResearch\stand\stand.py unity  <сессия> [--png 100,200]
python Tools\DarkwingResearch\stand\stand.py diff   <сессия> --png
python Tools\DarkwingResearch\stand\stand.py selftest <сессия>
```

Соглашение: одна строка movie-лога = один кадр NES = один `InputPolled` при повторе; пауза эмулятора кадров не добавляет. Точка старта сравнения — первый кадр после power-on reset, смещения не подбираются. Во время записи **нельзя** грузить состояния и перематывать (`EnableRewind=true`): `.mmo` от этого не портится структурно, но консоль под записанным треком прыгает, и обнаружить это постфактум нечем. При повторе (`stand.lua`) загрузка состояния помечается как `INVALID`, и `replay`/`unity` для такой сессии отказываются; автосохранение — нет (см. ниже).

Что доказано (2026-10-19):
- захват без ручных шагов: `Mesen.exe <rom> --recordMovie=<путь>` загружает ROM, играет титульную заставку и по закрытию окна пишет `.mmo` (943 и 693 строки `Input.txt` за 16 с и 12 с реального времени — ровно 60 кадров/с);
- сессия `Playthroughs/movie_probe` (трек из 943 кадров, старт от reset): `movie` → `replay --runs 2` → `unity` → `diff --png` проходят целиком, два независимых повтора байт-идентичны, кроме кадра 1-7 (Mesen рандомизирует work RAM и палитру при включении; измерено и вынесено в `powerOnWindowLines`, а не спрятано);
- **Человеческая запись** `Playthroughs/20261019T050329Z`: 24261 кадр/1424 диапазона, исходники не изменены. Полный маршрут теперь повторён в Core, штатной DarkwingMain и свежем Player: настоящий Quackerjack → победа/самолёт/счёт → карта count/mask1/1, 8600 очков, 5 жизней. f2033–24261 по всем пяти отображённым группам состояния 22229/22229; детали/ограничения — в ACCEPTANCE_REPORT. Старое заключение «порт блокируется x916» устарело. Повторный поиск пути/перебор прыжков не нужен.
- `emu.setInput` из Lua попадает в консоль так же, как собственный `--input` агента: кадр 100 из `m1_standing.mss` совпал с эталоном пиксель-в-пиксель (0 различающихся пикселей из 61440);
- расписание `--input` самого mesen-agent через `emu.getInput` НЕ видно (250 кадров с `right@100-120` записались нулями) — headless-самотест генерирует `input.bin` сам и гоняет его через режим replay;
- детектор: контрольная пара повторов молчит (1193 кадра), намеренная правка `x+1` в одном кадре найдена ровно на этом кадре (`selftest`);
- скорость headless-повтора измерена: mesen-agent считает ровно 60 кадров/с wall-clock и с полным отпечатком кадра, и с пустым Lua-callback (`--no-max-speed` не меняет ничего), поэтому `replay` сам подбирает `--timeout` под длину трека (~1.6 с на кадр) вместо жёстких 600 с;
- `INVALID` теперь ставит только загрузка состояния. Сохранение консоль не трогает: на длинном повторе mesen-agent сам пишет автосейв в изолированный `--home` (`SaveStates/<rom>_11.mss` примерно на 300-й секунде), и раньше это ложно аннулировало сессию;
- `trace.csv` обрывается на конце трека, а не на останове эмулятора: на длинных треках агент переполняет `--frames` (запрошено 24261 — выполнено 36046 при лимите 600 с и 29809/29810 при лимите 496 с, причина `timeout` вместо `frame_limit`, тогда как `--frames 600/120/6000` останавливались ровно на лимите). Поэтому `replay` сам считает лимит как `длина/60 × 1.15 + 30 с`, терпит `reason=timeout` с его же rc=4 и проверяет другое — что отпечатанных кадров ровно столько, сколько в треке; две записи одной сессии тогда всегда равной длины;
- `emu.exit()` в Lua Mesen 2.1.1 отсутствует (`attempt to call a nil value (field 'exit')`) — остановить прогон из скрипта нельзя, только досчитать трек;
- RESET-пролог f8 восстановлен; ранний Start его не обходит, NT/OAM/PAL f8–281 равны. Первые остатки текущего полного захвата: NT f282, PAL f293, OAM f463; G62/физическая NT-раскладка открыты. Суммы байтов — диагностика; строгие команды ниже используют байты/SHA-256, включая отрицательный контроль перестановки с прежней суммой.

Что НЕ проверено: не проверено, запрещает ли сам Mesen загрузку состояния во время записи фильма (`.mmo` от этого структурно не портится, но консоль под треком прыгает). Порядок 8-символьных полей (порт 1 раньше порта 2) теперь подтверждён на живой игре: в записи пользователя из 24261 кадра (10335 кадров с нажатыми кнопками, все восемь кнопок) `nonPadPresses` пуст, то есть все нажатия попадают ровно в 8-символьное поле порта 1.


Текущие минимальные данные и команды (без повторного захвата оригинала):
```
python Tools/DarkwingResearch/stand/stand.py states --original Temp/stand_original/replay1/buffers.bin --port Temp/stand_port_route/buffers.bin --start 2033
python Tools/DarkwingResearch/stand/stand.py buffers --original Temp/stand_original/replay1/buffers.bin --port Temp/stand_port_route/buffers.bin --start 8
python Tools/DarkwingResearch/stand/stand.py scene --session Playthroughs/20261019T050329Z --core Temp/stand_port_route --route Temp/stand_scene/route.json
python Tools/DarkwingResearch/stand/stand.py scene --session Playthroughs/20261019T050329Z --core Temp/stand_port_route --route Temp/stand_player/route.json
python Tools/DarkwingResearch/run_check.py --entry PhysCompare.Mission1Contracts --out Temp/m1_contracts
python Tools/DarkwingResearch/run_check.py --entry PhysCompare.Mission1Regressions --out Temp/m1_regressions
```
`buffers.bin`: один record = RAM2048 + физические NT2048 + OAM256 + PAL32 =4384 байта, pre-input poll1-based. `states` проверяет только явно отображённые группы; `buffers` — все байты соответствующих буферов. Снимки ReplayState содержат 4KiB PPU-address window; при сравнении с CIRAM берутся физические первые2KiB, при прямом Editor/Player сравнении проверен весь window. В `scene` номер снимка f означает Game.Frame=f−1 до обработки входа; `final` записан после потребления всех кадров. Смещений/переносов кнопок нет.

Свежий build: `Build/DarkwingDuck/DarkwingDuck.exe`, Development (replay включён условной компиляцией). Без аргумента игра интерактивна. Спецификации штатного повтора — `Temp/stand_scene/spec.json` и `Temp/stand_player/spec.json` (`captureAtStart=true`), обе разворачиваются в исходный input.bin. Editor: до Play установить SessionState `DarkwingReplay` на абсолютный путь scene spec; UseFlow=true, Game.Frame0. Player: `-darkwing-replay <absolute spec>`; окно/render/exit проверяет существующий `verify_player.ps1` с `-ReplayTimeoutSec 1200 -CompletedMission -ShowGameWindow`. После готового route требуется `stand.py scene`, один smoke не доказывает сравнение. Результаты, оставшиеся NT/RGB/audio/Suite расхождения — только в ACCEPTANCE_REPORT.
