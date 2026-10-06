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
