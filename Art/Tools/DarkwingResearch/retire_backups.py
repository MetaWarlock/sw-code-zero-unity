"""Prepare a verified, lossless retirement of explicitly selected backup folders.

Never deletes. Keep 2-3 full snapshots, consolidate other file contents by SHA256,
and consolidate Git bundle heads into one verified bundle. Native OS removal is
a separate, user-authorized operation after this command reports PASS.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import zipfile


def info(path):
    before = path.stat()
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError('Changed during read: ' + str(path))
    return {'bytes': before.st_size, 'sha256': h.hexdigest()}


def child(root, name):
    path = root / name
    if path.is_symlink() or path.is_junction() or path.resolve().parent != root:
        raise ValueError('Expected a direct, unlinked child: ' + name)
    return path


def walk(root):
    found = []
    for base, dirs, names in os.walk(root):
        for name in dirs + names:
            path = Path(base) / name
            if path.is_symlink() or path.is_junction():
                raise ValueError('Linked entry: ' + str(path))
        found.extend(Path(base) / name for name in sorted(names))
    return sorted(found)


def run_git(repo, *args):
    result = subprocess.run(['git', '-C', str(repo), *args], capture_output=True, check=True)
    return result.stdout.decode('utf-8').strip()


def is_bundle(path):
    if path.suffix != '.bundle':
        return False
    with path.open('rb') as stream:
        return stream.read(16).startswith(b'# v')


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def prepare(root, keep, retire, output, repo):
    root, repo = root.resolve(), repo.resolve()
    if len(keep) not in (2, 3) or not retire or set(keep) & set(retire):
        raise ValueError('Need 2-3 keepers and disjoint explicit retirement names')
    if len(set(keep + retire)) != len(keep + retire):
        raise ValueError('Duplicate input name')
    dest = child(root, output)
    previous = None
    if dest.exists():
        meta = dest / 'catalog.json'
        if meta.is_file():
            previous = json.loads(meta.read_text(encoding='utf-8'))
        if not previous or previous.get('complete') or previous.get('keep') != keep or previous.get('retire') != retire:
            raise ValueError('Output must be new or the same incomplete preparation')
    if output in keep + retire:
        raise ValueError('Output must be separate')
    for name in keep + retire:
        if not child(root, name).is_dir():
            raise ValueError('Not a backup directory: ' + name)
    dest.mkdir(exist_ok=True)
    objects = dest / 'objects'
    objects.mkdir(exist_ok=True)
    covered, snapshots, bundles, protected_refs = {}, {}, [], set()
    result = {'schema': 1, 'complete': False, 'root': str(root), 'keep': keep,
              'retire': retire, 'snapshots': snapshots, 'objects': previous['objects'] if previous else {}, 'gitBundles': []}
    write_json(dest / 'catalog.json', result)

    def save_content(path, value):
        sha = value['sha256']
        if sha not in covered:
            target = objects / sha
            if not target.exists():
                os.link(path, target)  # same volume; independent name survives original removal
            if info(target) != value:
                raise ValueError('Hardlink verification failed: ' + str(path))
            covered[sha] = {'path': str(target.relative_to(root)), **value}
            result['objects'][sha] = covered[sha]
        return covered[sha]['path']

    with ThreadPoolExecutor(max_workers=4) as pool:
        for name in keep + retire:
            source = child(root, name)
            files = walk(source)
            values = list(pool.map(info, files))
            entries = {p.relative_to(source).as_posix(): v for p, v in zip(files, values)}
            meta = source / 'manifest.json'
            if meta.exists():
                manifest = json.loads(meta.read_text(encoding='utf-8'))
                if manifest.get('schema') == 1 and 'complete' in manifest and not manifest['complete']:
                    raise ValueError('Incomplete snapshot: ' + name)
                if manifest.get('complete') and isinstance(manifest.get('files'), dict):
                    expected = manifest['files']
                    if set(entries) != set(expected) | {'manifest.json'}:
                        raise ValueError('Unexpected/missing files: ' + name)
                    for relative, value in expected.items():
                        if entries[relative] != value:
                            raise ValueError('Manifest mismatch: ' + name + '/' + relative)
                elif name in keep:
                    raise ValueError('Keeper lacks completed manifest: ' + name)
            elif name in keep:
                raise ValueError('Keeper lacks completed manifest: ' + name)
            snapshots[name] = {'bytes': sum(v['bytes'] for v in values), 'files': entries}
            if name in keep:
                for path, value in zip(files, values):
                    covered.setdefault(value['sha256'], {'path': str(path.relative_to(root)), **value})
            else:
                for path, value in zip(files, values):
                    if is_bundle(path):
                        bundles.append((path, value))
                        continue
                    if name == '_archive' and path.name == '20261031-ZERO-74-qoder-mm5sound-final.zip':
                        # Unpack the previous one-off pilot; no replacement ZIPs are made.
                        with zipfile.ZipFile(path) as archive:
                            members = archive.infolist()
                            if len({m.filename for m in members}) != len(members):
                                raise ValueError('Duplicate ZIP members')
                            zip_entries = {}
                            for member in members:
                                if member.is_dir():
                                    continue
                                h = hashlib.sha256()
                                pending = dest / 'zip-member.pending'
                                with archive.open(member) as src, pending.open('xb') as target:
                                    for block in iter(lambda: src.read(1024 * 1024), b''):
                                        h.update(block)
                                        target.write(block)
                                item = {'bytes': pending.stat().st_size, 'sha256': h.hexdigest()}
                                zip_entries[member.filename] = item
                                if member.filename.endswith('.bundle'):
                                    bundle_path = dest / ('import-' + item['sha256'] + '.bundle')
                                    pending.rename(bundle_path)
                                    bundles.append((bundle_path, item))
                                else:
                                    save_content(pending, item)
                                    pending.unlink()  # only this tool's temporary member
                            zipped_manifest = json.loads(archive.read('manifest.json'))
                            if not zipped_manifest.get('complete') or any(zip_entries[k] != v for k, v in zipped_manifest['files'].items()):
                                raise ValueError('Invalid pilot ZIP manifest')
                            snapshots[name].setdefault('expandedZips', {})[path.name] = zip_entries
                        continue
                    save_content(path, value)
            print(json.dumps({'verified': name, 'files': len(files), 'bytes': snapshots[name]['bytes'],
                              'uniqueObjects': len(result['objects'])}), flush=True)
            write_json(dest / 'catalog.json', result)

    for path, value in bundles:
        heads = run_git(repo, 'bundle', 'list-heads', str(path)).splitlines()
        for line in heads:
            oid, ref = line.split(' ', 1)
            target = 'refs/archive/zero81/' + oid
            if oid not in protected_refs:
                exists = subprocess.run(['git', '-C', str(repo), 'cat-file', '-e', oid], capture_output=True).returncode == 0
                if not exists:
                    run_git(repo, 'fetch', '--no-write-fetch-head', str(path), ref + ':' + target)
                else:
                    run_git(repo, 'update-ref', target, oid)
                protected_refs.add(oid)
        result['gitBundles'].append({'source': str(path.relative_to(root)), **value, 'heads': heads})
    history = dest / 'git-history.bundle'
    if protected_refs:
        run_git(repo, 'bundle', 'create', str(history), *['refs/archive/zero81/' + oid for oid in sorted(protected_refs)])
        run_git(repo, 'bundle', 'verify', str(history))
        actual = {line.split(' ', 1)[0] for line in run_git(repo, 'bundle', 'list-heads', str(history)).splitlines()}
        if actual != protected_refs:
            raise ValueError('Git history head coverage mismatch')
        result['history'] = info(history)
        result['history']['heads'] = len(protected_refs)
    for path, _ in bundles:
        if path.parent == dest:
            path.unlink()  # only extracted temporary import bundles, after consolidation
    for sha, value in result['objects'].items():
        if info(root / value['path']) != {k: value[k] for k in ('bytes', 'sha256')}:
            raise ValueError('Final preserved object mismatch: ' + sha)
    result['covered'] = covered
    result['retiredBytes'] = sum(snapshots[n]['bytes'] for n in retire)
    result['uniqueBytes'] = sum(v['bytes'] for v in result['objects'].values())
    result['complete'] = True
    write_json(dest / 'catalog.json', result)
    print(json.dumps({'result': 'PASS', 'catalog': str(dest / 'catalog.json'),
                      'retireDirectories': len(retire), 'retiredBytes': result['retiredBytes'],
                      'uniqueBytes': result['uniqueBytes'], 'history': result.get('history')}), flush=True)
    return result


def load_catalog(path):
    data = json.loads(path.read_text(encoding='utf-8'))
    if not data.get('complete'):
        raise ValueError('Catalog preparation did not complete')
    root = Path(data['root']).resolve()
    if not path.resolve().is_relative_to(root):
        raise ValueError('Catalog must be inside its original backup root')
    for value in data['covered'].values():
        candidate = root / value['path']
        if not candidate.resolve().is_relative_to(root) or candidate.is_symlink():
            raise ValueError('Unsafe covered path')
    return data, root


def verify_catalog(path, repo):
    data, root = load_catalog(path)
    locations = data['covered']
    with ThreadPoolExecutor(max_workers=4) as pool:
        values = list(locations.values())
        actual = pool.map(info, [root / v['path'] for v in values])
        for wanted, value in zip(values, actual):
            if value != {k: wanted[k] for k in ('bytes', 'sha256')}:
                raise ValueError('Stored file mismatch: ' + wanted['path'])
    replaced = {v['sha256'] for v in data['gitBundles']}
    for name, snapshot in data['snapshots'].items():
        zips = snapshot.get('expandedZips', {})
        for relative, value in snapshot['files'].items():
            if value['sha256'] not in locations and value['sha256'] not in replaced and Path(relative).name not in zips:
                raise ValueError('Uncovered original file: ' + name + '/' + relative)
        for members in zips.values():
            if any(v['sha256'] not in locations and v['sha256'] not in replaced for v in members.values()):
                raise ValueError('Uncovered pilot ZIP member')
    if data.get('history'):
        history = path.parent / 'git-history.bundle'
        if info(history) != {k: data['history'][k] for k in ('bytes', 'sha256')}:
            raise ValueError('Consolidated Git bundle changed')
        run_git(repo, 'bundle', 'verify', str(history))
        actual = {line.split(' ', 1)[0] for line in run_git(repo, 'bundle', 'list-heads', str(history)).splitlines()}
        expected = {line.split(' ', 1)[0] for bundle in data['gitBundles'] for line in bundle['heads']}
        if actual != expected:
            raise ValueError('Consolidated Git head mismatch')
    return {'result': 'PASS', 'coveredContents': len(locations), 'uniqueBytes': data['uniqueBytes'],
            'remainingRetiredFolders': [n for n in data['retire'] if (root / n).exists()]}


def restore_file(catalog, snapshot, relative, destination):
    data, root = load_catalog(catalog)
    if destination.exists() or destination.is_symlink():
        raise ValueError('Recovery destination must be new')
    for name in data['keep'] + data['retire'] + [catalog.parent.name]:
        if destination.resolve().is_relative_to(root / name):
            raise ValueError('Do not write inside retained or retired evidence')
    wanted = data['snapshots'][snapshot]['files'][relative]
    source = data['covered'].get(wanted['sha256'])
    if source is None:
        raise ValueError('This container was consolidated; use Git history or expandedZips metadata')
    path = root / source['path']
    if info(path) != wanted:
        raise ValueError('Recovery source mismatch')
    destination.parent.mkdir(parents=True, exist_ok=True)
    with path.open('rb') as reader, destination.open('xb') as writer:
        shutil.copyfileobj(reader, writer)
    if info(destination) != wanted:
        raise ValueError('Restored file verification failed')
    return {'result': 'PASS', 'destination': str(destination), **wanted}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest='command', required=True)
    a = sub.add_parser('prepare')
    a.add_argument('root', type=Path)
    a.add_argument('--keep', nargs='+', required=True)
    a.add_argument('--retire', nargs='+', required=True)
    a.add_argument('--output', required=True)
    a.add_argument('--repo', type=Path, required=True)
    a = sub.add_parser('verify')
    a.add_argument('catalog', type=Path)
    a.add_argument('--repo', type=Path, required=True)
    a = sub.add_parser('restore-file')
    a.add_argument('catalog', type=Path)
    a.add_argument('snapshot')
    a.add_argument('relative')
    a.add_argument('destination', type=Path)
    args = p.parse_args()
    if args.command == 'prepare':
        prepare(args.root, args.keep, args.retire, args.output, args.repo)
    elif args.command == 'verify':
        print(json.dumps(verify_catalog(args.catalog, args.repo)))
    else:
        print(json.dumps(restore_file(args.catalog, args.snapshot, args.relative, args.destination)))


if __name__ == '__main__':
    main()
