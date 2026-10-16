#!/usr/bin/env python3
"""Generate the WiX v4+ .wxs source for the DualNet MSI installer.

Stdlib-only, cross-platform (runs on GitHub Actions windows-latest and locally).

- Per-user install (no UAC prompt): %LOCALAPPDATA%\\Programs\\DualNet Downloader
- STABLE UpgradeCode across all future versions -> installing any newer MSI
  supersedes the older one automatically (MajorUpgrade). Re-installing the SAME
  version also works (AllowSameVersionUpgrades="yes" = repair in place).
- Start Menu + Desktop shortcuts, Add/Remove Programs icon + links.
- One component per file; component GUIDs are deterministic (uuid5 of the
  repo-relative path) so upgrades/removals behave correctly across versions.

Usage:
  python gen_wxs.py --pub <publish-dir> --out <DualNet.wxs> --version 4.1.2 \
                    --ico <app.ico> --license-rtf <LICENSE.rtf>
"""
import argparse, os, re, uuid

DEFAULT_UPGRADE_CODE = "{9C7B7F3A-52D4-4E62-A8F1-D6B4C2E77A10}"
APP_NAME = "DualNet Downloader"
MANUFACTURER = "DualNet"


def xml(s: str) -> str:
    return (s.replace('&', '&amp;').replace('<', '&lt;')
             .replace('>', '&gt;').replace('"', '&quot;'))


def re_id(name: str) -> str:
    return re.sub(r'[^A-Za-z0-9_.]', '_', name)[:40]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pub", required=True, help="publish output directory")
    ap.add_argument("--out", required=True, help="path of the .wxs to write")
    ap.add_argument("--version", required=True, help="MSI ProductVersion (x.y.z)")
    ap.add_argument("--ico", required=True, help="app icon path")
    ap.add_argument("--license-rtf", required=True, help="LICENSE.rtf for the UI dialog")
    ap.add_argument("--upgrade-code", default=DEFAULT_UPGRADE_CODE)
    args = ap.parse_args()

    assert os.path.isdir(args.pub), f"publish dir missing: {args.pub}"
    assert os.path.exists(args.ico), f"icon missing: {args.ico}"
    assert os.path.exists(args.license_rtf), f"license rtf missing: {args.license_rtf}"

    lines = []
    w = lines.append

    w('<?xml version="1.0" encoding="UTF-8"?>')
    w('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"')
    w('    xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">')
    w(f'  <Package Name="{APP_NAME}" Manufacturer="{MANUFACTURER}" Version="{args.version}"')
    w(f'          UpgradeCode="{args.upgrade_code}" Scope="perUser" Compressed="yes"')
    w('          Language="1033" Codepage="1252" InstallerVersion="500">')
    w('    <!-- Stable UpgradeCode: any newer MSI supersedes this one (MajorUpgrade). -->')
    w('    <MajorUpgrade DowngradeErrorMessage="A newer version of [ProductName] is already installed." AllowSameVersionUpgrades="yes" />')
    w('    <MediaTemplate EmbedCab="yes" CompressionLevel="mszip" />')
    w(f'    <Icon Id="AppIcon" SourceFile="{args.ico}" />')
    w('    <Property Id="ARPPRODUCTICON" Value="AppIcon" />')
    w('    <Property Id="ARPNOREPAIR" Value="1" />')
    w('    <Property Id="ARPHELPLINK" Value="https://github.com/mythos0/DualNet-Downloader" />')
    w('    <Property Id="ARPURLINFOABOUT" Value="https://github.com/mythos0/DualNet-Downloader" />')
    w('    <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />')
    w(f'    <WixVariable Id="WixUILicenseRtf" Value="{args.license_rtf}" />')

    w('    <StandardDirectory Id="LocalAppDataFolder">')
    w('      <Directory Id="ProgramsPFolder" Name="Programs">')
    w(f'        <Directory Id="INSTALLFOLDER" Name="{APP_NAME}">')

    comp_index = 0
    stack = []

    def flush_dirs(down_to):
        while len(stack) > down_to:
            w("          " + "  " * len(stack) + "</Directory>")
            stack.pop()

    def walk(dirpath, rel):
        nonlocal comp_index
        entries = sorted(os.listdir(dirpath))
        subdirs = [e for e in entries if os.path.isdir(os.path.join(dirpath, e))]
        files = [e for e in entries if os.path.isfile(os.path.join(dirpath, e))]
        for d in subdirs:
            dir_id = "D" + str(len(stack)) + "_" + re_id(d)
            w('          ' + '  ' * len(stack) + f'<Directory Id="{dir_id}" Name="{xml(d)}">')
            stack.append(dir_id)
            walk(os.path.join(dirpath, d), rel + [d])
            flush_dirs(len(stack) - 1)
        for f in files:
            full = os.path.join(dirpath, f)
            comp_index += 1
            cid = f"C{comp_index:04d}"
            fid = f"F{comp_index:04d}"
            key = ' KeyPath="yes"' if f == "DualNet.App.exe" else ""
            guid = uuid.uuid5(uuid.NAMESPACE_URL, "DualNet:" + "/".join(rel + [f]))
            w('          ' + '  ' * (len(stack) + 1) + f'<Component Id="{cid}" Guid="{guid}">')
            w('          ' + '  ' * (len(stack) + 2) +
              f'<File Id="{fid}" Name="{xml(f)}" Source="{full}"{key} />')
            w('          ' + '  ' * (len(stack) + 1) + '</Component>')

    walk(args.pub, [])
    flush_dirs(0)
    w('        </Directory>')
    w('      </Directory>')
    w('    </StandardDirectory>')

    w('    <StandardDirectory Id="ProgramMenuFolder">')
    w(f'      <Directory Id="AppMenuFolder" Name="{APP_NAME}">')
    w(f'        <Component Id="StartMenuShortcut" Guid="{uuid.uuid5(uuid.NAMESPACE_URL, "DualNet:shortcut-startmenu")}">')
    w(f'          <Shortcut Id="StartMenuApp" Name="{APP_NAME}" Description="{APP_NAME} - dual-NIC downloader + browser"')
    w('                   Target="[INSTALLFOLDER]DualNet.App.exe" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" />')
    w('          <RemoveFolder Id="RemoveAppMenuFolder" On="uninstall" />')
    w('          <RegistryValue Root="HKCU" Key="Software\\DualNet\\DualNet Downloader" Name="startmenu" Type="integer" Value="1" KeyPath="yes" />')
    w('        </Component>')
    w('      </Directory>')
    w('    </StandardDirectory>')

    w('    <StandardDirectory Id="DesktopFolder">')
    w(f'      <Component Id="DesktopShortcut" Guid="{uuid.uuid5(uuid.NAMESPACE_URL, "DualNet:shortcut-desktop")}">')
    w(f'        <Shortcut Id="DesktopApp" Name="{APP_NAME}" Description="{APP_NAME} - dual-NIC downloader + browser"')
    w('                 Target="[INSTALLFOLDER]DualNet.App.exe" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" />')
    w('        <RegistryValue Root="HKCU" Key="Software\\DualNet\\DualNet Downloader" Name="desktop" Type="integer" Value="1" KeyPath="yes" />')
    w('      </Component>')
    w('    </StandardDirectory>')

    w('  </Package>')
    w('</Wix>')

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))
    print(f"wxs written: {args.out} ({comp_index} file components + 2 shortcut components)")


if __name__ == "__main__":
    main()
