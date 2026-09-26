"""Download official .NET runtime packs for an offline, self-contained publish."""
import base64
import hashlib
import json
import pathlib
import sys
import urllib.request

version = sys.argv[1]
destination = pathlib.Path(sys.argv[2])
destination.mkdir(parents=True, exist_ok=True)
names = (
    "microsoft.netcore.app.runtime.win-x64",
    "microsoft.windowsdesktop.app.runtime.win-x64",
    "microsoft.aspnetcore.app.runtime.win-x64",
)

for name in names:
    filename = f"{name}.{version}.nupkg"
    base = f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{filename}"
    package = destination / filename
    digest_file = destination / f"{filename}.sha512"
    if package.exists() and digest_file.exists():
        digest = base64.b64decode(digest_file.read_text(encoding="ascii").strip())
        if hashlib.sha512(package.read_bytes()).digest() == digest:
            print(f"Verified cached pack: {filename}")
            continue
    registration = f"https://api.nuget.org/v3/registration5-semver1/{name}/{version}.json"
    with urllib.request.urlopen(registration, timeout=30) as response:
        catalog_url = json.load(response)["catalogEntry"]
    with urllib.request.urlopen(catalog_url, timeout=30) as response:
        catalog = json.load(response)
    if catalog["packageHashAlgorithm"] != "SHA512":
        raise RuntimeError(f"Unexpected hash algorithm: {filename}")
    encoded_digest = catalog["packageHash"]
    digest = base64.b64decode(encoded_digest)
    temporary = package.with_suffix(".download")
    hasher = hashlib.sha512()
    try:
        with urllib.request.urlopen(base, timeout=120) as response, temporary.open("wb") as output:
            while chunk := response.read(1024 * 1024):
                output.write(chunk)
                hasher.update(chunk)
        if hasher.digest() != digest:
            raise RuntimeError(f"SHA-512 mismatch: {filename}")
        temporary.replace(package)
        digest_file.write_text(encoded_digest, encoding="ascii")
    finally:
        temporary.unlink(missing_ok=True)
    print(f"Downloaded and verified: {filename}")
