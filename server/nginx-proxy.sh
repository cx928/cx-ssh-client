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
