"""Fetch the official C API package, extracting only Windows x64 runtime and notices."""
from pathlib import Path
import hashlib
import json
import urllib.request
import zipfile

root = Path(__file__).resolve().parent
destination = root / 'tools/steamaudio'
destination.mkdir(parents=True, exist_ok=True)
package = destination / 'steamaudio_4.8.1.zip'
url = 'https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_4.8.1.zip'
expected_sha256 = '4a0aa5ec1176f38f0b0993a37c2259d9e86f27e22d5e24f83ec4c3cb9a1d5449'
if not package.exists():
    with urllib.request.urlopen(url, timeout=60) as source, package.open('wb') as output:
        while block := source.read(1024 * 1024):
            output.write(block)
actual_sha256 = hashlib.sha256(package.read_bytes()).hexdigest()
if actual_sha256 != expected_sha256:
    raise RuntimeError('Steam Audio package SHA256 differs from the verified development download.')
with zipfile.ZipFile(package) as archive:
    for item in archive.infolist():
        name = item.filename
        if name.endswith('/'):
            continue
        if ('/lib/windows-x64/' in name and name.lower().endswith('.dll')) or name.endswith('/THIRDPARTY.md') or any(
            name.lower().endswith('/' + leaf) for leaf in ('license', 'license.txt', 'thirdpartynotices.txt', 'third_party_notices.txt')):
            target = destination / Path(name).name
            target.write_bytes(archive.read(item))
            print(name)
with urllib.request.urlopen('https://raw.githubusercontent.com/ValveSoftware/steam-audio/v4.8.1/LICENSE.md', timeout=30) as response:
    (destination / 'LICENSE.txt').write_bytes(response.read())
(destination / 'provenance.json').write_text(json.dumps({'source': url, 'version': '4.8.1',
    'package_sha256': actual_sha256}, indent=2), encoding='utf-8')
print('Official Steam Audio Windows x64 files downloaded.')
