#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把同步服务端整套文件部署到服务器 /root/remotehub/ 并执行一键安装。

在 Windows 本机运行:  python sync-server/deploy.py
"""
import io
import os
import sys

import paramiko

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# 凭据一律从环境变量读取, 不要写进仓库
#   set CX_DEPLOY_HOST=1.2.3.4
#   set CX_DEPLOY_USER=root
#   set CX_DEPLOY_PASSWORD=******
HOST = os.environ.get("CX_DEPLOY_HOST", "your-server.example.com")
PORT = int(os.environ.get("CX_DEPLOY_PORT", "22"))
USER = os.environ.get("CX_DEPLOY_USER", "root")
PASSWORD = os.environ.get("CX_DEPLOY_PASSWORD", "")

REMOTE_DIR = "/root/remotehub"
FILES = [
    "server.py",
    "install.sh",
    "install-online.sh",
    "status.sh",
    "adduser.sh",
    "uninstall.sh",
    "nginx-proxy.sh",
    "requirements.txt",
    "README-server.md",
]
TEXT_SUFFIX = (".py", ".sh", ".md", ".txt", ".service")


def connect():
    cli = paramiko.SSHClient()
    cli.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    for attempt in range(1, 4):
        try:
            print(f"[*] 连接 {HOST}:{PORT} (第 {attempt} 次)...", flush=True)
            cli.connect(HOST, port=PORT, username=USER, password=PASSWORD,
                        timeout=30, banner_timeout=90, auth_timeout=90,
                        allow_agent=False, look_for_keys=False)
            return cli
        except Exception as ex:
            print(f"    ! {ex}", flush=True)
            if attempt == 3:
                raise
    raise RuntimeError("unreachable")


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    cli = connect()

    print(f"[*] 上传文件到 {REMOTE_DIR} ...", flush=True)
    sftp = cli.open_sftp()
    try:
        sftp.stat(REMOTE_DIR)
    except IOError:
        sftp.mkdir(REMOTE_DIR)

    uploaded = 0
    for name in FILES:
        local = os.path.join(here, name)
        if not os.path.exists(local):
            print(f"    - 跳过(不存在): {name}")
            continue
        with open(local, "rb") as f:
            data = f.read()
        # 统一换行符为 LF, 避免 Windows CRLF 导致 bash 报错
        if name.endswith(TEXT_SUFFIX):
            data = data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
        sftp.putfo(io.BytesIO(data), f"{REMOTE_DIR}/{name}")
        sftp.chmod(f"{REMOTE_DIR}/{name}", 0o755 if name.endswith(".sh") else 0o644)
        print(f"    + {name} ({len(data)} 字节)")
        uploaded += 1
    sftp.close()
    print(f"[*] 已上传 {uploaded} 个文件", flush=True)

    print("[*] 执行一键安装脚本 ...", flush=True)
    _, stdout, _ = cli.exec_command(f"bash {REMOTE_DIR}/install.sh 2>&1", get_pty=True)
    for line in iter(stdout.readline, ""):
        sys.stdout.write(line)
        sys.stdout.flush()
    code = stdout.channel.recv_exit_status()
    print(f"[*] 安装脚本退出码: {code}")

    print("\n[*] 服务器上的文件清单:", flush=True)
    _, stdout, _ = cli.exec_command(f"ls -la {REMOTE_DIR}")
    print(stdout.read().decode(errors="replace"))

    cli.close()
    return code


if __name__ == "__main__":
    sys.exit(main())
