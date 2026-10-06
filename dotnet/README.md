# 程星SSH客户端 · 命令行版（cx-ssh）

开源的 SSH / SFTP / FTP / RDP 命令行客户端，是 [cx-ssh-client](https://github.com/cx928/cx-ssh-client) 项目的 .NET 8 实现之一。
与 **WinUI 图形版共用同一份加密密码库**，并共用同一套账号同步协议，两者可以互相读写、交叉同步。

- 界面语言：简体中文
- 许可协议：[MPL-2.0](LICENSE)
- 目标框架：`net8.0-windows`（Windows 10 1809 及以上）
- 命令行程序名：`cx-ssh`

---

## 1. 目录与文件

| 文件 | 说明 |
|---|---|
| `CxSshClient.Cli.csproj` | 项目文件（`OutputType=Exe`、`AssemblyName=cx-ssh`、`net8.0-windows`） |
| `Program.cs` | 程序入口、命令分发、全局帮助 |
| `Models/SessionInfo.cs` | 会话模型与协议枚举（数值与图形版严格一致） |
| `Models/VaultData.cs` | 密码库模型、JSON 容错解析、合并算法 |
| `Services/ArgParser.cs` | 自研参数解析（支持 `--k v`、`--k=v`、开关，无第三方依赖） |
| `Services/Output.cs` | 中文控制台输出、CJK 宽度对齐表格、口令遮罩 |
| `Services/CryptoService.cs` | DPAPI / AES-256-GCM / PBKDF2 封装 |
| `Services/VaultService.cs` | 密码库读写（原子写入）、查找、增删改 |
| `Services/SyncService.cs` | 同步协议客户端（登录 / 拉取 / 推送 / 409 合并重推） |
| `Services/SyncConfig.cs` | 同步参数本地保存（口令经 DPAPI 加密） |
| `Services/ImportExportService.cs` | CSV 与加密备份的导入导出、合并导入 |
| `Services/SshConnectionFactory.cs` | SSH.NET 连接构造与中文错误翻译 |
| `Services/SshService.cs` | 远程命令执行、交互式 ssh.exe、SFTP 会话 |
| `Services/FtpService.cs` | FTP / FTPS 会话（FluentFTP） |
| `Services/RdpLauncher.cs` | cmdkey 注入凭据 + mstsc 启动与清理 |
| `Commands/VaultCommands.cs` | `list` / `add` / `rm` / `show` |
| `Commands/ConnectionCommands.cs` | `exec` / `ssh` / `sftp` / `ftp` / `rdp` |
| `Commands/DataCommands.cs` | `import` / `export` / `sync` |

## 2. 编译与运行

本机 .NET 8 SDK 不在 `PATH`，请用绝对路径调用：

```powershell
# 编译（Release，win-x64）
C:\dotnet8\dotnet.exe build  <仓库>\cx-ssh-client\dotnet\CxSshClient.Cli.csproj -c Release -r win-x64

# 运行
C:\dotnet8\dotnet.exe run --project <仓库>\cx-ssh-client\dotnet\CxSshClient.Cli.csproj -c Release -r win-x64 -- list
```

编译产物：

```
dotnet\bin\Release\net8.0-windows\win-x64\cx-ssh.exe
```

依赖的 NuGet 包（均可从本地缓存离线还原）：

| 包 | 版本 | 用途 |
|---|---|---|
| `SSH.NET` | 2024.0.0 | SSH 命令执行、交互式终端、SFTP |
| `FluentFTP` | 50.0.1 | FTP / FTPS |
| `System.Security.Cryptography.ProtectedData` | 8.0.0 | DPAPI 本地加密 |

> 交互式 `cx-ssh ssh` 需要系统自带的 `ssh.exe`（Windows 可选功能「OpenSSH 客户端」）。
> `cx-ssh rdp` 需要 `mstsc.exe`（Windows 专业版/企业版自带）。

## 3. 会话存储（与图形版互通）

| 项目 | 值 |
|---|---|
| 路径 | `%LOCALAPPDATA%\cx-ssh-client\vault.dat` |
| 内容 | `ProtectedData.Protect(UTF8(json), null, CurrentUser)` 的字节（DPAPI，仅当前 Windows 用户可解密） |
| JSON | `{"sessions":[{"Id":"32位hex","Name":"","Group":"","Protocol":0,"Host":"","Port":22,"Username":"","Password":"","PrivateKeyPath":"","Note":"","CreatedAt":0,"UpdatedAt":0,"Extra":"{}"}],"tombstones":{},"updatedAt":0}` |
| `Protocol` | `Ssh=0` `Sftp=1` `Rdp=2` `Ftp=3` `Vnc=4` |

文件不存在、被截断、无法解密或内容不是合法 JSON 时，一律按**空库**处理并打印一条中文提示，绝不崩溃。

临时改用其它数据目录（便于测试或便携使用）：

```powershell
cx-ssh list --data-dir D:\my-vault
# 或设置环境变量
$env:CX_DATA_DIR = "D:\my-vault"
```

## 4. 命令一览

```
cx-ssh <命令> [参数]

会话管理    list  add  rm  show  import  export
远程连接    exec  ssh  sftp  ftp  rdp
账号同步    sync
全局选项    --data-dir <目录>   --help   --version
```

每个命令都支持 `--help`，例如 `cx-ssh sftp --help`。

### 退出码约定

| 退出码 | 含义 |
|---|---|
| `0` | 成功 |
| `1` | 运行期失败（连接失败、文件不存在、同步失败…）或用户取消 |
| `2` | 用法错误（未知命令 / 参数不合法） |
| `N` | `exec` 直接返回**远端命令**的退出码，便于脚本串联 |

---

### 4.1 `list` — 列出会话

```powershell
cx-ssh list
cx-ssh list --protocol ssh
cx-ssh list --group 生产 --json
```

| 选项 | 说明 |
|---|---|
| `--protocol <ssh\|sftp\|rdp\|ftp\|vnc>` | 只显示指定协议 |
| `--group <分组>` | 只显示指定分组 |
| `--json` | 以 JSON 输出，便于脚本处理 |

### 4.2 `add` — 新增会话

```powershell
cx-ssh add --name 生产Web --host 203.0.113.10 --user root --password 你的口令 --group 生产
cx-ssh add --name 内网FTP --host 192.0.2.20 --protocol ftp --port 21 --user demo
cx-ssh add --name 跳板机 --host 198.51.100.5 --user ops --key C:\keys\id_ed25519
cx-ssh add --name 办公桌面 --host 192.0.2.30 --protocol rdp --extra '{"fullscreen":false,"width":1600,"height":900}'
```

| 选项 | 说明 |
|---|---|
| `--name`（必填） | 会话名称，供其它命令引用 |
| `--host`（必填） | 主机名或 IP |
| `--protocol` | `ssh`(默认) / `sftp` / `rdp` / `ftp` / `vnc` |
| `--port` | 默认按协议取 `22 / 22 / 3389 / 21 / 5900` |
| `--user` | 登录用户名（也可写 `--username`） |
| `--password` | 登录口令 |
| `--group` / `--note` | 分组 / 备注 |
| `--key` | 私钥路径（与口令同时存在时**优先用私钥**认证） |
| `--extra` | 协议扩展设置（JSON），如 RDP 的 `fullscreen/admin/width/height`、FTP 的 `encryption/mode` |
| `--force` | 同名会话存在时覆盖 |

### 4.3 `rm` — 删除会话

```powershell
cx-ssh rm 生产Web
cx-ssh rm c6af3a60 --yes      # 用 ID 前缀，并跳过确认
```

删除会在密码库中留下**删除标记（tombstone）**，下次 `sync` 时把删除动作传播到其它设备。

### 4.4 `show` — 查看详情

```powershell
cx-ssh show 生产Web
cx-ssh show 生产Web --show-password     # 明文显示口令
cx-ssh show 生产Web --json
```

默认隐藏口令（显示为 `********`），避免终端记录或投屏泄露。

### 4.5 `exec` — 执行一条远程命令

```powershell
cx-ssh exec 生产Web "uname -a"
cx-ssh exec 生产Web "df -h" --out D:\tmp\df.txt
cx-ssh exec root@203.0.113.10 "systemctl status nginx" --password 你的口令
cx-ssh exec 生产Web "sleep 300" --timeout 600
```

输出标准输出、标准错误与远端退出码（`CX_EXIT_CODE=` 行），**进程退出码等于远端退出码**。
目标既可以是密码库里的会话名 / ID 前缀，也可以是临时写法 `user@host[:端口]`。

### 4.6 `ssh` — 交互式登录

```powershell
cx-ssh ssh 生产Web
cx-ssh ssh 生产Web --port 2222
cx-ssh ssh 生产Web --batch        # -o BatchMode=yes，禁止交互提问
```

直接调用系统 `ssh.exe`，因此键盘交互、`ssh-agent`、`known_hosts`、`~/.ssh/config` 与手动敲 `ssh` 完全一致。

> `ssh.exe` 不会读取本程序密码库里的口令。若该会话只保存了口令而没有私钥，
> 请在提示时手动输入口令；需要免交互请为该会话配置私钥（`add --key`）。

### 4.7 `sftp` — 文件传输

```powershell
cx-ssh sftp ls  生产Web                 # 当前工作目录
cx-ssh sftp ls  生产Web /root
cx-ssh sftp get 生产Web /etc/os-release D:\tmp\os-release.txt
cx-ssh sftp put 生产Web D:\tmp\a.txt /root/a.txt
cx-ssh sftp get 生产Web /var/log/syslog D:\tmp\syslog.txt --quiet
```

`--quiet` 关闭单行进度条（重定向输出时自动静默）。

### 4.8 `ftp` — FTP / FTPS

```powershell
cx-ssh ftp ls  内网FTP /
cx-ssh ftp get 内网FTP /pub/a.zip D:\tmp\a.zip
cx-ssh ftp put 内网FTP D:\tmp\b.txt /pub/b.txt
```

加密方式与数据连接模式取自会话的 `Extra` 字段（键名与图形版一致）：

```json
{"encryption":"none|explicit|implicit","mode":"passive|active"}
```

```powershell
cx-ssh add --name 加密FTP --host 192.0.2.40 --protocol ftp --user demo --extra '{"encryption":"explicit","mode":"passive"}'
```

### 4.9 `rdp` — 远程桌面

```powershell
cx-ssh rdp 办公桌面
```

流程：先用 `cmdkey` 把凭据写入 Windows 凭据管理器（`TERMSRV/<主机>`），再生成临时 `.rdp` 文件并启动 `mstsc.exe`；
远程桌面窗口关闭后自动删除 `.rdp` 文件与凭据。显示方式由 `Extra` 控制：

```json
{"fullscreen":true,"admin":false,"width":1600,"height":900}
```

### 4.10 `import` / `export` — 导入导出

```powershell
# CSV（明文，含口令）
cx-ssh export D:\backup\sessions.csv
cx-ssh import D:\backup\sessions.csv

# 加密备份（PBKDF2-SHA256 12 万次 + AES-256-GCM）
cx-ssh export D:\backup\vault.cxbak --encrypted --passphrase 你的口令
cx-ssh import D:\backup\vault.cxbak --encrypted --passphrase 你的口令
cx-ssh import D:\backup\vault.cxbak --encrypted --passphrase 你的口令 --replace --yes
```

- CSV 表头：`name,group,protocol,host,port,username,password,note`（中英文列名均可识别，至少要有主机列）。
- 默认**合并导入**：按 `(协议, 主机, 端口, 用户名)` 判定同一条会话，相同则更新，不同则新增。
- `--replace` 用备份整体替换密码库。
- 加密备份格式：`{"V":1,"Salt":"base64","Data":"base64(iv|tag|cipher)"}`，与图形版互通。

> CSV 是**明文**，包含口令，请妥善保管；需要加密请使用 `--encrypted`。

### 4.11 `sync` — 与自建服务端同步

```powershell
cx-ssh sync --server https://sync.example.com:8443 --user 你的账号 --password 你的同步口令
cx-ssh sync --server https://sync.example.com:8443 --user 你的账号 --password 你的同步口令 --trust-self-signed
cx-ssh sync --server https://sync.example.com:8443 --user 你的账号 --password 你的同步口令 --trust-self-signed --save
cx-ssh sync --dry-run
```

| 选项 | 说明 |
|---|---|
| `--server <URL>` | 服务端地址，缺省协议时自动补 `https://` |
| `--user` / `--password` | 同步账号与口令 |
| `--trust-self-signed` | 接受自签名证书（仅建议内网自建服务使用） |
| `--save` | 保存本次参数到 `sync.json`（口令经 DPAPI 加密），之后可直接 `cx-ssh sync` |
| `--dry-run` | 只拉取比较，不写回本地、不推送 |

**协议**（与图形版一致）：

```
POST /api/v1/auth/login {username,password}      -> {token}
GET  /api/v1/vault                               -> {data,rev,updated_at}   （404 = 云端无数据）
PUT  /api/v1/vault {data,updated_at,base_rev}    -> {rev}                   （版本冲突 HTTP 409）
Authorization: Bearer <token>
```

- `data` = `base64(iv(12) | tag(16) | cipher)`，AES-256-GCM。
- 密钥 = `PBKDF2-SHA256(同步口令, UTF8("novaremote-sync:" + 用户名小写), 120000 次, 32 字节)`
  （`novaremote-sync:` 是历史常量，**不可更改**）。
- 合并规则：按会话 `Id` 取 `UpdatedAt` 较新者；删除标记（tombstone）优先。
- 遇到 `409` 会自动「重新拉取 → 再合并 → 重推一次」。

## 5. 安全说明

- 密码库使用 **Windows DPAPI（CurrentUser）** 加密，复制到其它用户或其它机器后无法解密。
- 同步到云端的是**客户端加密后**的密文，服务端无法解密内容。
- `show` 默认隐藏口令；`list` 只显示「已保存 / -」。
- `export` 生成的 CSV 是明文，请自行妥善保管。
- `--trust-self-signed` 会接受任意服务端证书，仅在可信内网使用。

## 6. 常见问题

**Q：`cx-ssh ssh` 提示找不到 ssh.exe？**
安装 Windows 可选功能「OpenSSH 客户端」：设置 → 系统 → 可选功能 → 添加功能 → OpenSSH 客户端。

**Q：`exec` 报「认证被拒绝」？**
检查口令 / 私钥是否正确，以及服务端是否允许口令登录（`PasswordAuthentication yes`）。
若连接被拒绝后长时间无响应最终超时，通常是服务端或中间网络丢弃了认证报文。

**Q：`sync` 报「云端数据无法解密」？**
说明服务端保存的密文与当前同步口令不匹配（例如在服务端改过账号密码）。
需要用原来上传时的口令，或清空服务端该账号的密码库后重新推送。

**Q：`sync` 报证书错误？**
自建服务的自签名证书需要加 `--trust-self-signed`；或改用受信任的证书。

**Q：如何在脚本里安全传口令？**
避免写在命令行历史里，推荐先 `cx-ssh sync ... --save`（口令经 DPAPI 加密保存），
之后直接执行 `cx-ssh sync`。

## 7. 许可协议

本项目以 **Mozilla Public License 2.0（MPL-2.0）** 发布，全文见 [LICENSE](LICENSE)。

```
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. If a copy of the MPL was not distributed with this
file, You can obtain one at http://mozilla.org/MPL/2.0/.
```
