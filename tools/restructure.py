#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把工作区里的 程星SSH客户端 重构为 cx-ssh-client 项目结构：
  cx-ssh-client/
    winui/      WinUI 3 图形客户端
    server/     账号同步服务端
    tools/      构建辅助脚本
    installer/  Inno Setup 打包脚本
"""
import os
import shutil
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

WS = r"E:\Documents\deepseek-harness\default-workspace"
ROOT = os.path.join(WS, "cx-ssh-client")
SRC_WINUI = os.path.join(WS, "cx-ssh-client")
SRC_SERVER = os.path.join(WS, "sync-server")
SRC_TOOLS = os.path.join(WS, "tools")
SRC_INST = os.path.join(WS, "installer")
SRC_DIST = os.path.join(WS, "dist")

EXCLUDE_DIRS = {"obj", "bin", "obj2", "bin2", "cx-ssh-client-data", "cx-ssh-client-data"}

# ---------- 1. 复制源码 ----------
if os.path.exists(ROOT):
    shutil.rmtree(ROOT)
os.makedirs(ROOT)


def copy_tree(src, dst, skip_names=(), skip_files=()):
    count = 0
    for base, dirs, files in os.walk(src):
        dirs[:] = [d for d in dirs if d not in EXCLUDE_DIRS and d not in skip_names]
        rel = os.path.relpath(base, src)
        target = os.path.join(dst, rel) if rel != "." else dst
        os.makedirs(target, exist_ok=True)
        for f in files:
            if f in skip_files:
                continue
            shutil.copy2(os.path.join(base, f), os.path.join(target, f))
            count += 1
    return count


n1 = copy_tree(SRC_WINUI, os.path.join(ROOT, "winui"), skip_files={"README.md"})
n2 = copy_tree(SRC_SERVER, os.path.join(ROOT, "server"))
n3 = copy_tree(SRC_TOOLS, os.path.join(ROOT, "tools"))
os.makedirs(os.path.join(ROOT, "installer"), exist_ok=True)
for f in os.listdir(SRC_INST):
    if f.endswith(".iss"):
        shutil.copy2(os.path.join(SRC_INST, f), os.path.join(ROOT, "installer", f))
        n3 += 1
print(f"复制完成: winui={n1}, server={n2}, tools/installer={n3}")

# ---------- 2. 重命名与品牌替换 ----------
REPLACEMENTS = [
    # 命名空间
    ("CxSshClient.Models", "CxSshClient.Models"),
    ("CxSshClient.Services", "CxSshClient.Services"),
    ("CxSshClient.Helpers", "CxSshClient.Helpers"),
    ("CxSshClient.Views", "CxSshClient.Views"),
    # 路径与数据目录
    ("%LOCALAPPDATA%\\程星SSH客户端", "%LOCALAPPDATA%\\cx-ssh-client"),
    ("Local\\程星SSH客户端", "Local\\cx-ssh-client"),
    ("cx-ssh-client-data", "cx-ssh-client-data"),
    ('"cx-ssh-client"', '"cx-ssh-client"'),
    ("cx-ssh-client\\", "cx-ssh-client\\"),
    # 程序名与品牌
    ("cx-ssh-client.exe", "cx-ssh-client.exe"),
    ("cx-ssh-client-Setup", "cx-ssh-client-Setup"),
    ("cx-ssh-client-portable", "cx-ssh-client-portable"),
    ("cx-ssh-client_", "cx-ssh-client_"),
    ("cx-ssh-client-backup", "cx-ssh-client-backup"),
    ("程星SSH客户端", "程星SSH客户端"),
    ("程星SSH客户端 安装程序", "程星SSH客户端 安装程序"),
    ("cx-ssh-client", "程星SSH客户端"),
    ("程星SSH客户端", "程星SSH客户端"),
    # 兜底: 剩余 C# 标识符
    ("cx-ssh-client", "CxSshClient"),
    ("cx-ssh-client", "cx-ssh-client"),
]

TEXT_EXT = {".cs", ".xaml", ".csproj", ".manifest", ".md", ".sh", ".py", ".ps1", ".cmd",
            ".iss", ".json", ".txt", ".sln", ".xml", ".js", ".css", ".html"}
changed = 0
for base, dirs, files in os.walk(ROOT):
    for f in files:
        ext = os.path.splitext(f)[1].lower()
        if ext not in TEXT_EXT:
            continue
        p = os.path.join(base, f)
        try:
            text = open(p, encoding="utf-8").read()
        except UnicodeDecodeError:
            continue
        orig = text
        for a, b in REPLACEMENTS:
            text = text.replace(a, b)
        if text != orig:
            open(p, "w", encoding="utf-8", newline="\n").write(text)
            changed += 1
print(f"重命名替换: 修改了 {changed} 个文件")

# ---------- 3. csproj 版本与名称 ----------
csproj = os.path.join(ROOT, "winui", "CxSshClient.csproj")
if os.path.exists(csproj):
    t = open(csproj, encoding="utf-8").read()
    t = t.replace("<AssemblyName>程星SSH客户端</AssemblyName>", "<AssemblyName>cx-ssh-client</AssemblyName>")
    t = t.replace("<RootNamespace>程星SSH客户端</RootNamespace>", "<RootNamespace>CxSshClient</RootNamespace>")
    t = t.replace("<Version>1.0.2</Version>", "<Version>1.0.0</Version>")
    open(csproj, "w", encoding="utf-8", newline="\n").write(t)
    print("csproj: AssemblyName=cx-ssh-client, RootNamespace=CxSshClient, Version=1.0.0")

# 原 程星SSH客户端.csproj 已被复制为 CxSshClient.csproj? 检查文件名
for f in os.listdir(os.path.join(ROOT, "winui")):
    if f.endswith(".csproj"):
        print("winui 项目文件:", f)

print("\n=== 目录结构 ===")
for base, dirs, files in os.walk(ROOT):
    dirs[:] = [d for d in dirs if d not in EXCLUDE_DIRS and d != "www"]
    depth = base[len(ROOT):].count(os.sep)
    if depth <= 2:
        print("  " * depth + os.path.basename(base) + "/" + (f"   ({len(files)} 个文件)" if files else ""))
