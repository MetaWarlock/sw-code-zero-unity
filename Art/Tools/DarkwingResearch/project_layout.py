"""Project-owned file layout and XHTML documentation maintenance (ZERO-2).

Run from the assigned Unity project root. This tool never executes gameplay,
deletes evidence, follows linked directories or edits Unity serialized assets.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())
OWNED = ('Docs', 'Research', 'Tools', 'AgentScripts', 'Playthroughs', 'Build')
TEXT = {'.py', '.ps1', '.cs', '.csproj', '.lua', '.in', '.json', '.jsonl', '.md', '.xhtml', '.html', '.css', '.csv', '.txt', '.js', '.cmd', '.bat'}
SKIP = {'vendor', 'bin', 'obj', '__pycache__', '.git', 'home'}

def sha(path):
    return hashlib.file_digest(path.open('rb'), 'sha256').hexdigest()

def checked(relative):
    p = ROOT / relative
    if p.is_symlink() or p.is_junction() or not p.resolve().is_relative_to(ROOT):
        raise ValueError('Unsafe path: ' + str(p))
    return p

def move(source, destination):
    a, b = checked(source), checked(destination)
    if not a.exists():
        return
    if b.exists():
        raise ValueError('Destination already exists: ' + str(b))
    b.parent.mkdir(parents=True, exist_ok=True)
    try:
        a.rename(b)
    except PermissionError:
        # Windows may hold a handle to the directory itself (Explorer/cwd).
        # Move disjoint children without terminating any unrelated process.
        if not a.is_dir():
            raise
        b.mkdir()
        for child in list(a.iterdir()):
            move(child.relative_to(ROOT), b.relative_to(ROOT) / child.name)

def relocate():
    # Preflight every root before making the first change.
    for name in OWNED:
        a, b = checked(name), checked('Art/' + name)
        if a.exists() and b.exists():
            raise ValueError('Conflicting roots: ' + name)
    for name in OWNED:
        move(name, 'Art/' + name)
    (ROOT / 'Art/Temp').mkdir(exist_ok=True)
    # Unity's own runtime directory remains Unity-owned. Never move its lock/jobs.
    for p in list((ROOT / 'Temp').iterdir()):
        if p.name in {'UnityLockfile', 'ProcessJobs'}:
            continue
        move('Temp/' + p.name, 'Art/Temp/' + p.name)
    for p in list(ROOT.glob('m1z_pit*_report.txt')) + [ROOT / 'trace.txt']:
        if p.exists():
            move(p.name, 'Art/Temp/root/' + p.name)
    return {'result': 'PASS', 'roots': ['Art/' + n for n in OWNED]}

def update_paths():
    changed = []
    # Only maintained sources and documents. Captures/recording bytes and
    # historical evidence metadata retain their original provenance paths.
    roots = [ROOT/'Art/Tools', ROOT/'Art/AgentScripts', ROOT/'Art/Docs', ROOT/'Art/Research/DarkwingDuck/docs', ROOT/'Art/Unity/CodeZero/Darkwing']
    prefixes = ('Tools', 'AgentScripts', 'Docs', 'Research', 'Playthroughs', 'Build', 'Temp')
    pattern = re.compile(r'(?<![\w/\\])(' + '|'.join(prefixes) + r')([/\\])')
    for base in roots:
        for p in base.rglob('*'):
            if not p.is_file() or p.suffix.lower() not in {'.py','.ps1','.cs','.csproj','.lua','.in','.md','.xhtml','.css','.js','.cmd','.bat'} or any(x in SKIP | {'archive','runs','Research','evidence'} for x in p.relative_to(base).parts):
                continue
            if p.name in {Path(__file__).name, 'LayoutMigration.cs'}:
                continue
            try:
                old = p.read_text(encoding='utf-8-sig')
            except UnicodeError:
                continue
            new = old
            for prefix in prefixes:
                for sep in ('/', '\\', '\\\\'):
                    project = str(ROOT).replace('\\', sep)
                    new = new.replace(project + sep + prefix + sep, project + sep + 'Art' + sep + prefix + sep)
            new = pattern.sub(lambda m: 'Art' + m[2] + m[0], new)
            for prefix in prefixes:
                for quote in ('"', "'"):
                    new = new.replace(quote+'/'+prefix+'/',quote+'/Art/'+prefix+'/')
            if p.suffix in {'.cs','.py','.ps1','.csproj','.md','.xhtml'}:
                lines=[]
                for line in new.splitlines(keepends=True):
                    dest = 'Packages/su.learn.codezero/Darkwing' if 'AssetDatabase.' in line else 'Art/Unity/CodeZero/Darkwing'
                    line=line.replace('Assets/Darkwing',dest)
                    line=line.replace('Assets/Scenes/DarkwingMain.unity','Packages/su.learn.codezero/Scenes/DarkwingMain.unity')
                    line=line.replace('..\\..\\..\\Assets\\Darkwing','..\\..\\..\\Unity\\CodeZero\\Darkwing')
                    lines.append(line)
                new=''.join(lines)
                new=new.replace('"Assets", "Darkwing", "Data"','"Art", "Unity", "CodeZero", "Darkwing", "Data"')
            # Moving Tools one level down changes project-root discovery.
            if p.suffix == '.py':
                new = re.sub(r'Path\(__file__\)\.resolve\(\)\.parents\[\d+\]',
                             "next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())", new)
                if p.is_relative_to(ROOT/'Art/Tools'):
                    for prefix in prefixes:
                        for quote in ('"', "'"):
                            new = re.sub(r'\b(ROOT|root)\s*/\s*'+re.escape(quote+prefix+quote),
                                         lambda m: m[1]+' / '+quote+'Art/'+prefix+quote, new)
            if new != old:
                p.write_text(new, encoding='utf-8', newline='\n')
                changed.append(p.relative_to(ROOT).as_posix())
    return {'result': 'PASS', 'changed': changed}

def policy():
    if (ROOT/'Art/AGENTS.xhtml').exists():
        raise ValueError('Policy migration already completed. Edit canonical Art/AGENTS.xhtml in place.')
    tracker = 'https://beholder.learn.su/workbench/beholder/tracker/6ae2823cf8f3281e9196c7ab/issues'
    rules = (ROOT/'AGENTS.md').read_text(encoding='utf-8-sig')
    # Preserve all existing acceptance/ownership rules, with explicit new policy
    # taking precedence over the former four-file task workflow.
    for name in OWNED + ('Temp',):
        rules = re.sub(r'(?<![\w/\\])'+name+r'([/\\])', lambda m: 'Art'+m[1]+m[0], rules)
    rules = rules.replace('.md', '.xhtml')
    rules = rules.replace('Update the four progress files with findings, remaining gaps and the next concrete action.',
        'Keep factual verification/provenance in STATUS.xhtml, FACTS.xhtml and COVERAGE.csv. Maintain all actionable work, priorities, blockers, assignments and next steps exclusively in Beholder; do not recreate a file-based backlog.')
    new = '''# Правила проекта SW Code Zero

## Обязательная организация работы — решение пользователя 2026-10-29

- Каноническое место абсолютно всех собственных файлов проекта — `Art/`: `Art/Docs`, `Art/Tools`, `Art/AgentScripts`, `Art/Research`, `Art/Playthroughs`, `Art/Build` и `Art/Unity/CodeZero`. Все собственные временные файлы — только `Art/Temp`. Корневой Unity `Temp` очищается Unity и не используется нашими инструментами. `Art/Temp` тоже не заменяет версионирование исходников и отдельную резервную копию эталонов.
- Игровые исходники/ресурсы/сцена хранятся физически в локальном пакете `Art/Unity/CodeZero`, подключённом как `su.learn.codezero`. Путь AssetDatabase — `Packages/su.learn.codezero/…`; физический путь для Python/.NET — `Art/Unity/CodeZero/…`. Не создавать дубли или junction в Assets. Unity-managed каталоги Assets/Library/Packages/ProjectSettings/UserSettings/Logs и автоматически создаваемые IDE-файлы остаются служебной оболочкой Unity. Минимальные корневые AGENTS.md, .gitignore и конфигурация загрузчиков — только необходимые точки подключения, а не место документов/инструментов. Проверенные внешние резервные копии остаются за пределами проекта по preservation gate.
- Вся поддерживаемая документация — UTF-8 XHTML 1.0 Strict, корректный XML в namespace `http://www.w3.org/1999/xhtml`, с внешним общим `Art/Docs/documentation.css`. Разметка стандартными тегами h1–h6/p/ul/ol/dl/table/pre/code/blockquote и ссылками. Запрещены `class`, встроенный `style`, script, обработчики событий, произвольные теги и data-атрибуты. CSS оформляет существующие семантические элементы, структуру и стандартные атрибуты. `id` разрешён только для якорей и уникален. Проверять XML, локальную Strict DTD, ссылки и визуальное чтение. Новые .md/.html документы не создавать. Корневой AGENTS.md — лишь автоматически читаемый указатель на этот XHTML. Машиночитаемые CSV/JSON, сырой дизассемблерный вывод, оригинальные captures/логи/входы и неизменённые лицензии зависимостей остаются своими форматами: это данные/свидетельства, не параллельные документы.
- Все текущие задачи, приоритеты, исполнители, blockers, следующие действия и человеческие запросы ведутся только в [Beholder ZERO](TRACKER). MCP `mcp__beholder` — основной доступ: сначала найти существующие задачи, читать/обновлять их, затем создавать отсутствующие. Текстовые документы содержат факты, спецификации, команды, проверенные результаты и ссылки на трекер; не дублируют очередь задач. После подтверждённой записи в трекере удалять перенесённый текст задачи из документов, сохраняя факты и provenance. GAPS/MILESTONES — только указатели на трекер. История не теряется: Git и проверенный внешний snapshot.
- Каждый агент работает под собственной заранее назначенной учётной записью. Проверить MCP get_user_profile/list_employees перед назначением задач; не приписывать работу другому агенту и не использовать пользователя человека. В текущем подключении агент: **Шнырь Codex**, `sh.codex@beholder.learn.su`, Person `6ae28336f8f3281e9196c8d9`. Другому агенту нужна его отдельная назначенная identity; не выдумывать email/пароль и не считать простую запись Person полноценным пользователем. При отсутствии нужного подключения запросить его у человека. Эта политика сама по себе не разрешает запуск подагентов.
- После каждой крупной итерации получать актуальный 5-часовой лимит через get_usage_limits. Найти окно `windowDurationMins=300`, остаток = 100 − usedPercent. Если остаток **меньше 20%**, немедленно прекратить всю работу, включая автоматические продолжения и подагентов, сообщить значение и ждать новой команды пользователя. Сброс окна, таймер или молчание не являются командой продолжить; reset credits автоматически не использовать. Проверки лимита и остановку фиксировать в текущей задаче. Нельзя завершать goal как выполненный из-за лимита. Если API недоступен, указать, что остаток неизвестен, не выдумывать значение.

Ниже сохранены прежние правила качества и сохранности. В вопросах путей, формата документов и ведения задач новые пункты выше имеют приоритет. Исторические имена путей в доказательствах не переписывать как будто они существовали в старой ревизии.

'''.replace('TRACKER',tracker)
    (ROOT/'Art/AGENTS.source.md').write_text(new+rules,encoding='utf-8')
    (ROOT/'AGENTS.md').write_text('# SW Code Zero — entry point\n\nПеред любой работой прочитать [полные правила](Art/AGENTS.xhtml). Канонические материалы — Art/, временные — Art/Temp/. Задачи — только Beholder ZERO. После крупной итерации проверить 5-часовой лимит; при остатке <20% остановиться до команды пользователя.\n',encoding='utf-8')
    gap = ROOT/'Art/Research/DarkwingDuck/GAPS.md'
    # Even narrowly closed historical rows may contain outstanding actions;
    # transfer their complete text to the appropriate tracker issues first.
    gap.write_text('# Указатель расхождений\n\nДействующая очередь полностью перенесена в [Beholder ZERO]('+tracker+'). Здесь больше не ведутся задачи. Источники переноса и IDs сохранены в [метаданных](reference/layout-migration.json).\n\nПроверенные факты и ограничения — [FACTS](FACTS.xhtml), [матрица покрытия](COVERAGE.csv).\n',encoding='utf-8')
    (ROOT/'Art/Docs/DarkwingPort/MILESTONES.md').write_text('# Этапы приёмки\n\nЕдинственный актуальный список этапов, статусов и обязательств — [Beholder ZERO]('+tracker+'), этапы ZERO-3–ZERO-11. Локальная очередь удалена после проверки записи в трекере.\n\nПостоянные критерии — [техническое задание](IMPLEMENTATION_BRIEF_RU.xhtml), свидетельства — [факты](../../Research/DarkwingDuck/FACTS.xhtml) и [покрытие](../../Research/DarkwingDuck/COVERAGE.csv). История исходной очереди сохранена во внешнем snapshot и Git.\n',encoding='utf-8')
    (ROOT/'Art/Research/DarkwingDuck/STATUS.md').write_text('# Состояние проверенного checkpoint\n\n2026-10-29. База до организационного рефакторинга: `c740f1c3ee7f0204b3a3aa804d26c78032b10c85` + унаследованные незакоммиченные изменения, сохранённые в проверенном внешнем снимке. Игровая приёмка этим переносом не изменена.\n\nОчередь, следующие действия и текущие gates — только [Beholder ZERO]('+tracker+'). Подробный исходный STATUS с версиями и непогашенными обязательствами перенесён в ZERO-33, техническая передача — ZERO-32; рефакторинг — ZERO-2. Здесь сохраняются только факты проверок.\n\n## Сохранность до переноса\n\nsnapshot/verify/audit — PASS. Snapshot: `D:/Art/SW Code Zero Unity - Git Backups/20261029-art-layout-before`; 3573 файла, 2155814477 bytes; manifest SHA-256 `cf01b8d2a789f5e4af5cc70cafa571d685c60b1c3c72ce32608e9a67740f9bf9`. Включены исходные записи, Build/UserStand и фактический playable Build/DarkwingDuckMissions23.\n\nСписок перенесённых задач и provenance — [layout-migration.json](reference/layout-migration.json). Свидетельства поведения — [FACTS](FACTS.xhtml), [COVERAGE.csv](COVERAGE.csv).\n',encoding='utf-8')
    handoff = ROOT/'Art/Docs/DarkwingPort/RESEARCH_HANDOFF_RU.md'
    text = handoff.read_text(encoding='utf-8-sig')
    pos = text.find('### Подключение времени')
    if pos >= 0:
        text = text[:pos]+'## Текущая работа\n\nТехнические планы и незавершённые действия перенесены в Beholder ZERO-12/ZERO-13/ZERO-32; исходный журнал gates — ZERO-33. Актуальная очередь: [трекер]('+tracker+').\n'
    text = re.sub(r'^\*\*Resume .*?(?=\n\n)', '', text, count=1, flags=re.M|re.S)
    text = text.replace('очередь — [MILESTONES](MILESTONES.md)', 'очередь — [Beholder ZERO]('+tracker+')')
    handoff.write_text(text,encoding='utf-8')
    brief=ROOT/'Art/Docs/DarkwingPort/IMPLEMENTATION_BRIEF_RU.md'
    text=brief.read_text(encoding='utf-8-sig')
    start=text.find('Вести четыре компактных файла')
    end=text.find('После каждой существенной итерации',start)
    if start>=0 and end>=0:
        text=text[:start]+'Все текущие задачи, их статусы, следующие действия, назначения и blockers вести только в [Beholder ZERO]('+tracker+'). В Art/Research/DarkwingDuck/ сохраняются STATUS.xhtml и FACTS.xhtml с фактическими результатами и COVERAGE.csv с матрицей свидетельств; GAPS.xhtml и MILESTONES.xhtml ссылаются на трекер и не дублируют очередь. Правила путей/XHTML/лимита — Art/AGENTS.xhtml.\n\n'+text[end:]
    brief.write_text(text,encoding='utf-8')
    return {'result':'PASS','policy':'Art/AGENTS.xhtml','tracker':'ZERO','taskTextRemoved':True}

def convert(node, modules, python_packages):
    if python_packages:
        sys.path.insert(0, str(python_packages))
    from lxml import etree, html
    ns = 'http://www.w3.org/1999/xhtml'
    css = ROOT / 'Art/Docs/documentation.css'
    docs = []
    for base in (ROOT/'Art/Docs', ROOT/'Art/Research', ROOT/'Art/Tools'):
        docs.extend(p for p in base.rglob('*.md') if not any(x in SKIP for x in p.relative_to(base).parts))
    rules = ROOT / 'Art/AGENTS.source.md'
    if rules.exists():
        docs.append(rules)
    existing = [p for p in (ROOT/'Art').rglob('*.xhtml')
                if not any(x in SKIP | {'Build','Temp'} for x in p.relative_to(ROOT/'Art').parts)]
    dtd = etree.DTD(str(ROOT/'Art/Tools/DarkwingResearch/vendor/xhtml1/xhtml1-strict.dtd'))
    attributes = {e.name: {a.name for a in e.iterattributes()} for e in dtd.iterelements()}
    convert_js = "const fs=require('fs');const{pathToFileURL}=require('url');import(pathToFileURL(process.argv[1]+'/marked/lib/marked.esm.js')).then(m=>process.stdout.write(m.marked.parse(fs.readFileSync(0,'utf8'))));"
    converted = []
    for p in [*docs, *existing]:
        raw = p.read_text(encoding='utf-8-sig')
        if p.suffix == '.md':
            body = subprocess.check_output([str(node), '-e', convert_js, str(modules)], input=raw.encode()).decode()
            fragments = html.fragment_fromstring(body, create_parent='div')
        else:
            tree = html.fromstring(raw.encode('utf-8'))
            fragments = tree.find('body')
            if fragments is None:
                raise ValueError('Missing body: ' + str(p))
        for e in list(fragments.iter()):
            if not isinstance(e.tag, str):
                continue
            if e.tag in {'script', 'style'}:
                e.drop_tree()
                continue
            if e.tag in {'main','nav','section','article','header','footer','aside','figure','figcaption','details','summary'}:
                e.tag = 'div'
            if e.tag in {'box','row','grid'}:
                e.tag = 'div'
            if e.tag == 'divider':
                e.tag = 'hr'
            if e.tag == 'link':
                e.tag = 'a' if e.get('href') else 'span'
            if e.tag == 'title':
                e.tag = 'strong'
            if e.tag == 'table' and any(c.tag not in {'caption','col','colgroup','thead','tbody','tfoot','tr'} for c in e):
                e.tag = 'div'
            if e.tag not in attributes:
                e.tag = 'span'
            if e.tag == 'del':
                pass
            for a in list(e.attrib):
                if a in {'class','style','role'} or a.startswith(('data-','on','aria-')) or a not in attributes[e.tag]:
                    del e.attrib[a]
            if e.tag == 'img' and e.get('alt') is None:
                e.set('alt','')
            for attr in ('href','src'):
                if attr in e.attrib and not re.match(r'^[a-z]+:', e.attrib[attr], re.I):
                    e.attrib[attr] = re.sub(r'\.md(?=($|#))', '.xhtml', e.attrib[attr])
        ids = set()
        for e in fragments.iter():
            if isinstance(e.tag, str) and re.fullmatch('h[1-6]', e.tag) and not e.get('id'):
                slug = re.sub(r'[^\w\- ]', '', ''.join(e.itertext()).lower()).replace(' ', '-')
                slug = re.sub('-+', '-', slug).strip('-') or 'heading'
                if not re.match(r'[A-Za-z_\u0080-\uffff]', slug):
                    slug = 'h-' + slug
                candidate, n = slug, 1
                while candidate in ids:
                    n += 1
                    candidate = slug + '-' + str(n)
                e.set('id', candidate)
                ids.add(candidate)
        title = next((''.join(e.itertext()) for e in fragments.iter() if e.tag == 'h1'), p.stem)
        target = (ROOT/'Art/AGENTS.xhtml') if p == rules else p.with_suffix('.xhtml')
        root = etree.Element('{'+ns+'}html', nsmap={None:ns})
        root.set('{http://www.w3.org/XML/1998/namespace}lang','ru')
        root.set('lang','ru')
        head = etree.SubElement(root, '{'+ns+'}head')
        etree.SubElement(head, '{'+ns+'}meta', {'http-equiv':'Content-Type','content':'application/xhtml+xml; charset=UTF-8'})
        etree.SubElement(head, '{'+ns+'}title').text = title
        etree.SubElement(head, '{'+ns+'}link', {'rel':'stylesheet','type':'text/css','href':os.path.relpath(css,target.parent).replace('\\','/')})
        body = etree.SubElement(root,'{'+ns+'}body')
        body.text = fragments.text
        for e in list(fragments):
            body.append(e)
        for e in body.iter():
            if isinstance(e.tag,str) and not e.tag.startswith('{'):
                e.tag = '{'+ns+'}'+e.tag
        target.write_bytes(etree.tostring(root,encoding='UTF-8',xml_declaration=True,pretty_print=True,
                          doctype='<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Strict//EN" "https://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd">'))
        etree.parse(str(target))
        if p.suffix == '.md':
            p.unlink()  # Replacement exists and XML parsing passed; original is externally snapshotted.
        converted.append(target.relative_to(ROOT).as_posix())
    return {'result':'PASS','converted':converted}

def validate(python_packages, scope=None):
    if python_packages:
        sys.path.insert(0,str(python_packages))
    from lxml import etree
    from urllib.parse import unquote
    dtd=etree.DTD(str(ROOT/'Art/Tools/DarkwingResearch/vendor/xhtml1/xhtml1-strict.dtd'))
    errors=[]
    docs=[]
    candidates=[scope] if scope and scope.is_file() else (scope or ROOT/'Art').rglob('*.xhtml')
    for p in candidates:
        if not scope and any(x in SKIP or x in {'Temp','Build'} for x in p.relative_to(ROOT/'Art').parts):
            continue
        docs.append(p)
        try:
            tree=etree.parse(str(p),etree.XMLParser(resolve_entities=False,no_network=True))
            root=tree.getroot()
            if not dtd.validate(tree):
                errors.extend(str(p.relative_to(ROOT))+': '+str(e) for e in dtd.error_log)
            if root.tag!='{http://www.w3.org/1999/xhtml}html':
                errors.append(str(p)+': XHTML namespace/root')
            for e in root.iter():
                if not isinstance(e.tag,str): continue
                if 'class' in e.attrib or 'style' in e.attrib:
                    errors.append(str(p)+': class/style')
                for attr in ('href','src'):
                    value=e.get(attr,'')
                    if not value or re.match(r'^[a-z]+:|^//',value,re.I): continue
                    dest=value.split('#')[0].split('?')[0]
                    if '%' in dest:
                        from urllib.parse import unquote
                        dest=unquote(dest)
                    linked=p if not dest else p.parent/dest
                    if not linked.exists():
                        errors.append(str(p.relative_to(ROOT))+': missing '+value)
                    elif '#' in value and linked.suffix=='.xhtml':
                        fragment=unquote(value.split('#',1)[1])
                        linked_tree=tree if linked==p else etree.parse(str(linked))
                        if fragment and not linked_tree.xpath('//*[@id=$id]',id=fragment):
                            errors.append(str(p.relative_to(ROOT))+': missing anchor '+value)
        except etree.XMLSyntaxError as exc:
            errors.append(str(p)+': '+str(exc))
    return {'result':'FAIL' if errors else 'PASS','documents':len(docs),'issues':errors}

def selftest():
    import tempfile
    source=(ROOT/'Art/index.xhtml').read_text(encoding='utf-8')
    # Test the actual strict validator. Missing/extra structure, forbidden classes,
    # broken files and anchors must all fail, even if the XML is well formed.
    cases=[source, source.replace('<body>','<body class="custom">'),
           source.replace('</body>',''), source.replace('<h1>','<unknown>').replace('</h1>','</unknown>'),
           source.replace('href="AGENTS.xhtml"','href="missing-evidence.json"'),
           source.replace('href="AGENTS.xhtml"','href="AGENTS.xhtml#missing-anchor"')]
    with tempfile.TemporaryDirectory(dir=ROOT/'Art/Temp',prefix='xhtml-validation-') as name:
        p=Path(name)/'check.xhtml'
        # Put a known-good document at the project root so relative links resolve;
        # all fixture files are instead in Art/Temp with absolute relative paths.
        for i,text in enumerate(cases):
            text=re.sub(r'(href|src)="([^"#]+)"',lambda m: m[0] if re.match(r'[a-z]+:',m[2],re.I)
                        else m[1]+'="'+os.path.relpath(ROOT/'Art'/m[2],p.parent).replace('\\','/')+'"',text)
            p.write_text(text,encoding='utf-8')
            actual=validate(None,p)['result']
            expected='PASS' if i==0 else 'FAIL'
            if actual!=expected: raise AssertionError(f'case {i}: {actual} != {expected}')
    return {'result':'PASS','positive':1,'negative':5}

def repair_links():
    from lxml import etree
    from urllib.parse import unquote
    ns={'h':'http://www.w3.org/1999/xhtml'}
    repaired=[]
    unavailable=[]
    for p in (ROOT/'Art').rglob('*.xhtml'):
        if any(x in SKIP | {'Temp','Build'} for x in p.relative_to(ROOT/'Art').parts): continue
        tree=etree.parse(str(p))
        changed=False
        for e in list(tree.xpath('//*[@href or @src]')):
            for attr in ('href','src'):
                v=e.get(attr,'')
                if not v or re.match(r'^[a-z]+:|^//|^#',v,re.I): continue
                dest=unquote(v.split('#')[0].split('?')[0])
                if (p.parent/dest).exists(): continue
                if '../../Assets/Darkwing/' in v:
                    e.set(attr,v.replace('../../Assets/Darkwing/','../../Unity/CodeZero/Darkwing/'))
                    repaired.append(v); changed=True; continue
                if p.name=='ACCEPTANCE_REPORT.xhtml' and '../../Temp/' in v:
                    # Do not replace unavailable historical evidence with current output.
                    e.tag='{'+ns['h']+'}span'
                    e.attrib.pop(attr,None)
                    e.text='[ИСТОРИЧЕСКОЕ СВИДЕТЕЛЬСТВО НЕДОСТУПНО: '+v+'] '+(e.text or '')
                    unavailable.append(v); changed=True; continue
                prefix=p.stem.split('_')[0]
                meta=p.parent/(prefix+'_tree.json')
                if meta.exists():
                    data=json.loads(meta.read_text(encoding='utf-8-sig'))
                    m=re.search(r'/repos/(.+)/git/trees/([^/]+)$',data.get('url',''))
                    original=re.sub(r'\.xhtml(?=($|#))','.md',v).lstrip('./')
                    local=p.parent/(prefix+'_'+original.split('#')[0].replace('/','_'))
                    if local.exists():
                        e.set(attr,local.name+('#'+v.split('#',1)[1] if '#' in v else ''))
                    elif m:
                        repo,rev=m.groups()
                        if attr=='src': e.set(attr,'https://raw.githubusercontent.com/'+repo+'/'+rev+'/'+original)
                        else: e.set(attr,'https://github.com/'+repo+'/blob/'+rev+'/'+original)
                    else: continue
                    repaired.append(v); changed=True
        if changed:
            tree.write(str(p),encoding='UTF-8',xml_declaration=True,pretty_print=True)
    return {'result':'PASS','repaired':repaired,'explicitlyUnavailableHistoricalEvidence':unavailable}

def shortcuts():
    code = r'''
$ErrorActionPreference='Stop'
$layoutRoot=$args[0]
$standRoot=Join-Path $layoutRoot 'Art/Build/UserStand'
$exe=Join-Path $layoutRoot 'Art/Build/ArtLayout/DarkwingDuck.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Fresh verified build is missing' }
$shell=New-Object -ComObject WScript.Shell
$result=@()
foreach ($p in Get-ChildItem -LiteralPath $standRoot -Filter *.lnk) {
  $link=$shell.CreateShortcut($p.FullName)
  foreach ($field in @('TargetPath','Arguments','WorkingDirectory')) {
    $value=$link.$field
    foreach ($folder in @('Tools','Playthroughs','Build')) {
      $old=Join-Path $layoutRoot $folder
      $new=Join-Path $layoutRoot ('Art/'+$folder)
      $value=$value.Replace($old,$new)
    }
    $link.$field=$value
  }
  if ($p.Name -like 'Darkwing Port*') {
    $link.TargetPath=$exe
    $link.WorkingDirectory=Split-Path $exe
  }
  $link.Save()
  if (-not (Test-Path -LiteralPath $link.TargetPath)) { throw ('Missing target '+$link.TargetPath) }
  $result += [pscustomobject]@{name=$p.Name;target=$link.TargetPath;arguments=$link.Arguments;workingDirectory=$link.WorkingDirectory}
}
$result | ConvertTo-Json -Depth 3
'''
    import base64
    code=code.replace('$layoutRoot=$args[0]', "$layoutRoot='"+str(ROOT).replace("'","''")+"'")
    code='[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n'+code
    encoded=base64.b64encode(code.encode('utf-16-le')).decode('ascii')
    run=subprocess.run(['powershell','-NoProfile','-NonInteractive','-EncodedCommand',encoded],capture_output=True,text=True,encoding='utf-8')
    if run.returncode:
        raise RuntimeError(run.stderr)
    spec=ROOT/'Art/Build/UserStand/Playtest2.json'
    data=json.loads(spec.read_text(encoding='utf-8-sig'))
    data['output']=(ROOT/'Art/Temp/user-playtest2-layout').as_posix()
    spec.write_text(json.dumps(data,ensure_ascii=False),encoding='utf-8')
    return {'result':'PASS','shortcuts':json.loads(run.stdout),'replayOutput':data['output']}

def verify_layout(snapshot):
    if snapshot is None:
        raise ValueError('--snapshot is required')
    manifest=json.loads((snapshot/'manifest.json').read_text(encoding='utf-8'))
    missing=[]; changed=[]; exact=0; docs=0
    payload_errors=[]
    for stored,entry in manifest['files'].items():
        if not stored.startswith('files/'): continue
        old=stored[6:]
        new=old
        if old=='Assets/Darkwing.meta': new='Art/Unity/CodeZero/Darkwing.meta'
        elif old=='Docs/GameDesignDocument/gdd.xsd': new='Art/Tools/DarkwingResearch/archive/gdd-markup-20261029/gdd.xsd'
        elif old.startswith('Assets/Darkwing/'): new='Art/Unity/CodeZero/Darkwing/'+old[len('Assets/Darkwing/'):]
        elif old.startswith('Assets/Scenes/DarkwingMain.unity'): new='Art/Unity/CodeZero/Scenes/'+old.split('/')[-1]
        elif old.split('/')[0] in OWNED: new='Art/'+old
        elif old.startswith('Temp/') and not old.startswith('Temp/ProcessJobs/'): new='Art/'+old
        path=ROOT/new
        if path.suffix=='.md' and old!='AGENTS.md':
            path=path.with_suffix('.xhtml'); docs+=1
        if not path.exists():
            missing.append(new); continue
        if sha(path)==entry['sha256']:
            exact+=1; continue
        changed.append(new)
        # Reference data, game assets and the prior actual playable executable
        # are immutable across this organizational migration.
        immutable=(old.startswith(('Playthroughs/','Research/','Tools/DarkwingResearch/runs/','Tools/Research/'))
                   and not old.endswith(('.md','COVERAGE.csv')))
        immutable=immutable or (old.startswith('Assets/Darkwing/') and path.suffix!='.cs')
        immutable=immutable or old.startswith('Build/DarkwingDuckMissions23/') or old.endswith('.nes')
        if immutable: payload_errors.append(new)
    return {'result':'FAIL' if missing or payload_errors else 'PASS','byteIdenticalFiles':exact,
            'convertedDocuments':docs,'changedMaintainedFiles':changed,'missing':missing,
            'changedImmutablePayload':payload_errors,'beforeManifestSha256':sha(snapshot/'manifest.json')}

def references():
    """Update maintained source citations to documents actually converted to XHTML.

    AGENTS.md remains the mandatory root loader. Historical archives, vendor
    files, raw captures and provenance metadata are never rewritten.
    """
    names={p.stem for p in (ROOT/'Art').rglob('*.xhtml')
           if not any(x in SKIP | {'Temp','Build','evidence'} for x in p.relative_to(ROOT/'Art').parts)}-{'AGENTS'}
    pattern=re.compile(r'([A-Za-z_0-9-]+)\.md(?!\w)')
    changes=[]
    for base in (ROOT/'Art/Tools',ROOT/'Art/AgentScripts',ROOT/'Art/Unity'):
        for p in base.rglob('*'):
            if not p.is_file() or p.suffix not in {'.cs','.py','.ps1'}: continue
            if any(x in SKIP | {'archive','runs','Research'} for x in p.relative_to(base).parts): continue
            if p.name in {'project_layout.py','finalize_layout.py'}: continue
            old=p.read_text(encoding='utf-8-sig')
            new=pattern.sub(lambda m: m[1]+'.xhtml' if m[1] in names else m[0],old)
            if new==old: continue
            runtime=p.is_relative_to(ROOT/'Art/Unity')
            if runtime:
                for a,b in zip(old.splitlines(),new.splitlines()):
                    if a!=b and not a.lstrip().startswith(('//','/*','*')):
                        raise ValueError('Refuse a non-comment runtime change: '+str(p))
            before=sha(p)
            p.write_text(new,encoding='utf-8',newline='\n')
            changes.append({'path':p.relative_to(ROOT).as_posix(),'beforeSha256':before,
                            'afterSha256':sha(p),'runtimeCommentsOnly':runtime})
    return {'result':'PASS','changes':changes,'runtimeBehaviorChanged':False}

def main():
    ap=argparse.ArgumentParser(__doc__)
    ap.add_argument('command',choices=['relocate','paths','policy','convert','repair-links','shortcuts','validate','selftest','verify-layout','references','finalize'])
    ap.add_argument('--node',type=Path)
    ap.add_argument('--node-modules',type=Path)
    ap.add_argument('--python-packages',type=Path)
    ap.add_argument('--path',type=Path,help='Validate a specific XHTML file or directory')
    ap.add_argument('--snapshot',type=Path,help='Verified external pre-migration snapshot')
    a=ap.parse_args()
    if a.command=='relocate': result=relocate()
    elif a.command=='paths': result=update_paths()
    elif a.command=='policy': result=policy()
    elif a.command=='repair-links': result=repair_links()
    elif a.command=='shortcuts': result=shortcuts()
    elif a.command=='selftest': result=selftest()
    elif a.command=='verify-layout': result=verify_layout(a.snapshot)
    elif a.command=='references': result=references()
    elif a.command=='finalize':
        from finalize_layout import main as finalize
        result=finalize()
    elif a.command=='convert': result=convert(a.node,a.node_modules,a.python_packages)
    else: result=validate(a.python_packages,a.path.resolve() if a.path else None)
    print(json.dumps(result,ensure_ascii=False,indent=2))
    return 0 if result['result']=='PASS' else 1

if __name__=='__main__':
    sys.exit(main())
