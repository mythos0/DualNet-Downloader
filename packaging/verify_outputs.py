#!/usr/bin/env python3
"""Release gate: verify the built MSI and portable zip contain everything the app
needs to start (this check would have caught the v4.0.0/v4.1.1 shipping bugs:
missing ijwhost.dll, missing VC runtime, missing theme file)."""
import argparse, os, struct, sys, zipfile

REQUIRED_ZIP = [
    "DualNet.App.exe", "DualNet.App.dll", "DualNet.App.runtimeconfig.json", "DualNet.App.deps.json",
    "DualNet.Core.dll", "CefSharp.dll", "CefSharp.Core.dll", "CefSharp.Wpf.dll",
    "CefSharp.Core.Runtime.dll", "CefSharp.BrowserSubprocess.exe", "CefSharp.BrowserSubprocess.Core.dll",
    "libcef.dll", "chrome_elf.dll", "d3dcompiler_47.dll", "libEGL.dll", "libGLESv2.dll",
    "resources.pak", "icudtl.dat", "v8_context_snapshot.bin",
    "ijwhost.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll",
    "msvcp140_codecvt_ids.dll", "vcruntime140.dll", "vcruntime140_1.dll",
    "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll",
    "PresentationFramework.dll", "WindowsBase.dll", "wpfgfx_cor3.dll", "vcruntime140_cor3.dll",
]

# Theme dictionaries are compiled by WPF into BAML resources stored in
# DualNet.App.g.resources as "themes/dark.baml" / "themes/light.baml".
# Two pitfalls the first v4.1.2 gate check tripped over (false negative):
#   1. a compiled Page item never keeps its .xaml extension -> ".baml"
#   2. the .g.resources key blob is UTF-16LE, so an ASCII/UTF-8 byte grep
#      never sees the key at all (the only ASCII hit is a C# string literal).
# Normalize both encodings, then look for the markers.
def _norm(data: bytes) -> bytes:
    parts = [data.lower()]
    for off in (0, 1):  # both UTF-16LE alignments
        try:
            parts.append(data[off:].decode("utf-16-le", errors="ignore")
                         .lower().encode("utf-8", errors="ignore"))
        except Exception:
            pass
    return b"\n".join(parts)


def themes_in_dll(zipf, dll_path):
    with zipf.open(dll_path) as f:
        data = _norm(f.read())
    dark = (b"themes/dark.baml" in data) or (b"themes/dark.xaml" in data)
    light = (b"themes/light.baml" in data) or (b"themes/light.xaml" in data)
    return dark and light


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zip", required=True)
    ap.add_argument("--msi", required=True)
    a = ap.parse_args()
    fail = False

    print(f"zip: {a.zip} ({os.path.getsize(a.zip):,} bytes)")
    print(f"msi: {a.msi} ({os.path.getsize(a.msi):,} bytes)")

    with zipfile.ZipFile(a.zip) as z:
        infos = z.infolist()
        # Basename of every entry, at any depth — resilient to staging layout.
        names = set()
        for n in infos:
            base = n.filename.rstrip("/").split("/")[-1]
            if base:
                names.add(base)
        # Canonical layout: every file entry must live under the app folder.
        bad_layout = [n.filename for n in infos
                      if not n.filename.endswith("/")
                      and not n.filename.startswith("DualNet Downloader/")]
        if bad_layout:
            print(f"BAD ZIP LAYOUT — {len(bad_layout)} entries outside "
                  f"'DualNet Downloader/' (first 10): {bad_layout[:10]}")
            fail = True
        missing = [r for r in REQUIRED_ZIP if r not in names]
        if missing:
            print("MISSING in zip:", missing)
            fail = True
        else:
            print(f"OK: all {len(REQUIRED_ZIP)} required files present in zip")
        if not any(n.endswith("locales/en-US.pak") for n in z.namelist()):
            print("MISSING: locales/en-US.pak")
            fail = True
        if not themes_in_dll(z, "DualNet Downloader/DualNet.App.dll"):
            print("MISSING: theme dictionaries (dark/light) not compiled into DualNet.App.dll")
            fail = True
        else:
            print("OK: dark + light theme dictionaries compiled into DualNet.App.dll")

    # MSI is an OLE compound file — verify structure + size plausibility.
    with open(a.msi, "rb") as f:
        header = f.read(8)
        if header[:8] != b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1":
            print("MISSING: MSI is not an OLE compound file")
            fail = True
        else:
            print("OK: MSI has a valid OLE compound-file signature")

    if fail:
        print("RELEASE GATE: FAILED")
        sys.exit(1)
    print("RELEASE GATE: PASSED")


if __name__ == "__main__":
    main()
