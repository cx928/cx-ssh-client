// ui.cpp - Win32 界面实现：会话管理主窗口（工具栏 + ListView + 状态栏）、
//          会话编辑对话框（自建窗口，不依赖对话框资源）、连接分发、协议探测
// 程星SSH客户端 (cx-ssh-client) 原生 C++ 版 / MPL-2.0
#include "ui.h"
#include "probe.h"

#include <commctrl.h>
#include <commdlg.h>
#include <shellapi.h>
#include <cstdio>
#include <cstring>

// 模态会话编辑对话框（定义在本文件末尾，实现是自建窗口而非对话框资源）
bool ShowEditDialog(HWND parent, Session& s, bool isNew);

namespace {

// ------------------------------- 常量 --------------------------------------

const wchar_t* const kMainClass = L"CxSshClientMainWnd";
const wchar_t* const kEditClass = L"CxSshClientEditWnd";
const wchar_t* const kAppTitle  = L"程星SSH客户端 cx-ssh-client  v0.1.0（C++ 原生版）";

enum : int {
    IDC_TOOLBAR = 1001,
    IDC_LISTVIEW,
    IDC_STATUSBAR,

    IDM_NEW = 2001,
    IDM_EDIT,
    IDM_DELETE,
    IDM_CONNECT,
    IDM_TEST,
    IDM_IMPORT,
    IDM_EXPORT,
    IDM_EXIT,
    IDM_ABOUT,

    IDC_LBL_NAME = 3001, IDC_LBL_PROTO, IDC_LBL_HOST, IDC_LBL_PORT,
    IDC_LBL_USER,        IDC_LBL_PASS,  IDC_LBL_NOTE,
    IDC_ED_NAME  = 3101, IDC_CB_PROTO,  IDC_ED_HOST, IDC_ED_PORT,
    IDC_ED_USER,         IDC_ED_PASS,   IDC_ED_NOTE,

