#!/usr/bin/env bash
# ============================================================================
#  RemoteHub 账号同步服务端 · 一键安装 / 更新脚本
#
#  用法（root 权限）：
#      bash install.sh
#
#  可选环境变量：
#      REMOTEHUB_PORT=8443          监听端口（被占用时自动改用其它端口）
#      REMOTEHUB_ADMIN_USER=admin   同步账号名
#
#  特点：纯 Python 标准库实现，无需 pip / venv / 联网；不修改系统其他服务；
#        重复执行即为「更新」，账号与密码库数据不会丢失。
# ============================================================================
set -euo pipefail

INSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"
SERVICE="${REMOTEHUB_SERVICE:-remotehub}"
# 允许通过管道执行 (curl | bash): 此时 BASH_SOURCE 为空
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || echo .)"
PORT="${REMOTEHUB_PORT:-8443}"
ADMIN_USER="${REMOTEHUB_ADMIN_USER:-admin}"

say()  { echo "      $*"; }
step() { echo; echo "[$1] $2"; }
ok()   { echo "      ✅ $*"; }
warn() { echo "      ⚠️  $*"; }

# ---------------------------------------------------------------------------
step 1/8 "环境检查"
# ---------------------------------------------------------------------------
PY_BIN="$(command -v python3 || true)"
[ -n "$PY_BIN" ] || { echo "错误: 未找到 python3"; exit 1; }
PY_VER="$($PY_BIN -c 'import sys;print("%d.%d.%d"%sys.version_info[:3])')"
$PY_BIN - <<'EOF'
import sys, hashlib
if sys.version_info < (3, 6):
    print("错误: 需要 Python >= 3.6"); sys.exit(1)
assert hasattr(hashlib, "scrypt"), "Python 缺少 scrypt 支持"
EOF
say "python3: $PY_BIN ($PY_VER)"
say "系统: $(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME" || uname -s)"
say "内存: $(free -m 2>/dev/null | awk '/^Mem:/{print $2" MB"}')  |  磁盘 /: $(df -h / | awk 'NR==2{print $5" 已用"}')"

# ---------------------------------------------------------------------------
step 2/8 "端口检查"
# ---------------------------------------------------------------------------
port_used() { ss -lnt 2>/dev/null | awk '{print $4}' | grep -qE "[:.]$1\$"; }
if port_used "$PORT"; then
  if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
    say "端口 $PORT 正被本服务使用，保持不变"
  else
    NEWPORT=""
    for p in $(seq $((PORT + 1)) $((PORT + 20))); do
      if ! port_used "$p"; then NEWPORT=$p; break; fi
    done
    if [ -n "$NEWPORT" ]; then
      warn "端口 $PORT 已被其他程序占用，自动改用 $NEWPORT"
      warn "客户端同步地址请填 https://<服务器IP>:$NEWPORT"
      PORT="$NEWPORT"
    else
      echo "错误: $PORT 及后续端口均被占用，请用 REMOTEHUB_PORT=xxxx bash install.sh 指定"; exit 1
    fi
  fi
else
  say "端口 $PORT 空闲"
fi

# ---------------------------------------------------------------------------
step 3/8 "部署服务端程序"
# ---------------------------------------------------------------------------
mkdir -p "$INSTALL_DIR"
if [ ! -f "$SRC_DIR/server.py" ]; then
  echo "错误: 未找到 server.py（当前为在线单文件模式请改用 install-online.sh，"
  echo "      或先下载完整部署包：/root/remotehub/）"
  exit 1
fi
cp "$SRC_DIR/server.py" "$INSTALL_DIR/server.py"
chmod 644 "$INSTALL_DIR/server.py"
for f in status.sh uninstall.sh adduser.sh nginx-proxy.sh; do
  [ -f "$SRC_DIR/$f" ] && install -m 755 "$SRC_DIR/$f" "$INSTALL_DIR/$f"
