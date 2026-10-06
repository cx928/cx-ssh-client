// session.cpp - 会话数据模型 / DPAPI 加密存储 / CSV 导入导出
// 程星SSH客户端 (cx-ssh-client) 原生 C++ 版 / MPL-2.0
//
// 数据文件格式（%LOCALAPPDATA%\cx-ssh-client\sessions.dat）：
//   [8 字节魔数 "CXSSH001"][4 字节小端版本号][DPAPI 密文]
// 密文解密后是一段 UTF-8 文本，制表符分隔：
//   CXSSHCLIENT-SESSIONS\t1
//   name\tprotocol\thost\tport\tusername\tpassword\tnote
//   字段中的 \\ \t \r \n 以反斜杠转义。

#include "session.h"

#include <dpapi.h>
#include <shlobj.h>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>

namespace {

const char  kMagic[8]     = { 'C', 'X', 'S', 'S', 'H', '0', '0', '1' };
const DWORD kFormatVer    = 1;

const wchar_t* const kProtocols[] = { L"SSH", L"SFTP", L"FTP", L"RDP", L"VNC", nullptr };

// 单字段最大长度，防止畸形文件把内存吃光
const size_t kMaxFieldLen = 64 * 1024;
const size_t kMaxSessions = 100000;

std::string EscapeField(const std::wstring& w) {
    const std::string s = WideToUtf8(w);
    std::string o;
    o.reserve(s.size() + 8);
    for (char c : s) {
        switch (c) {
        case '\\': o += "\\\\"; break;
        case '\t': o += "\\t";  break;
        case '\r': o += "\\r";  break;
        case '\n': o += "\\n";  break;
        default:   o += c;      break;
        }
    }
    return o;
}

std::wstring UnescapeField(const std::string& s) {
    std::string o;
    o.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '\\' && i + 1 < s.size()) {
            const char n = s[++i];
            switch (n) {
            case '\\': o += '\\'; break;
            case 't':  o += '\t'; break;
            case 'r':  o += '\r'; break;
            case 'n':  o += '\n'; break;
            default:   o += n;    break;   // 未知转义按原字符处理，保证不丢数据
            }
        } else {
            o += s[i];
        }
    }
    return Utf8ToWide(o);
}

// 把一段文本按分隔符切分（不处理转义，转义在字段级处理）
std::vector<std::string> SplitTab(const std::string& line) {
    std::vector<std::string> parts;
    size_t start = 0;
    for (size_t i = 0; i <= line.size(); ++i) {
        if (i == line.size() || line[i] == '\t') {
            parts.push_back(line.substr(start, i - start));
            start = i + 1;
        }
    }
    return parts;
}

// 读取整个文件；成功返回 true
bool ReadWholeFile(const std::wstring& path, std::string& out, std::wstring& err) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        err = L"打开文件失败：" + Win32ErrText(GetLastError());
        return false;
    }
    out.clear();
    // 先按文件大小预留，避免读大文件时反复扩容（每次扩容都要复制一遍已有内容）
    LARGE_INTEGER li{};
    if (GetFileSizeEx(h, &li) && li.QuadPart > 0 && li.QuadPart <= (LONGLONG)(64u * 1024u * 1024u)) {
        out.reserve((size_t)li.QuadPart);
    }
    char buf[8192];
    DWORD got = 0;
    for (;;) {
        if (!ReadFile(h, buf, sizeof(buf), &got, nullptr)) {
            err = L"读取文件失败：" + Win32ErrText(GetLastError());
            CloseHandle(h);
            return false;
        }
        if (got == 0) break;
        out.append(buf, got);
        if (out.size() > 64u * 1024u * 1024u) {   // 64MB 上限，明显异常
            err = L"文件过大，疑似不是本程序的数据文件";
            CloseHandle(h);
            return false;
        }
    }
    CloseHandle(h);
    return true;
}

// 把一块内存完整写进句柄
bool WriteAll(HANDLE h, const void* data, size_t n, std::wstring& err) {
    const char* p = (const char*)data;
    size_t left = n;
    while (left > 0) {
        const DWORD chunk = (left > (1u << 20)) ? (1u << 20) : (DWORD)left;   // 每次最多 1MB
        DWORD wrote = 0;
        if (!WriteFile(h, p, chunk, &wrote, nullptr) || wrote == 0) {
            err = L"写入文件失败：" + Win32ErrText(GetLastError());
            return false;
        }
        p += wrote;
        left -= wrote;
    }
    return true;
}