    IDA_ACCEL = 4001,
};

const UINT WM_APP_PROBE_DONE = WM_APP + 1;

// ------------------------------- 全局状态 ----------------------------------

HINSTANCE g_hInst  = nullptr;
HWND      g_hMain  = nullptr;
HWND      g_hTool  = nullptr;
HWND      g_hList  = nullptr;
HWND      g_hStatus = nullptr;
HFONT     g_hFont  = nullptr;
UINT      g_dpi    = 96;
bool      g_probing = false;

std::vector<Session> g_sessions;

// 前置声明
void DoConnect(const Session& s);

// ------------------------------- 小工具 ------------------------------------

int Dp(int v) { return MulDiv(v, (int)g_dpi, 96); }

UINT QueryDpi(HWND hwnd) {
    typedef UINT (WINAPI *PFN_GetDpiForWindow)(HWND);
    static PFN_GetDpiForWindow p = nullptr;
    static bool inited = false;
    if (!inited) {
        inited = true;
        HMODULE u = GetModuleHandleW(L"user32.dll");
        if (u) p = (PFN_GetDpiForWindow)(void*)GetProcAddress(u, "GetDpiForWindow");
    }
    if (p && hwnd) {
        const UINT d = p(hwnd);
        if (d >= 72 && d <= 480) return d;
    }
    HDC dc = GetDC(nullptr);
    UINT d = 96;
    if (dc) { d = (UINT)GetDeviceCaps(dc, LOGPIXELSY); ReleaseDC(nullptr, dc); }
    if (d < 72 || d > 480) d = 96;
    return d;
}

HFONT MakeUiFont(UINT dpi) {
    LOGFONTW lf{};
    lf.lfHeight  = -MulDiv(9, (int)dpi, 72);
    lf.lfWeight  = FW_NORMAL;
    lf.lfCharSet = DEFAULT_CHARSET;
    lf.lfQuality = CLEARTYPE_QUALITY;
    wcscpy_s(lf.lfFaceName, L"Microsoft YaHei UI");
    HFONT f = CreateFontIndirectW(&lf);
    if (!f) {
        wcscpy_s(lf.lfFaceName, L"SimSun");
        f = CreateFontIndirectW(&lf);
    }
    return f;
}

void ApplyFont(HWND h) { if (h && g_hFont) SendMessageW(h, WM_SETFONT, (WPARAM)g_hFont, TRUE); }

void CenterWindow(HWND hChild, HWND hParent) {
    RECT rc, rp;
    if (!GetWindowRect(hChild, &rc)) return;
    if (!hParent || !GetWindowRect(hParent, &rp)) {
        rp.left = 0; rp.top = 0;
        rp.right  = GetSystemMetrics(SM_CXSCREEN);
        rp.bottom = GetSystemMetrics(SM_CYSCREEN);
    }
    const int w = rc.right - rc.left, h = rc.bottom - rc.top;
    const int x = rp.left + ((rp.right - rp.left) - w) / 2;
    const int y = rp.top + ((rp.bottom - rp.top) - h) / 2;
    SetWindowPos(hChild, nullptr, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
}

void SetStatus(const std::wstring& text) {
    if (g_hStatus) SendMessageW(g_hStatus, SB_SETTEXTW, 1, (LPARAM)text.c_str());
}

void ShowErr(const std::wstring& what, const std::wstring& err) {
    MessageBoxW(g_hMain, (what + L"\r\n\r\n" + err).c_str(), L"cx-ssh-client", MB_ICONERROR | MB_OK);
}

void ShowInfo(const std::wstring& text, const std::wstring& title) {
    MessageBoxW(g_hMain, text.c_str(), title.c_str(), MB_ICONINFORMATION | MB_OK);
}

std::wstring GetText(HWND h) {
    if (!h) return std::wstring();
    const int n = GetWindowTextLengthW(h);
    if (n <= 0) return std::wstring();
    std::wstring s((size_t)n, L'\0');
    const int got = GetWindowTextW(h, &s[0], n + 1);
    s.resize(got > 0 ? (size_t)got : 0);
    return s;
}

void SetText(HWND h, const std::wstring& s) { if (h) SetWindowTextW(h, s.c_str()); }

int ParseIntOr(const std::wstring& s, int fallback) {
    const std::wstring t = Trim(s);
    if (t.empty()) return fallback;
    wchar_t* end = nullptr;
    const long v = wcstol(t.c_str(), &end, 10);
    if (end == t.c_str()) return fallback;
    if (v < INT_MIN || v > INT_MAX) return fallback;
    return (int)v;
}

// 命令注入防护：这些字段会被拼进 cmd.exe 命令行，禁止 shell 元字符
bool HasShellMeta(const std::wstring& s) {
    return s.find_first_of(L"&|<>^\"'`%!\r\n\t") != std::wstring::npos;
}

bool ValidateForShell(const Session& s, std::wstring& err) {
    if (HasShellMeta(s.host))     { err = L"主机名包含不允许的字符（& | < > ^ \" ' ` %% !）"; return false; }
    if (HasShellMeta(s.username)) { err = L"用户名包含不允许的字符（& | < > ^ \" ' ` %% !）"; return false; }
    return true;
}

// ------------------------------ 列表操作 -----------------------------------

void RefreshList() {
    if (!g_hList) return;
    SendMessageW(g_hList, WM_SETREDRAW, FALSE, 0);
    ListView_DeleteAllItems(g_hList);
    for (size_t i = 0; i < g_sessions.size(); ++i) {
        const Session& s = g_sessions[i];
        const std::wstring port = FormatI64(s.port);
        LVITEMW it{};
        it.mask     = LVIF_TEXT | LVIF_PARAM;
        it.iItem    = (int)i;
        it.iSubItem = 0;
        it.pszText  = const_cast<wchar_t*>(s.name.c_str());
        it.lParam   = (LPARAM)i;
        const int row = ListView_InsertItem(g_hList, &it);
        if (row < 0) continue;
        ListView_SetItemText(g_hList, row, 1, const_cast<wchar_t*>(s.protocol.c_str()));
        ListView_SetItemText(g_hList, row, 2, const_cast<wchar_t*>(s.host.c_str()));
        ListView_SetItemText(g_hList, row, 3, const_cast<wchar_t*>(port.c_str()));
        ListView_SetItemText(g_hList, row, 4, const_cast<wchar_t*>(s.username.c_str()));
        ListView_SetItemText(g_hList, row, 5, const_cast<wchar_t*>(s.note.c_str()));
    }
    SendMessageW(g_hList, WM_SETREDRAW, TRUE, 0);
    InvalidateRect(g_hList, nullptr, TRUE);

    if (g_hStatus) {
        const std::wstring t = L"会话数：" + FormatI64((long long)g_sessions.size());
        SendMessageW(g_hStatus, SB_SETTEXTW, 0, (LPARAM)t.c_str());
    }
    SetStatus(L"就绪");
}

int SelectedIndex() {
    if (!g_hList) return -1;
    const int row = ListView_GetNextItem(g_hList, -1, LVNI_SELECTED);
    if (row < 0) return -1;
    LVITEMW it{};
    it.mask  = LVIF_PARAM;
    it.iItem = row;
    if (!ListView_GetItem(g_hList, &it)) return -1;
    const int idx = (int)it.lParam;
    if (idx < 0 || idx >= (int)g_sessions.size()) return -1;
    return idx;
}

void SelectRow(int idx) {
    if (!g_hList || idx < 0 || idx >= (int)g_sessions.size()) return;
    ListView_SetItemState(g_hList, idx, LVIS_SELECTED | LVIS_FOCUSED,
                          LVIS_SELECTED | LVIS_FOCUSED);
    ListView_EnsureVisible(g_hList, idx, FALSE);
}

bool PersistSessions() {
    std::wstring err;
    if (!SaveSessions(g_sessions, err)) {
        ShowErr(L"保存会话失败。", err);
        return false;
    }
    return true;
}

// ------------------------------ 连接分发 -----------------------------------

void DoConnect(const Session& s) {
    const std::wstring proto = s.protocol.empty() ? std::wstring(L"SSH") : s.protocol;
    std::wstring verr;
    if (!ValidateForShell(s, verr)) {
        ShowErr(L"无法启动连接：字段包含危险字符。", verr);
        return;
    }

    INT_PTR      rc = 32;      // ShellExecute 返回值 <=32 视为失败
    std::wstring what;

    if (proto == L"SSH" || proto == L"SFTP") {
        const std::wstring target = s.username.empty() ? s.host : (s.username + L"@" + s.host);
        const std::wstring params =
            L"/c start \"cx-ssh-client\" cmd.exe /k ssh -p " + FormatI64(s.port) + L" " + target;
        what = L"ssh -p " + FormatI64(s.port) + L" " + target;
        rc = (INT_PTR)ShellExecuteW(g_hMain, L"open", L"cmd.exe", params.c_str(), nullptr, SW_SHOWNORMAL);
    } else if (proto == L"RDP") {
        const std::wstring params = L"/v:" + s.host + L":" + FormatI64(s.port);
        what = L"mstsc " + params;
        rc = (INT_PTR)ShellExecuteW(g_hMain, L"open", L"mstsc.exe", params.c_str(), nullptr, SW_SHOWNORMAL);
    } else if (proto == L"FTP") {
        const std::wstring url = L"ftp://" + (s.username.empty() ? std::wstring() : s.username + L"@") +
                                 s.host + L":" + FormatI64(s.port);
        what = url;
        rc = (INT_PTR)ShellExecuteW(g_hMain, L"open", url.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    } else if (proto == L"VNC") {
        const std::wstring url = L"vnc://" + s.host + L":" + FormatI64(s.port);
        what = url;
        rc = (INT_PTR)ShellExecuteW(g_hMain, L"open", url.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
        if (rc <= 32) {
            MessageBoxW(g_hMain,
                        (L"系统没有注册 vnc:// 协议的打开方式。\r\n\r\n"
                         L"请先安装任意 VNC 查看器（例如 TightVNC / RealVNC / TigerVNC），"
                         L"安装后本程序即可直接调用。\r\n\r\n目标地址：\r\n" + url).c_str(),
                        L"未找到 VNC 查看器", MB_ICONWARNING | MB_OK);
            return;
        }
    } else {
        MessageBoxW(g_hMain, (L"不支持的协议：" + proto).c_str(), L"cx-ssh-client",
                    MB_ICONERROR | MB_OK);
        return;
    }

    if (rc <= 32) {
        ShowErr(L"启动失败：" + what,
                L"ShellExecute 返回码 " + FormatI64((long long)rc) +
                L"。请确认对应的客户端程序已安装并在 PATH 中。");
    } else {
        SetStatus(L"已启动：" + what);
    }
}

// ------------------------------ 协议探测 -----------------------------------

struct ProbeTask {
    Session s;
    int     timeoutMs = 5000;
};

DWORD WINAPI ProbeThread(LPVOID param) {
    ProbeTask*   t = (ProbeTask*)param;
    ProbeResult* r = new ProbeResult();
    *r = ProbeSession(t->s.protocol, t->s.host, t->s.port, t->timeoutMs);
    if (!PostMessageW(g_hMain, WM_APP_PROBE_DONE, (WPARAM)r, (LPARAM)t)) {
        delete r;              // 主窗口已销毁
        delete t;
    }
    return 0;
}

void StartProbe(const Session& s) {
    if (g_probing) { SetStatus(L"已有探测正在进行，请稍候…"); return; }
    if (s.host.empty()) { ShowErr(L"无法测试连接。", L"主机为空。"); return; }

    g_probing = true;
    SetStatus(L"正在测试连接 " + s.host + L":" + FormatI64(s.port) + L" …");
    SetCursor(LoadCursorW(nullptr, IDC_WAIT));

    ProbeTask* t = new ProbeTask();
    t->s = s;
    HANDLE h = CreateThread(nullptr, 0, ProbeThread, t, 0, nullptr);
    if (!h) {
        delete t;
        g_probing = false;
        SetStatus(L"就绪");
        ShowErr(L"无法开始测试连接。", L"创建线程失败：" + Win32ErrText(GetLastError()));
        return;
    }
    CloseHandle(h);      // 线程会自行结束，句柄不再需要
}

void ShowProbeResult(const Session& s, const ProbeResult& r) {
    std::wstring text;
    text += L"会话：" + s.name + L"\r\n";
    text += L"目标：" + s.protocol + L"  " + s.host + L":" + FormatI64(s.port) + L"\r\n";
    text += L"结果：" + r.summary + L"\r\n";
    text += L"耗时：" + FormatI64(r.elapsedMs) + L" 毫秒\r\n";
    if (!r.banner.empty()) text += L"\r\n探测到 Banner：\r\n" + r.banner + L"\r\n";
    if (!r.detail.empty()) text += L"\r\n详细过程：\r\n" + r.detail;

    SetStatus(r.summary);
    MessageBoxW(g_hMain, text.c_str(),
                r.ok ? L"测试连接 — 成功" : L"测试连接 — 失败",
                (r.ok ? MB_ICONINFORMATION : MB_ICONWARNING) | MB_OK);
}

// ------------------------------ 命令处理 -----------------------------------

void OnNew() {
    Session s;
    s.protocol = L"SSH";
    s.port     = 22;
    if (!ShowEditDialog(g_hMain, s, true)) return;
    g_sessions.push_back(s);
    if (!PersistSessions()) { g_sessions.pop_back(); return; }
    RefreshList();
    SelectRow((int)g_sessions.size() - 1);
    SetStatus(L"已新建会话：" + s.name);
}

void OnEdit() {
    const int idx = SelectedIndex();
    if (idx < 0) { SetStatus(L"请先选中一条会话"); return; }
    Session s = g_sessions[idx];
    if (!ShowEditDialog(g_hMain, s, false)) return;
    g_sessions[idx] = s;
    PersistSessions();
    RefreshList();
    SelectRow(idx);
    SetStatus(L"已保存会话：" + s.name);
}

void OnDelete() {
    const int idx = SelectedIndex();
    if (idx < 0) { SetStatus(L"请先选中一条会话"); return; }
    const std::wstring q = L"确定删除会话「" + g_sessions[idx].name + L"」吗？";
    if (MessageBoxW(g_hMain, q.c_str(), L"删除确认",
                    MB_ICONQUESTION | MB_YESNO | MB_DEFBUTTON2) != IDYES) {
        return;
    }
    const Session backup = g_sessions[idx];
    g_sessions.erase(g_sessions.begin() + idx);
    if (!PersistSessions()) { g_sessions.insert(g_sessions.begin() + idx, backup); return; }
    RefreshList();
    if (!g_sessions.empty()) {
        const int n = (int)g_sessions.size();
        SelectRow(idx < n ? idx : n - 1);
    }
    SetStatus(L"已删除会话");
}

void OnConnect() {
    const int idx = SelectedIndex();
    if (idx < 0) { SetStatus(L"请先选中一条会话"); return; }
    DoConnect(g_sessions[idx]);
}

void OnTest() {
    const int idx = SelectedIndex();
    if (idx < 0) { SetStatus(L"请先选中一条会话"); return; }
    StartProbe(g_sessions[idx]);
}

std::wstring PickFile(bool save) {
    wchar_t buf[MAX_PATH * 2] = { 0 };
    OPENFILENAMEW ofn{};
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner   = g_hMain;
    ofn.lpstrFilter = L"CSV 文件 (*.csv)\0*.csv\0所有文件 (*.*)\0*.*\0\0";
    ofn.lpstrFile   = buf;
    ofn.nMaxFile    = MAX_PATH * 2;
    ofn.lpstrDefExt = L"csv";
    ofn.Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR |
                (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST);
    const BOOL ok = save ? GetSaveFileNameW(&ofn) : GetOpenFileNameW(&ofn);
    if (!ok) return std::wstring();
    return std::wstring(buf);
}

void OnExport() {
    if (g_sessions.empty()) { ShowInfo(L"当前没有会话可以导出。", L"导出CSV"); return; }
    const std::wstring path = PickFile(true);
    if (path.empty()) return;
    std::wstring err;
    if (!ExportCsv(path, g_sessions, err)) { ShowErr(L"导出 CSV 失败。", err); return; }
    ShowInfo(L"已导出 " + FormatI64((long long)g_sessions.size()) + L" 条会话到：\r\n" + path,
             L"导出CSV 成功");
    SetStatus(L"已导出：" + path);
}

void OnImport() {
    const std::wstring path = PickFile(false);
    if (path.empty()) return;
    std::vector<Session> got;
    std::wstring err;
    if (!ImportCsv(path, got, err)) { ShowErr(L"导入 CSV 失败。", err); return; }

    const std::wstring q = L"从文件解析到 " + FormatI64((long long)got.size()) +
                           L" 条会话。\r\n\r\n「是」追加到现有列表\r\n「否」替换现有列表\r\n「取消」放弃导入";
    const int r = MessageBoxW(g_hMain, q.c_str(), L"导入CSV", MB_ICONQUESTION | MB_YESNOCANCEL);
    if (r == IDCANCEL) return;

    const std::vector<Session> backup = g_sessions;
    if (r == IDYES) g_sessions.insert(g_sessions.end(), got.begin(), got.end());
    else            g_sessions = got;

    if (!PersistSessions()) { g_sessions = backup; return; }
    RefreshList();
    ShowInfo(L"导入完成，当前共 " + FormatI64((long long)g_sessions.size()) + L" 条会话。",
             L"导入CSV 成功");
}

void OnAbout() {
    ShowInfo(L"程星SSH客户端 cx-ssh-client  v0.1.0（C++ 原生版）\r\n\r\n"
             L"程星工作室  官网：cxbk.com/cc\r\n"
             L"许可证：MPL-2.0\r\n\r\n"
             L"纯 Win32 API + WinSock2 实现，零第三方依赖。\r\n"
             L"会话密码使用 Windows DPAPI 加密后保存在：\r\n" + StorageFilePath(),
             L"关于");
}

// ------------------------------ 菜单 / 工具栏 ------------------------------

HMENU BuildMenu() {
    HMENU m = CreateMenu();

    HMENU f = CreatePopupMenu();
    AppendMenuW(f, MF_STRING, IDM_IMPORT, L"导入CSV(&I)…");
    AppendMenuW(f, MF_STRING, IDM_EXPORT, L"导出CSV(&E)…");
    AppendMenuW(f, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(f, MF_STRING, IDM_EXIT, L"退出(&X)");
    AppendMenuW(m, MF_POPUP, (UINT_PTR)f, L"文件(&F)");

    HMENU s = CreatePopupMenu();
    AppendMenuW(s, MF_STRING, IDM_NEW,     L"新建(&N)…\tCtrl+N");
    AppendMenuW(s, MF_STRING, IDM_EDIT,    L"编辑(&E)…\tCtrl+E");
    AppendMenuW(s, MF_STRING, IDM_DELETE,  L"删除(&D)\tDel");
    AppendMenuW(s, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(s, MF_STRING, IDM_CONNECT, L"连接(&C)\tEnter");
    AppendMenuW(s, MF_STRING, IDM_TEST,    L"测试连接(&T)\tCtrl+T");
    AppendMenuW(m, MF_POPUP, (UINT_PTR)s, L"会话(&S)");

    HMENU h = CreatePopupMenu();
    AppendMenuW(h, MF_STRING, IDM_ABOUT, L"关于(&A)…");
    AppendMenuW(m, MF_POPUP, (UINT_PTR)h, L"帮助(&H)");
    return m;
}

void BuildToolbar(HWND hParent) {
    g_hTool = CreateWindowExW(0, TOOLBARCLASSNAMEW, nullptr,
                              WS_CHILD | WS_VISIBLE | TBSTYLE_FLAT | TBSTYLE_TOOLTIPS |
                              TBSTYLE_LIST | CCS_NODIVIDER | CCS_NOPARENTALIGN | CCS_TOP,
                              0, 0, 0, 0, hParent, (HMENU)(INT_PTR)IDC_TOOLBAR, g_hInst, nullptr);
    if (!g_hTool) return;

    SendMessageW(g_hTool, TB_BUTTONSTRUCTSIZE, (WPARAM)sizeof(TBBUTTON), 0);
    SendMessageW(g_hTool, TB_SETEXTENDEDSTYLE, 0, TBSTYLE_EX_MIXEDBUTTONS);
    ApplyFont(g_hTool);
    SendMessageW(g_hTool, TB_SETPADDING, 0, MAKELPARAM(Dp(10), Dp(6)));

    // 文案一次性放进工具栏字符串池，iString 用返回的起始下标（文档化做法）
    static const wchar_t kTexts[] = L"新建\0编辑\0删除\0连接\0测试连接\0导入CSV\0导出CSV\0";
    const INT_PTR base = (INT_PTR)SendMessageW(g_hTool, TB_ADDSTRINGW, 0, (LPARAM)kTexts);
    if (base < 0) return;

    struct BtnDef { int cmd; int strIdx; };
    const BtnDef defs[] = {
        { IDM_NEW, 0 }, { IDM_EDIT, 1 }, { IDM_DELETE, 2 },
        { IDM_CONNECT, 3 }, { IDM_TEST, 4 },
        { IDM_IMPORT, 5 }, { IDM_EXPORT, 6 },
    };
    const int kSepAfter[] = { 2, 4 };   // 第 2、4 个按钮之后插入分隔线

    TBBUTTON btns[16] = {};
    int n = 0;
    for (int i = 0; i < (int)(sizeof(defs) / sizeof(defs[0])); ++i) {
        btns[n].iBitmap   = I_IMAGENONE;
        btns[n].idCommand = defs[i].cmd;
        btns[n].fsState   = TBSTATE_ENABLED;
        btns[n].fsStyle   = BTNS_BUTTON | BTNS_AUTOSIZE | BTNS_SHOWTEXT;
        btns[n].iString   = base + defs[i].strIdx;
        ++n;
        for (int k = 0; k < 2; ++k) {
            if (kSepAfter[k] == i) {
                btns[n].fsState = TBSTATE_ENABLED;
                btns[n].fsStyle = BTNS_SEP;
                ++n;
            }
        }
    }
    SendMessageW(g_hTool, TB_ADDBUTTONSW, (WPARAM)n, (LPARAM)btns);
    SendMessageW(g_hTool, TB_AUTOSIZE, 0, 0);
}

void BuildList(HWND hParent) {
    g_hList = CreateWindowExW(WS_EX_CLIENTEDGE, WC_LISTVIEWW, nullptr,
                              WS_CHILD | WS_VISIBLE | WS_TABSTOP | LVS_REPORT |
                              LVS_SINGLESEL | LVS_SHOWSELALWAYS,
                              0, 0, 0, 0, hParent, (HMENU)(INT_PTR)IDC_LISTVIEW, g_hInst, nullptr);
    if (!g_hList) return;
    ListView_SetExtendedListViewStyle(g_hList,
        LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES | LVS_EX_DOUBLEBUFFER | LVS_EX_LABELTIP);
    ApplyFont(g_hList);

    struct Col { const wchar_t* text; int width; };
    static const Col cols[] = {
        { L"名称", 190 }, { L"协议", 70 }, { L"主机", 190 },
        { L"端口", 60 }, { L"用户名", 120 }, { L"备注", 200 },
    };
    for (int i = 0; i < (int)(sizeof(cols) / sizeof(cols[0])); ++i) {
        LVCOLUMNW c{};
        c.mask     = LVCF_TEXT | LVCF_WIDTH | LVCF_SUBITEM;
        c.iSubItem = i;
        c.cx       = Dp(cols[i].width);
        c.pszText  = const_cast<wchar_t*>(cols[i].text);
        ListView_InsertColumn(g_hList, i, &c);
    }
}

void ResizeColumns() {
    if (!g_hList) return;
    static const int w[] = { 190, 70, 190, 60, 120, 200 };
    for (int i = 0; i < 6; ++i) ListView_SetColumnWidth(g_hList, i, Dp(w[i]));
}

void BuildStatus(HWND hParent) {
    g_hStatus = CreateWindowExW(0, STATUSCLASSNAMEW, nullptr,
                                WS_CHILD | WS_VISIBLE | SBARS_SIZEGRIP,
                                0, 0, 0, 0, hParent, (HMENU)(INT_PTR)IDC_STATUSBAR, g_hInst, nullptr);
    if (!g_hStatus) return;
    ApplyFont(g_hStatus);
}

void LayoutChildren(HWND hwnd) {
    RECT rc;
    GetClientRect(hwnd, &rc);
    const int cx = rc.right - rc.left;
    const int cy = rc.bottom - rc.top;

    int top = 0;
    if (g_hTool) {
        SendMessageW(g_hTool, TB_AUTOSIZE, 0, 0);
        RECT t;
        GetWindowRect(g_hTool, &t);
        int th = t.bottom - t.top;
        if (th <= 0) {
            // 工具栏带 CCS_NORESIZE 时 TB_AUTOSIZE 只算尺寸不改窗口，
            // 这里直接用按钮高度兜底，保证工具栏一定看得见。
            const DWORD bs = (DWORD)SendMessageW(g_hTool, TB_GETBUTTONSIZE, 0, 0);
            th = (int)HIWORD(bs) + Dp(2);
        }
        if (th <= 0) th = Dp(30);
        // 显式摆放：宽度跟随客户区，高度取上面算出来的值
        MoveWindow(g_hTool, 0, 0, cx > 0 ? cx : 0, th, TRUE);
        top = th;
    }
    int bottom = 0;
    if (g_hStatus) {
        SendMessageW(g_hStatus, WM_SIZE, 0, 0);
        RECT t;
        GetWindowRect(g_hStatus, &t);
        bottom = t.bottom - t.top;
        if (bottom <= 0) bottom = Dp(23);
        const int parts[2] = { cx - Dp(380), -1 };
        SendMessageW(g_hStatus, SB_SETPARTS, 2, (LPARAM)parts);
    }
    if (g_hList) {
        const int h = cy - top - bottom;
        MoveWindow(g_hList, 0, top, cx > 0 ? cx : 0, h > 0 ? h : 0, TRUE);
    }
}

// ------------------------------ 编辑对话框 ---------------------------------

struct EditCtx {
    Session s;
    bool    isNew = true;
    HWND    hName = nullptr, hProto = nullptr, hHost = nullptr, hPort = nullptr;
    HWND    hUser = nullptr, hPass = nullptr, hNote = nullptr;
    std::wstring lastProto;
    bool    ok   = false;
    bool    done = false;
};

HWND MakeChild(HWND parent, const wchar_t* cls, const wchar_t* text, DWORD style, DWORD exStyle,
               int id) {
    HWND h = CreateWindowExW(exStyle, cls, text, WS_CHILD | WS_VISIBLE | style,
                             0, 0, 0, 0, parent, (HMENU)(INT_PTR)id, g_hInst, nullptr);
    ApplyFont(h);
    return h;
}

void EditLayout(HWND hwnd, EditCtx* c) {
    const int m   = Dp(12);
    const int lw  = Dp(56);
    const int gap = Dp(8);
    RECT rc;
    GetClientRect(hwnd, &rc);
    const int cx = rc.right - rc.left;
    const int ew = cx - m * 2 - lw - gap;
    if (ew <= 0) return;

    const int lblIds[7] = { IDC_LBL_NAME, IDC_LBL_PROTO, IDC_LBL_HOST, IDC_LBL_PORT,
                            IDC_LBL_USER, IDC_LBL_PASS,  IDC_LBL_NOTE };
    HWND edits[6] = { c->hName, c->hProto, c->hHost, c->hPort, c->hUser, c->hPass };

    const int rowH = Dp(24);
    const int step = Dp(31);
    int y = m;
    for (int i = 0; i < 6; ++i) {
        HWND lb = GetDlgItem(hwnd, lblIds[i]);
        if (lb) MoveWindow(lb, m, y + Dp(5), lw, Dp(18), TRUE);
        if (edits[i]) MoveWindow(edits[i], m + lw + gap, y, ew, rowH, TRUE);
        y += step;
    }
    HWND lbNote = GetDlgItem(hwnd, IDC_LBL_NOTE);
    if (lbNote) MoveWindow(lbNote, m, y + Dp(5), lw, Dp(18), TRUE);
    const int noteH = Dp(66);
    if (c->hNote) MoveWindow(c->hNote, m + lw + gap, y, ew, noteH, TRUE);
    y += noteH + Dp(14);

    HWND ok = GetDlgItem(hwnd, IDOK);
    HWND ca = GetDlgItem(hwnd, IDCANCEL);
    const int bw = Dp(88), bh = Dp(28);
    if (ca) MoveWindow(ca, cx - m - bw, y, bw, bh, TRUE);
    if (ok) MoveWindow(ok, cx - m - bw * 2 - Dp(8), y, bw, bh, TRUE);
}

LRESULT CALLBACK EditProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    EditCtx* c = (EditCtx*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);

    switch (msg) {
    case WM_NCCREATE: {
        CREATESTRUCTW* cs = (CREATESTRUCTW*)lp;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, (LONG_PTR)cs->lpCreateParams);
        return TRUE;
    }
    case WM_CREATE: {
        if (!c) return -1;

        struct L { int id; const wchar_t* t; };
        static const L ls[] = {
            { IDC_LBL_NAME,  L"名称：" }, { IDC_LBL_PROTO, L"协议：" }, { IDC_LBL_HOST, L"主机：" },
            { IDC_LBL_PORT,  L"端口：" }, { IDC_LBL_USER,  L"用户名：" }, { IDC_LBL_PASS, L"密码：" },
            { IDC_LBL_NOTE,  L"备注：" },
        };
        for (int i = 0; i < 7; ++i) MakeChild(hwnd, L"STATIC", ls[i].t, SS_LEFT, 0, ls[i].id);

        c->hName = MakeChild(hwnd, L"EDIT", L"", ES_LEFT | ES_AUTOHSCROLL | WS_TABSTOP,
                             WS_EX_CLIENTEDGE, IDC_ED_NAME);
        c->hHost = MakeChild(hwnd, L"EDIT", L"", ES_LEFT | ES_AUTOHSCROLL | WS_TABSTOP,
                             WS_EX_CLIENTEDGE, IDC_ED_HOST);
        c->hPort = MakeChild(hwnd, L"EDIT", L"", ES_LEFT | ES_NUMBER | WS_TABSTOP,
                             WS_EX_CLIENTEDGE, IDC_ED_PORT);
        c->hUser = MakeChild(hwnd, L"EDIT", L"", ES_LEFT | ES_AUTOHSCROLL | WS_TABSTOP,
                             WS_EX_CLIENTEDGE, IDC_ED_USER);
        c->hPass = MakeChild(hwnd, L"EDIT", L"", ES_LEFT | ES_AUTOHSCROLL | ES_PASSWORD | WS_TABSTOP,
                             WS_EX_CLIENTEDGE, IDC_ED_PASS);
        c->hNote = MakeChild(hwnd, L"EDIT", L"",
                             ES_LEFT | ES_MULTILINE | ES_WANTRETURN | ES_AUTOVSCROLL |
                             WS_VSCROLL | WS_TABSTOP, WS_EX_CLIENTEDGE, IDC_ED_NOTE);

        c->hProto = MakeChild(hwnd, L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP,
                              WS_EX_CLIENTEDGE, IDC_CB_PROTO);
        for (int i = 0; ProtocolList()[i]; ++i)
            SendMessageW(c->hProto, CB_ADDSTRING, 0, (LPARAM)ProtocolList()[i]);

        MakeChild(hwnd, L"BUTTON", L"确定", BS_DEFPUSHBUTTON | WS_TABSTOP, 0, IDOK);
        MakeChild(hwnd, L"BUTTON", L"取消", BS_PUSHBUTTON | WS_TABSTOP, 0, IDCANCEL);

        SetText(c->hName, c->s.name);
        SetText(c->hHost, c->s.host);
        SetText(c->hPort, FormatI64(c->s.port));
        SetText(c->hUser, c->s.username);
        SetText(c->hPass, c->s.password);
        SetText(c->hNote, c->s.note);

        int sel = 0;
        for (int i = 0; ProtocolList()[i]; ++i) {
            if (c->s.protocol == ProtocolList()[i]) { sel = i; break; }
        }
        SendMessageW(c->hProto, CB_SETCURSEL, sel, 0);
        c->lastProto = ProtocolList()[sel];

        EditLayout(hwnd, c);
        return 0;
    }
    case WM_SIZE:
        if (c) EditLayout(hwnd, c);
        return 0;
    case WM_COMMAND: {
        const int id   = LOWORD(wp);
        const int code = HIWORD(wp);

        if (id == IDC_CB_PROTO && code == CBN_SELCHANGE && c) {
            // 协议变化时，如果端口还是旧协议的默认端口，就跟着一起换
            const int sel = (int)SendMessageW(c->hProto, CB_GETCURSEL, 0, 0);
            wchar_t buf[32] = { 0 };
            if (sel >= 0) SendMessageW(c->hProto, CB_GETLBTEXT, sel, (LPARAM)buf);
            const std::wstring np = buf;
            if (ParseIntOr(GetText(c->hPort), -1) == DefaultPortFor(c->lastProto)) {
                SetText(c->hPort, FormatI64(DefaultPortFor(np)));
            }
            c->lastProto = np;
            return 0;
        }
        if (id == IDOK && c) {
            Session s;
            s.name     = Trim(GetText(c->hName));
            s.host     = Trim(GetText(c->hHost));
            s.username = Trim(GetText(c->hUser));
            s.password = GetText(c->hPass);      // 密码保留原样，不做 Trim
            s.note     = GetText(c->hNote);

            const int sel = (int)SendMessageW(c->hProto, CB_GETCURSEL, 0, 0);
            wchar_t buf[32] = { 0 };
            if (sel >= 0) SendMessageW(c->hProto, CB_GETLBTEXT, sel, (LPARAM)buf);
            s.protocol = NormalizeProtocol(buf);
            if (s.protocol.empty()) s.protocol = L"SSH";

            if (s.host.empty()) {
                MessageBoxW(hwnd, L"主机不能为空。", L"输入有误", MB_ICONWARNING | MB_OK);
                SetFocus(c->hHost);
                return 0;
            }
            const int port = ParseIntOr(GetText(c->hPort), -1);
            if (port <= 0 || port > 65535) {
                MessageBoxW(hwnd, L"端口必须是 1 到 65535 之间的整数。", L"输入有误",
                            MB_ICONWARNING | MB_OK);
                SetFocus(c->hPort);
                return 0;
            }
            s.port = port;
            if (s.name.empty()) s.name = s.username.empty() ? s.host : (s.username + L"@" + s.host);

            c->s  = s;
            c->ok = true;
            DestroyWindow(hwnd);
            return 0;
        }
        if (id == IDCANCEL) { DestroyWindow(hwnd); return 0; }
        break;
    }
    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        if (c) c->done = true;
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// ------------------------------ 主窗口过程 ---------------------------------

LRESULT CALLBACK MainProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CREATE: {
        g_hMain = hwnd;
        g_dpi   = QueryDpi(hwnd);
        g_hFont = MakeUiFont(g_dpi);
        SetMenu(hwnd, BuildMenu());
        BuildToolbar(hwnd);
        BuildList(hwnd);
        BuildStatus(hwnd);
        LayoutChildren(hwnd);
        RefreshList();
        return 0;
    }
    case WM_SIZE:
        LayoutChildren(hwnd);
        return 0;
    case WM_GETMINMAXINFO: {
        MINMAXINFO* mmi = (MINMAXINFO*)lp;
        mmi->ptMinTrackSize.x = Dp(660);
        mmi->ptMinTrackSize.y = Dp(420);
        return 0;
    }
    case WM_SETFOCUS:
        if (g_hList) SetFocus(g_hList);
        return 0;
    case WM_DPICHANGED: {
        g_dpi = HIWORD(wp);
        if (g_dpi < 72 || g_dpi > 480) g_dpi = 96;
        HFONT nf = MakeUiFont(g_dpi);
        if (nf) {
            if (g_hFont) DeleteObject(g_hFont);
            g_hFont = nf;
            ApplyFont(g_hTool);
            ApplyFont(g_hList);
            ApplyFont(g_hStatus);
            if (g_hTool) SendMessageW(g_hTool, TB_SETPADDING, 0, MAKELPARAM(Dp(10), Dp(6)));
        }
        ResizeColumns();
        RECT* r = (RECT*)lp;
        SetWindowPos(hwnd, nullptr, r->left, r->top, r->right - r->left, r->bottom - r->top,
                     SWP_NOZORDER | SWP_NOACTIVATE);
        LayoutChildren(hwnd);
        return 0;
    }
    case WM_NOTIFY: {
        NMHDR* nh = (NMHDR*)lp;
        if (nh && nh->idFrom == IDC_LISTVIEW) {
            if (nh->code == NM_DBLCLK) {
                const int idx = SelectedIndex();
                if (idx >= 0) DoConnect(g_sessions[idx]);
                return 0;
            }
            if (nh->code == LVN_KEYDOWN) {
                const NMLVKEYDOWN* k = (NMLVKEYDOWN*)lp;
                if (k->wVKey == VK_RETURN) { OnConnect(); return 0; }
                if (k->wVKey == VK_DELETE) { OnDelete();  return 0; }
            }
        }
        break;
    }
    case WM_APP_PROBE_DONE: {
        ProbeResult* r = (ProbeResult*)wp;
        ProbeTask*   t = (ProbeTask*)lp;
        g_probing = false;
        SetCursor(LoadCursorW(nullptr, IDC_ARROW));
        if (r && t) ShowProbeResult(t->s, *r);
        delete r;
        delete t;
        return 0;
    }
    case WM_COMMAND: {
        switch (LOWORD(wp)) {
        case IDM_NEW:     OnNew();     return 0;
        case IDM_EDIT:    OnEdit();    return 0;
        case IDM_DELETE:  OnDelete();  return 0;
        case IDM_CONNECT: OnConnect(); return 0;
        case IDM_TEST:    OnTest();    return 0;
        case IDM_IMPORT:  OnImport();  return 0;
        case IDM_EXPORT:  OnExport();  return 0;
        case IDM_ABOUT:   OnAbout();   return 0;
        case IDM_EXIT:    DestroyWindow(hwnd); return 0;
        default: break;
        }
        break;
    }
    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        if (g_hFont) { DeleteObject(g_hFont); g_hFont = nullptr; }
        g_hMain = nullptr;
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

bool RegisterClasses() {
    WNDCLASSEXW wc{};
    wc.cbSize        = sizeof(wc);
    wc.style         = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc   = MainProc;
    wc.hInstance     = g_hInst;
    wc.hIcon         = LoadIconW(nullptr, IDI_APPLICATION);
    wc.hCursor       = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wc.lpszClassName = kMainClass;
    wc.hIconSm       = LoadIconW(nullptr, IDI_APPLICATION);
    if (!RegisterClassExW(&wc)) return false;

    WNDCLASSEXW ec{};
    ec.cbSize        = sizeof(ec);
    ec.style         = CS_HREDRAW | CS_VREDRAW;
    ec.lpfnWndProc   = EditProc;
    ec.hInstance     = g_hInst;
    ec.hIcon         = LoadIconW(nullptr, IDI_APPLICATION);
    ec.hCursor       = LoadCursorW(nullptr, IDC_ARROW);
    ec.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
    ec.lpszClassName = kEditClass;
    if (!RegisterClassExW(&ec)) return false;
    return true;
}

void EnableDpiAwareness() {
    HMODULE u = GetModuleHandleW(L"user32.dll");
    if (u) {
        typedef BOOL (WINAPI *PFN_SetCtx)(HANDLE);
        PFN_SetCtx p = (PFN_SetCtx)(void*)GetProcAddress(u, "SetProcessDpiAwarenessContext");
        if (p && p((HANDLE)-4)) return;                       // PER_MONITOR_AWARE_V2
        typedef BOOL (WINAPI *PFN_SetAware)(void);
        PFN_SetAware p2 = (PFN_SetAware)(void*)GetProcAddress(u, "SetProcessDPIAware");
        if (p2 && p2()) return;
    }
    HMODULE s = LoadLibraryW(L"shcore.dll");                  // Win8.1 上的备用方案
    if (s) {
        typedef HRESULT (WINAPI *PFN_SetProcessDpiAwareness)(int);
        PFN_SetProcessDpiAwareness p3 =
            (PFN_SetProcessDpiAwareness)(void*)GetProcAddress(s, "SetProcessDpiAwareness");
        if (p3) p3(2);                                        // PROCESS_PER_MONITOR_DPI_AWARE
        FreeLibrary(s);
    }
}

} // namespace

// ---------------------------------------------------------------------------
// 模态会话编辑对话框
// ---------------------------------------------------------------------------
bool ShowEditDialog(HWND parent, Session& s, bool isNew) {
    EditCtx ctx;
    ctx.s     = s;
    ctx.isNew = isNew;

    RECT rc = { 0, 0, Dp(430), Dp(340) };
    AdjustWindowRectEx(&rc, WS_POPUP | WS_CAPTION | WS_SYSMENU, FALSE,
                       WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT);

    HWND h = CreateWindowExW(WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT, kEditClass,
                             isNew ? L"新建会话" : L"编辑会话",
                             WS_POPUP | WS_CAPTION | WS_SYSMENU,
                             CW_USEDEFAULT, CW_USEDEFAULT,
                             rc.right - rc.left, rc.bottom - rc.top,
                             parent, nullptr, g_hInst, &ctx);
    if (!h) return false;

    CenterWindow(h, parent);
    EnableWindow(parent, FALSE);
    ShowWindow(h, SW_SHOW);
    UpdateWindow(h);
    if (ctx.hName) SetFocus(ctx.hName);

    MSG msg;
    while (!ctx.done && GetMessageW(&msg, nullptr, 0, 0) > 0) {
        // 单行控件里回车 = 确定，ESC = 取消；备注框保留回车换行
        if (msg.message == WM_KEYDOWN && msg.hwnd && IsChild(h, msg.hwnd) && msg.hwnd != ctx.hNote) {
            if (msg.wParam == VK_RETURN) { SendMessageW(h, WM_COMMAND, IDOK, 0);     continue; }
            if (msg.wParam == VK_ESCAPE) { SendMessageW(h, WM_COMMAND, IDCANCEL, 0); continue; }
        }
        if (!IsDialogMessageW(h, &msg)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    if (IsWindow(h)) DestroyWindow(h);
    EnableWindow(parent, TRUE);
    SetForegroundWindow(parent);

    if (ctx.ok) { s = ctx.s; return true; }
    return false;
}

// ---------------------------------------------------------------------------
int RunMainWindow(HINSTANCE hInst, int nCmdShow) {
    g_hInst = hInst;
    EnableDpiAwareness();

    INITCOMMONCONTROLSEX icc{};
    icc.dwSize = sizeof(icc);
    icc.dwICC  = ICC_BAR_CLASSES | ICC_LISTVIEW_CLASSES | ICC_STANDARD_CLASSES;
    InitCommonControlsEx(&icc);

    if (!RegisterClasses()) {
        MessageBoxW(nullptr, L"注册窗口类失败。", L"cx-ssh-client", MB_ICONERROR | MB_OK);
        return 1;
    }

    // 载入会话；数据损坏时提示并备份重建，绝不崩溃
    std::wstring err;
    if (!LoadSessions(g_sessions, err)) {
        const std::wstring backup = BackupCorruptStore();
        std::wstring m = L"读取本地会话数据失败：\r\n\r\n" + err + L"\r\n\r\n";
        m += backup.empty()
             ? L"已忽略损坏的数据，本次使用空列表。"
             : (L"损坏的文件已备份为：\r\n" + backup + L"\r\n\r\n本次将重建会话列表。");
        MessageBoxW(nullptr, m.c_str(), L"数据文件损坏 — 已重建", MB_ICONWARNING | MB_OK);
        g_sessions.clear();
    }

    const UINT dpi = QueryDpi(nullptr);
    RECT rc = { 0, 0, MulDiv(940, (int)dpi, 96), MulDiv(580, (int)dpi, 96) };
    AdjustWindowRectEx(&rc, WS_OVERLAPPEDWINDOW, TRUE, 0);

    HWND hwnd = CreateWindowExW(0, kMainClass, kAppTitle, WS_OVERLAPPEDWINDOW,
                                CW_USEDEFAULT, CW_USEDEFAULT,
                                rc.right - rc.left, rc.bottom - rc.top,
                                nullptr, nullptr, hInst, nullptr);
    if (!hwnd) {
        MessageBoxW(nullptr, L"创建主窗口失败。", L"cx-ssh-client", MB_ICONERROR | MB_OK);
        return 1;
    }

    ShowWindow(hwnd, nCmdShow);
    UpdateWindow(hwnd);

    ACCEL accs[4] = {};
    accs[0].fVirt = FCONTROL | FVIRTKEY; accs[0].key = 'N'; accs[0].cmd = IDM_NEW;
    accs[1].fVirt = FCONTROL | FVIRTKEY; accs[1].key = 'E'; accs[1].cmd = IDM_EDIT;
    accs[2].fVirt = FCONTROL | FVIRTKEY; accs[2].key = 'T'; accs[2].cmd = IDM_TEST;
    accs[3].fVirt = FVIRTKEY;            accs[3].key = VK_F5; accs[3].cmd = IDM_CONNECT;
    HACCEL hAcc = CreateAcceleratorTableW(accs, 4);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        if (hAcc && TranslateAcceleratorW(hwnd, hAcc, &msg)) continue;
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    if (hAcc) DestroyAcceleratorTable(hAcc);
    return (int)msg.wParam;
}