done
if [ -d "$INSTALL_DIR/venv" ]; then
  rm -rf "$INSTALL_DIR/venv"; say "已清理历史遗留的 venv 目录（当前实现为纯标准库，不需要虚拟环境）"
fi
ok "程序文件已更新到 $INSTALL_DIR"

# ---------------------------------------------------------------------------
step 4/8 "同步账号"
# ---------------------------------------------------------------------------
if [ -f "$INSTALL_DIR/CREDENTIALS.txt" ]; then
  say "已存在账号，保留不变"
else
  ADMIN_PASSWORD="$($PY_BIN -c 'import secrets,string; print("".join(secrets.choice(string.ascii_letters+string.digits) for _ in range(16)))')"
  REMOTEHUB_USER_PASSWORD="$ADMIN_PASSWORD" $PY_BIN "$INSTALL_DIR/server.py" adduser "$ADMIN_USER"
  (umask 077; printf '账号: %s\n密码: %s\n' "$ADMIN_USER" "$ADMIN_PASSWORD" > "$INSTALL_DIR/CREDENTIALS.txt")
  ok "$ADMIN_USER 账号已创建"
fi
say "账号列表: $($PY_BIN "$INSTALL_DIR/server.py" listusers 2>/dev/null | tr '\n' ' ')"

# ---------------------------------------------------------------------------
step 5/8 "TLS 证书"
# ---------------------------------------------------------------------------
USE_TLS=1
if [ -f "$INSTALL_DIR/server.crt" ] && [ -f "$INSTALL_DIR/server.key" ]; then
  say "复用已有证书"
else
  IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
  if command -v openssl >/dev/null 2>&1; then
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
      -keyout "$INSTALL_DIR/server.key" -out "$INSTALL_DIR/server.crt" \
      -subj "/CN=remotehub" -addext "subjectAltName=IP:${IP:-127.0.0.1}" >/dev/null 2>&1 || \
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
      -keyout "$INSTALL_DIR/server.key" -out "$INSTALL_DIR/server.crt" \
      -subj "/CN=remotehub" >/dev/null 2>&1 || USE_TLS=0
    [ -f "$INSTALL_DIR/server.key" ] && chmod 600 "$INSTALL_DIR/server.key"
  else
    USE_TLS=0
  fi
  if [ "$USE_TLS" = "1" ]; then ok "已生成自签名证书（10 年有效）"; else warn "未找到 openssl，将以 HTTP 模式运行"; fi
fi
TLS_ARGS=""
[ "$USE_TLS" = "1" ] && [ -f "$INSTALL_DIR/server.crt" ] && TLS_ARGS="--cert $INSTALL_DIR/server.crt --key $INSTALL_DIR/server.key"

# ---------------------------------------------------------------------------
step 6/8 "systemd 服务"
# ---------------------------------------------------------------------------
cat > /etc/systemd/system/$SERVICE.service <<EOF
[Unit]
Description=RemoteHub account sync server (程星SSH客户端)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
Environment=REMOTEHUB_HOME=$INSTALL_DIR
Environment=REMOTEHUB_PORT=$PORT
ExecStart=$PY_BIN $INSTALL_DIR/server.py serve --port $PORT $TLS_ARGS
WorkingDirectory=$INSTALL_DIR
Restart=always
RestartSec=3
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable $SERVICE >/dev/null 2>&1 || true
systemctl restart $SERVICE
sleep 2
systemctl is-active --quiet $SERVICE && ok "服务已启动并设为开机自启" || warn "服务未启动，请查看: journalctl -u $SERVICE -n 50"

# ---------------------------------------------------------------------------
step 7/8 "防火墙"
# ---------------------------------------------------------------------------
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
  ufw allow $PORT/tcp >/dev/null 2>&1 || true; ok "ufw 已放行 $PORT"
elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state 2>/dev/null | grep -q running; then
  firewall-cmd --permanent --add-port=$PORT/tcp >/dev/null 2>&1 || true
  firewall-cmd --reload >/dev/null 2>&1 || true; ok "firewalld 已放行 $PORT"
elif command -v iptables >/dev/null 2>&1; then
  iptables -C INPUT -p tcp --dport $PORT -j ACCEPT 2>/dev/null || iptables -I INPUT -p tcp --dport $PORT -j ACCEPT
  ok "iptables 已放行 $PORT"
else
  warn "未检测到防火墙，跳过"
fi
say "如使用云厂商安全组 / 宝塔面板防火墙，请另外放行 $PORT/tcp"

# ---------------------------------------------------------------------------
step 8/8 "自检"
# ---------------------------------------------------------------------------
PUB_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
SCHEME=https; [ -z "$TLS_ARGS" ] && SCHEME=http
HEALTH="$($PY_BIN - "$SCHEME" "$PORT" <<'EOF'
import ssl, sys, urllib.request
scheme, port = sys.argv[1], sys.argv[2]
url = f"{scheme}://127.0.0.1:{port}/health"
ctx = ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE
last = None
for _ in range(8):
    try:
        with urllib.request.urlopen(url, context=ctx, timeout=3) as r:
            print(r.read().decode()); break
    except Exception as e:
        last = e
else:
    print("ERR " + str(last))
EOF
)"
if echo "$HEALTH" | grep -q '"ok"'; then
  ok "健康检查通过: $HEALTH"
else
  warn "自检失败: $HEALTH"
  warn "排查: journalctl -u $SERVICE -n 50"
fi

# ---------------------------------------------------------------------------
# Web 服务 / 面板检测 (nginx · 宝塔 · 1Panel)
# ---------------------------------------------------------------------------
NGINX_BIN=""
for c in nginx /usr/sbin/nginx /usr/local/nginx/sbin/nginx \
         /www/server/nginx/sbin/nginx \
         /opt/1panel/apps/openresty/openresty/sbin/openresty \
         /opt/1panel/apps/openresty/openresty/sbin/nginx; do
  if command -v "$c" >/dev/null 2>&1; then NGINX_BIN="$(command -v "$c")"; break; fi
done
PANEL=""
if [ -d /www/server/panel ]; then PANEL="/www/server/panel (宝塔)";
elif [ -d /opt/1panel ]; then PANEL="/opt/1panel (1Panel)"; fi
if [ -n "$NGINX_BIN" ] || [ -n "$PANEL" ]; then
  echo
  echo "      ℹ️  检测到 Web 服务/面板：${NGINX_BIN:+nginx=$NGINX_BIN }${PANEL:+面板=$PANEL}"
  say "本服务自带 HTTPS，直接监听 $PORT，无需 nginx 即可使用"
  say "如需用域名访问，可执行: bash $INSTALL_DIR/nginx-proxy.sh <你的域名>"
fi

# ---------------------------------------------------------------------------
echo
echo "=============================================================="
echo "  ✅ RemoteHub 同步服务已就绪"
echo "--------------------------------------------------------------"
echo "  同步地址 : $SCHEME://${PUB_IP:-<服务器IP>}:$PORT"
if [ -f "$INSTALL_DIR/CREDENTIALS.txt" ]; then
  echo "  $(tr '\n' ' ' < "$INSTALL_DIR/CREDENTIALS.txt")"
fi
echo "  数据目录 : $INSTALL_DIR （SQLite + 证书 + 密钥）"
echo "  服务管理 : systemctl status|restart $SERVICE"
echo "--------------------------------------------------------------"
echo "  一键命令（在服务器上直接执行）："
echo "    查看状态 : bash $INSTALL_DIR/status.sh"
echo "    新增账号 : bash $INSTALL_DIR/adduser.sh <用户名>"
echo "    实时日志 : journalctl -u $SERVICE -f"
echo "    卸载     : bash $INSTALL_DIR/uninstall.sh"
echo "=============================================================="
echo
