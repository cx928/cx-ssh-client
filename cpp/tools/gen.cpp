// gen.cpp - 开发/验证辅助工具（不是 cx-ssh-client 本体）
// 程星SSH客户端 (cx-ssh-client) / MPL-2.0
//
// 用途：
//   1) gen.exe <N>            生成 N 条会话的加密存储文件，供 measure-mem.ps1 测内存
//   2) gen.exe dump <文件>     把当前存储文件解密后导出成 UTF-8 TSV，
//                             供 uitest.ps1 核对「界面 -> 加密存储」整条链路
//
// 它直接复用 src\session.cpp（同一份 DPAPI 存储实现），所以导出的内容就是程序真正写下去的东西。
#include "common.h"
#include "session.h"

#include <cstdio>
#include <cwchar>

int wmain(int argc, wchar_t** argv) {
    // ---------- dump 模式 ----------
    if (argc > 2 && wcscmp(argv[1], L"dump") == 0) {
        std::vector<Session> list;
        std::wstring err;
        if (!LoadSessions(list, err)) {
            wprintf(L"LoadSessions failed: %s\n", err.c_str());
            return 1;
        }
        std::string out = "\xEF\xBB\xBF";                 // 方便 PowerShell 按 UTF-8 读
        out += "name\tprotocol\thost\tport\tusername\tpassword\tnote\r\n";
        for (const Session& s : list) {
            out += WideToUtf8(s.name);     out += '\t';
            out += WideToUtf8(s.protocol); out += '\t';
            out += WideToUtf8(s.host);     out += '\t';
            out += std::to_string(s.port); out += '\t';
            out += WideToUtf8(s.username); out += '\t';
            out += WideToUtf8(s.password); out += '\t';
            std::string note = WideToUtf8(s.note);
            for (char& c : note) if (c == '\r' || c == '\n' || c == '\t') c = '|';
            out += note;
            out += "\r\n";
        }
        FILE* f = nullptr;
        if (_wfopen_s(&f, argv[2], L"wb") != 0 || !f) { wprintf(L"open out failed\n"); return 1; }
        fwrite(out.data(), 1, out.size(), f);
        fclose(f);
        wprintf(L"dumped %d sessions\n", (int)list.size());
        return 0;
    }

    // ---------- 造数据模式 ----------
    const int n = (argc > 1) ? _wtoi(argv[1]) : 100;
    if (n <= 0) { wprintf(L"usage: gen <N> | gen dump <outfile>\n"); return 1; }

    std::vector<Session> list;
    list.reserve((size_t)n);
    const wchar_t* protos[] = { L"SSH", L"SFTP", L"FTP", L"RDP", L"VNC" };
    for (int i = 0; i < n; ++i) {
        Session s;
        s.name     = L"会话 " + FormatI64(i) + L" - 生产环境服务器";
        s.protocol = protos[i % 5];
        s.host     = L"192.168." + FormatI64(i / 256 % 256) + L"." + FormatI64(i % 256);
        s.port     = DefaultPortFor(s.protocol);
        s.username = L"user" + FormatI64(i);
        s.password = L"pwd-" + FormatI64(i) + L"-with,comma\"and quote";
        s.note     = L"备注 " + FormatI64(i) + L"：这台机器用于压力测试，请勿删除";
        list.push_back(s);
    }
    std::wstring err;
    if (!SaveSessions(list, err)) {
        wprintf(L"SaveSessions failed: %s\n", err.c_str());
        return 1;
    }
    wprintf(L"wrote %d sessions -> %s\n", n, StorageFilePath().c_str());
    return 0;
}
