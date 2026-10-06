#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给服务端脚本加上 INSTALL_DIR / SERVICE 覆盖变量, 便于多实例与隔离测试"""
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "sync-server")

# ---------- install.sh ----------
p = os.path.join(SRC, "install.sh")
t = open(p, encoding="utf-8").read()
old_head = """INSTALL_DIR=/opt/remotehub
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PORT="${REMOTEHUB_PORT:-8443}"
ADMIN_USER="${REMOTEHUB_ADMIN_USER:-admin}"
SERVICE=remotehub"""
new_head = """INSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"
SERVICE="${REMOTEHUB_SERVICE:-remotehub}"
# 允许通过管道执行 (curl | bash): 此时 BASH_SOURCE 为空
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || echo .)"
PORT="${REMOTEHUB_PORT:-8443}"
ADMIN_USER="${REMOTEHUB_ADMIN_USER:-admin}\""""
if old_head in t:
    t = t.replace(old_head, new_head)
    print("install.sh: 已加覆盖变量")
else:
    print("!! install.sh: 头部未匹配")

old_copy = """mkdir -p "$INSTALL_DIR"
cp "$SRC_DIR/server.py" "$INSTALL_DIR/server.py\""""
new_copy = """mkdir -p "$INSTALL_DIR"
if [ ! -f "$SRC_DIR/server.py" ]; then
  echo "错误: 未找到 server.py（当前为在线单文件模式请改用 install-online.sh，"
  echo "      或先下载完整部署包：/root/remotehub/）"
  exit 1
fi
cp "$SRC_DIR/server.py" "$INSTALL_DIR/server.py\""""
if old_copy in t:
    t = t.replace(old_copy, new_copy)
    print("install.sh: 已加 server.py 存在性检查")
else:
    print("!! install.sh: 拷贝块未匹配")
open(p, "w", encoding="utf-8").write(t)

# ---------- 运维脚本 ----------
patches = {
    "status.sh": [
        ('SERVICE=remotehub\nINSTALL_DIR=/opt/remotehub',
         'SERVICE="${REMOTEHUB_SERVICE:-remotehub}"\nINSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"'),
    ],
    "adduser.sh": [
        ('INSTALL_DIR=/opt/remotehub',
         'INSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"'),
    ],
    "uninstall.sh": [
        ('SERVICE=remotehub\nINSTALL_DIR=/opt/remotehub',
         'SERVICE="${REMOTEHUB_SERVICE:-remotehub}"\nINSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"'),
    ],
    "nginx-proxy.sh": [
        ('BACKEND_PORT="${2:-8443}"\nINSTALL_DIR=/opt/remotehub',
         'BACKEND_PORT="${2:-8443}"\nINSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"'),
    ],
}
for name, pairs in patches.items():
    fp = os.path.join(SRC, name)
    if not os.path.exists(fp):
        print(f"!! {name} 不存在")
        continue
    txt = open(fp, encoding="utf-8").read()
    n = 0
    for a, b in pairs:
        if a in txt:
            txt = txt.replace(a, b)
            n += 1
    open(fp, "w", encoding="utf-8").write(txt)
    print(f"{name}: 已替换 {n} 处")
