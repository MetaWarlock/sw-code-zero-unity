# STATUS — Darkwing Duck NES → Unity port

Обновлён: 2026-10-11, этап M0 (подготовка и воспроизводимость).

## Текущее состояние

- [x] Прочитаны AGENTS.md, IMPLEMENTATION_BRIEF_RU.md, RESEARCH_HANDOFF_RU.md, START_HERE_RU.md.
- [x] ROM подтверждён: SHA-256 `1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00`.
- [x] Python 3.14.2 работает; `Tools/DarkwingResearch/extract_rom.py` присутствует (подготовительное извлечение).
- [x] mesen-agent v0.1.0 работает (`Tools/DarkwingResearch/vendor/mesen-agent/mesen-agent.exe`).
- [x] Unity CLI `C:\Users\Warlock\AppData\Local\Unity\bin\unity.exe` доступен; редакторы 6000.4.7f1/6000.4.9f1/6000.6.0f1/6000.6.5f1 установлены; проект на 6000.6.5f1.
- [ ] Определение используемого редактора: подключён ли сейчас открытый Editor к проекту (проверить через `unity` CLI).
- [ ] Локальная история изменений (git init) — до изменения проекта.
- [ ] Детерминизм: два одинаковых запуска из одного состояния → одинаковые данные.
- [ ] toolchain-lock.json.

## Что доказано

См. FACTS.md. Пока только идентификация ROM и работоспособность инструментов.

## Следующий конкретный шаг

M0: git init, детерминизм-прогон (два запуска record_reference из одного snapshot, сравнение RAM/PNG), toolchain-lock.json. Затем M1: аудит подготовительного извлечения и план полного извлечения.

## Актуальные команды

```powershell
# Детерминизм-проба (записывает PNG/WAV/RAM из одного входа)
python Tools/DarkwingResearch/record_reference.py --out Research/DarkwingDuck/reference/<имя> --frames 300 --timeout 45
# Вызов эмулятора напрямую
./Tools/DarkwingResearch/vendor/mesen-agent/mesen-agent.exe "Art/Darkwing Duck (USA).nes" --help
```