bool WriteWholeFile(const std::wstring& path, const std::string& data, std::wstring& err) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        err = L"创建文件失败：" + Win32ErrText(GetLastError());
        return false;
    }
    if (!WriteAll(h, data.data(), data.size(), err)) {
        CloseHandle(h);
        return false;
    }
    FlushFileBuffers(h);
    CloseHandle(h);
    return true;
}

// 创建目录（含中间层级）；已存在也算成功
bool MakeDirs(const std::wstring& dir) {
    if (dir.empty()) return false;
    if (CreateDirectoryW(dir.c_str(), nullptr)) return true;
    const DWORD e = GetLastError();
    if (e == ERROR_ALREADY_EXISTS) return true;
    if (e == ERROR_PATH_NOT_FOUND) {
        const size_t pos = dir.find_last_of(L'\\');
        if (pos == std::wstring::npos || pos == 0) return false;
        if (!MakeDirs(dir.substr(0, pos))) return false;
        if (CreateDirectoryW(dir.c_str(), nullptr)) return true;
        return GetLastError() == ERROR_ALREADY_EXISTS;
    }
    return false;
}

// 目录是否可用（能建出来 + 能写文件）。用一次「建探针文件再删掉」来确认。
bool DirWritable(const std::wstring& dir) {
    if (!MakeDirs(dir)) return false;
    const std::wstring probe = dir + L"\\.cx-write-probe.tmp";
    HANDLE h = CreateFileW(probe.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    CloseHandle(h);                       // FILE_FLAG_DELETE_ON_CLOSE：句柄一关文件就没了
    return true;
}

// 确保存储目录存在
bool EnsureStorageDir(std::wstring& err) {
    const std::wstring file = StorageFilePath();
    const size_t pos = file.find_last_of(L'\\');
    if (pos == std::wstring::npos) { err = L"无法解析数据目录"; return false; }
    const std::wstring folder = file.substr(0, pos);
    if (MakeDirs(folder)) return true;
    err = L"创建目录失败：" + Win32ErrText(GetLastError()) + L"（" + folder + L"）";
    return false;
}

// ------------------------------- CSV ---------------------------------------

std::string CsvQuote(const std::wstring& w) {
    const std::string s = WideToUtf8(w);
    bool need = false;
    for (char c : s) {
        if (c == ',' || c == '"' || c == '\r' || c == '\n') { need = true; break; }
    }
    if (!need) return s;
    std::string o = "\"";
    for (char c : s) {
        if (c == '"') o += "\"\"";
        else          o += c;
    }
    o += "\"";
    return o;
}

// 解析一条 CSV 记录（支持引号包裹、"" 转义），返回消耗到的位置
bool ParseCsvRecord(const std::string& s, size_t& i, std::vector<std::string>& fields) {
    fields.clear();
    std::string cur;
    bool inQuote = false;
    bool any = false;
    while (i < s.size()) {
        const char c = s[i];
        if (inQuote) {
            if (c == '"') {
                if (i + 1 < s.size() && s[i + 1] == '"') { cur += '"'; i += 2; continue; }
                inQuote = false; ++i; continue;
            }
            cur += c; ++i; continue;
        }
        if (c == '"') { inQuote = true; any = true; ++i; continue; }
        if (c == ',') { fields.push_back(cur); cur.clear(); any = true; ++i; continue; }
        if (c == '\r') { ++i; continue; }
        if (c == '\n') { ++i; break; }
        cur += c; any = true; ++i;
    }
    if (any || !cur.empty() || !fields.empty()) fields.push_back(cur);
    return !fields.empty();
}

int ParsePort(const std::wstring& s, int fallback) {
    const std::wstring t = Trim(s);
    if (t.empty()) return fallback;
    wchar_t* end = nullptr;
    const long v = wcstol(t.c_str(), &end, 10);
    if (end == t.c_str() || v <= 0 || v > 65535) return fallback;
    return (int)v;
}

} // namespace

// ---------------------------------------------------------------------------
// 协议工具
// ---------------------------------------------------------------------------

const wchar_t* const* ProtocolList() { return kProtocols; }

std::wstring NormalizeProtocol(const std::wstring& raw) {
    const std::wstring up = ToUpperAscii(Trim(raw));
    for (int i = 0; kProtocols[i]; ++i) {
        if (up == kProtocols[i]) return up;
    }
    return std::wstring();
}

int DefaultPortFor(const std::wstring& proto) {
    const std::wstring p = NormalizeProtocol(proto);
    if (p == L"SSH" || p == L"SFTP") return 22;
    if (p == L"FTP")                  return 21;
    if (p == L"RDP")                  return 3389;
    if (p == L"VNC")                  return 5900;
    return 22;
}

