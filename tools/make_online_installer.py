#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把 sync-server/ 下的 install.sh 与依赖文件合并成**单文件自包含安装脚本**
(install-online.sh)，可放到 CDN 上通过 curl | bash 直接部署。

用法:  python tools/make_online_installer.py
"""
import hashlib
import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.normpath(os.path.join(HERE, "..", "sync-server"))
OUT = os.path.join(SRC, "install-online.sh")

EMBED = ["server.py", "status.sh", "adduser.sh", "uninstall.sh", "nginx-proxy.sh"]

COPY_BLOCK = """mkdir -p "$INSTALL_DIR"
if [ ! -f "$SRC_DIR/server.py" ]; then
  echo "错误: 未找到 server.py（当前为在线单文件模式请改用 install-online.sh，"
  echo "      或先下载完整部署包：/root/remotehub/）"
  exit 1
fi
cp "$SRC_DIR/server.py" "$INSTALL_DIR/server.py"
chmod 644 "$INSTALL_DIR/server.py"
for f in status.sh uninstall.sh adduser.sh nginx-proxy.sh; do
  [ -f "$SRC_DIR/$f" ] && install -m 755 "$SRC_DIR/$f" "$INSTALL_DIR/$f"
done"""


def read_text(name: str) -> str:
    with open(os.path.join(SRC, name), "rb") as f:
        data = f.read()
    data = data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return data.decode("utf-8")


def main() -> int:
    install = read_text("install.sh")
    if COPY_BLOCK not in install:
        print("!! 未在 install.sh 中找到待替换的拷贝块，请检查脚本是否被改动")
        return 1
    # 去掉 BOM（如果有）
    install = install.lstrip("\ufeff")

    # 组装内嵌写入块
    pieces = ["mkdir -p \"$INSTALL_DIR\"",
              "# ===== 以下文件由本脚本内嵌写入（自包含安装，无需同目录其他文件） ====="]
    hashes = []
    for name in EMBED:
        content = read_text(name)
        delim = "NOVA_EMBED_" + name.replace(".", "_").replace("-", "_").upper()
        if delim in content:
            print(f"!! {name} 内容中已包含分隔符 {delim}")
            return 1
        hashes.append((name, hashlib.sha256(content.encode()).hexdigest()[:16]))
        mode = "755" if name.endswith(".sh") else "644"
        pieces.append(f"cat > \"$INSTALL_DIR/{name}\" <<'{delim}'\n{content}\n{delim}\nchmod {mode} \"$INSTALL_DIR/{name}\"")
        print(f"   内嵌 {name} ({len(content)} 字节)")

    embed_block = "\n".join(pieces)
    out = install.replace(COPY_BLOCK, embed_block)

    banner = f"""#!/usr/bin/env bash
# ============================================================================
#  RemoteHub 账号同步服务端 · 在线一键部署（单文件自包含）
#
#  生成时间 : {time.strftime('%Y-%m-%d %H:%M:%S')}
#  内含文件 : {", ".join(n for n, _ in hashes)}
#  校验摘要 : {"; ".join(f"{n}={h}" for n, h in hashes)}
#
#  使用（在服务器上，root 权限）：
#      curl -fsSL <本文件URL> | bash
#      wget -qO- <本文件URL> | bash
#      python3 -c "import urllib.request as u,sys;sys.stdout.write(u.urlopen('<本文件URL>').read().decode())" | bash
#
#  可选环境变量：
#      REMOTEHUB_PORT=8443            监听端口（占用时自动换端口）
#      REMOTEHUB_ADMIN_USER=admin     同步账号名
#      REMOTEHUB_INSTALL_DIR=/opt/remotehub   安装目录
#      REMOTEHUB_SERVICE=remotehub    systemd 服务名
# ============================================================================
"""
    # 去掉原脚本的 shebang 行，改用新 banner
    lines = out.split("\n")
    if lines and lines[0].startswith("#!"):
        lines = lines[1:]
    out = banner + "\n".join(lines)

    # 管道执行时没有源目录，明确提示（内嵌模式下不需要）
    out = out.replace(
        'SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || echo .)"',
        'SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || echo .)"   # 在线模式不使用')

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(out)

    size = os.path.getsize(OUT)
    print(f"\n✅ 已生成: {OUT} ({size} 字节 / {size / 1024:.1f} KB)")
    print(f"   内含 {len(EMBED)} 个文件，单文件即可部署")
    return 0


if __name__ == "__main__":
    sys.exit(main())
