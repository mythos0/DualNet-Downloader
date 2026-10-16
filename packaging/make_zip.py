#!/usr/bin/env python3
"""Build the DualNet portable zip deterministically from the publish directory.

Replaces Compress-Archive, which SILENTLY dropped files on the CI runner
(v4.1.2 release gate caught ijwhost.dll missing from the zip even though it
was copied into the publish dir moments earlier — no error anywhere in the
runner log). Python's zipfile writes canonical forward-slash entry names and
this script verifies entry parity before exiting, so a silent drop is
impossible: any missing/locked/unreadable file fails the build loudly.

Usage:
  python make_zip.py --pub <publish-dir> --out <zip> [--prefix "DualNet Downloader"]
"""
import argparse
import os
import sys
import zipfile


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pub", required=True, help="publish output directory")
    ap.add_argument("--out", required=True, help="zip file to write")
    ap.add_argument("--prefix", default="DualNet Downloader",
                    help="top-level folder inside the zip")
    a = ap.parse_args()

    if not os.path.isdir(a.pub):
        print(f"zip build failed: publish dir missing: {a.pub}")
        sys.exit(1)

    prefix = a.prefix.strip("/") + "/"

    sources = []
    for root, dirs, files in os.walk(a.pub):
        dirs.sort()
        files.sort()
        for f in files:
            full = os.path.join(root, f)
            rel = os.path.relpath(full, a.pub).replace(os.sep, "/")
            sources.append((full, prefix + rel))

    if not sources:
        print(f"zip build failed: publish dir is empty: {a.pub}")
        sys.exit(1)

    # Explicit directory entries so Windows Explorer shows the tree properly.
    dir_names = {prefix}
    for _, name in sources:
        parts = name.split("/")[:-1]
        for i in range(1, len(parts) + 1):
            dir_names.add("/".join(parts[:i]) + "/")

    try:
        with zipfile.ZipFile(a.out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for d in sorted(dir_names):
                info = zipfile.ZipInfo(d)
                info.external_attr = 0o755 << 16 | 0x10  # directory bit
                z.writestr(info, b"")
            for full, name in sources:
                z.write(full, name)
    except OSError as ex:
        print(f"zip build FAILED while reading publish dir: {ex}")
        sys.exit(1)

    # Parity check: every source file must be present under its exact name.
    with zipfile.ZipFile(a.out) as z:
        entries = set(z.namelist())
        missing = [n for _, n in sources if n not in entries]
        if missing:
            print(f"zip build FAILED — {len(missing)} source file(s) missing "
                  f"from the archive (first 20): {missing[:20]}")
            sys.exit(1)

    total = sum(os.path.getsize(f) for f, _ in sources)
    print(f"zip built: {a.out}")
    print(f"zip contents: {len(sources)} files, {len(dir_names)} dir entries, "
          f"{total:,} source bytes")


if __name__ == "__main__":
    main()
