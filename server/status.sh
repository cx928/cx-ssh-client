#!/usr/bin/env bash
# RemoteHub 同步服务 · 状态与自检
set -uo pipefail
SERVICE="${REMOTEHUB_SERVICE:-remotehub}"
INSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"
PORT="$(systemctl show -p Environment $SERVICE 2>/dev/null | tr ' ' '\n' | sed -n 's/^REMOTEHUB_PORT=//p' | head -1)"
[ -n "${PORT:-}" ] || PORT=8443
PY_BIN="$(command -v python3 || echo python3)"
SCHEME=https
[ -f "$INSTALL_DIR/server.crt" ] || SCHEME=http
PUB_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"

echo "================= RemoteHub 服务状态 ================="
systemctl status $SERVICE --no-pager -l 2>/dev/null | head -12
echo
echo "----------------- 端口监听 -----------------"
ss -lntp 2>/dev/null | grep -E "[:.]$PORT\b" || echo "  未监听 $PORT"
echo
echo "----------------- 健康检查 -----------------"
$PY_BIN - "$SCHEME" "$PORT" <<'EOF'
import ssl, sys, json, urllib.request
scheme, port = sys.argv[1], sys.argv[2]
ctx = ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE
try:
    with urllib.request.urlopen(f"{scheme}://127.0.0.1:{port}/health", context=ctx, timeout=5) as r:
        print("  " + r.read().decode())
except Exception as e:
    print("  ❌ 无法访问:", e)
EOF
echo
echo "----------------- 账号与数据 -----------------"
$PY_BIN "$INSTALL_DIR/server.py" listusers 2>/dev/null | sed 's/^/  账号: /' || echo "  无法读取账号"
[ -f "$INSTALL_DIR/CREDENTIALS.txt" ] && echo "  凭据文件: $INSTALL_DIR/CREDENTIALS.txt"
if [ -f "$INSTALL_DIR/remotehub.db" ]; then
  $PY_BIN - "$INSTALL_DIR/remotehub.db" <<'EOF'
import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
u = c.execute("SELECT COUNT(*) FROM users").fetchone()[0]
v = c.execute("SELECT COUNT(*) FROM vault").fetchone()[0]
rev = c.execute("SELECT COALESCE(MAX(rev),0) FROM vault").fetchone()[0]
print(f"  账号数: {u}   已同步密码库: {v}   当前版本号: {rev}")
EOF
fi
echo
echo "----------------- 访问地址 -----------------"
echo "  $SCHEME://${PUB_IP:-<服务器IP>}:$PORT"
echo "======================================================"