std::wstring StorageFilePath() {
    // 只在首次调用时做一次可写性探测，结果缓存下来（std::call_once 保证线程安全）
    static std::wstring cached;
    static std::once_flag once;
    std::call_once(once, []() {
        std::vector<std::wstring> cands;

        // 首选：%LOCALAPPDATA%\cx-ssh-client
        wchar_t base[MAX_PATH * 2] = { 0 };
        if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, base))) {
            std::wstring p = base;
            if (!p.empty() && p.back() != L'\\') p += L'\\';
            cands.push_back(p + L"cx-ssh-client");
        }
        // 备选 1：程序所在目录\cx-ssh-client-data（便携模式 / 受限环境）
        {
            wchar_t exe[MAX_PATH * 2] = { 0 };
            if (GetModuleFileNameW(nullptr, exe, MAX_PATH) > 0) {
                std::wstring p = exe;
                const size_t pos = p.find_last_of(L'\\');
                if (pos != std::wstring::npos) cands.push_back(p.substr(0, pos) + L"\\cx-ssh-client-data");
            }
        }
        // 备选 2：%TEMP%\cx-ssh-client
        {
            wchar_t tmp[MAX_PATH * 2] = { 0 };
            if (GetTempPathW(MAX_PATH, tmp) > 0) {
                std::wstring p = tmp;
                if (!p.empty() && p.back() != L'\\') p += L'\\';
                cands.push_back(p + L"cx-ssh-client");
            }
        }

        for (const std::wstring& d : cands) {
            if (DirWritable(d)) { cached = d + L"\\sessions.dat"; return; }
        }
        // 都不行就返回首选路径，具体错误留给保存时如实上报
        cached = cands.empty() ? std::wstring(L"sessions.dat")
                               : (cands[0] + L"\\sessions.dat");
    });
    return cached;
}

// ---------------------------------------------------------------------------
// 文本序列化
// ---------------------------------------------------------------------------

std::string SessionsToText(const std::vector<Session>& list) {
    std::string out;
    out.reserve(list.size() * 96 + 32);   // 粗估每条约 96 字节，省掉反复扩容的整块复制
    out += "CXSSHCLIENT-SESSIONS\t1\r\n";
    for (const Session& s : list) {
        const std::wstring port = FormatI64(s.port);
        out += EscapeField(s.name);              out += '\t';
        out += EscapeField(s.protocol);          out += '\t';
        out += EscapeField(s.host);              out += '\t';
        out += WideToUtf8(port);                 out += '\t';
        out += EscapeField(s.username);          out += '\t';
        out += EscapeField(s.password);          out += '\t';
        out += EscapeField(s.note);
        out += "\r\n";
    }
    return out;
}

bool TextToSessions(const std::string& text, std::vector<Session>& out, std::wstring& err) {
    out.clear();
    size_t i = 0;
    // 允许 UTF-8 BOM
    if (text.size() >= 3 && (unsigned char)text[0] == 0xEF &&
        (unsigned char)text[1] == 0xBB && (unsigned char)text[2] == 0xBF) {
        i = 3;
    }
    bool headerSeen = false;
    while (i < text.size()) {
        size_t end = text.find('\n', i);
        if (end == std::string::npos) end = text.size();
        std::string line = text.substr(i, end - i);
        i = end + 1;
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
        if (line.empty()) continue;

        const std::vector<std::string> f = SplitTab(line);
        if (!headerSeen) {
            headerSeen = true;
            if (!f.empty() && f[0] == "CXSSHCLIENT-SESSIONS") {
                if (f.size() >= 2) {
                    const long v = strtol(f[1].c_str(), nullptr, 10);
                    if (v > (long)kFormatVer) {
                        err = L"数据文件版本(" + FormatI64(v) + L")高于本程序支持的版本(" +
                              FormatI64(kFormatVer) + L")";
                        return false;
                    }
                }
                continue;   // 表头行
            }
            // 没有表头也允许解析，按数据行处理
        }
        if (f.size() < 3) {
            err = L"数据文件第 " + FormatI64((long long)out.size() + 1) + L" 条记录字段不足";
            return false;
        }
        for (const std::string& cell : f) {
            if (cell.size() > kMaxFieldLen) { err = L"数据文件中存在超长字段，疑似损坏"; return false; }
        }

        Session s;
        s.name     = UnescapeField(f[0]);
        s.protocol = NormalizeProtocol(UnescapeField(f[1]));
        s.host     = UnescapeField(f[2]);
        s.port     = (f.size() > 3) ? ParsePort(UnescapeField(f[3]), 22) : DefaultPortFor(s.protocol);
        s.username = (f.size() > 4) ? UnescapeField(f[4]) : std::wstring();
        s.password = (f.size() > 5) ? UnescapeField(f[5]) : std::wstring();
        s.note     = (f.size() > 6) ? UnescapeField(f[6]) : std::wstring();

        if (s.protocol.empty()) s.protocol = L"SSH";
        if (s.port <= 0 || s.port > 65535) s.port = DefaultPortFor(s.protocol);
        // 名称缺失时用 用户名@主机 兜底，保证列表可读
        if (s.name.empty()) s.name = s.username.empty() ? s.host : (s.username + L"@" + s.host);
        out.push_back(s);
        if (out.size() > kMaxSessions) { err = L"会话数量超出上限"; return false; }
    }
    return true;
}

