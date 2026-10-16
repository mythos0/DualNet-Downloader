#!/usr/bin/env python3
"""Make a simple LICENSE.rtf for the MSI welcome dialog."""
import argparse

ap = argparse.ArgumentParser()
ap.add_argument("--license", required=True)
ap.add_argument("--out", required=True)
a = ap.parse_args()

text = open(a.license, encoding="utf-8-sig").read()

def esc(s):
    return (s.replace("\\", "\\\\").replace("{", "\\{").replace("}", "\\}")
             .replace("&", "\\&").replace("<", "\\lt").replace(">", "\\gt"))

body = esc(text).replace("\r\n", "\\par\n").replace("\n", "\\par\n")
rtf = ("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Segoe UI;}}\\viewkind4\\uc1\\pard\\fs16\\f0 "
       + body + "\\par}")
open(a.out, "w", encoding="ascii").write(rtf)
print(f"LICENSE.rtf written: {a.out}")
