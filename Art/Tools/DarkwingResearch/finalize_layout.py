"""Supporting module for project_layout.py finalize (ZERO-2).

This is the one-shot migration manifest producer, not a separate CLI/launcher.
The existing project_layout entry point uses the bundled Python (lxml).
"""
from pathlib import Path
import hashlib
import json
import shutil
from lxml import etree

ROOT=next(p for p in Path(__file__).resolve().parents if (p/'ProjectSettings').is_dir())
NS='http://www.w3.org/1999/xhtml'
def element(tag,text=None,**attrs):
    e=etree.Element('{'+NS+'}'+tag,**attrs);e.text=text;return e
def load(relative): return etree.parse(str(ROOT/relative))
def save(tree,relative): tree.write(str(ROOT/relative),encoding='UTF-8',xml_declaration=True,pretty_print=True)
def digest(p): return hashlib.file_digest(p.open('rb'),'sha256').hexdigest()

def main():
    accepted = ROOT/'Art/Research/DarkwingDuck/reference/project-layout/evidence.json'
    if accepted.exists() and json.loads(accepted.read_text(encoding='utf-8')).get('humanResult') == 'PASS':
        raise ValueError('Migration already accepted. Edit canonical records in place; do not replace the human PASS with the initial AWAITING HUMAN template.')
    catalog='Art/Tools/DarkwingResearch/README.xhtml'
    tree=load(catalog);body=tree.find('{'+NS+'}body')
    for e in tree.iter():
        if e.text:
            e.text=e.text.replace('original/replay1 --port Art/Temp/check/core','original/replay1/buffers.bin --port Art/Temp/check/core/buffers.bin')
            e.text=e.text.replace('states --original A --port B','states --original A/buffers.bin --port B/buffers.bin')
            e.text=e.text.replace('buffers --original A --port B','buffers --original A/buffers.bin --port B/buffers.bin')
    if not tree.xpath('//*[@id="project-layout"]'):
        body.append(element('h2','ACTIVE: размещение проекта и строгая документация',id='project-layout'))
        body.append(element('p','Состояние 2026-10-29: собственные материалы находятся в Art; Unity импортирует игровой контент через локальный пакет su.learn.codezero → Art/Unity/CodeZero. Все собственные временные результаты — Art/Temp. Текущие задачи и человеческие проверки — только Beholder ZERO; плейтесты назначаются warlock (dima@iqseo.ru).'))
        body.append(element('p','Канонический entry point project_layout.py: проверка XHTML 1.0 Strict DTD, class/style, ссылок и якорей; verify-layout сверяет перенос с проверенным внешним snapshot. Команды relocate/paths/policy/convert/repair-links/shortcuts — явно ограниченные одноразовые операции ZERO-2, не регулярный workflow. policy отказывается повторно перезаписывать правила. finalize_layout.py сохраняет компактные свидетельства/обновляет каталог; не выполняет игру.'))
        body.append(element('p','Зависимости: обычный Python stdlib для переносов, установленный Git и существующий PowerShell/WScript для ярлыков; bundled Python с lxml для XHTML и finalize; bundled Node/marked только для разовой конверсии. Пути bundled runtime получать через load_workspace_dependencies. Локальная DTD и три entity-файла W3C сохранены в vendor/xhtml1 без изменений. Сырые captures/CSV/JSON/логи/лицензии и исходный shared_dialogue.html — данные, не поддерживаемая HTML-документация.'))
        body.append(element('pre','''# cwd: D:/Art/SW Code Zero Unity (или назначенный worktree)
# BUNDLED_PYTHON — Python из load_workspace_dependencies, без новой установки.
& $BUNDLED_PYTHON Art/Tools/DarkwingResearch/project_layout.py validate
& $BUNDLED_PYTHON Art/Tools/DarkwingResearch/project_layout.py selftest
python Art/Tools/DarkwingResearch/project_layout.py verify-layout --snapshot "D:/Art/SW Code Zero Unity - Git Backups/20261029-art-layout-before"
python Art/Tools/DarkwingResearch/preserve.py selftest
python Art/Tools/DarkwingResearch/preserve.py snapshot "D:/Art/SW Code Zero Unity - Git Backups/NEW_UNIQUE_NAME" --include Art/Build/UserStand --include Art/Build/ArtLayout --include Art/Build/DarkwingDuckMissions23
python Art/Tools/DarkwingResearch/preserve.py verify "D:/Art/SW Code Zero Unity - Git Backups/NEW_UNIQUE_NAME"
python Art/Tools/DarkwingResearch/preserve.py audit "D:/Art/SW Code Zero Unity - Git Backups/NEW_UNIQUE_NAME"'''))
        body.append(element('p','validate выводит JSON result/documents/issues; exit0 означает только XML/DTD/локальные ссылки и отсутствие custom classes, exit1 — конкретные нарушения. selftest: один положительный и пять отрицательных контролей. verify-layout не разрешает пропажу файлов и изменение immutable payload, перечисляет документные/исходные изменения отдельно; это не игровая эквивалентность. Полные точные команды игр сохраняются в существующих stand/build/run_check, их копии не создаются.'))
        body.append(element('p','Текущий стенд: Art/Build/UserStand; ярлыки Darkwing Port ведут на Art/Build/ArtLayout/DarkwingDuck.exe (build_a9780eeca272). Прежний Art/Build/DarkwingDuckMissions23 сохранён без изменения. Все ярлыки проверены по фактическим целям. Playtest2 сохраняет прежний ввод, новый output — Art/Temp/user-playtest2-layout. Старый GDD XSD архивирован как исторический: archive/gdd-markup-20261029/gdd.xsd; он не валидирует новый формат. validate.ps1 делегирует текущему общему validator.'))
        body.append(element('p','Проверка SOURCE/COST: .NET build и 7 city-строк 1924–1930 точны сохранённому до переноса tooling_before.csv. Проверка PORT-TO-PORT: полный ввод миссии1 24261, пять mapped-групп f2033–24261 по 22229 exact; источник сравнения — прежний PORT unity/buffers.bin, не оригинальная ROM. Suite остаётся прежним FAIL18/24 RESULT,8/24 STRICT,errors0, полный JSON byte-equivalent baseline. Известные отказы не ослаблены.'))
    save(tree,catalog)
    # Preserve the superseded schema as source, without leaving an active
    # permissive class/style validator next to the strict documentation.
    schema=ROOT/'Art/Docs/GameDesignDocument/gdd.xsd'
    archive=ROOT/'Art/Tools/DarkwingResearch/archive/gdd-markup-20261029/gdd.xsd'
    if schema.exists():
        archive.parent.mkdir(parents=True,exist_ok=True)
        if archive.exists(): raise FileExistsError(archive)
        schema.rename(archive)
    (ROOT/'Art/Docs/GameDesignDocument/style.css').write_text('@import url("../documentation.css");\n',encoding='utf-8')
    # Extend the existing storage/recovery lesson; no new session diary lesson.
    relative='Art/Docs/GameReconstruction/KNOWLEDGE_RU.xhtml'
    tree=load(relative)
    if not tree.xpath('//*[@id="art-layout-storage"]'):
        heading=next(e for e in tree.iter('{'+NS+'}h3') if (e.text or '').startswith('N03.'))
        note=element('p','Дополнение 2026-10-29 (принятое правило, проверено переносом этого проекта): отделять собственное хранилище от служебного Temp движка физическим корнем Art; временный вывод — Art/Temp, исходники — версионируемые Art/Tools и Art/AgentScripts. Для Unity импортируемые редактируемые файлы подключаются локальным UPM-пакетом из Art/Unity, с сохранением GUID; виртуальный Packages-путь не подходит для обычного File.ReadAllBytes. Повторяемый контроль: до переноса проверить внешний snapshot и реальные цели ярлыков; после — совпадение immutable bytes, API-импорт/сборку, действующие команды и новый snapshot/verify/audit. Ошибка: заменить один текстовый prefix без проверки абсолютных, относительных, компонентных и AssetDatabase-путей. Evidence — reference/project-layout; это опыт одного Unity-проекта, не универсальное утверждение для другого движка.',id='art-layout-storage')
        heading.addnext(note)
    save(tree,relative)
    # Factual status/coverage, with all actionable gates remaining in Beholder.
    relative='Art/Research/DarkwingDuck/STATUS.xhtml'
    tree=load(relative);body=tree.find('{'+NS+'}body')
    if not tree.xpath('//*[@id="art-layout-checks"]'):
        body.append(element('h2','Проверки переноса 2026-10-29',id='art-layout-checks'))
        body.append(element('p','HEAD c740f1c3 + унаследованные локальные правки + staged перенос Art; отдельного коммита нет. XML/Strict DTD/локальные ссылки/якоря: PASS (35 документов); validator controls1+5 PASS; Python17 PASS; preservation controls1+7 PASS. verify-layout:3435 byte-identical файлов, пропаж и изменённых immutable payload нет.'))
        body.append(element('p','Unity6000.6.5f1 PID300180 импортировал su.learn.codezero из Art/Unity/CodeZero; recompile PASS, GUID DarkwingMain и ссылки Launcher/Data сохранены. Fresh Development Player build_a9780eeca272:0errors/12warnings, normal boot/render/clean-exit PASS,17 прежних Bloom warnings, exceptions0. Input System автоматически добавил Preloaded Actions при build; единственное отличие source_before/source_after — ProjectSettings/ProjectSettings.asset. Исходное локальное preloadedAssets=[] восстановлено через PlayerSettings API, runtime C# не изменялся по поведению.'))
        body.append(element('p','Полный Core ввод24261 от reset завершён. PORT-TO-PORT с прежним сохранённым портом:5mapped-групп f2033–24261 по22229 exact. Это не original-to-port и не полный image/audio PASS. Suite JSON равен baseline:RESULT18/24,STRICT8/24,errors0,общийFAIL;16групп Mission1Contracts PASS. .NET cost build PASS;7city-строк1924–1930 byte-equivalent прежнему CSV.'))
        body.append(element('p','Плейтест текущей сборки: AWAITING HUMAN, задача ZERO-36 назначена warlock/dima@iqseo.ru; приёмка refactor остаётся в ZERO-2. Потерянные старые Temp-свидетельства явно отмечены, ZERO-35. Метаданные и проверенные отчёты: reference/project-layout/evidence.json. После этой записи запланирован additive snapshot20261029-art-layout-preserved; его фактические snapshot/verify/audit результаты фиксируются в ZERO-2 без изменения уже снятого дерева.'))
    save(tree,relative)
    relative='Art/Research/DarkwingDuck/FACTS.xhtml'
    tree=load(relative);body=tree.find('{'+NS+'}body')
    if not tree.xpath('//*[@id="art-layout-20261029"]'):
        body.insert(0,element('h2','Организация хранения 2026-10-29',id='art-layout-20261029'))
        body.insert(1,element('p','Собственные исходники/ресурсы/сцена физически перенесены в Art/Unity/CodeZero; AssetDatabase импортирует локальный пакет su.learn.codezero. Docs/Tools/Research/AgentScripts/Playthroughs/Build перенесены в Art; собственный Temp — Art/Temp. Данные ROM/ввод/captures и прежний playable сохранены без изменений байтов;3435 файлов exact по внешнему до-снимку, пропаж0. Runtime C# отличается от до-снимка только путями в комментариях. Документы — Strict XHTML/CSS без классов, задачи — Beholder ZERO, плейтесты — пользователь warlock. Набор проверок/границы — STATUS.xhtml и reference/project-layout/evidence.json; это организационный перенос, не новая игровая приёмка.'))
    save(tree,relative)
    import csv
    coverage=ROOT/'Art/Research/DarkwingDuck/COVERAGE.csv'
    if 'ArtLayout20261029' not in coverage.read_text(encoding='utf-8-sig'):
        with coverage.open('a',encoding='utf-8',newline='') as f:
            csv.writer(f).writerow(['tooling','shared','ArtLayout20261029','HEADc740f1c3+inherited_dirty+staged_layout','yes','yes','PASS_AUTOMATED_AWAITING_HUMAN','reference/project-layout/evidence.json; immutable bytes/GUID/import/build/smoke/XHTML/test controls verified; M1 port-to-port five groups22229exact, not original; Suite baseline FAIL18/24 RESULT8/24 STRICT unchanged; human gate ZERO-36 assigned warlock, no manual result'])
    # A factual report can describe limits; the work queue itself is elsewhere.
    relative='Art/Docs/DarkwingPort/ACCEPTANCE_REPORT.xhtml'
    tree=load(relative);body=tree.find('{'+NS+'}body')
    if not tree.xpath('//*[@id="historical-report"]'):
        body.insert(1,element('p','Исторический отчёт о названных в нём версиях. Текущая очередь и состояния приёмки — Beholder ZERO. После миграции 2026-10-29 10 старых ссылок на Temp оказались недоступны и явно помечены; ZERO-35 хранит этот gap. Новая сборка и новая XML-проверка не восстанавливают эти исторические свидетельства.',id='historical-report'))
    save(tree,relative)
    # Keep original data formats. Compact reports are durable; large recomputable
    # exports remain in Art/Temp and have preserved input/tool derivations.
    evidence=ROOT/'Art/Research/DarkwingDuck/reference/project-layout'
    evidence.mkdir(parents=True,exist_ok=True)
    sources=['layout-build/build_final.json','layout-build/source_before.json','layout-build/source_after.json','layout-build/binary_manifest.json','layout-build/commands.json','layout-player/report.json','layout-contracts/returned.txt','layout-suite/suite_result.json','layout-m1-port-comparison.json','layout-ownership.json','layout-xhtml-validation.json','layout-shortcuts.json']
    records=[]
    for name in sources:
        p=ROOT/'Art/Temp'/name
        if not p.exists(): raise FileNotFoundError(p)
        out=evidence/name
        out.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,out)
        records.append({'path':out.relative_to(ROOT).as_posix(),'bytes':out.stat().st_size,'sha256':digest(out)})
    metadata={'schema':1,'date':'2026-10-29','sourceHead':'c740f1c3ee7f0204b3a3aa804d26c78032b10c85','sourceState':'inherited dirty changes plus Art layout migration; no commit made','issue':'ZERO-2','humanGateIssue':'ZERO-36','humanResult':'AWAITING HUMAN','agent':'sh.codex@beholder.learn.su','human':'dima@iqseo.ru','build':'build_a9780eeca272','scene':'Packages/su.learn.codezero/Scenes/DarkwingMain.unity','buildSourceDifference':'only auto-added InputSystem preloadedAssets; inherited [] restored through Editor API after build; runtime C# behavior unchanged','beforeSnapshot':'D:/Art/SW Code Zero Unity - Git Backups/20261029-art-layout-before','afterSnapshot':'D:/Art/SW Code Zero Unity - Git Backups/20261029-art-layout-preserved','input':{'path':'Art/Playthroughs/20261019T050329Z/input.bin','bytes':24261,'sha256':digest(ROOT/'Art/Playthroughs/20261019T050329Z/input.bin'),'start':'recorded reset; poll1, pre-input snapshots; comparison f2033–24261','comparisonSource':'previous port Art/Playthroughs/20261019T050329Z/unity/buffers.bin','comparisonSourceSha256':digest(ROOT/'Art/Playthroughs/20261019T050329Z/unity/buffers.bin')},'commands':['python Art/Tools/DarkwingResearch/stand/stand.py unity Art/Playthroughs/20261019T050329Z --output Art/Temp/layout-m1-full --png 2033,2199,23000,24261 --timeout 300','python Art/Tools/DarkwingResearch/stand/stand.py states --original Art/Playthroughs/20261019T050329Z/unity/buffers.bin --port Art/Temp/layout-m1-full/buffers.bin --start 2033','python Art/Tools/DarkwingResearch/run_check.py --suite --out Art/Temp/layout-suite','python Art/Tools/DarkwingResearch/run_check.py --entry PhysCompare.Mission1Contracts --out Art/Temp/layout-contracts'],'records':records}
    (evidence/'evidence.json').write_text(json.dumps(metadata,ensure_ascii=False,indent=2),encoding='utf-8')
    return {'result':'PASS','evidenceFiles':len(records),'evidence':str(evidence)}