// ---------------------------------------------------------------------------
// DPAPI 加密存储
// ---------------------------------------------------------------------------

bool LoadSessions(std::vector<Session>& out, std::wstring& err) {
    out.clear();
    err.clear();
    const std::wstring path = StorageFilePath();

    const DWORD attr = GetFileAttributesW(path.c_str());
    if (attr == INVALID_FILE_ATTRIBUTES) {
        const DWORD e = GetLastError();
        if (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND) return true;  // 首次运行
        err = L"访问数据文件失败：" + Win32ErrText(e);
        return false;
    }

    std::string raw;
    if (!ReadWholeFile(path, raw, err)) return false;
    if (raw.size() < sizeof(kMagic) + 4) { err = L"数据文件长度不足，已损坏"; return false; }
    if (memcmp(raw.data(), kMagic, sizeof(kMagic)) != 0) { err = L"数据文件魔数不匹配，不是本程序的数据文件"; return false; }

    DWORD ver = 0;
    memcpy(&ver, raw.data() + sizeof(kMagic), 4);
    if (ver > kFormatVer) {
        err = L"数据文件版本(" + FormatI64(ver) + L")高于本程序支持的版本(" + FormatI64(kFormatVer) + L")";
        return false;
    }

    const size_t blobOff = sizeof(kMagic) + 4;
    DATA_BLOB in{};
    in.pbData = (BYTE*)raw.data() + blobOff;
    in.cbData = (DWORD)(raw.size() - blobOff);
    DATA_BLOB plain{};
    if (!CryptUnprotectData(&in, nullptr, nullptr, nullptr, nullptr,
                            CRYPTPROTECT_UI_FORBIDDEN, &plain)) {
        const DWORD e = GetLastError();
        err = L"DPAPI 解密失败：" + Win32ErrText(e) +
              L"（数据文件可能由其他 Windows 账户或计算机创建）";
        return false;
    }
    // 直接从 DPAPI 的输出缓冲区解析，省掉一份整块明文的拷贝
    std::string text;
    if (plain.pbData && plain.cbData) {
        text.assign((const char*)plain.pbData, plain.cbData);
        LocalFree(plain.pbData);
        plain.pbData = nullptr;
    }
    raw.clear();          // 密文已经用不上了，尽早还给分配器
    raw.shrink_to_fit();

    std::wstring perr;
    if (!TextToSessions(text, out, perr)) {
        out.clear();
        err = L"数据内容解析失败：" + perr;
        return false;
    }
    return true;
}

bool SaveSessions(const std::vector<Session>& list, std::wstring& err) {
    err.clear();
    if (!EnsureStorageDir(err)) return false;

    std::string text = SessionsToText(list);
    DATA_BLOB in{};
    in.pbData = (BYTE*)text.data();
    in.cbData = (DWORD)text.size();
    DATA_BLOB cipher{};
    // 整块加密；UI_FORBIDDEN 保证不会弹系统对话框
    if (!CryptProtectData(&in, L"cx-ssh-client sessions", nullptr, nullptr, nullptr,
                          CRYPTPROTECT_UI_FORBIDDEN, &cipher)) {
        err = L"DPAPI 加密失败：" + Win32ErrText(GetLastError());
        return false;
    }
    text.clear();
    text.shrink_to_fit();                      // 明文用完立刻释放，别和密文同时占着

    // 先写临时文件再改名，避免写入中断把原数据毁掉。
    // 文件头和密文分两次写，中间不再复制一份完整密文。
    const std::wstring path = StorageFilePath();
    const std::wstring tmp  = path + L".tmp";
    bool ok = false;
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        err = L"创建文件失败：" + Win32ErrText(GetLastError());
    } else {
        char head[sizeof(kMagic) + 4];
        memcpy(head, kMagic, sizeof(kMagic));
        memcpy(head + sizeof(kMagic), &kFormatVer, 4);
        ok = WriteAll(h, head, sizeof(head), err) &&
             WriteAll(h, cipher.pbData, cipher.cbData, err);
        if (ok) FlushFileBuffers(h);
        CloseHandle(h);
    }
    if (cipher.pbData) LocalFree(cipher.pbData);
    if (!ok) { DeleteFileW(tmp.c_str()); return false; }

    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        err = L"替换数据文件失败：" + Win32ErrText(GetLastError());
        DeleteFileW(tmp.c_str());
        return false;
    }
    return true;
}

