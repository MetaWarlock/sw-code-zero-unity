# STATUS — Darkwing Duck NES → Unity port

Обновлён: 2026-10-11, этап M2 (реверс движка + скелет Unity).

## Текущее состояние

- [x] M0: воспроизводимость. Детерминизм доказан (с кадра 4 всё идентично; кадры 0–3 — мусор неинициализированной RAM). toolchain-lock.json, git-история.
- [x] M1: извлечение подготовительных таблиц + runtime-семантика: MMC1-модель исправлена и подтверждена; CHR-привязка миссии 1 доказана рендером (bg=банк 16, спрайты=банк 14, PRG $8000=5, фикс. 7); палитры/OAM/VRAM захватываются покадрово.
- [x] Инструменты: probe_run.py (захват MMC1/PPU/VRAM/RAM/OAM/ввод), disasm.py (6502+CDL), labels.json.
- [x] M2 движок: планировщик процессов, NMI, ввод, банк-очередь, слоты объектов — задокументированы в FACTS.md. Игровой цикл $C9AC.
- [x] M2 flow-агент: полный игровой поток, дескрипторы миров, прогресс ($38 tier / $69 битмаска), пауза/HUD → docs/game_flow.md.
- [ ] M2 аудио-агент: в работе (docs/audio_engine.md).
- [ ] M2 игрок-агент: в работе (docs/player_mechanics.md).
- [x] Unity: Darkwing.Core (детерминированный Step, NTSC 60.0988 Гц аккумулятор), DarkwingLauncher, сцена DarkwingMain через Editor API, компиляция чистая (unity-pipeline, редактор пользователя 76852).

## Следующий конкретный шаг

1. Собрать результаты игрок/аудио агентов → FACTS.md.
2. Экспортер extracted/*.json → Assets/Darkwing/Data (данные с провенансом).
3. M3: рендер фона миссии 1 из VRAM-верифицированных данных; сравнение с golden PNG.

## Актуальные команды

```powershell
# Эмулятор-пробы
python Tools/DarkwingResearch/probe_run.py --out Research/DarkwingDuck/reference/<name> --frames N --load-state <mss> --input "0:a@10-13" --screenshot-every 100
# Дизасм
python Tools/DarkwingResearch/disasm.py --bank 5 --cpu-base 0x8000 --start 0xOFF --length 0xLEN --out out.txt
# Unity через живой редактор (проект открыт пользователем, PID 76852)
unity command set_autotick --enable true --project-path "D:\Art\SW Code Zero Unity"
unity command recompile --project-path "D:\Art\SW Code Zero Unity"; unity command recompile_status --project-path ...
unity command run_script --file AgentScripts/X.cs --entry X.Main --project-path "D:\Art\SW Code Zero Unity"
```

## Правила

- ROM не менять. Golden-данные — только из оригинала. Сравнения с кадра ≥4.
- Не запускать второй Editor: проект открыт пользователем; изменения сцены — через run_script.
