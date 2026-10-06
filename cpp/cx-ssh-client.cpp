// cx-ssh-client.cpp - 程星SSH客户端（原生 C++ 版）入口
//   程星工作室  官网：cxbk.com/cc   许可证：MPL-2.0
//
// 纯 Win32 API + WinSock2，无任何第三方依赖。
// 命令行：
//   cx-ssh-client.exe                                  启动图形界面
//   cx-ssh-client.exe --probe <协议> <主机> <端口>      真实协议探测，结果打到标准输出
//   cx-ssh-client.exe --selftest                       CSV 往返自检
//   cx-ssh-client.exe --help                           帮助
#include "common.h"
#include "session.h"
#include "probe.h"
#include "ui.h"

#include <shellapi.h>
#include <dpapi.h>
#include <cstdarg>
#include <cstdio>
#include <cstring>

// ===========================================================================
// 标准输出：本程序是 GUI 子系统，需要判断 stdout 到底指向哪里
// ===========================================================================
namespace {

HANDLE g_out          = nullptr;
bool   g_outIsConsole = false;

void WriteAllBytes(HANDLE h, const char* p, size_t n) {
    while (n > 0) {
        const DWORD chunk = (n > (1u << 20)) ? (1u << 20) : (DWORD)n;
        DWORD wrote = 0;
        if (!WriteFile(h, p, chunk, &wrote, nullptr) || wrote == 0) return;
        p += wrote;
        n -= wrote;
    }
}

} // namespace

void OutInit() {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    if (h && h != INVALID_HANDLE_VALUE) {
        const DWORD t = GetFileType(h);
        if (t == FILE_TYPE_PIPE || t == FILE_TYPE_DISK) {
            g_out = h; g_outIsConsole = false; return;   // 被重定向，按 UTF-8 写字节
        }
        if (t == FILE_TYPE_CHAR) {
            g_out = h; g_outIsConsole = true; return;    // 直接挂在控制台上
        }
    }
    // 没有可用的标准输出（例如从资源管理器双击）：借用父进程控制台，再不行自建一个
    if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
    HANDLE c = CreateFileW(L"CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, nullptr,
                           OPEN_EXISTING, 0, nullptr);
    if (c != INVALID_HANDLE_VALUE) { g_out = c; g_outIsConsole = true; }
}

void OutW(const std::wstring& line) {
    if (!g_out) return;
    std::wstring t = line;
    t += L"\r\n";
    if (g_outIsConsole) {
        DWORD wrote = 0;
        WriteConsoleW(g_out, t.c_str(), (DWORD)t.size(), &wrote, nullptr);
    } else {
        const std::string u = WideToUtf8(t);
        WriteAllBytes(g_out, u.data(), u.size());
    }
}

void OutF(const wchar_t* fmt, ...) {
    wchar_t buf[4096] = { 0 };
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(buf, _TRUNCATE, fmt, ap);
    va_end(ap);
    OutW(buf);
}

// ===========================================================================
// 通用工具
// ===========================================================================

