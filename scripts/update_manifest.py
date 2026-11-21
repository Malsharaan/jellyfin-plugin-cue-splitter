#!/usr/bin/env python3
"""Add (or replace) a version entry in the Jellyfin plugin repository manifest.

The manifest is a JSON array of packages, as expected by Jellyfin's
"Dashboard > Plugins > Repositories". Versions are kept newest first.
"""
import argparse
import json
import pathlib

PACKAGE = {
    "guid": "ad5ffba7-aeea-42e5-a9fe-e3f8e8daaf4c",
    "name": "CUE Splitter",
    "overview": "Virtual tracks from CUE sheets",
    "description": (
        "Shows the tracks of single-file album images (FLAC, APE, WavPack, WAV...) "
        "described by a .cue sheet as individual songs, without splitting or modifying "
        "the original file. Tracks are decoded on the fly, sample-accurately."
    ),
    "owner": "Malsharaan",
    "category": "Music",
}


def version_key(v: str):
    return tuple(int(p) for p in v.split("."))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--target-abi", required=True)
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--timestamp", required=True)
    parser.add_argument("--changelog", default="")
    args = parser.parse_args()

    path = pathlib.Path(args.manifest)
    manifest = json.loads(path.read_text(encoding="utf-8")) if path.exists() else []

    package = next((p for p in manifest if p.get("guid") == PACKAGE["guid"]), None)
    if package is None:
        package = dict(PACKAGE, versions=[])
        manifest.append(package)
    else:
        package.update(PACKAGE)

    versions = [v for v in package.get("versions", []) if v.get("version") != args.version]
    versions.append({
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": args.target_abi,
        "sourceUrl": args.source_url,
        "checksum": args.checksum,
        "timestamp": args.timestamp,
    })
    versions.sort(key=lambda v: version_key(v["version"]), reverse=True)
    package["versions"] = versions

    path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
