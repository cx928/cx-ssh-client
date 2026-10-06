#!/usr/bin/env bash
# RemoteHub 同步服务 · 新增/重置同步账号
#  用法: bash adduser.sh <用户名> [密码]
#        不给密码则自动生成 16 位随机密码
set -euo pipefail
INSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"
USER_NAME="${1:-}"
[ -n "$USER_NAME" ] || { echo "用法: bash adduser.sh <用户名> [密码]"; exit 2; }

PY_BIN="$(command -v python3)"
PASSWORD="${2:-}"
if [ -z "$PASSWORD" ]; then
  PASSWORD="$($PY_BIN -c 'import secrets,string; print("".join(secrets.choice(string.ascii_letters+string.digits) for _ in range(16)))')"
fi

if REMOTEHUB_USER_PASSWORD="$PASSWORD" $PY_BIN "$INSTALL_DIR/server.py" adduser "$USER_NAME" 2>/dev/null; then
  echo "✅ 账号已创建"
else
  echo "ℹ️  账号已存在，改为重置密码"
  REMOTEHUB_USER_PASSWORD="$PASSWORD" $PY_BIN "$INSTALL_DIR/server.py" passwd "$USER_NAME"
  echo "✅ 密码已重置"
fi
echo "   用户名: $USER_NAME"
echo "   密码  : $PASSWORD"
echo "   ⚠️  修改密码后，云端旧密文将无法解密：请先在客户端「设置 → 清空全部会话」并同步一次。"
bash "$INSTALL_DIR/status.sh" 2>/dev/null | grep -E "账号:" || true