std::string WideToUtf8(const std::wstring& w) {
    if (w.empty()) return std::string();
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(),
                                      nullptr, 0, nullptr, nullptr);
    if (n <= 0) return std::string();
    std::string s((size_t)n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return std::wstring();
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    if (n <= 0) return std::wstring();
    std::wstring w((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

std::wstring Win32ErrText(DWORD code) {
    std::wstring msg = L"错误 " + FormatI64((long long)code);
    if (code == 0) return L"成功";
    LPWSTR buf = nullptr;
    const DWORD n = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                                   FORMAT_MESSAGE_IGNORE_INSERTS,
                                   nullptr, code, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
                                   (LPWSTR)&buf, 0, nullptr);
    if (n && buf) {
        std::wstring t(buf, n);
        while (!t.empty() && (t.back() == L'\r' || t.back() == L'\n' || t.back() == L' '))
            t.pop_back();
        if (!t.empty()) msg += L"：" + t;
    }
    if (buf) LocalFree(buf);
    return msg;
}

std::wstring Trim(const std::wstring& s) {
    size_t b = 0, e = s.size();
    while (b < e && (s[b] == L' ' || s[b] == L'\t' || s[b] == L'\r' || s[b] == L'\n')) ++b;
    while (e > b && (s[e - 1] == L' ' || s[e - 1] == L'\t' || s[e - 1] == L'\r' || s[e - 1] == L'\n')) --e;
    return s.substr(b, e - b);
}

std::wstring ToUpperAscii(const std::wstring& s) {
    std::wstring o = s;
    for (wchar_t& c : o) if (c >= L'a' && c <= L'z') c = (wchar_t)(c - L'a' + L'A');
    return o;
}

std::wstring FormatI64(long long v) {
    wchar_t b[32] = { 0 };
    swprintf_s(b, 32, L"%lld", v);
    return std::wstring(b);
}

bool EqualsNoCase(const std::wstring& a, const std::wstring& b) {
    return ToUpperAscii(a) == ToUpperAscii(b);
}

// ===========================================================================
// 命令行：--probe / --selftest / --help
// ===========================================================================

namespace {

void PrintUsage() {
    OutW(L"程星SSH客户端 cx-ssh-client v0.1.0（C++ 原生版）");
    OutW(L"");
    OutW(L"用法：");
    OutW(L"  cx-ssh-client.exe                                 启动图形界面");
    OutW(L"  cx-ssh-client.exe --probe <协议> <主机> <端口>     协议探测，结果打印到标准输出");
    OutW(L"  cx-ssh-client.exe --selftest                      CSV 导入导出往返自检");
    OutW(L"  cx-ssh-client.exe --help                          显示本帮助");
    OutW(L"");
    OutW(L"协议：SSH / SFTP / FTP / RDP / VNC");
    OutW(L"退出码：0 = 成功，2 = 探测失败，3 = 参数错误");
}

int CmdProbe(const std::wstring& proto, const std::wstring& host, const std::wstring& portText) {
    const int port = _wtoi(portText.c_str());
    if (host.empty() || port <= 0 || port > 65535) {
        OutW(L"参数错误：主机或端口无效。");
        return 3;
    }

    OutW(L"=== cx-ssh-client 协议探测 ===");
    OutW(L"协议 : " + (proto.empty() ? std::wstring(L"(未指定)") : proto));
    OutW(L"目标 : " + host + L":" + FormatI64(port));
    OutW(L"超时 : 5000 毫秒");
    OutW(L"");

    const ProbeResult r = ProbeSession(proto, host, port, 5000);

    OutW(std::wstring(L"结果 : ") + (r.ok ? L"成功" : L"失败") + L" — " + r.summary);
    OutW(L"耗时 : " + FormatI64(r.elapsedMs) + L" 毫秒");
    if (!r.banner.empty()) {
        OutW(L"BANNER: " + r.banner);
    } else {
        OutW(L"BANNER: (未获取到)");
    }
    if (!r.detail.empty()) {
        // detail 里带换行，逐行输出，保证每行前缀一致
        std::wstring d = r.detail;
        size_t pos = 0;
        while (pos <= d.size()) {
            size_t e = d.find_first_of(L"\r\n", pos);
            if (e == std::wstring::npos) e = d.size();
            const std::wstring line = Trim(d.substr(pos, e - pos));
            if (!line.empty()) OutW(L"详情 : " + line);
            if (e >= d.size()) break;
            pos = e + 1;
            while (pos < d.size() && (d[pos] == L'\r' || d[pos] == L'\n')) ++pos;
            if (pos >= d.size()) break;
        }
    }
    OutW(L"");
    OutW(r.ok ? L"结论 : 探测成功" : L"结论 : 探测失败");
    return r.ok ? 0 : 2;
}

// 构造一条会话
Session MakeSession(const wchar_t* name, const wchar_t* proto, const wchar_t* host, int port,
                    const wchar_t* user, const wchar_t* pass, const wchar_t* note) {
    Session s;
    s.name = name; s.protocol = proto; s.host = host; s.port = port;
    s.username = user; s.password = pass; s.note = note;
    return s;
}

bool SameSession(const Session& a, const Session& b, std::wstring& why) {
    if (a.name     != b.name)     { why = L"name 不一致";     return false; }
    if (a.protocol != b.protocol) { why = L"protocol 不一致"; return false; }
    if (a.host     != b.host)     { why = L"host 不一致";     return false; }
    if (a.port     != b.port)     { why = L"port 不一致";     return false; }
    if (a.username != b.username) { why = L"username 不一致"; return false; }
    if (a.password != b.password) { why = L"password 不一致"; return false; }
    if (a.note     != b.note)     { why = L"note 不一致";     return false; }
    return true;
}

// DPAPI 加解密自检（不触碰真实数据文件）
bool DpapiRoundTrip(std::wstring& err) {
    const std::string plain = "cx-ssh-client DPAPI \xE6\xB5\x8B\xE8\xAF\x95 password=\xE4\xB8\xAD\xE6\x96\x87";
    DATA_BLOB in{};
    in.pbData = (BYTE*)plain.data();
    in.cbData = (DWORD)plain.size();
    DATA_BLOB cipher{};
    if (!CryptProtectData(&in, L"selftest", nullptr, nullptr, nullptr,
                          CRYPTPROTECT_UI_FORBIDDEN, &cipher)) {
        err = L"CryptProtectData 失败：" + Win32ErrText(GetLastError());
        return false;
    }
    DATA_BLOB back{};
    const BOOL ok = CryptUnprotectData(&cipher, nullptr, nullptr, nullptr, nullptr,
                                       CRYPTPROTECT_UI_FORBIDDEN, &back);
    const DWORD e = GetLastError();
    if (cipher.pbData) LocalFree(cipher.pbData);
    if (!ok) {
        if (back.pbData) LocalFree(back.pbData);
        err = L"CryptUnprotectData 失败：" + Win32ErrText(e);
        return false;
    }
    std::string got;
    if (back.pbData && back.cbData) got.assign((const char*)back.pbData, back.cbData);
    if (back.pbData) LocalFree(back.pbData);
    if (got != plain) { err = L"DPAPI 解密结果与原文不一致"; return false; }
    return true;
}

int CmdSelfTest() {
    OutW(L"=== cx-ssh-client --selftest ===");
    OutW(L"");

    // ---------- 1. CSV 导入导出往返 ----------
    OutW(L"[1/4] CSV 导入导出往返测试");
    std::vector<Session> src;
    src.push_back(MakeSession(L"生产服务器 A", L"SSH",  L"156.239.3.215", 22,
                              L"root",  L"p@ss,w\"rd",       L"生产环境，勿动"));
    src.push_back(MakeSession(L"文件服务器",   L"SFTP", L"10.0.0.5",      2222,
                              L"deploy", L"简单密码123",      L"备注里有,逗号和\"引号\""));
    src.push_back(MakeSession(L"远程桌面",     L"RDP",  L"rdp.example.com", 3389,
                              L"admin",  L"",                L"多行\n备注第二行\r\n第三行"));
    OutF(L"      生成会话数：%d", (int)src.size());

    // 依次尝试几个可写目录，避免个别环境里 %TEMP% 不可写导致自检中断
    std::wstring dir;
    {
        wchar_t buf[MAX_PATH * 2] = { 0 };
        if (GetTempPathW(MAX_PATH, buf) > 0) dir = buf;
    }
    std::vector<std::wstring> candidates;
    if (!dir.empty()) candidates.push_back(dir);
    {
        const std::wstring store = StorageFilePath();          // %LOCALAPPDATA%\cx-ssh-client\sessions.dat
        const size_t pos = store.find_last_of(L'\\');
        if (pos != std::wstring::npos) candidates.push_back(store.substr(0, pos + 1));
    }
    {
        wchar_t exe[MAX_PATH * 2] = { 0 };
        if (GetModuleFileNameW(nullptr, exe, MAX_PATH) > 0) {
            std::wstring p = exe;
            const size_t pos = p.find_last_of(L'\\');
            if (pos != std::wstring::npos) candidates.push_back(p.substr(0, pos + 1));
        }
    }

    std::wstring csvPath;
    for (const std::wstring& d : candidates) {
        std::wstring p = d;
        if (!p.empty() && p.back() != L'\\') p += L'\\';
        p += L"cx-ssh-client-selftest.csv";
        // 先探测一次可写性，把真实路径打出来便于排查
        HANDLE h = CreateFileW(p.c_str(), GENERIC_WRITE, 0, nullptr,
                               CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            CloseHandle(h);
            csvPath = p;
            OutW(L"      自检目录：" + p);
            break;
        }
        OutF(L"      跳过不可写目录 %s（%s）", p.c_str(), Win32ErrText(GetLastError()).c_str());
    }
    if (csvPath.empty()) {
        OutW(L"      失败：临时目录、数据目录、程序目录都不可写");
        return 1;
    }

    std::wstring err;
    if (!ExportCsv(csvPath, src, err)) {
        OutW(L"      导出失败：" + err);
        return 1;
    }
    OutW(L"      导出 CSV：" + csvPath);

    // 校验 BOM 与表头
    {
        HANDLE h = CreateFileW(csvPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) { OutW(L"      失败：无法重新打开 CSV"); return 1; }
        unsigned char head[8] = { 0 };
        DWORD got = 0;
        ReadFile(h, head, 3, &got, nullptr);
        CloseHandle(h);
        const bool bom = (got == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF);
        OutF(L"      UTF-8 BOM：%s", bom ? L"存在（Excel 友好）" : L"缺失");
        if (!bom) { OutW(L"      失败：CSV 缺少 BOM"); return 1; }
    }

    std::vector<Session> got;
    if (!ImportCsv(csvPath, got, err)) {
        OutW(L"      导入失败：" + err);
        return 1;
    }
    OutF(L"      重新导入会话数：%d", (int)got.size());

    bool allOk = (got.size() == src.size());
    if (!allOk) OutW(L"      失败：数量不一致");
    for (size_t i = 0; i < src.size() && i < got.size(); ++i) {
        std::wstring why;
        const bool same = SameSession(src[i], got[i], why);
        OutF(L"      第 %d 条「%s」：%s", (int)(i + 1), src[i].name.c_str(),
             same ? L"一致 (PASS)" : (L"不一致 (FAIL) - " + why).c_str());
        if (same) {
            OutF(L"          协议=%s 主机=%s 端口=%d 用户名=%s 密码=%s",
                 got[i].protocol.c_str(), got[i].host.c_str(), got[i].port,
                 got[i].username.c_str(), got[i].password.c_str());
        } else {
            allOk = false;
        }
    }

    // ---------- 2. 内部文本序列化往返 ----------
    OutW(L"");
    OutW(L"[2/4] 会话文本序列化往返（存储格式，含制表符/换行转义）");
    std::vector<Session> src2;
    src2.push_back(MakeSession(L"带\t制表符", L"SSH", L"h1", 22, L"u1", L"p\t1", L"备注\n换行"));
    const std::string text = SessionsToText(src2);
    std::vector<Session> back2;
    std::wstring terr;
    bool textOk = TextToSessions(text, back2, terr) && back2.size() == 1;
    if (textOk) {
        std::wstring why;
        textOk = SameSession(src2[0], back2[0], why);
        if (!textOk) terr = why;
    }
    OutF(L"      文本序列化往返：%s", textOk ? L"一致 (PASS)" : L"不一致 (FAIL)");
    if (!textOk) { OutW(L"      原因：" + terr); allOk = false; }

    // ---------- 3. DPAPI 加解密 ----------
    OutW(L"");
    OutW(L"[3/4] DPAPI（CryptProtectData / CryptUnprotectData）加解密自检");
    std::wstring derr;
    const bool dpOk = DpapiRoundTrip(derr);
    OutF(L"      DPAPI 往返：%s", dpOk ? L"成功 (PASS)" : L"失败 (FAIL)");
    if (!dpOk) { OutW(L"      原因：" + derr); allOk = false; }
    OutF(L"      存储文件路径：%s", StorageFilePath().c_str());

    // ---------- 4. 加密存储往返（会临时占用真实存储文件，测完原样还原） ----------
    OutW(L"");
    OutW(L"[4/4] 本地加密存储往返（SaveSessions / LoadSessions）");
    const std::wstring store = StorageFilePath();
    std::string backup;
    bool hadBackup = false;
    {
        HANDLE h = CreateFileW(store.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            char buf[8192];
            DWORD got = 0;
            while (ReadFile(h, buf, sizeof(buf), &got, nullptr) && got > 0) backup.append(buf, got);
            CloseHandle(h);
            hadBackup = true;
        }
    }
    OutF(L"      原有存储文件：%s", hadBackup ? L"存在（测完会原样还原）" : L"不存在（测完会删除）");

    bool storeOk = true;
    std::wstring serr;
    std::vector<Session> loaded;
    if (!SaveSessions(src, serr)) {
        storeOk = false;
    } else if (!LoadSessions(loaded, serr)) {
        storeOk = false;
    } else if (loaded.size() != src.size()) {
        storeOk = false;
        serr = L"读回条数不一致：" + FormatI64((long long)loaded.size()) +
               L" != " + FormatI64((long long)src.size());
    } else {
        for (size_t i = 0; i < src.size(); ++i) {
            std::wstring why;
            if (!SameSession(src[i], loaded[i], why)) {
                storeOk = false;
                serr = L"第 " + FormatI64((long long)(i + 1)) + L" 条 " + why;
                break;
            }
        }
    }
    OutF(L"      加密存储往返：%s", storeOk ? L"一致 (PASS)" : L"不一致 (FAIL)");
    if (!storeOk) { OutW(L"      原因：" + serr); allOk = false; }

    // 确认落盘内容确实是密文：明文密码不应出现在文件里
    if (storeOk) {
        HANDLE h = CreateFileW(store.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) {
            OutW(L"      密文检查：无法重新打开存储文件 (FAIL)");
            allOk = false;
        } else {
            std::string raw;
            char buf[8192];
            DWORD got = 0;
            while (ReadFile(h, buf, sizeof(buf), &got, nullptr) && got > 0) raw.append(buf, got);
            CloseHandle(h);
            const bool magicOk = raw.size() > 12 && memcmp(raw.data(), "CXSSH001", 8) == 0;
            const bool leak    = raw.find("p@ss,w\"rd") != std::string::npos ||
                                 raw.find(WideToUtf8(L"简单密码123")) != std::string::npos ||
                                 raw.find("CXSSHCLIENT-SESSIONS") != std::string::npos;
            OutF(L"      文件头魔数 CXSSH001：%s", magicOk ? L"正确" : L"缺失");
            OutF(L"      明文泄漏检查：%s", leak ? L"发现明文密码 (FAIL)" : L"未发现明文密码，内容已加密 (PASS)");
            if (!magicOk || leak) allOk = false;
        }
    }

    // 还原
    if (hadBackup) {
        HANDLE h = CreateFileW(store.c_str(), GENERIC_WRITE, 0, nullptr,
                               CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            DWORD wrote = 0;
            WriteFile(h, backup.data(), (DWORD)backup.size(), &wrote, nullptr);
            CloseHandle(h);
        }
        OutW(L"      已还原原有存储文件");
    } else {
        DeleteFileW(store.c_str());
        OutW(L"      已清理自检产生的新存储文件");
    }

    OutW(L"");
    OutF(L"总体结果：%s", allOk ? L"全部通过 (SELFTEST PASS)" : L"存在失败项 (SELFTEST FAIL)");
    DeleteFileW(csvPath.c_str());
    return allOk ? 0 : 1;
}

} // namespace

// ===========================================================================
// 入口
// ===========================================================================
int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, LPWSTR, int nCmdShow) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc < 2) {
        if (argv) LocalFree(argv);
        return RunMainWindow(hInst, nCmdShow);     // 无参数：纯图形界面
    }

    const std::wstring a1 = ToUpperAscii(argv[1]);
    const bool isProbe    = (a1 == L"--PROBE"    || a1 == L"-PROBE");
    const bool isSelfTest = (a1 == L"--SELFTEST" || a1 == L"-SELFTEST");
    const bool isHelp     = (a1 == L"--HELP" || a1 == L"-H" || a1 == L"/?" || a1 == L"-?");
    const bool isUnknown  = (!isProbe && !isSelfTest && !isHelp && a1.rfind(L"--", 0) == 0);
    const bool cliMode    = (isProbe || isSelfTest || isHelp || isUnknown);

    // 只有命令行模式才接管控制台。GUI 模式下绝不 AttachConsole/AllocConsole，
    // 否则双击启动时会平白多弹一个黑色控制台窗口。
    if (cliMode) OutInit();

    int rc = 0;
    if (isProbe) {
        if (argc < 5) {
            OutW(L"参数错误：--probe 需要 <协议> <主机> <端口> 三个参数。");
            OutW(L"");
            PrintUsage();
            rc = 3;
        } else {
            rc = CmdProbe(argv[2], argv[3], argv[4]);
        }
    } else if (isSelfTest) {
        rc = CmdSelfTest();
    } else if (isHelp) {
        PrintUsage();
        rc = 0;
    } else if (isUnknown) {
        OutW(L"未知参数：" + std::wstring(argv[1]));
        OutW(L"");
        PrintUsage();
        rc = 3;
    }

    LocalFree(argv);
    if (cliMode) return rc;
    return RunMainWindow(hInst, nCmdShow);
}
