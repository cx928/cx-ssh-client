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
│  ├─ common.h/.cpp       公共头 + 通用工具（UTF-8 转换、Win32 错误文本、格式化）
│  ├─ session.h/.cpp      会话模型、DPAPI 加密存储、文本序列化、CSV 导入导出
│  ├─ probe.h/.cpp        WinSock2 TCP 连接 + 各协议 banner 探测
│  └─ ui.h/.cpp           主窗口、工具栏、ListView、状态栏、编辑对话框、连接分发
├─ tools/
│  └─ gen.cpp             验证辅助工具（造 N 条会话 / dump 存储文件），不是程序本体
├─ app.rc                 资源脚本（嵌入清单）
├─ app.manifest           comctl32 v6 + 高 DPI + UTF-8 代码页清单
├─ build.ps1              一键编译脚本
├─ uitest.ps1             跨进程 UI 自动化测试（25 项断言）
├─ measure-mem.ps1        内存占用测量脚本
└─ README.md              本文件
```

编译产物位于 `build\cx-ssh-client.exe`。

### 自测脚本怎么用

```powershell
# 1) 编译程序 + 验证工具
.\build.ps1 -Clean -WithTools

# 2) 跨进程 UI 自动化：真的去点对话框、填字段、点确定，再解密存储文件核对结果
powershell -ExecutionPolicy Bypass -File .\uitest.ps1

# 3) 内存测量：分别加载 0 / 1 / 2000 / 10000 条会话，报告工作集与私有字节
powershell -ExecutionPolicy Bypass -File .\measure-mem.ps1 -Counts 0,1,2000,10000
```

`uitest.ps1` 用的都是标准 Win32 手法，顺便记录了两个容易踩的坑（脚本里有注释）：
`GetWindowTextW` 对**其它进程里的控件**取不到文本（必须改用 `WM_GETTEXT`）；
会进入模态循环的消息（如点击「新建」）必须用 `PostMessage`，否则自己的 `SendMessage` 会被卡住。

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
编译 5 个 .cpp → 统计 error/warning 数量 → 输出 `build\cx-ssh-client.exe`。

常用参数：

```powershell
.\build.ps1 -Clean                              # 先清空 build 目录
.\build.ps1 -GxxPath "C:\msys64\ucrt64\bin\g++.exe"
.\build.ps1 -NoManifest                         # 不嵌入清单（没有 windres 时）
.\build.ps1 -WithTools                          # 顺便编译验证工具 build\gen.exe
```

### 3. 体积与内存相关的编译选项

默认就带上，不需要手工加：

| 选项 | 作用 |
| --- | --- |
| `-ffunction-sections -fdata-sections -Wl,--gc-sections` | 每个函数/数据独立成节，链接时丢弃没被引用的部分 |
| `-fno-rtti` | 本程序不用 RTTI，关掉可省一批类型信息 |
| `-s` | 剥掉符号表 |
| `-static` | 静态链接 libstdc++/libgcc/libwinpthread，产物不依赖任何第三方 DLL |

这几项把 exe 从 921.8 KB 压到 **482.5 KB（−47.7%）**，进程启动时要映射和触碰的页也随之减少。

### 4. 等价的裸编译命令

不想用脚本时，手工执行下面这一行即可（在 `cpp\` 目录下）：

```powershell
g++ -std=c++17 -municode -O2 -static -mwindows `
    -ffunction-sections -fdata-sections -fno-rtti -Wl,--gc-sections -s `
    -finput-charset=UTF-8 -fexec-charset=UTF-8 `
    cx-ssh-client.cpp src\common.cpp src\session.cpp src\probe.cpp src\ui.cpp build\app.res `
    -o build\cx-ssh-client.exe `
    -lws2_32 -lcomctl32 -lcrypt32 -lshlwapi -lole32 -lshell32 -lcomdlg32 -lgdi32 -ladvapi32
```

（`build\app.res` 可选，用 `windres --input=app.rc --output=build\app.res --include-dir=. -O coff` 生成。
注意 **`-O coff` 不能省**：windres 默认输出裸 `.res`，新版 binutils 的 ld 不认。）

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
12. **协议下拉列表一屏只显示约 3 行**（共 5 项，其余靠滚动条）。
    这是本机 comctl32 给下拉列表的高度上限。已经用四种办法验证过都突破不了：
    加大建框高度、`CB_SETMINVISIBLE(5/6/8/10)`、把父窗口撑到 1000px 高、换成系统默认字体
    （行高从 24px 降到 20px，可见行数始终是 3.4 行）。
    **注意这是外观问题，不影响功能**：5 个协议都在列表里，滚动即可选中。
    （顺带说明：这个下拉框原来根本打不开——见下面「已修复的问题」。）

