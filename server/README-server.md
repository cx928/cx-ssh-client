# RemoteHub 账号同步服务端 · 服务器端说明

> 本目录（服务器上为 `/root/remotehub/`）包含同步服务端的全部文件。程序本体安装到 `/opt/remotehub/`。

## 方式一：在线一键部署（推荐，任意新服务器）

`install-online.sh` 是**单文件自包含**版本（内嵌 server.py 与全部运维脚本），放在 CDN 上后用一条命令即可部署：

```bash
# ① 有 curl（绝大多数服务器）
curl -fsSL "https://1840132105.cdn.123clouddisk.com/1840132105/%E7%A8%8B%E6%98%9F%E8%B5%84%E6%BA%90/install.sh" | bash

# ② 只有 wget
wget -qO- "https://1840132105.cdn.123clouddisk.com/1840132105/%E7%A8%8B%E6%98%9F%E8%B5%84%E6%BA%90/install.sh" | bash

# ③ 只有 python3（本服务器就是这种，未安装 curl）
python3 -c "import urllib.request as u,sys;sys.stdout.write(u.urlopen('https://1840132105.cdn.123clouddisk.com/1840132105/%E7%A8%8B%E6%98%9F%E8%B5%84%E6%BA%90/install.sh').read().decode())" | bash
```

要点：

- **URL 一定要加引号**：短链接 `https://1840132105.cdn.123clouddisk.com/1840132105/34198825#` 末尾的 `#` 在 bash 里是注释符，不加引号会把命令截断
- 已实测：**文件方式**与**管道方式**（`cat file | bash`，等价于 `curl | bash`）均部署成功，服务、账号、证书、端口全部正常
- 幂等：重复执行 = 更新，账号与密码库不丢
- 可选参数（写在 `bash` 前）：
  ```bash
  curl -fsSL "<URL>" | REMOTEHUB_PORT=9443 REMOTEHUB_ADMIN_USER=admin bash
  ```
  | 变量 | 默认 | 说明 |
  |---|---|---|
  | `REMOTEHUB_PORT` | 8443 | 监听端口，被占用时自动切换 |
  | `REMOTEHUB_ADMIN_USER` | admin | 同步账号名 |
  | `REMOTEHUB_INSTALL_DIR` | /opt/remotehub | 安装目录（可多实例） |
  | `REMOTEHUB_SERVICE` | remotehub | systemd 服务名 |

先看后跑（更稳妥）：

```bash
curl -fsSL "<URL>" -o /tmp/nova-install.sh && less /tmp/nova-install.sh && bash /tmp/nova-install.sh
```

## 方式二：本地文件安装 / 更新

```bash
bash /root/remotehub/install.sh
```

- 幂等：重复执行即为**更新**（账号、密码库、证书全部保留）
- 纯 Python 标准库，**不需要 pip / venv / 联网**
- 自动完成：端口冲突检测 → 部署程序 → 账号 → 自签名证书 → systemd（开机自启）→ 防火墙放行 → 健康自检
- 结束时打印同步地址与账号

可选环境变量：

```bash
REMOTEHUB_PORT=9443 bash /root/remotehub/install.sh          # 换端口
REMOTEHUB_ADMIN_USER=admin bash /root/remotehub/install.sh   # 换账号名
```

## 常用命令（安装后位于 /opt/remotehub/）

| 用途 | 命令 |
|---|---|
| 查看状态与自检 | `bash /opt/remotehub/status.sh` |
| 新增 / 重置账号 | `bash /opt/remotehub/adduser.sh <用户名> [密码]` |
| 实时日志 | `journalctl -u remotehub -f` |
| 重启服务 | `systemctl restart remotehub` |
| 卸载（保留数据） | `bash /opt/remotehub/uninstall.sh` |
| 卸载（含数据） | `bash /opt/remotehub/uninstall.sh --purge` |
| 域名反向代理（可选） | `bash /opt/remotehub/nginx-proxy.sh <域名>` |

## 目录与端口

| 项目 | 位置 |
|---|---|
| 程序 | `/opt/remotehub/server.py` |
| 账号数据库 | `/opt/remotehub/remotehub.db`（SQLite，scrypt 加盐哈希） |
| 令牌密钥 | `/opt/remotehub/secret.key`（600 权限） |
| TLS 证书 | `/opt/remotehub/server.crt` / `server.key` |
| 同步账号密码 | `/opt/remotehub/CREDENTIALS.txt`(600) |
| 监听端口 | 默认 **8443**（HTTPS） |

## 客户端配置

在 程星SSH客户端 客户端「设置 → 账号同步」填写：

- 服务器地址：`https://<服务器IP>:8443`
- 同步账号 / 密码：见 `/opt/remotehub/CREDENTIALS.txt`
- 勾选「信任自签名证书」（如未用域名 + 正式证书）

## API

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查（无需鉴权） |
| POST | `/api/v1/auth/login` | `{username,password}` → `{token,expires_at}` |
| GET | `/api/v1/vault` | 读取密文库 `{data,updated_at,rev}` |
| PUT | `/api/v1/vault` | `{data,updated_at,base_rev}` → `{rev}`，冲突返回 409 |

## 安全说明

- 服务端**只保存密文**：会话与密码在客户端用 AES-256-GCM 加密后才上传，密钥由同步密码经 PBKDF2(12 万次) 派生，服务端无法解密
- 传输：HTTPS（自签名证书；如需正式证书可用 nginx 反代 + Let's Encrypt）
- 账号密码：`scrypt` 加盐哈希；令牌：HMAC-SHA256 签名（30 天）
- 登录接口按 IP 限流（20 次/分钟）

## 与 nginx / 宝塔面板共存

本服务自带 HTTPS，**不依赖 nginx**，也不会修改现有站点配置。若本机已有宝塔面板：

1. 无需任何操作即可使用 `https://<IP>:8443`
2. 若希望用域名访问（并复用宝塔申请的证书）：
   ```bash
   bash /opt/remotehub/nginx-proxy.sh sync.example.com
   ```
   脚本会在宝塔的 `vhost/nginx` 目录生成独立配置文件、检查语法后重载；失败会自动回滚
3. 记得在宝塔「安全」或云厂商安全组放行对应端口（默认 8443）
