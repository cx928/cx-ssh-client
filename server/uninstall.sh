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
