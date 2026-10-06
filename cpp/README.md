# 程星SSH客户端 · 原生 C++ 版（cx-ssh-client / cpp）

> 程星工作室 · 官网 [cxbk.com/cc](https://cxbk.com/cc) · 许可证 **MPL-2.0**

这是 cx-ssh-client 的**零依赖原生 C++ 版本**：只使用 Windows 自带的 Win32 API 与 WinSock2，
不依赖 Qt、libssh2、OpenSSL、paramiko 或任何第三方运行库。编译产物是一个单文件绿色 exe，
拷贝到任何 Windows 7 SP1 及以上系统的机器上都能直接运行。

* 语言标准：C++17
* 界面：纯 Win32 API（简体中文）
* 编译器：MinGW-w64 g++（UCRT 运行时 + POSIX 线程）
* 存储：`%LOCALAPPDATA%\cx-ssh-client\sessions.dat`，整块 **DPAPI 加密**
* 版本：v0.1.0

---

## 一、功能一览

### 1. 会话管理主窗口

* 工具栏按钮（含快捷键）：**新建 / 编辑 / 删除 / 连接 / 测试连接 / 导入CSV / 导出CSV**
* 原生 `ListView`（报表模式）：名称、协议、主机、端口、用户名、备注
* **双击某一行即连接**；`Enter` 连接，`Del` 删除，`Ctrl+N` 新建，`Ctrl+E` 编辑，`Ctrl+T` 测试连接，`F5` 连接
* 底部状态栏显示会话总数与最近一次操作结果
* 窗口**可自由缩放**，工具栏 / 列表 / 状态栏自动重排
* **高 DPI 感知**：启动时调用 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`
  （旧系统依次回退到 `SetProcessDPIAware` / `shcore!SetProcessDpiAwareness`），
  字体与所有尺寸按 `GetDpiForWindow` 缩放，并处理 `WM_DPICHANGED`

### 2. 会话编辑对话框

自建模态窗口（不依赖对话框资源模板），字段如下：

| 字段 | 说明 |
| --- | --- |
| 名称 | 留空时自动填 `用户名@主机` |
| 协议 | 下拉框：SSH / SFTP / FTP / RDP / VNC |
| 主机 | 必填，支持域名或 IP |
| 端口 | 1–65535，仅允许数字；切换协议时若端口仍是旧协议默认值会自动同步 |
| 用户名 | — |
| 密码 | 掩码显示，仅用于本地保存，不参与任何网络登录 |
| 备注 | 多行文本 |

### 3. 本地加密存储

* 默认路径：`%LOCALAPPDATA%\cx-ssh-client\sessions.dat`
* 启动时按顺序探测「可建目录且可写文件」的位置，取第一个可用的（结果在进程内缓存）：
  1. `%LOCALAPPDATA%\cx-ssh-client\sessions.dat`（默认）
  2. `<exe 所在目录>\cx-ssh-client-data\sessions.dat`（便携模式／受限环境）
  3. `%TEMP%\cx-ssh-client\sessions.dat`

  这样在 AppData 被组策略、杀软或沙箱限制写入时，程序仍可正常工作，不会一保存就失败。
  实际使用的路径可以在「帮助 → 关于」里看到。
* 整个数据块用 **DPAPI**（`CryptProtectData` / `CryptUnprotectData` +
  `CRYPTPROTECT_UI_FORBIDDEN`）加密，**只有当前 Windows 用户能解开**，无法弹窗打扰用户
* 先写 `sessions.dat.tmp` 再原子改名，避免写一半断电毁掉原数据
* 明文格式（解密后）是简单的 UTF-8 制表符文本，便于人工排查：

```
CXSSHCLIENT-SESSIONS	1
name	protocol	host	port	username	password	note
```

  字段里的 `\`、制表符、回车、换行分别转义为 `\\`、`\t`、`\r`、`\n`。
* 文件头是 8 字节魔数 `CXSSH001` + 4 字节小端版本号
* **文件损坏 / 被别的账户加密 / 版本过高时不会崩溃**：弹窗说明原因，把坏文件改名为
  `sessions.dat.bad-YYYYMMDD-HHMMSS` 备份，然后以空列表重建

### 4. 测试连接（真实协议探测）

纯 WinSock2 实现，**非阻塞 connect + select**，默认 5 秒超时；探测在后台线程执行，
界面不会卡死。结果用 `MessageBox` 显示成功/失败原因、耗时和探测到的 banner。

| 协议 | 探测方式 |
| --- | --- |
| SSH / SFTP | 先发送客户端标识 `SSH-2.0-CXSSHClient_1.0`，再读服务端标识，校验是否以 `SSH-` 开头；`SSH-2.0-` / `SSH-1.99-` 判为合规，仅 `SSH-1.x` 会提示旧版风险 |
| FTP | 读取欢迎行，校验是否以 `220` 开头 |
| VNC | 读取 12 字节 `RFB 003.00x\n`，解析 major/minor，并**回发 12 字节客户端版本号**（16 位大端 major/minor，最高 3.8）；3.7+ 还会继续读 1 字节安全类型数量以确认握手推进 |
| RDP | 只做 TCP 连通性测试（RDP 需要 TLS + CredSSP 协商，本程序不实现完整握手，会在结果里明确说明） |

### 5. 连接

按协议调用系统已有客户端，不自己实现终端：

| 协议 | 动作 |
| --- | --- |
| SSH / SFTP | `ShellExecuteW("open", "cmd.exe", "/c start \"cx-ssh-client\" cmd.exe /k ssh -p <端口> <用户>@<主机>")`，在新控制台窗口里跑系统自带 OpenSSH；用 `/k` 保证 ssh 退出后窗口保留，方便看报错 |
| RDP | `mstsc.exe /v:<主机>:<端口>` |
| FTP | 用资源管理器打开 `ftp://<用户>@<主机>:<端口>` |
| VNC | 用系统注册的 `vnc://<主机>:<端口>` 处理程序；若没有注册则提示安装 TightVNC / RealVNC / TigerVNC |

> 安全说明：主机名与用户名会被拼进 `cmd.exe` 命令行，因此连接前会校验并拒绝包含
> `& | < > ^ " ' \` % !` 等 shell 元字符的字段，避免从 CSV 导入的数据造成命令注入。

### 6. CSV 导入 / 导出

* 列顺序固定：`name,protocol,host,port,username,password,note`
* **UTF-8 带 BOM + CRLF**，Excel 双击不乱码
* 完整支持带引号的字段：字段内含逗号、双引号、换行时自动加引号，内部 `"` 写成 `""`；
  导入时用状态机解析，可正确处理引号内的逗号与换行
* 导入时可选「追加」或「替换」，并自动跳过表头行、忽略空行

---

## 二、目录与文件

```
cpp/
├─ cx-ssh-client.cpp      入口：wWinMain、标准输出接管、命令行参数分发
├─ src/
│  ├─ common.h            公共头（Windows 版本宏、通用工具声明）
│  ├─ session.h/.cpp      会话模型、DPAPI 加密存储、文本序列化、CSV 导入导出
│  ├─ probe.h/.cpp        WinSock2 TCP 连接 + 各协议 banner 探测
│  └─ ui.h/.cpp           主窗口、工具栏、ListView、状态栏、编辑对话框、连接分发
├─ app.rc                 资源脚本（嵌入清单）
├─ app.manifest           comctl32 v6 + 高 DPI + UTF-8 代码页清单
├─ build.ps1              一键编译脚本
└─ README.md              本文件
```

编译产物位于 `build\cx-ssh-client.exe`。

---

## 三、编译

### 1. 准备 MinGW-w64 g++

任选一种：

```powershell
# 方式 A：winget 安装 WinLibs（UCRT + POSIX 线程）
winget install --id BrechtSanders.WinLibs.POSIX.UCRT --silent --accept-package-agreements --accept-source-agreements

# 方式 B：MSYS2
#   安装后在 UCRT64 终端里执行：pacman -S mingw-w64-ucrt-x86_64-gcc

# 方式 C：scoop
scoop install mingw-winlibs
```

`build.ps1` 会按下面的顺序自动找到 g++，都不需要你手工配 PATH：

1. 当前 PATH 里的 `g++`
2. `%LOCALAPPDATA%\Microsoft\WinGet\Packages\*\mingw64\bin\g++.exe`
3. `%USERPROFILE%\scoop\apps\mingw*\current\bin\g++.exe`
4. `C:\msys64\ucrt64\bin\g++.exe`、`C:\msys64\mingw64\bin\g++.exe`
5. `C:\mingw64\bin\g++.exe`、`C:\mingw-toolchain\mingw64\bin\g++.exe`
6. `C:\ProgramData\chocolatey\lib\mingw\tools\install\mingw64\bin\g++.exe`
7. `C:\Program Files\mingw-w64\*\mingw64\bin\g++.exe`

都找不到时可以用 `-GxxPath` 手工指定。

### 2. 一键编译

```powershell
cd cpp
powershell -ExecutionPolicy Bypass -File build.ps1
```

脚本会：找到 g++ → 用 `windres` 编译 `app.rc`（嵌入清单，启用现代控件外观与高 DPI）→
编译 4 个 .cpp → 统计 error/warning 数量 → 输出 `build\cx-ssh-client.exe`。

常用参数：

```powershell
.\build.ps1 -Clean                              # 先清空 build 目录
.\build.ps1 -GxxPath "C:\msys64\ucrt64\bin\g++.exe"
.\build.ps1 -NoManifest                         # 不嵌入清单（没有 windres 时）
```

### 3. 等价的裸编译命令

不想用脚本时，手工执行下面这一行即可（在 `cpp\` 目录下）：

```powershell
g++ -std=c++17 -municode -O2 -static -mwindows `
    -finput-charset=UTF-8 -fexec-charset=UTF-8 `
    cx-ssh-client.cpp src\session.cpp src\probe.cpp src\ui.cpp build\app.res `
    -o build\cx-ssh-client.exe `
    -lws2_32 -lcomctl32 -lcrypt32 -lshlwapi -lole32 -lshell32 -lcomdlg32 -lgdi32 -ladvapi32
```

（`build\app.res` 可选，用 `windres --input=app.rc --output=build\app.res --include-dir=.` 生成。）

> 源码统一保存为 **UTF-8 带 BOM**，同时编译时显式指定
> `-finput-charset=UTF-8 -fexec-charset=UTF-8`，字符串全部使用 `L"…"` 宽字面量并调用宽字符
> API（`CreateWindowExW` / `MessageBoxW` / `ShellExecuteW` …），因此中文在任何系统区域设置下都不会乱码。
> `-municode` 让入口点使用 `wWinMain`。

---

## 四、用法

### 图形界面

直接双击 `cx-ssh-client.exe`，或：

```powershell
.\build\cx-ssh-client.exe
```

1. 点「新建」，填写名称 / 协议 / 主机 / 端口 / 用户名 / 密码 / 备注，确定；
2. 选中一行，点「测试连接」可以先确认网络与协议是否通；
3. 点「连接」或直接双击该行，即可用系统自带客户端打开会话；
4. 「导出CSV」可以把全部会话导出成 Excel 能直接打开的表格；
   「导入CSV」支持把表格再导回来。

### 命令行（用于脚本 / 排障）

```powershell
# 真实协议探测，结果打印到标准输出
cx-ssh-client.exe --probe ssh 156.239.3.215 22
cx-ssh-client.exe --probe ftp 127.0.0.1 21
cx-ssh-client.exe --probe vnc 127.0.0.1 5900
cx-ssh-client.exe --probe rdp 127.0.0.1 3389

# 自检：CSV 往返 + 文本序列化 + DPAPI + 加密存储往返（共 4 步）
cx-ssh-client.exe --selftest

# 帮助
cx-ssh-client.exe --help
```

退出码：`0` 成功 / `2` 探测失败 / `3` 参数错误。

`--probe` 输出示例：

```
=== cx-ssh-client 协议探测 ===
协议 : ssh
目标 : 156.239.3.215:22
超时 : 5000 毫秒

结果 : 成功 — SSH 服务可用
耗时 : 126 毫秒
BANNER: SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.10
详情 : TCP 156.239.3.215:22 已连接。
详情 : 协议版本符合 SSH-2.0 规范。

结论 : 探测成功
```

程序是 GUI 子系统，但 `--probe` / `--selftest` 会先判断标准输出到底指向管道、文件还是控制台：
重定向时按 UTF-8 字节写入（方便 `> out.txt` 与管道处理），直接挂在控制台上时改用
`WriteConsoleW`，两条路径中文都不会乱码。**不带参数启动时程序完全不碰控制台**，
双击运行不会多弹一个黑色窗口。

### 自检输出示例

```
=== cx-ssh-client --selftest ===

[1/4] CSV 导入导出往返测试
      生成会话数：3
      自检目录：...\cx-ssh-client-selftest.csv
      UTF-8 BOM：存在（Excel 友好）
      重新导入会话数：3
      第 1 条「生产服务器 A」：一致 (PASS)
      ...
[2/4] 会话文本序列化往返（存储格式，含制表符/换行转义）
      文本序列化往返：一致 (PASS)
[3/4] DPAPI（CryptProtectData / CryptUnprotectData）加解密自检
      DPAPI 往返：成功 (PASS)
[4/4] 本地加密存储往返（SaveSessions / LoadSessions）
      加密存储往返：一致 (PASS)
      文件头魔数 CXSSH001：正确
      明文泄漏检查：未发现明文密码，内容已加密 (PASS)

总体结果：全部通过 (SELFTEST PASS)
```

第 4 步会临时占用真实存储文件，跑完自动还原（原本不存在就删除），不会破坏你的会话数据。

---

## 五、已知限制

1. **不实现 SSH 协议本身**。本程序是「会话管理器 + 连接启动器」，真正的 SSH 会话交给系统自带的
   OpenSSH（`ssh.exe`）或用户自己的客户端。因此在 exe 里**不存在**任何加密算法实现，
   也不会有密码/token 之类的凭据。
2. **密码不会自动登录**。出于同样的原因（不做 SSH 协议），保存的密码只存在本地供你查阅，
   不会喂给 `ssh.exe`。自动登录需要 sshpass / plink 之类的外部工具。
3. **RDP 只做 TCP 连通性测试**。完整 RDP 需要 X.509 + TLS + CredSSP(NLA) 协商，
   属于另一个量级的工程；`mstsc` 本身也没有可编程的握手接口。
4. **VNC 探测只走到版本号交换**（读完 12 字节版本号、回发客户端版本、再读安全类型数量），
   不实现 DES 挑战响应认证。
5. **SFTP 与 SSH 共用同一套探测与连接路径**，因为 SFTP 是跑在 SSH 之上的子系统；
   连接时同样使用 `ssh`，需要传文件时请用 `scp`/`sftp` 或 WinSCP。
6. **没有实现列表排序与搜索**，会话很多时只能靠滚动；数据量在几百条量级时完全够用。
7. **DPAPI 是「当前用户 + 当前机器」绑定的**：把 `sessions.dat` 拷到别的账户或别的电脑上解不开，
   程序会提示并把文件备份后重建。跨机迁移请用「导出CSV」。
8. **CSV 里是明文密码**。导出文件带 BOM 且不加密，请自行妥善保管，别丢到公共目录。
9. **依赖系统已安装对应客户端**：`ssh.exe`（Windows 10 1809+ 通常自带，属于「可选功能」）、
   `mstsc.exe`（系统自带）、VNC 查看器（需自行安装）。缺失时程序会给出明确提示。
10. **仅支持 Windows x64**。代码里用到的都是 `_WIN32_WINNT >= 0x0601` 的 API。
    本仓库用 MSYS2 的 **UCRT** 工具链构建，生成的 exe 只依赖系统 DLL
    （KERNEL32 / USER32 / GDI32 / COMCTL32 / SHELL32 / comdlg32 / CRYPT32 / WS2_32 以及 UCRT 的
    `api-ms-win-crt-*`）：Windows 10/11 开箱即用；Windows 7 SP1 / 8.1 需要先装微软的
    通用 C 运行库更新（KB2999226，通常已随 Windows Update 装上）。
    如果目标机器上没有 UCRT，改用 MSVCRT 版工具链重新编译即可
    （`build.ps1 -GxxPath <msvcrt 版 g++>`），源码不用改。
11. **工具栏、状态栏不支持用户自定义**（不能拖动、隐藏或改顺序），
    按钮布局写死在 `BuildToolbar()` 里。这块用的是最朴素的 comctl32 工具栏，
    好处是不用维护任何配置，也就能保持零依赖。