---

## 六、内存占用

在同一台机器上实测（MSYS2 UCRT GCC 16.2.0，Windows GUI 子系统）：

| 会话数 | 数据文件 | 工作集 | 私有字节 | 句柄 | GDI | USER |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 0 KB | 0.79 MB | 2.17 MB | 165 | 24 | 20 |
| 1 | 0.4 KB | 0.43 MB | 2.12 MB | 167 | 24 | 20 |
| 2000 | 304 KB | 3.01 MB | 3.26 MB | 167 | 30 | 22 |
| 10000 | 1.5 MB | 0.51 MB | 8.25 MB | 167 | 24 | 20 |

* **私有字节**才是程序真正占住的内存；工作集里大部分是 libstdc++ / comctl32 / 字体等**共享只读页**。
* 每多一条会话约 **0.55–0.6 KB**（优化前是 1.29 KB）。
* 界面稳定后程序会主动调用一次 `SetProcessWorkingSetSize(-1,-1)`，
  把已经用不到的页还给系统。实测（2000 条会话）：启动第 1 秒工作集约 16 MB，
  第 2 秒降到 0.17 MB，之后稳定在 **0.5 MB 左右**；私有字节始终稳定在 3.2 MB，没有反弹。
  需要时这些页会自动缺页调回，代价很小。

做了这些优化：

| 优化 | 效果 |
| --- | --- |
| ListView 改用**虚拟列表**（`LVS_OWNERDATA` + `LVN_GETDISPINFO`） | 控件不再复制一份全部单元格文本，每条会话只存一份；1500 行的列表内存降约一半 |
| 存储读写去掉整块拷贝 | 保存时文件头与密文分两次写，不再复制一份完整密文；明文加密后立刻释放 |
| 按文件大小 `reserve`、按会话数 `reserve` | 读大文件/拼长文本时不再反复扩容复制 |
| 导入 CSV 不再整份拷贝会话列表 | 「追加失败截断、替换失败换回」，全程 O(1) 换出 |
| `-ffunction-sections -fdata-sections -fno-rtti -Wl,--gc-sections -s` | exe 921.8 KB → **482.5 KB（−47.7%）** |
| 空闲时回收工作集 | 工作集 16 MB → **0.5 MB** |

---

## 七、已修复的问题（相对初版）

| # | 问题 | 现象 | 修法 |
| --- | --- | --- | --- |
| 1 | **协议下拉框完全打不开** | 建框时高度给 0，comctl32 算出的下拉列表高度就是 0；`CB_GETDROPPEDCONTROLRECT` 返回 (0,0)-(0,0)，用户根本没法选协议 | 建框时就把展开区算进高度，`EditLayout` 再按实际行高精确定位；补 `CB_SETMINVISIBLE` |
| 2 | 探测的 TCP 连接与协议握手共用一份超时预算 | 慢链路下 connect 花掉 4 s，握手只剩 1 s，会误报「端口已连接但未收到 SSH 标识」 | 两个阶段各拿完整的 5 s 预算 |
| 3 | Winsock 非阻塞 connect 失败时误报超时 | 端口关闭时应立刻报「拒绝」，却等了 5 s 才报超时 | `select` 同时等 writefds 和 **exceptfds**（Winsock 特有语义） |
| 4 | 探测结果把 TCP 阶段说明覆盖掉 | 结果里看不到「TCP 已连接」这一步 | 拆出 `pr` 再合并 detail |
| 5 | 后台探测线程可能泄漏内存 | 主窗口已销毁时 `PostMessage(NULL,…)` 会成功投递到线程队列而无人处理，`ProbeResult`/`ProbeTask` 两块内存泄漏 | 任务里记下创建时的 HWND，投递前 `IsWindow` 校验，失败就地释放 |
| 6 | 保存失败时界面与磁盘不一致 | 「编辑」保存失败后，列表显示新值但磁盘还是旧值 | 保存失败回滚内存中的会话 |
| 7 | 探测用全局套接字 | 后台线程共用 `g_sock`，存在竞态 | 改成局部变量并在所有路径关闭 |
| 8 | 启动时会多弹一个黑色控制台窗口 | GUI 模式下 `OutInit()` 仍会 AttachConsole/AllocConsole | 只有确定是命令行模式才接管控制台 |
| 9 | 菜单句柄退出时未释放 | — | `WM_DESTROY` 里显式 `DestroyMenu` |
| 10 | 工具栏高度 0×0 完全不显示 | `CCS_NORESIZE` 让 `TB_AUTOSIZE` 只算不改窗口 | 改用 `CCS_NOPARENTALIGN` 并显式 `MoveWindow`，用 `TB_GETBUTTONSIZE` 兜底 |
