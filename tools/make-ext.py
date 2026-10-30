#!/usr/bin/env python3
"""把 ext/browser 打成可分发的扩展压缩包。

产物：dist/OpenDM-Extension-v<版本>.zip
压缩包内顶层目录为 OpenDM-Extension/，解压后直接「加载已解压的扩展程序」
选中该文件夹即可（manifest.json 位于目录根部）。

用法：
    python tools/make-ext.py            # 版本取 manifest.json 里的 version
    python tools/make-ext.py 1.2.0      # 手动指定版本
"""

import json
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "ext", "browser")
DIST = os.path.join(ROOT, "dist")
TOP = "OpenDM-Extension"


def read_version():
    with open(os.path.join(SRC, "manifest.json"), encoding="utf-8") as f:
        return json.load(f)["version"]


def collect(base):
    """返回 base 下所有文件的相对路径（posix 风格），排序保证产物可复现。"""
    out = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames.sort()
        for name in sorted(filenames):
            if name.endswith((".zip", ".pem", ".crx")):
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, base).replace(os.sep, "/")
            out.append(rel)
    return out


def main():
    version = sys.argv[1] if len(sys.argv) > 1 else read_version()
    os.makedirs(DIST, exist_ok=True)
    out_path = os.path.join(DIST, f"OpenDM-Extension-v{version}.zip")

    files = collect(SRC)

    # 顺带把安装说明放进包里，用户解压后就能看到
    readme = os.path.join(ROOT, "ext", "README.md")
    extra = []
    if os.path.exists(readme):
        extra.append((readme, "README.md"))

    # 固定的时间戳，让同一个源打出来的包字节一致
    stamp = (2026, 1, 1, 0, 0, 0)

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for rel in files:
            info = zipfile.ZipInfo(f"{TOP}/{rel}", date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(os.path.join(SRC, rel), "rb") as f:
                z.writestr(info, f.read())
        for path, name in extra:
            info = zipfile.ZipInfo(f"{TOP}/{name}", date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(path, "rb") as f:
                z.writestr(info, f.read())

    size = os.path.getsize(out_path)
    print(f"packed {len(files) + len(extra)} files -> {out_path} ({size} bytes, v{version})")
    for rel in files:
        print("  " + rel)
    for _, name in extra:
        print("  " + name + "  (说明文档)")


if __name__ == "__main__":
    main()
