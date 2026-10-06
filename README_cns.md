# 程星SSH客户端 (cx-ssh-client)

程星工作室开发的 SSH 客户端工具，支持 SSH 远程连接、SFTP 文件管理，并同时提供 **WinUI 图形版 / .NET 命令行版 / C++ 原生版** 三种实现。

[简体中文](README_cns.md) | [繁體中文](README.md) | [English](README_en.md)

⚠️ 当前版本为预览版本（Preview），主要用于功能验证与测试。

## 📦 版本信息

- 当前版本：**v1.0.0**
- 官网：<https://www.cxbk.cc>
- 下载页面：<https://github.com/cx928/cx-ssh-client/releases>

## 🧩 四种实现

| 实现 | 目录 | 技术栈 | 说明 |
|---|---|---|---|
| **WinUI 图形版**（推荐） | `winui/` | WinUI 3 + .NET 8 | 五协议客户端 + 加密密码库 + 账号同步，Mica 界面 |
| **.NET 命令行版** | `dotnet/` | .NET 8 CLI | 终端里管理会话、执行命令、传文件、同步 |
| **C++ 原生版** | `cpp/` | Win32 API（MinGW 编译） | 零第三方依赖，单文件程序，含协议探测 |
| **Python 版（历史版本 v0.0.3）** | `legacy/` | Python 3 + paramiko | 早期预览版，保留备查 |

## ✨ 主要功能（WinUI 图形版）

| 协议 | 能力 |
|---|---|
| **SSH** | 内嵌终端（xterm.js + WebView2）、256 色主题、字号调节、窗口自适应（NAWS）、密码/私钥认证、断线重连 |
| **SFTP** | 双栏文件管理、上传/下载进度、新建目录、删除、双击进入 |
| **FTP** | 同上，支持 FTP / 显式 FTPS / 隐式 FTPS，主动/被动模式 |
| **RDP** | 调用系统 mstsc，凭据经 cmdkey 注入并在退出后自动清除；全屏/分辨率/管理员会话可配置 |
| **VNC** | 自研 RFB 3.3/3.7/3.8 引擎（Hextile/CopyRect/Raw）、键鼠映射、剪贴板双向同步、Ctrl+Alt+Del、缩放与只读模式 |
| **密码库** | 会话与密码用 Windows DPAPI 加密存储；支持加密备份（AES-256-GCM + PBKDF2）与 CSV 明文导入导出 |
| **账号同步** | 自建 Linux 服务端（零依赖 Python + systemd）、服务端只存密文、rev 乐观锁 + 冲突自动合并 + 删除墓碑 |


## 🚀 快速开始

### 1. 图形版（安装包）

下载 `cx-ssh-client-Setup-1.0.0.exe` 双击安装（无需管理员权限），或使用便携版 ZIP 解压即用。

源码构建：

```powershell
dotnet build winui/CxSshClient.csproj -c Release -p:Platform=x64 -r win-x64
dotnet publish winui/CxSshClient.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o dist
```

### 2. 命令行版

```powershell
dotnet build dotnet/CxSshClient.Cli.csproj -c Release -r win-x64
cx list
cx add --name 生产服务器 --host 1.2.3.4 --user root --password ******
cx exec 生产服务器 "uname -a"
cx sftp ls 生产服务器 /root
```

### 3. C++ 原生版

```powershell
cd cpp
./build.ps1          # 需要 MinGW-w64 g++
```

## ☁️ 账号同步服务端（可选）

`server/` 内含零依赖（纯 Python 标准库）的同步服务端，一条命令部署到 Linux：

```bash
curl -fsSL "<install-online.sh 的下载地址>" | bash
```

服务端只保存**密文**（会话与密码在客户端用 AES-256-GCM 加密，密钥由同步密码经 PBKDF2 派生），并支持 systemd 托管、一键更新、状态自检、账号管理。详见 `server/README-server.md`。

## 🔐 安全说明

- 本地密码库使用 Windows **DPAPI**（当前用户）加密，换用户/换机器无法解密
- 导出备份使用 **AES-256-GCM + PBKDF2-SHA256（12 万次）**
- 同步服务端仅存储密文，账号密码用 `scrypt` 加盐哈希，令牌为 HMAC-SHA256 签名
- 本仓库不含任何服务器地址与密码；部署脚本的凭据全部通过环境变量传入

## 🛠 构建要求

- Windows 10 1809+ / Windows 11
- .NET 8 SDK（WinUI 与 CLI 版）
- Windows App SDK 1.5（NuGet 自动还原）
- MinGW-w64 g++（仅 C++ 版）
- Microsoft Edge WebView2 Runtime（SSH 终端渲染，Win11 自带）

## 📄 许可证

[Mozilla Public License 2.0](LICENSE)
