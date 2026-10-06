#!/usr/bin/env bash
# ============================================================================
#  RemoteHub 账号同步服务端 · 在线一键部署（单文件自包含）
#
#  生成时间 : 2026-10-05 13:54:41
#  内含文件 : server.py, status.sh, adduser.sh, uninstall.sh, nginx-proxy.sh
#  校验摘要 : server.py=2164449afab75b6b; status.sh=c938970ecaab7560; adduser.sh=d8c8f43959e8cf15; uninstall.sh=1309d15aede0ae19; nginx-proxy.sh=984ca365813a7efb
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
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || echo .)"   # 在线模式不使用
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
# ===== 以下文件由本脚本内嵌写入（自包含安装，无需同目录其他文件） =====
cat > "$INSTALL_DIR/server.py" <<'NOVA_EMBED_SERVER_PY'
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
RemoteHub 账号同步服务端 · 纯 Python 标准库实现 (零第三方依赖)

特性
  - HTTPS (自签名证书) / 可降级 HTTP
  - 账号: scrypt 加盐哈希; 令牌: HMAC-SHA256 签名 (默认 30 天)
  - 密码库: 对服务端不透明的 Base64 密文 (客户端 AES-256-GCM 加密)
  - 乐观锁 rev, 冲突返回 409
  - SQLite (WAL) 持久化, ThreadingHTTPServer 并发

用法
  python3 server.py serve [--port 8443] [--cert crt --key key] [--http]
  python3 server.py adduser <用户名>      # 密码取环境变量 REMOTEHUB_USER_PASSWORD
  python3 server.py listusers
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import secrets
import sqlite3
import ssl
import sys
import threading
import time
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

VERSION = "1.0.1"
DATA_DIR = Path(os.environ.get("REMOTEHUB_HOME", "/opt/remotehub"))
DB_PATH = DATA_DIR / "remotehub.db"
SECRET_PATH = DATA_DIR / "secret.key"
TOKEN_TTL = 30 * 86400
MAX_BODY = 16 * 1024 * 1024

_lock = threading.Lock()


def get_secret() -> bytes:
    if SECRET_PATH.exists():
        return SECRET_PATH.read_bytes()
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    s = secrets.token_bytes(32)
    SECRET_PATH.write_bytes(s)
    try:
        os.chmod(SECRET_PATH, 0o600)
    except OSError:
        pass
    return s


SECRET = get_secret()


def connect():
    conn = sqlite3.connect(str(DB_PATH), timeout=30)
    conn.execute("PRAGMA journal_mode=WAL")
    return conn


def init_db():
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    with _lock, closing(connect()) as c:
        c.execute("""CREATE TABLE IF NOT EXISTS users(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            username TEXT UNIQUE NOT NULL,
            salt TEXT NOT NULL,
            pwhash TEXT NOT NULL,
            created_at INTEGER NOT NULL)""")
        c.execute("""CREATE TABLE IF NOT EXISTS vault(
            user_id INTEGER PRIMARY KEY,
            data TEXT NOT NULL,
            updated_at INTEGER NOT NULL,
            rev INTEGER NOT NULL DEFAULT 1)""")
        c.commit()


def hash_password(password: str, salt_hex: str = None):
    salt = bytes.fromhex(salt_hex) if salt_hex else secrets.token_bytes(16)
    h = hashlib.scrypt(password.encode("utf-8"), salt=salt, n=16384, r=8, p=1, dklen=32)
    return salt.hex(), h.hex()


def add_user(username: str, password: str) -> bool:
    with _lock, closing(connect()) as c:
        if c.execute("SELECT 1 FROM users WHERE username=?", (username,)).fetchone():
            return False
        salt, h = hash_password(password)
        c.execute("INSERT INTO users(username,salt,pwhash,created_at) VALUES(?,?,?,?)",
                  (username, salt, h, int(time.time())))
        c.commit()
    return True


