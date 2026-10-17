# Отчёт о приёмке — порт Darkwing Duck (NES) → Unity

Дата: 2026-10-17 (смена 61). Ветка `main`, проект `D:\Art\SW Code Zero Unity`.
Все числа получены командами из раздела 9. Ни один эталон не заменён выводом порта и ни один
порог приёмки не ослаблен: расхождения перечислены как есть.

## 1. Идентификация источника

| что | значение |
|---|---|
| ROM | `Art/Darkwing Duck (USA).nes` |
| SHA-256 (замер 2026-10-17, `certutil`) | `1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00` |
| совпадение с задокументированным | да, побайтно (IMPLEMENTATION_BRIEF_RU.md, раздел «источник») |
| ROM в собранном игроке | отсутствует: `find Build -iname "*.nes"` → пусто |

Игровые данные извлечены из ROM побанково в `Assets/Darkwing/Data/*.bytes`
(`manifest.json` + `sha256-per-bank` в `Research/DarkwingDuck/COVERAGE.csv`), то есть в билде
лежат разобранные таблицы/паттерны, а не образцовой ROM-файл и не код эмулятора.

## 2. Что реально исполняется в собранном игроке

`Build/Windows/DarkwingDuck_Data/Managed/` содержит три сборки порта:
`Darkwing.Core.dll`, `Darkwing.Audio.dll`, `Darkwing.UnityApp.dll`
(asmdef'ы `Assets/Darkwing/Core|Audio|UnityApp/*.asmdef`). Это нативный редактируемый C#:
PPU/MMC1-модель, физика игрока, диспетчер OAM, декодер комнат, поведенческий диспетчер банка 6,
аудио-секвенсор — всё свои классы, без wrapper'а эмулятора.

## 3. Standalone-сборка (закрытый пункт приёмки)

| поле | значение |
|---|---|
| команда | `unity command build --target StandaloneWindows64 --outputPath "Build/Windows/DarkwingDuck.exe" --confirm true --project-path "D:/Art/SW Code Zero Unity"` |
| buildId | `build_1850d22ae797` |
| платформа | `StandaloneWindows64` (активный таргет редактора, build support установлен) |
| вывод | `D:/Art/SW Code Zero Unity/Build/Windows/DarkwingDuck.exe` |
| размер сборки | 106 445 494 Б (103 MiB на диске) |
| длительность | 38 487 мс |
| ошибки / предупреждения | 0 / 5 |
| SHA-256 `DarkwingDuck.exe` | `001aeebfb92718f8b49f0c0e0a03e313c54669552b08e952b2fee3ddd143683a` |
| сцены в билде | `Assets/Scenes/DarkwingMain.unity` (enabled), `SampleScene.unity` (disabled) |

Сборка запущена из живого редактора через pipeline-команду `build`, поэтому второй (batch-mode)
редактор не поднимался и конфликт с `Temp/UnityLockfile` не возник.

Предупреждения сборки (0 ошибок / 5 предупреждений). Все пять строк восстановлены дословно из
`Logs/Editor.log` (хвост ~70 МБ, та же редактор-сессия, что выполнила `build_1850d22ae797`):

| # | предупреждение | контекст |
|---|---|---|
| 1 | `Pipeline: No RuntimePipelineConfig asset found (Project Settings > Pipeline > Runtime). Pipeline will be disabled in Player builds.` | блокирует покадровое управление игроком извне (см. границу раздела 4) |
| 2 | `Assets\Darkwing\Core\EnemyBehaviors.cs(1676,21): warning CS1717: Assignment made to same variable; did you mean to assign something else?` | осознанный no-op `s.T480 = s.T480;` в `Riser` case 0 (зеркало $A431) |
| 3 | `Assets\Darkwing\Core\EnemyBehaviors.cs(1676,38): warning CS1717: Assignment made to same variable; did you mean to assign something else?` | то же, `s.T4B0 = s.T4B0;` |
| 4 | `Assets\Darkwing\Core\EnemyBehaviors.cs(1677,21): warning CS1717: Assignment made to same variable; did you mean to assign something else?` | то же, `s.YVelFrac = s.YVelFrac;` |
| 5 | `Assets\Darkwing\UnityApp\AudioOutFeed.cs(15,46): warning UAC1001: Field 'Source' type 'Darkwing.Audio.AudioSequencer' is skipped by serialization (missing the [Serializable] attribute).` | `Source` присваивается кодом (`DarkwingLauncher`), а не инспектором — сериализация не нужна |

Ни одно предупреждение не меняет поведение собранного игрока; #2–#4 оставлены как есть,
чтобы не трогать порт без конкретной необходимости.

## 4. Проверка собранного игрока

Скрипт `Tools/DarkwingResearch/verify_player.ps1` (запуск → два снимка окна → корректное закрытие):

| проверка | результат |
|---|---|
| процесс | PID 60672, жив ≥ 50 с, окно `SW Code Zero Unity`, rect 2560×1440 |
| цикл кадров идёт | WorkingSet 379 052 → 380 080 КБ; `TotalProcessorTime` 25 047 → 66 453 мс |
| кадр рисуется | снимок A: 10 различных цветов, 108 235 из 230 400 отсчётов не чёрные; снимок B: 107 890 |
| кадр меняется | SHA-256 A ≠ SHA-256 B (текст «PRESS START» в разных фазах анимации) |
| содержимое | титульный экран `$C792`: логотип, силуэт, спрайт утки, «PRESS START» |
| `Player.log` | 66 строк; движок 6000.6.5f1, D3D12, RTX 5090; исключений и скриптовых ошибок нет |
| шум в логе | 17 повторов `Shader 'Hidden/Universal Render Pipeline/Bloom' is not supported or has been stripped from the build` + `d3d12: failed to query info queue interface (0x80004002)` |
| завершение | по закрытию окна, чистое (`Input System … Shutdown`, `Cleanup mono`); crash-дампа нет (`Crashes/` отсутствует) |

Доказательства: `Docs/DarkwingPort/evidence/player_boot_a.png`,
`Docs/DarkwingPort/evidence/player_boot_b.png`.

Граница этого пункта: сверка покадровая и идёт в редакторе через harness (раздел 5); в собранном
игроке проверено «запускается, рисует, не падает, корректно закрывается», а не побайтовое равенство
кадра эталону. Отдельный открытая задача — `RuntimePipelineConfig` отсутствует, поэтому pipeline
в билде выключен и покадровые сверки из самого игрока невозможны (записано в GAPS).

## 5. Покадровые сверки с ROM (редактор, `PhysCompare.Suite`)

База: `Docs/DarkwingPort/evidence/suite_summary.txt` (25 строк, смена 58; побайтово воспроизведена
в сменах 59 и 60). 20 строк дают `RESULT: MATCH`, 5 расходятся — все пять перечислены ниже как есть.

| строка | RESULT | STRICT |
|---|---|---|
| walk / jump | MATCH 119/119 | расходится с f1 по `$10` (59/119) |
| b_attack | MATCH 170/170 | MATCH 170/170, ENEMIES MATCH 170/170 |
| hook | MATCH 298/298 | расходится с f3 по `$10` (149/298) |
| traverse | MATCH 599/599 | расходится с f1 по `$10` (547/599) |
| contact / m1route | MATCH 399/399 | MATCH 399/399 |
| hurt | MATCH 1056/1056 field-frames (16 полей × 66 кадров) | — |
| door | MATCH 1470/1470 field-frames (15 полей × 98 кадров) | — |
| worldseeds | 133/133 поля по 7 захватам | — |
| 00…06:fall (7 строк) | MATCH 371/371, кроме `05:fall` | `05:fall`: основной RESULT расходится с f683 (370/371); STRICT всех семи — расхождение с f329 по `$10` |
| longroute (m1) | MATCH 2499/2499 | `$25` расходится с f814 (2163/2499) |
| m2(w01) / m7(w06) | MATCH 1499/1499 | MATCH 1499/1499 |
| m3(w02) | f1314 (1313/1499) | `$25` с f50 (1131/1499) |
| m4(w03) | f198 (1498/1499) | `$25` с f190 (908/1499) |
| m5(w04) | f507 (1497/1499) | `$25` с f499 (1038/1499) |
| m6(w05) | f330 (1034/1499) | `$10` с f303 (977/1499) |

Прочитанные отдельно: `walk`/`jump`/`hook`/`traverse` и `00…06:fall` расходятся по полю `$10`
(маска колонок) — это окно стиджинга G62/G66, а не позиция игрока; позиция (`X`,`Y`) в этих
строках совпадает до конца захвата.

## 6. Экраны, progression, рендер

| блок | состояние | доказательство |
|---|---|---|
| boot → title | PASS frame-exact (смена 60) | `evidence/title_verify.txt`, `evidence/title_port.png`; nametable `$2000-$23FF` 0 расхождений, OAM 59/60 кадров, `$3F00` 59/60, `$2000/$2001=$90/$1E`, CHR 31/25 |
| брифинг/текст | PASS | `flow,textengine,AA00_dialog_and_C5F7_intro,gold_briefing_b0` |
| карта города | фон PASS,整体 render FAIL | `hud,map_screen,city_map_background` PASS (побайтово nametable + визуал); `city_map_render` FAIL — HELP-облака/курсор (G32.1, задача #7) |
| карточка миссии | PASS 160/160 по всем 7 мирам | смена 45.2, `$9636/$967A` blit |
| пауза | PASS frame-exact | `flow,pause,start` + `gold_pause_m1`/`gold_pause_resume` (hold 68/69, resume 78/79, OAM побайтно) |
| game over / continue-карточка | PASS | `flow,gameover,D46C_card_and_prompt,gold_gameover_f300` |
| двери/переходы `$E388` | PASS | `flow,doors,E388_section_loader,golden_98f` (1470/1470 field-frames) |
| миссия 1 от входа до выхода | PASS | `flow,level,traversal_m1,gold_traverse_m1` (599/599) |
| 7 побед → финал | переход есть, финал перенесён | `flow,progression,seven_wins_natural,$D838_branch_ending` = yes; тело финала `$D841…$D8D0` — смена 59 |
| враги/боссы | 47/47 поведений + 7/7 боссов перенесено | `enemies,dispatch,bank6_8000` = partial-pass; `enemies,dispatch,bank6_frame_exact(_sweep)` = pass (ENEMIES MATCH 599/599) |
| аудио | секвенсор + интеграция | `audio,sequencer,bank3_port` partial-pass, `audio,sfx,punch_3f` pass, `audio,integration,darkwing_game` pass; регистровый APU и WAV-сверка не закрыты |

## 7. Что НЕ закрыто (ограничения приёмки)

1. **G66** — вертикальный редрауз `$E7C6…$E935` перенесён функционально, но не структурно:
   порт перерисовывает весь ряд (`ReloadRow = LoadRow + StreamColumns`) вместо 8 колонок за проход,
   маски `$10/$11/$12` и окно атрибутов `$0780` не смоделированы. Это причина расхождений `$10`
   в разделе 5.
2. **G62** — фаза окна стиджинга зависит от позиции цикл-счётчика при входе в `$DE24`; без
   цикл-точного CPU (запрещён AGENTS.md) не переносится. Эффект не выходит за окно загрузки.
3. **G71** — титульный экран: один кадр из 60 не совпадает по OAM и счётчик `$97` 212 против 220.
4. **G69/G70** — хвост финала за `$D8D0` (читатели `$65`, ветка полёта `$F51B`) и отсутствие
   золотого захвата финала: резидентность фейда, длины `$FEFF`, момент `JSR $FF55(#$02)`
   покадрово не подтверждены.
5. **Secrets/bonus** — в `COVERAGE.csv` нет ни одной строки про бонус-раунды и секреты: не
   вытеснено, не перенесено, не сверено. Критерий AGENTS.md «secrets/bonus areas» не выполнен.
6. **Continue-экран** — перенесён только гейт `$5D`; ветка выбора темпа после continue не перенесена
   (нет захвата-потребителя).
7. **Боссы** — код перенесён, но покадровой золотой сверки боя нет (ни одного босс-захвата).
8. **Аудио** — регистровый APU-фронт и WAV-сверка не закрыты; известна RMS 0.44 расхождения.
9. **Bloom-варнинг** в игроке (раздел 4) — постпроцесс-шейдер вырезан из билда, лог спамится;
   на картинку порта не влияет, но требует либо отключения Volume-постпроцесса, либо
   Always Included Shaders.

## 8. Итог по критериям AGENTS.md («полное завершение»)

| критерий | статус |
|---|---|
| 7 миссий | перенесены и играбельны; покадровая позиция совпадает у m1/m2/m7 полностью, у m3…m6 расходится на 1–2 кадра и на f330 у m6 (см. раздел 5) |
| все боссы | перенесены (7/7), золотой сверки нет |
| секреты/бонус-зоны | **не выполнены** (пункт 7.5) |
| UI / progression / финал | титул, брифинг, карта (фон), карточка, пауза, game over, двери, 7 побед, финал `$D841…$D8D0` — перенесены; хвост финала и HELP-облака карты — нет |
| аудио | перенесено частично (секвенсор+SFX+интеграция), APU/WAV-сверка не закрыта |
| доказательства приёмки | есть: 25-строчный Suite, титул/пауза/карта/двери/контакт сверки, `Player.log`, снимки игрока |
| проверенная standalone-сборка | **выполнено** (разделы 3 и 4) |

Вывод: последний инфраструктурный пункт (сборка + запуск) закрыт с доказательствами. Полным
«дособрать игру» в смысле AGENTS.md результат пока не является — остаются пункты 7.1…7.9, из
которых содержательно самые дорогие G66 (структурный редрауз + маски), бонус-зоны и золотая
сверка боёв.

## 9. Как воспроизвести

```bash
# 0. редактор должен быть запущен для этого проекта (другой редактор может держать другой проект)
#    запуск: Tools/DarkwingResearch/launch_editor.ps1

# 1. идентификатор источника
certutil -hashfile "Art/Darkwing Duck (USA).nes" SHA256

# 2. сборка (из живого редактора, без второго batch-редактора)
unity command build --target StandaloneWindows64 \
  --outputPath "Build/Windows/DarkwingDuck.exe" --confirm true \
  --project-path "D:/Art/SW Code Zero Unity"
unity command build_status --project-path "D:/Art/SW Code Zero Unity" --format json

# 3. проверка игрока (запуск, два снимка, закрытие)
powershell -ExecutionPolicy Bypass -File Tools/DarkwingResearch/verify_player.ps1 \
  -Exe "D:\Art\SW Code Zero Unity\Build\Windows\DarkwingDuck.exe" \
  -PngA Docs\DarkwingPort\evidence\player_boot_a.png \
  -PngB Docs\DarkwingPort\evidence\player_boot_b.png

# 4. покадровый Suite (25 строк, пишется в Temp/suite_summary.txt)
unity recompile --project-path "D:/Art/SW Code Zero Unity"
unity command run_script --file AgentScripts/PhysCompare.cs --entry PhysCompare.Suite \
  --project-path "D:/Art/SW Code Zero Unity"
diff Temp/suite_summary.txt Docs/DarkwingPort/evidence/suite_summary.txt

# 5. титульный экран против gold_title2
unity command run_script --file AgentScripts/TitleVerify.cs --entry TitleVerify.Run \
  --project-path "D:/Art/SW Code Zero Unity"
```
