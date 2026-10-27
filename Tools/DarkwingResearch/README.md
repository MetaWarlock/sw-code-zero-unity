# Каталог исследовательских инструментов

Единая точка входа для всех агентов. Проверено по исходникам 2026-10-27; это каталог возможностей и команд, не заявление о прохождении игры. Правила владения, хранения и резервирования — [AGENTS.md](../../AGENTS.md), урок о потере Temp — [N03](../../Docs/GameReconstruction/KNOWLEDGE_RU.md#n03-рабочий-каталог-среды-исполнения-уничтожает-невосстановимые-артефакты).

## Перед написанием скрипта

1. Найти нужную возможность в таблице и поиском по исходникам этого каталога и `AgentScripts/`. Прочитать текущие параметры/пример; не устанавливать инструмент заново ради нового диалога.
2. Доработать подходящий entry point: входная запись, диапазон, поля, банк/адрес и выход — параметры, а не копия под новый кадр/смену. Переиспользовать существующий запуск Mesen/Unity.
3. Если расширение не подходит, сначала записать конкретный пробел и причину в текущем STATUS, затем добавить один канонический инструмент и строку сюда. Узкий source-specific probe допустим, новый универсальный движок не требуется.
4. До передачи выполнить preservation gate AGENTS: исходники в Git/подготовленном source-коммите, необходимые ignored-данные с проверенным backup, полная команда не зависит от утрачиваемого кода в Temp. Размещение файла под Tools само по себе не означает, что файл сохранён в Git.

## Поддерживаемые точки входа

Команды ниже выполняются из корня **назначенного проекта/worktree**, не из этого каталога. `python`, `dotnet` и Unity CLI обозначают уже настроенные локальные runtime; не устанавливать новые зависимости без конкретной ошибки. Перед использованием старого инструмента в worktree проверить hard-coded пути. Таблица перечисляет основные entry points; отсутствие другого уже существующего файла здесь не разрешает его переписывать.

| Задача / область | Канонические исходники и зависимости | Интерфейс / выход / проверка и границы |
|---|---|---|
| Запись человека и обычное воспроизведение оригинала | [stand/Start-Playthrough.ps1](stand/Start-Playthrough.ps1), [stand/stand.py](stand/stand.py), существующий Mesen GUI | Запуск без параметров записывает в `Playthroughs/<session>`: `.mmo`, `input.bin`, meta/movie JSON. `-ReplayMovie` использует IPC/SingleInstance; запрещён в private parallel copies. Ключи и ограничения — исходник launcher/stand. Проверка: независимое встроенное movie-воспроизведение и сравнение с декодированным вводом; один запуск не доказывает валидность всей цепочки. |
| Оригинальный headless replay и детерминизм | [stand/stand.py](stand/stand.py), [stand/stand.lua.in](stand/stand.lua.in), установленный vendor/mesen-agent, ROM и session | `replay SESSION --runs 2 --output OUT`. Выход: replay1/2 с `buffers.bin`, trace, PNG по `--png` и replay-метаданными. Проверять обе длины, bytes/SHA, старт и исключения, не только exit. `selftest SESSION` — проверки чувствительности стенда; прочитать параметры до запуска, не направлять на единственные сохраняемые результаты. |
| Core-повтор того же ввода | [stand/stand.py](stand/stand.py), [AgentScripts/StandRoute.cs](../../AgentScripts/StandRoute.cs), действующий Editor/Unity CLI | `unity SESSION --output OUT [--png FRAMES] [--audio-state]`. Выход: mapped trace, buffers, изображения и спецификация replay. Поля и формат — stand/StandRoute; не называть Core-запуск проверкой интерактивного Player. |
| Сопоставление оригинала и порта / двух версий порта | [stand/stand.py](stand/stand.py) | `states --original A --port B --start N [--end N] [--audio]`; `buffers --original A --port B --start N`; `scene --session SESSION --core CORE --route ROUTE_JSON`. Явно указывать стороны, диапазон и scope; port-to-port PASS не равен original-to-port. `diff SESSION` помогает локализовать первое расхождение. |
| Suite, контракты и существующие C# probes | [run_check.py](run_check.py), [AgentScripts/PhysCompare.cs](../../AgentScripts/PhysCompare.cs), другие существующие AgentScripts, действующий Editor | `--entry ENTRY --out OUT`, либо `--file PATH --entry ENTRY --args JSON --out OUT`. `--suite` проверяет стандартную Suite: exit0 PASS,1 behavioral FAIL,2 ERROR. Без `--suite` успешный запуск не означает PASS возвращённого контракта. Сохраняет execution/stdout/stderr/returned; сверять scope и отрицательные контроли. |
| Сборка через действующий Editor | [build_player.py](build_player.py), Unity CLI | `--out EXE --evidence OUT [--development]`. BuildReport, commands, source/binary hashes. Для аргумента `-darkwing-replay` нужна сборка с включённым диагностическим replay-кодом; читать актуальные preprocessor guards. Проверка: build result/errors, затем короткий observable replay перед полным. Сборка не доказывает проходимость. |
| Standalone smoke и существующий route replay | [verify_player.ps1](verify_player.ps1), готовый EXE, при replay — существующая spec | `-Exe EXE -PngA PNG -PngB PNG -Report JSON [-Replay SPEC -ReplayTimeoutSec N -CompletedMission]`. Report `success`, final/exit и PNG. Smoke доказывает boot/render/exit; `CompletedMission` содержит проектные ожидания м1, не универсальную приёмку всех миров. Не повторять timeout вслепую: сначала доказать старт/прогресс и режим сборки. |
| Исследовательский запуск оригинала по расписанию кнопок | [probe_run.py](probe_run.py), [runtime_capture.lua.in](runtime_capture.lua.in), mesen-agent/ROM; узкий ранее существующий вариант [record_reference.py](record_reference.py) | `probe_run.py --out NEW_OUT --frames N --input 0:right@FIRST-LAST`; параметры capture/load-state смотрим в исходнике. Выход — существующий runtime capture/метаданные, без перезаписи NEW_OUT. RAM patches разрешены лишь как явно помеченные исследовательские fixtures, не доказательство непрерывного прохождения. Для задачи сначала выбрать один подходящий entry point, не копировать запуск. |
| Разбор измеренных фаз и OAM-cost | [analyze_frame_timing.py](analyze_frame_timing.py), [frame_timing.lua](frame_timing.lua), [oam_timing.lua](oam_timing.lua), [AgentScripts/OamTimingVerify.cs](../../AgentScripts/OamTimingVerify.cs), [AgentScripts/SchedulerVerify.cs](../../AgentScripts/SchedulerVerify.cs) | Анализ CSV: `TRACE --oam OAM_CSV --dependencies --focus FIRST LAST`. Контракты запускать через run_check. Измеренные исходные цены/commit clocks — oracle evidence; они не должны становиться runtime-входом. Форматы/ограничения — исходники, FACTS и handoff §8. |
| Аудиоисследование и сравнение | [audio_timing.lua](audio_timing.lua), [audio_probe.lua.in](audio_probe.lua.in), [compare_audio_port.py](compare_audio_port.py), существующие AudioPort entry points | До выбора прочитать исходник и [audio_engine](../../Research/DarkwingDuck/docs/audio_engine.md): различать driver-state/register comparison, PCM, порядок каналов и ручное прослушивание. Не подменять один verdict другим. |

## Примеры команд

Пути `OUT` выбирает исполнитель в назначенном worktree. В примерах `Temp/check` — **воспроизводимый промежуточный вывод**, не место единственного эталона или окончательной приёмки; до передачи нужные результаты сохраняются по правилам AGENTS.

```powershell
# Уже существующие human recordings; новую запись не требуется создавать ради примера.
python Tools/DarkwingResearch/stand/stand.py replay Playthroughs/20261019T212535Z --runs 2 --output Temp/check/original
python Tools/DarkwingResearch/stand/stand.py unity Playthroughs/20261019T212535Z --output Temp/check/core
python Tools/DarkwingResearch/stand/stand.py states --original Temp/check/original/replay1 --port Temp/check/core --start 8
python Tools/DarkwingResearch/stand/stand.py buffers --original Temp/check/original/replay1 --port Temp/check/core --start 8
python Tools/DarkwingResearch/run_check.py --suite --out Temp/check/suite
python Tools/DarkwingResearch/run_check.py --entry PhysCompare.Mission1Contracts --out Temp/check/contracts
python Tools/DarkwingResearch/build_player.py --out Build/DarkwingDuck/DarkwingDuck.exe --evidence Temp/check/build --development
```

Это примеры синтаксиса по текущим исходникам, не команды, выполненные при создании каталога, и не ожидаемый PASS города. Включение `--start 8` явно исключает исторически известное окно power-on именно этой записи; для другой записи границу надо установить отдельно. Перед параллельным запуском проверить владельца Editor и выхода. Существующий пользовательский стенд `Build/UserStand`/shortcuts обновляется на месте согласно AGENTS; этот каталог не создаёт новый стенд.

## ACTIVE: измерение цен и городских фаз

Канонические исходники: [cost/reached_body_aggregate.cs](cost/reached_body_aggregate.cs), [проект .NET 10](cost/reached_body_aggregate.csproj), [cost/city_cost_compare.py](cost/city_cost_compare.py), [упаковщик](cost/city_phase_load.py), [экспортёр](cost/city_phase_dump.py). Это доработанные восстановленные инструменты, не новый агрегатор. Проект `.csproj` явно разрешён в Git; `bin/obj` остаются игнорируемыми.

Входы: игровые Core/Audio и `Assets/Darkwing/Data`, `Playthroughs/20261019T212535Z/input.bin` (29 360 bytes, SHA проверяется кодом), original `replay1/buffers.bin`; сохранённые данные в `Research/DarkwingDuck/reference/cost-harness/data/`. `city_phase.pkl` — доверенный локальный pickle (не загружать произвольные чужие pickle). Данные и история восстановления идентифицированы в [evidence.json](../../Research/DarkwingDuck/reference/cost-harness/evidence.json). Выход всегда новый CSV внутри назначенного проекта; существующие CSV/buffer-каталоги не перезаписываются. Выход city — CSV цен по poll; clock — CSV часов и sibling `buffers.bin` (4384 bytes/poll); compare — диагностические дельты и опциональный JSON.

```powershell
# Из корня назначенного проекта. --project-root, --session, --reference-dir,
# --legacy-reference-dir, --output-root доступны у C# entry point; --reference у Python.
dotnet run --project Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release -- city 1924 6400 Temp/cost-harness/city.csv
python Tools/DarkwingResearch/cost/city_cost_compare.py Temp/cost-harness/city.csv --expect-json Research/DarkwingDuck/reference/cost-harness/data/baseline-before/cost-summary.json
dotnet run --project Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release -- clock native 6420 Temp/cost-harness/clock.csv
# Только если reduced CSV отсутствуют: новый выход, без перезаписи существующего.
python Tools/DarkwingResearch/cost/city_phase_dump.py Research/DarkwingDuck/reference/cost-harness/data/phases
# Обратная упаковка существующих full/reduced CSV в НОВЫЙ файл:
python Tools/DarkwingResearch/cost/city_phase_load.py Temp/cost-harness/repacked.pkl --idle Research/DarkwingDuck/reference/cost-harness/data/phases/city_idle_phase.csv --phases Research/DarkwingDuck/reference/cost-harness/data/phases/city_phases.csv
```

`city_cost_compare.py`: exit0 без `--expect-json` означает только успешный разбор; с ним — равенство диагностической сводки сохранённой, **не ROM MATCH**. Полный ожидаемый диапазон задают `--first/--last` (1924/6400); позиционный limit (6387) ограничивает расчёт дельт. Пропущенные/лишние/дублированные строки отклоняются. При смене диапазона указывать все границы. Сводку без усечения сохранять через `--json NEW_FILE`. Известные ненулевые дельты остаются в выводе.

Проверено при переносе: city CSV, native clock CSV и все 6420 buffer records побайтово равны запуску исходного восстановленного инструмента; pickle→CSV→pickle сохраняет обе структуры. Четыре отрицательных контроля отвергают пропуск/дубликат/лишнюю строку/изменённую цену. Проверки путей дополнительно отвергают перезапись, выход вне назначенного проекта, неверный clock mode и отсутствие legacy-эталона с exit2; запуск из другого cwd с явным --project-root даёт тот же первый CSV-record. Ошибки перехватываются CLI, а не оставляются аварийному завершению .NET. Native clock по-прежнему отказывает с poll1942 (`CAFC player`); f6388 **не исправлен**. `clock oracle` подставляет измеренные исходные цены только как fixture и не считается runtime/приёмкой. `census/slots/audiodiff/probe` сохранены, но в этой миграции отдельно **NOT RUN**. Режим `m1` требует утраченные `full_phases.csv`, `full_bank5.csv`, `actor_prefix.csv`, original buffers в `--legacy-reference-dir`: он выдаёт явную ошибку, не заменяет их новыми эталонами.

## ARCHIVED: исторические варианты

[archive/cost-recovery-20261026](archive/cost-recovery-20261026/README.md) — 27 сохранённых исходников старых probes/patch-процедур и исходный `.orig.cs`. Это исторические варианты с отмеченными отсутствующими зависимостями; активный городской блокер остаётся открытым. Полный прежний `Build/CostHarness` после проверки перенесён в существующий внешний backup-root как `20261027-costharness-original`; рабочих копий исходников в Build больше нет.

После полного закрытия блокера его узкие инструменты **архивируем, не удаляем**. Общие инструменты оставляем ACTIVE. Для записи ARCHIVED нужны причина, закрывшая ревизия/evidence, сохранённые зависимости и команда контрольного запуска; обновляем вызывающие пути. Вернувшийся блокер продолжает этот инструмент, а не порождает новый. Архив не используется как оправдание хранения единственных копий вне Git.

## ACTIVE: проверяемая сохранность

[preserve.py](preserve.py) — один локальный snapshot/verify/audit entry point, Python stdlib + Git. Создан для отсутствовавшей возможности; запуск игры, сборка и сравнение не дублируются. Из корня проекта:

```powershell
python Tools/DarkwingResearch/preserve.py selftest
python Tools/DarkwingResearch/preserve.py snapshot "D:/Art/SW Code Zero Unity - Git Backups/NEW_UNIQUE_NAME"
python Tools/DarkwingResearch/preserve.py verify "D:/Art/SW Code Zero Unity - Git Backups/NEW_UNIQUE_NAME"
python Tools/DarkwingResearch/preserve.py audit "D:/Art/SW Code Zero Unity - Git Backups/NEW_UNIQUE_NAME"
```

Snapshot сохраняет .vscode, Tools, AgentScripts, Assets, Packages/ProjectSettings, Docs/Research, Playthroughs, Art, AGENTS/.gitignore, текущий Temp и legacy Build/CostHarness при наличии; Git bundle `--all`, состояние индекса/патчи и dirty-исходники. Не копирует `.git`, caches, `bin/obj`, `node_modules`, UnityLockfile; не является архивом всех Unity builds или всего компьютера. Новый каталог вне checkout, без перезаписи/удаления/публикации; при ошибке остаётся явно незавершённый снимок. Выход `manifest.json` — список путей, размеров и SHA-256, версия/время; verify заново читает файлы. Exit0 PASS /1 найденные нарушения /2 ошибка. Для восстановления сначала проверить snapshot, клонировать bundle в НОВОЙ папке и восстановить дерево `files/` с сохранением относительных путей; dirty/staged различия дополнительно записаны в git-state.json. Не накладывать восстановление на живой checkout без отдельного решения. Snapshot содержит локальные ROM/данные и приватную историю: не публиковать его.

Audit проверяет хеши текущих долговечных файлов против снимка, пропажи, ignored/untracked исходники Tools/AgentScripts, скрипты в disposable-каталогах. `Library/PackageCache` отдельно учитывается как UPM-cache из версионируемых Packages/manifest+lock; произвольные другие скрипты Library не исключены. Сгенерированные исключения нельзя объявлять вручную без источника/процедуры. Проверка не доказывает работоспособность всех режимов, не восстанавливает утерянные данные и не защищает от отказа диска с обеими копиями. Самотест отвергает modified/missing/extra/incomplete backup и размещение backup внутри checkout. Snapshot до переноса: `20261027-tooling-before`; после переноса: `20261027-tooling-preserved`. Новая правка долговечных файлов требует нового снимка для чистого audit; старые снимки не заменять.

## Сохранность и формат записи инструмента

Для нового или изменённого инструмента обновить одну строку/секцию здесь: **назначение и scope; canonical path/entry point; входы и зависимости; команда и cwd; выход/схема и смысл exit/verdict; ограничения; короткий контроль**. Не повторять весь исходник или журнал сессии.

Канонические исходники/шаблоны → Tools/AgentScripts и source commit. Оригинальный ввод → существующий Playthroughs. Нужные captures/phase tables/baselines → установленное Research/reference-хранилище. Бинарники, obj/bin, scratch PNG/CSV → disposable output. Для ignored необходимых данных отдельно проверяются backup и компактные metadata: ROM/input hashes, settings/граница кадра, producer revision/command, размеры/количество записей и SHA-256. Сам факт Git-ignore ничего не резервирует. Внешнюю выгрузку без разрешения не выполнять.