def set_password(username: str, password: str) -> bool:
    with _lock, closing(connect()) as c:
        row = c.execute("SELECT id FROM users WHERE username=?", (username,)).fetchone()
        if not row:
            return False
        salt, h = hash_password(password)
        c.execute("UPDATE users SET salt=?,pwhash=? WHERE id=?", (salt, h, row[0]))
        c.commit()
    return True


def verify_user(username: str, password: str):
    with _lock, closing(connect()) as c:
        row = c.execute("SELECT id,salt,pwhash FROM users WHERE username=?", (username,)).fetchone()
    if not row:
        return None
    uid, salt, want = row
    _, got = hash_password(password, salt)
    return uid if hmac.compare_digest(got, want) else None


def make_token(uid: int) -> str:
    payload = json.dumps({"u": uid, "e": int(time.time()) + TOKEN_TTL}, separators=(",", ":"))
    b64 = base64.urlsafe_b64encode(payload.encode()).decode().rstrip("=")
    sig = hmac.new(SECRET, b64.encode(), hashlib.sha256).hexdigest()
    return b64 + "." + sig


def check_token(token: str):
    try:
        b64, sig = token.split(".", 1)
        want = hmac.new(SECRET, b64.encode(), hashlib.sha256).hexdigest()
        if not hmac.compare_digest(want, sig):
            return None
        pad = "=" * (-len(b64) % 4)
        payload = json.loads(base64.urlsafe_b64decode(b64 + pad))
        if payload.get("e", 0) < time.time():
            return None
        return int(payload["u"])
    except Exception:
        return None


_rate = {}
_rate_lock = threading.Lock()


def rate_ok(ip: str) -> bool:
    now = time.time()
    with _rate_lock:
        hits = [t for t in _rate.get(ip, []) if now - t < 60]
        _rate[ip] = hits
        if len(hits) >= 20:
            return False
        hits.append(now)
    return True


class Handler(BaseHTTPRequestHandler):
    server_version = "RemoteHub/" + VERSION
    protocol_version = "HTTP/1.1"
    timeout = 60

    def log_message(self, fmt, *args):
        sys.stderr.write("[%s] %s %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"),
                                           self.address_string(), fmt % args))

    # ---------- 基础工具 ----------
    def _json(self, code: int, obj):
        body = json.dumps(obj, separators=(",", ":")).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def _read_json(self):
        try:
            n = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            return None
        if n <= 0 or n > MAX_BODY:
            return None
        try:
            return json.loads(self.rfile.read(n).decode("utf-8"))
        except Exception:
            return None

    def _auth_uid(self):
        auth = self.headers.get("Authorization", "")
        if not auth.startswith("Bearer "):
            return None
        return check_token(auth[7:].strip())

    # ---------- 路由 ----------
    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/health":
            return self._json(200, {"status": "ok", "version": VERSION, "time": int(time.time())})
        if path == "/api/v1/vault":
            uid = self._auth_uid()
            if uid is None:
                return self._json(401, {"error": "invalid token"})
            with _lock, closing(connect()) as c:
                row = c.execute("SELECT data,updated_at,rev FROM vault WHERE user_id=?",
                                (uid,)).fetchone()
            if not row:
                return self._json(404, {"error": "not found"})
            return self._json(200, {"data": row[0], "updated_at": row[1], "rev": row[2]})
        return self._json(404, {"error": "unknown endpoint"})

    def do_POST(self):
        path = self.path.split("?")[0]
        if path == "/api/v1/auth/login":
            ip = self.client_address[0] if self.client_address else "?"
            if not rate_ok(ip):
                return self._json(429, {"error": "too many attempts"})
            body = self._read_json()
            if not body:
                return self._json(400, {"error": "bad request"})
            uid = verify_user(str(body.get("username", "")).strip(), str(body.get("password", "")))
            if uid is None:
                return self._json(401, {"error": "invalid credentials"})
            return self._json(200, {"token": make_token(uid),
                                    "expires_at": int(time.time()) + TOKEN_TTL,
                                    "server_version": VERSION})
        return self._json(404, {"error": "unknown endpoint"})

    def do_PUT(self):
        path = self.path.split("?")[0]
        if path == "/api/v1/vault":
            uid = self._auth_uid()
            if uid is None:
                return self._json(401, {"error": "invalid token"})
            body = self._read_json()
            if not body:
                return self._json(400, {"error": "bad request"})
            data = body.get("data")
            if not isinstance(data, str) or not data:
                return self._json(400, {"error": "missing data"})
            if len(data) > 8 * 1024 * 1024:
                return self._json(413, {"error": "payload too large"})
            try:
                base_rev = int(body.get("base_rev", 0))
                updated_at = int(body.get("updated_at", int(time.time() * 1000)))
            except (TypeError, ValueError):
                return self._json(400, {"error": "bad fields"})
            with _lock, closing(connect()) as c:
                row = c.execute("SELECT rev FROM vault WHERE user_id=?", (uid,)).fetchone()
                cur = row[0] if row else 0
                if base_rev != cur:
                    return self._json(409, {"error": "conflict", "rev": cur})
                new_rev = cur + 1
                if row:
                    c.execute("UPDATE vault SET data=?,updated_at=?,rev=? WHERE user_id=?",
                              (data, updated_at, new_rev, uid))
                else:
                    c.execute("INSERT INTO vault(user_id,data,updated_at,rev) VALUES(?,?,?,?)",
                              (uid, data, updated_at, new_rev))
                c.commit()
            return self._json(200, {"rev": new_rev, "updated_at": updated_at})
        return self._json(404, {"error": "unknown endpoint"})