std::wstring BackupCorruptStore() {
    const std::wstring path = StorageFilePath();
    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) return std::wstring();

    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t suffix[64] = { 0 };
    swprintf_s(suffix, 64, L".bad-%04d%02d%02d-%02d%02d%02d",
               st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    const std::wstring dst = path + suffix;
    if (MoveFileExW(path.c_str(), dst.c_str(), MOVEFILE_REPLACE_EXISTING)) return dst;
    return std::wstring();
}

// ---------------------------------------------------------------------------
// CSV 导入导出
// ---------------------------------------------------------------------------

bool ExportCsv(const std::wstring& path, const std::vector<Session>& list, std::wstring& err) {
    err.clear();
    std::string out = "\xEF\xBB\xBF";                       // UTF-8 BOM，Excel 友好
    out += "name,protocol,host,port,username,password,note\r\n";
    for (const Session& s : list) {
        out += CsvQuote(s.name);              out += ',';
        out += CsvQuote(s.protocol);          out += ',';
        out += CsvQuote(s.host);              out += ',';
        out += std::to_string(s.port);        out += ',';
        out += CsvQuote(s.username);          out += ',';
        out += CsvQuote(s.password);          out += ',';
        out += CsvQuote(s.note);
        out += "\r\n";
    }
    return WriteWholeFile(path, out, err);
}

bool ImportCsv(const std::wstring& path, std::vector<Session>& out, std::wstring& err) {
    err.clear();
    out.clear();
    std::string raw;
    if (!ReadWholeFile(path, raw, err)) return false;

    size_t i = 0;
    if (raw.size() >= 3 && (unsigned char)raw[0] == 0xEF &&
        (unsigned char)raw[1] == 0xBB && (unsigned char)raw[2] == 0xBF) {
        i = 3;
    }

    bool headerSeen = false;
    while (i < raw.size()) {
        if (raw[i] == '\r' || raw[i] == '\n') { ++i; continue; }   // 跳过空行
        std::vector<std::string> f;
        if (!ParseCsvRecord(raw, i, f)) break;
        if (f.size() == 1 && f[0].empty()) continue;

        if (!headerSeen) {
            headerSeen = true;
            const std::wstring first = ToUpperAscii(Trim(Utf8ToWide(f[0])));
            if (first == L"NAME") continue;   // 跳过表头
        }

        Session s;
        s.name     = Utf8ToWide(f.size() > 0 ? f[0] : std::string());
        s.protocol = NormalizeProtocol(Utf8ToWide(f.size() > 1 ? f[1] : std::string()));
        s.host     = Utf8ToWide(f.size() > 2 ? f[2] : std::string());
        s.port     = (f.size() > 3) ? ParsePort(Utf8ToWide(f[3]), 0) : 0;
        s.username = Utf8ToWide(f.size() > 4 ? f[4] : std::string());
        s.password = Utf8ToWide(f.size() > 5 ? f[5] : std::string());
        s.note     = Utf8ToWide(f.size() > 6 ? f[6] : std::string());

        if (s.protocol.empty()) s.protocol = L"SSH";
        if (s.port <= 0) s.port = DefaultPortFor(s.protocol);
        if (s.host.empty() && s.name.empty()) continue;             // 整行空白，忽略
        if (s.name.empty()) s.name = s.username.empty() ? s.host : (s.username + L"@" + s.host);

        out.push_back(s);
        if (out.size() > kMaxSessions) { err = L"CSV 记录数超出上限"; return false; }
    }
    if (out.empty()) {
        err = L"CSV 中没有解析到任何会话记录（列顺序应为 name,protocol,host,port,username,password,note）";
        return false;
    }
    return true;
}
