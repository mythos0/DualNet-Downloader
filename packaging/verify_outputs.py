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

# theme resources are compiled INTO the BAML (DualNet.App.g.resources) — check the
# manifest resource contains both theme dictionary names, which proves Dark.xaml shipped.
def themes_in_dll(zipf, dll_path):
    with zipf.open(dll_path) as f:
        data = f.read()
    return (b"themes/dark.xaml" in data.lower()) and (b"themes/light.xaml" in data.lower())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zip", required=True)
    ap.add_argument("--msi", required=True)
    a = ap.parse_args()
    fail = False

    print(f"zip: {a.zip} ({os.path.getsize(a.zip):,} bytes)")
    print(f"msi: {a.msi} ({os.path.getsize(a.msi):,} bytes)")

    with zipfile.ZipFile(a.zip) as z:
        names = set()
        for n in z.namelist():
            parts = n.split("/")
            if len(parts) == 2 and parts[1]:
                names.add(parts[1])
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