def serve(port: int, cert: str = None, key: str = None, http_only: bool = False):
    init_db()
    srv = ThreadingHTTPServer(("0.0.0.0", port), Handler)
    srv.daemon_threads = True
    scheme = "http"
    if not http_only and cert and key and os.path.exists(cert) and os.path.exists(key):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        try:
            ctx.minimum_version = ssl.TLSVersion.TLSv1_2
        except AttributeError:
            pass
        ctx.load_cert_chain(cert, key)
        srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
        scheme = "https"
    print(f"RemoteHub {VERSION} listening on {scheme}://0.0.0.0:{port}", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        srv.server_close()


def cli():
    ap = argparse.ArgumentParser(description="RemoteHub 同步服务端")
    sub = ap.add_subparsers(dest="cmd")

    sp = sub.add_parser("serve", help="启动服务")
    sp.add_argument("--port", type=int, default=int(os.environ.get("REMOTEHUB_PORT", "8443")))
    sp.add_argument("--cert", default=os.environ.get("REMOTEHUB_CERT", str(DATA_DIR / "server.crt")))
    sp.add_argument("--key", default=os.environ.get("REMOTEHUB_KEY", str(DATA_DIR / "server.key")))
    sp.add_argument("--http", action="store_true", help="仅 HTTP (不使用证书)")

    ap_user = sub.add_parser("adduser", help="新增同步账号")
    ap_user.add_argument("username")

    ap_pw = sub.add_parser("passwd", help="修改账号密码")
    ap_pw.add_argument("username")

    sub.add_parser("listusers", help="列出账号")

    args = ap.parse_args()
    if args.cmd == "serve":
        serve(args.port, args.cert, args.key, args.http)
    elif args.cmd == "adduser":
        init_db()
        pw = os.environ.get("REMOTEHUB_USER_PASSWORD", "")
        if not pw:
            print("错误: 缺少环境变量 REMOTEHUB_USER_PASSWORD")
            sys.exit(2)
        print(f"OK: 用户 {args.username} 已创建" if add_user(args.username.strip(), pw)
              else f"错误: 用户 {args.username} 已存在")
    elif args.cmd == "passwd":
        init_db()
        pw = os.environ.get("REMOTEHUB_USER_PASSWORD", "")
        if not pw:
            print("错误: 缺少环境变量 REMOTEHUB_USER_PASSWORD")
            sys.exit(2)
        print("OK" if set_password(args.username.strip(), pw) else "错误: 用户不存在")
    elif args.cmd == "listusers":
        init_db()
        with closing(connect()) as c:
            for u, t in c.execute("SELECT username,created_at FROM users ORDER BY id"):
                print(u, time.strftime("%Y-%m-%d", time.localtime(t)))
    else:
        ap.print_help()
        sys.exit(2)


if __name__ == "__main__":
    cli()

NOVA_EMBED_SERVER_PY
chmod 644 "$INSTALL_DIR/server.py"
cat > "$INSTALL_DIR/status.sh" <<'NOVA_EMBED_STATUS_SH'
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

NOVA_EMBED_STATUS_SH
chmod 755 "$INSTALL_DIR/status.sh"
cat > "$INSTALL_DIR/adduser.sh" <<'NOVA_EMBED_ADDUSER_SH'
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

NOVA_EMBED_ADDUSER_SH
chmod 755 "$INSTALL_DIR/adduser.sh"
cat > "$INSTALL_DIR/uninstall.sh" <<'NOVA_EMBED_UNINSTALL_SH'
#!/usr/bin/env bash
# RemoteHub 同步服务 · 卸载（默认保留数据，加 --purge 彻底删除）
set -uo pipefail
SERVICE="${REMOTEHUB_SERVICE:-remotehub}"
INSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"
PURGE=0
[ "${1:-}" = "--purge" ] && PURGE=1

PORT="$(systemctl show -p Environment $SERVICE 2>/dev/null | tr ' ' '\n' | sed -n 's/^REMOTEHUB_PORT=//p' | head -1)"
[ -n "${PORT:-}" ] || PORT=8443

echo "=== 卸载 RemoteHub 同步服务 ==="
systemctl disable --now $SERVICE 2>/dev/null && echo "  已停止并取消开机自启"
rm -f /etc/systemd/system/$SERVICE.service
systemctl daemon-reload 2>/dev/null
echo "  已删除 systemd 单元"

if command -v iptables >/dev/null 2>&1; then
  iptables -D INPUT -p tcp --dport $PORT -j ACCEPT 2>/dev/null && echo "  已移除 iptables 放行规则"
fi

if [ "$PURGE" = "1" ]; then
  rm -rf "$INSTALL_DIR"
  echo "  ✅ 已彻底删除 $INSTALL_DIR（包含账号与密码库数据）"
else
  rm -f "$INSTALL_DIR/server.py"
  echo "  ✅ 程序已删除；数据保留在 $INSTALL_DIR"
  echo "     （含账号、密码库、证书；如需彻底删除请执行: bash $0 --purge）"
fi

NOVA_EMBED_UNINSTALL_SH
chmod 755 "$INSTALL_DIR/uninstall.sh"
cat > "$INSTALL_DIR/nginx-proxy.sh" <<'NOVA_EMBED_NGINX_PROXY_SH'
#!/usr/bin/env bash
# ============================================================================
#  RemoteHub 同步服务 · nginx / 宝塔面板 反向代理配置（可选）
#
#  用法:  bash nginx-proxy.sh <域名> [后端端口]
#  例如:  bash nginx-proxy.sh sync.example.com
#         bash nginx-proxy.sh example.com 8443
#
#  作用: 让 https://<域名>/api/v1/... 转发到本机 RemoteHub 服务，
#        客户端「服务器地址」即可填 https://<域名>（无需带端口）。
#  注意: 需要域名已解析到本机，并且证书由 nginx 侧提供（宝塔可一键申请 Let's Encrypt）。
# ============================================================================
set -euo pipefail

DOMAIN="${1:-}"
[ -n "$DOMAIN" ] || { echo "用法: bash nginx-proxy.sh <域名> [后端端口]"; exit 2; }
BACKEND_PORT="${2:-8443}"
INSTALL_DIR="${REMOTEHUB_INSTALL_DIR:-/opt/remotehub}"

# 找 nginx（含宝塔 /usr/local 与 1Panel OpenResty）
NGINX_BIN=""
for c in nginx /usr/sbin/nginx /usr/local/nginx/sbin/nginx \
         /www/server/nginx/sbin/nginx \
         /opt/1panel/apps/openresty/openresty/sbin/openresty \
         /opt/1panel/apps/openresty/openresty/sbin/nginx \
         /opt/1panel/apps/openresty/openresty/nginx/sbin/nginx; do
  if command -v "$c" >/dev/null 2>&1; then NGINX_BIN="$(command -v "$c")"; break; fi
done
[ -n "$NGINX_BIN" ] || { echo "错误: 未找到 nginx/OpenResty。本服务已自带 HTTPS，可直接使用 https://<IP>:$BACKEND_PORT"; exit 1; }

# 决定配置目录：宝塔 vhost、1Panel OpenResty conf.d、普通发行版 conf.d
if [ -d /www/server/panel/vhost/nginx ]; then
  CONF_DIR=/www/server/panel/vhost/nginx
  RELOAD="/etc/init.d/nginx reload 2>/dev/null || $NGINX_BIN -s reload"
  echo "检测到宝塔面板 nginx，配置目录: $CONF_DIR"
elif [ -d /opt/1panel/apps/openresty/openresty/conf/conf.d ]; then
  CONF_DIR=/opt/1panel/apps/openresty/openresty/conf/conf.d
  RELOAD="$NGINX_BIN -s reload"
  echo "检测到 1Panel OpenResty，配置目录: $CONF_DIR"
elif [ -d /etc/nginx/conf.d ]; then
  CONF_DIR=/etc/nginx/conf.d
  RELOAD="$NGINX_BIN -s reload"
else
  CONF_DIR=/etc/nginx/conf.d
  mkdir -p "$CONF_DIR"
  RELOAD="$NGINX_BIN -s reload"
fi

CONF="$CONF_DIR/remotehub-$DOMAIN.conf"
if [ -f "$CONF" ]; then
  echo "配置已存在，将覆盖: $CONF"
  cp "$CONF" "$CONF.bak.$(date +%s)"
fi

cat > "$CONF" <<EOF
# RemoteHub 同步服务反向代理（由 nginx-proxy.sh 生成）
server {
    listen 80;
    server_name $DOMAIN;
    return 301 https://\$host\$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name $DOMAIN;

    # 证书：宝塔面板可在「网站 → SSL」一键申请；下面为占位路径
    ssl_certificate     /www/server/panel/vhost/cert/$DOMAIN/fullchain.pem;
    ssl_certificate_key /www/server/panel/vhost/cert/$DOMAIN/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;

    client_max_body_size 32m;

    location / {
        proxy_pass https://127.0.0.1:$BACKEND_PORT;
        proxy_ssl_verify off;              # 后端为自签名证书
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 120s;
        proxy_http_version 1.1;
    }
}
EOF
echo "已写入: $CONF"

# 语法检查后重载
if $NGINX_BIN -t >/dev/null 2>&1; then
  eval "$RELOAD"
  echo "✅ nginx 已重载，客户端服务器地址可填: https://$DOMAIN"
  echo "   （若证书文件不存在，nginx 会启动失败：请在宝塔「网站 → SSL」为 $DOMAIN 申请证书后重载）"
else
  echo "⚠️  nginx 配置检查未通过，请执行 $NGINX_BIN -t 查看原因；已回滚本次配置"
  rm -f "$CONF"
  exit 1
fi

NOVA_EMBED_NGINX_PROXY_SH
chmod 755 "$INSTALL_DIR/nginx-proxy.sh"
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
