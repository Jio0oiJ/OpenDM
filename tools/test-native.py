#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
模拟 Chrome / Edge 的 native messaging 调用，验证 OpenDM 的「被浏览器唤起」应答。

真实调用链是：浏览器读注册表 → 找到 host 清单 → 启动清单里的 exe →
用「4 字节小端长度 + UTF-8 JSON」的报文通信。这个脚本做的就是最后一步，
所以只要它通了，浏览器那条链路的协议部分就是对的。

用法：
    python tools/test-native.py dist/OpenDM.exe
"""
import json
import os
import struct
import subprocess
import sys

CREATE_NO_WINDOW = 0x08000000


def call(exe, payload, timeout=15):
    """启动 host，发一条消息，读一条响应。"""
    p = subprocess.Popen(
        [exe],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        creationflags=CREATE_NO_WINDOW,
    )
    try:
        data = json.dumps(payload).encode("utf-8")
        p.stdin.write(struct.pack("<I", len(data)) + data)
        p.stdin.flush()

        hdr = p.stdout.read(4)
        if len(hdr) < 4:
            return None
        n = struct.unpack("<I", hdr)[0]
        body = p.stdout.read(n)
        return json.loads(body.decode("utf-8"))
    finally:
        # start 会让程序继续开着窗口，这里统一收掉
        try:
            p.terminate()
            p.wait(timeout=timeout)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass


def main():
    exe = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "dist/OpenDM.exe")
    if not os.path.isfile(exe):
        print("找不到 exe:", exe)
        return 1

    print("测试对象:", exe)
    ok = True

    # 1) getPath —— 没装启动器时应该也能应答，只是 path 为空
    r = call(exe, {"cmd": "getPath"})
    print("\n[1] getPath ->", r)
    if not r or not r.get("ok"):
        print("    失败：应返回 ok")
        ok = False

    # 2) setPath 传一个不存在的路径，必须被拒绝
    r = call(exe, {"cmd": "setPath", "path": r"C:\definitely\not\here\OpenDM.exe"})
    print("[2] setPath(不存在) ->", r)
    if not r or r.get("ok") is not False:
        print("    失败：不存在的路径应被拒绝")
        ok = False

    # 3) setPath 传一个非 exe，必须被拒绝
    r = call(exe, {"cmd": "setPath", "path": sys.executable})
    print("[3] setPath(非 exe) ->", r)
    if not r or r.get("ok") is not False:
        print("    失败：非 exe 应被拒绝")
        ok = False

    # 4) setPath 指向自己，应该成功并写注册表
    r = call(exe, {"cmd": "setPath", "path": exe})
    print("[4] setPath(自身) ->", r)
    if not r or not r.get("ok"):
        print("    失败：合法路径应写入成功")
        ok = False

    # 5) 再读一次，应该和刚才写的一致
    r = call(exe, {"cmd": "getPath"})
    print("[5] getPath ->", r)
    if not r or r.get("path", "").lower() != exe.lower():
        print("    失败：读回的路径与写入不一致")
        ok = False

    # 6) 注册表是否真的写进去了（Chrome 从这里找 host）
    try:
        import winreg
        key_path = r"Software\Google\Chrome\NativeMessagingHosts\com.opendm.launcher"
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path) as k:
            manifest = winreg.QueryValue(k, None)
        print("[6] 注册表 ->", manifest)
        if not os.path.isfile(manifest):
            print("    失败：注册表指向的清单文件不存在")
            ok = False
        else:
            m = json.load(open(manifest, encoding="utf-8"))
            print("    清单内容: name=%s type=%s path=%s" % (m["name"], m["type"], m["path"]))
            print("    allowed_origins:", m["allowed_origins"])
            if m["type"] != "stdio":
                print("    失败：type 必须是 stdio")
                ok = False
            if not any("ndhkhjeaooenioieceapibfhpajkkflf" in o for o in m["allowed_origins"]):
                print("    失败：allowed_origins 与扩展 ID 不匹配")
                ok = False
    except Exception as e:
        print("[6] 注册表读取失败:", e)
        ok = False

    # 7) start —— 浏览器唤起主程序时发的指令
    r = call(exe, {"cmd": "start"})
    print("[7] start ->", r)
    if not r or not r.get("ok"):
        print("    失败：start 应返回 ok")
        ok = False

    # 8) 未知指令
    r = call(exe, {"cmd": "no-such-thing"})
    print("[8] 未知指令 ->", r)
    if not r or r.get("ok") is not False:
        print("    失败：未知指令应返回 ok:false")
        ok = False

    print("\n" + ("全部通过 ✓" if ok else "有失败项 ✗"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
