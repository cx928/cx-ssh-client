// probe.cpp - 真实协议探测：TCP + SSH/FTP/VNC banner 握手（WinSock2）
// 程星SSH客户端 (cx-ssh-client) 原生 C++ 版 / MPL-2.0
#include "probe.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <cstdio>
#include <cstring>

namespace {

// WSA 自动初始化/清理
class WinsockGuard {
public:
    WinsockGuard() {
        WSADATA wsa{};
        ok_ = (WSAStartup(MAKEWORD(2, 2), &wsa) == 0);
    }
    ~WinsockGuard() { if (ok_) WSACleanup(); }
    bool ok() const { return ok_; }
private:
    bool ok_ = false;
};

SOCKET g_sock = INVALID_SOCKET;

void CloseSock() {
    if (g_sock != INVALID_SOCKET) {
        closesocket(g_sock);
        g_sock = INVALID_SOCKET;
    }
}

// 解析端口文本
std::string PortText(int port) {
    char b[16] = { 0 };
    sprintf_s(b, 16, "%d", port);
    return std::string(b);
}

// 非阻塞 connect + select 超时。成功返回 true 并令 out 为已连接的套接字。
bool ConnectTimeout(const std::wstring& host, int port, int timeoutMs, SOCKET& out, std::wstring& err) {
    out = INVALID_SOCKET;
    if (host.empty()) { err = L"主机名为空"; return false; }
    if (port <= 0 || port > 65535) { err = L"端口号非法：" + FormatI64(port); return false; }

    const std::string portText = PortText(port);
    ADDRINFOW hints{};
    hints.ai_family   = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    ADDRINFOW* res = nullptr;
    const int gr = GetAddrInfoW(host.c_str(), Utf8ToWide(portText).c_str(), &hints, &res);
    if (gr != 0 || !res) {
        err = L"域名解析失败：" + host + L"（WSA 错误 " + FormatI64(gr) + L"）";
        return false;
    }

    std::wstring lastErr = L"没有可用的地址";
    for (ADDRINFOW* p = res; p; p = p->ai_next) {
        SOCKET s = socket(p->ai_family, p->ai_socktype, p->ai_protocol);
        if (s == INVALID_SOCKET) { lastErr = L"创建套接字失败：" + Win32ErrText(WSAGetLastError()); continue; }

        // 设为非阻塞，connect 立刻返回，再用 select 等待可写
        u_long nb = 1;
        ioctlsocket(s, FIONBIO, &nb);

        const int cr = connect(s, p->ai_addr, (int)p->ai_addrlen);
        if (cr == SOCKET_ERROR) {
            const int ce = WSAGetLastError();
            if (ce != WSAEWOULDBLOCK && ce != WSAEINPROGRESS && ce != WSAEINVAL) {
                lastErr = L"连接失败：" + Win32ErrText(ce);
                closesocket(s);
                continue;
            }
            fd_set wset;
            FD_ZERO(&wset);
            FD_SET(s, &wset);
            timeval tv{};
            tv.tv_sec  = timeoutMs / 1000;
            tv.tv_usec = (timeoutMs % 1000) * 1000;
            const int sel = select(0, nullptr, &wset, nullptr, &tv);
            if (sel == 0) {
                lastErr = L"连接超时（" + FormatI64(timeoutMs) + L" 毫秒内未建立 TCP 连接）";
                closesocket(s);
                continue;
            }
            if (sel == SOCKET_ERROR) {
                lastErr = L"select 失败：" + Win32ErrText(WSAGetLastError());
                closesocket(s);
                continue;
            }
            int soErr = 0;
            int len = sizeof(soErr);
            if (getsockopt(s, SOL_SOCKET, SO_ERROR, (char*)&soErr, &len) != 0 || soErr != 0) {
                lastErr = L"连接失败：" + Win32ErrText(soErr ? soErr : WSAGetLastError());
                closesocket(s);
                continue;
            }
        }

        // 关闭 Nagle，探测时响应更快
        BOOL nodelay = TRUE;
        setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char*)&nodelay, sizeof(nodelay));

        out = s;
        FreeAddrInfoW(res);
        return true;
    }

    FreeAddrInfoW(res);
    err = lastErr;
    return false;
}

// 带超时收数据。>0 收到字节数；0 对端关闭；-1 出错；-2 超时
int RecvTimeout(SOCKET s, char* buf, int len, int timeoutMs) {
    fd_set rset;
    FD_ZERO(&rset);
    FD_SET(s, &rset);
    timeval tv{};
    tv.tv_sec  = timeoutMs / 1000;
    tv.tv_usec = (timeoutMs % 1000) * 1000;
    const int sel = select(0, &rset, nullptr, nullptr, &tv);
    if (sel == 0) return -2;
    if (sel == SOCKET_ERROR) return -1;
    const int n = recv(s, buf, len, 0);
    if (n == 0) return 0;
    if (n == SOCKET_ERROR) return -1;
    return n;
}

bool SendAll(SOCKET s, const char* data, int len) {
    int sent = 0;
    while (sent < len) {
        const int n = send(s, data + sent, len - sent, 0);
        if (n == SOCKET_ERROR || n == 0) return false;
        sent += n;
    }
    return true;
}

// 剩余超时预算，避免各阶段各等一个完整超时
int Remain(int totalMs, ULONGLONG startTick) {
    const ULONGLONG used = GetTickCount64() - startTick;
    if (used >= (ULONGLONG)totalMs) return 0;
    return (int)((ULONGLONG)totalMs - used);
}

std::wstring Sanitize(const std::string& raw) {
    std::string out;
    out.reserve(raw.size());
    for (unsigned char c : raw) {
        if (c == '\r' || c == '\n' || c == '\t')      out += ' ';
        else if (c < 0x20 || c == 0x7F)               out += '.';
        else                                          out += (char)c;
    }
    return Trim(Utf8ToWide(out));
}

// 从缓冲区里取第一行
std::string FirstLine(const std::string& buf) {
    size_t e = buf.find_first_of("\r\n");
    if (e == std::string::npos) e = buf.size();
    return buf.substr(0, e);
}

// --------------------------- 各协议探测 ------------------------------------

ProbeResult DoSsh(int timeoutMs, ULONGLONG start) {
    ProbeResult r;
    // 主动发送客户端标识，保证服务端一定会回应（RFC 4253 允许双向先发）
    const char* clientBanner = "SSH-2.0-CXSSHClient_1.0\r\n";
    SendAll(g_sock, clientBanner, (int)strlen(clientBanner));

    std::string buf;
    while ((int)buf.size() < 1024) {
        const int left = Remain(timeoutMs, start);
        if (left <= 0) break;
        char tmp[512];
        const int n = RecvTimeout(g_sock, tmp, (int)sizeof(tmp), left);
        if (n > 0) {
            buf.append(tmp, n);
            if (buf.find('\n') != std::string::npos) break;   // 拿到一整行
            continue;
        }
        if (n == 0)  { r.detail += L"服务端提前关闭连接。"; break; }
        if (n == -2) { r.detail += L"等待 SSH 标识超时。"; break; }
        r.detail += L"接收出错：" + Win32ErrText(WSAGetLastError()) + L"。";
        break;
    }

    const std::string line = FirstLine(buf);
    const std::wstring w   = Sanitize(line);
    if (w.rfind(L"SSH-", 0) == 0) {
        r.ok     = true;
        r.banner = w;
        r.summary = L"SSH 服务可用";
        if (w.rfind(L"SSH-2.0-", 0) == 0 || w.rfind(L"SSH-1.99-", 0) == 0) {
            r.detail += L"协议版本符合 SSH-2.0 规范。";
        } else {
            r.detail += L"服务端仅支持旧版 SSH-1，存在兼容与安全风险。";
        }
    } else if (!w.empty()) {
        r.summary = L"端口已连接，但返回内容不是 SSH 标识";
        r.detail += L"收到：" + w;
    } else {
        r.summary = L"端口已连接，但未收到 SSH 标识（可能不是 SSH 服务）";
    }
    return r;
}

ProbeResult DoFtp(int timeoutMs, ULONGLONG start) {
    ProbeResult r;
    std::string buf;
    while ((int)buf.size() < 1024) {
        const int left = Remain(timeoutMs, start);
        if (left <= 0) break;
        char tmp[512];
        const int n = RecvTimeout(g_sock, tmp, (int)sizeof(tmp), left);
        if (n > 0) {
            buf.append(tmp, n);
            if (buf.find('\n') != std::string::npos) break;
            continue;
        }
        if (n == 0)  { r.detail += L"服务端提前关闭连接。"; break; }
        if (n == -2) { r.detail += L"等待 FTP 欢迎信息超时。"; break; }
        r.detail += L"接收出错：" + Win32ErrText(WSAGetLastError()) + L"。";
        break;
    }

    const std::wstring w = Sanitize(FirstLine(buf));
    if (w.rfind(L"220", 0) == 0) {
        r.ok      = true;
        r.banner  = w;
        r.summary = L"FTP 服务可用";
        r.detail += L"收到 220 欢迎码，服务就绪。";
    } else if (!w.empty()) {
        r.summary = L"端口已连接，但不是标准 FTP 欢迎信息";
        r.detail += L"收到：" + w;
    } else {
        r.summary = L"端口已连接，但未收到 FTP 欢迎信息（可能不是 FTP 服务）";
    }
    return r;
}

ProbeResult DoVnc(int timeoutMs, ULONGLONG start) {
    ProbeResult r;
    char buf[64] = { 0 };
    int  got = 0;
    while (got < 12) {
        const int left = Remain(timeoutMs, start);
        if (left <= 0) break;
        const int n = RecvTimeout(g_sock, buf + got, 12 - got, left);
        if (n > 0) { got += n; continue; }
        if (n == 0)  { r.detail += L"服务端提前关闭连接。"; break; }
        if (n == -2) { r.detail += L"等待 RFB 版本号超时。"; break; }
        r.detail += L"接收出错：" + Win32ErrText(WSAGetLastError()) + L"。";
        break;
    }

    if (got < 12 || strncmp(buf, "RFB ", 4) != 0) {
        const std::wstring w = Sanitize(std::string(buf, (size_t)(got > 0 ? got : 0)));
        r.summary = L"端口已连接，但未收到 RFB 版本号（可能不是 VNC 服务）";
        if (!w.empty()) r.detail += L"收到：" + w;
        return r;
    }

    // "RFB 003.008\n" -> major=3 minor=8
    int major = 0, minor = 0;
    if (sscanf_s(std::string(buf, 12).c_str(), "RFB %3d.%3d", &major, &minor) != 2) {
        r.summary = L"RFB 版本号格式异常";
        r.detail += L"收到：" + Sanitize(std::string(buf, 12));
        return r;
    }

    // 按协议回发客户端选定版本（12 字节，其中的 16 位大端版本号 major/minor）
    int useMinor = minor > 8 ? 8 : minor;          // 本程序最高支持 3.8
    if (major != 3 || useMinor < 3) useMinor = 3;  // 至少 3.3
    char reply[16] = { 0 };
    sprintf_s(reply, 16, "RFB %03d.%03d\n", 3, useMinor);
    const bool replied = SendAll(g_sock, reply, 12);

    r.ok     = true;
    r.summary = L"VNC 服务可用";
    r.banner  = Sanitize(std::string(buf, 12));
    r.detail += L"服务端版本 " + FormatI64(major) + L"." + FormatI64(minor) +
                L"，已回发客户端版本 " + Sanitize(std::string(reply, 12)) +
                (replied ? L"。\r\n" : L"（回发失败）。\r\n");

    // 顺手读取安全类型数量（1 字节），验证握手确实在推进
    if (useMinor >= 7) {
        char c = 0;
        const int left = Remain(timeoutMs, start);
        const int n = (left > 0) ? RecvTimeout(g_sock, &c, 1, left) : -2;
        if (n == 1) {
            const int cnt = (unsigned char)c;
            if (cnt == 0) {
                r.detail += L"服务端要求进一步认证（返回 0 个安全类型）。";
            } else {
                r.detail += L"服务端提供 " + FormatI64(cnt) + L" 种安全类型，握手正常。";
            }
        }
    }
    return r;
}

} // namespace

// ---------------------------------------------------------------------------

bool TcpConnectTest(const std::wstring& host, int port, int timeoutMs, std::wstring& err) {
    err.clear();
    WinsockGuard wsa;
    if (!wsa.ok()) { err = L"Winsock 初始化失败：" + Win32ErrText(WSAGetLastError()); return false; }
    SOCKET s = INVALID_SOCKET;
    if (!ConnectTimeout(host, port, timeoutMs, s, err)) return false;
    closesocket(s);
    return true;
}

ProbeResult ProbeSession(const std::wstring& proto, const std::wstring& host, int port, int timeoutMs) {
    ProbeResult r;
    const std::wstring p = ToUpperAscii(Trim(proto));

    WinsockGuard wsa;
    if (!wsa.ok()) {
        r.summary = L"探测失败";
        r.detail  = L"Winsock 初始化失败：" + Win32ErrText(WSAGetLastError());
        return r;
    }

    const ULONGLONG start = GetTickCount64();
    std::wstring cerr;
    if (!ConnectTimeout(host, port, timeoutMs, g_sock, cerr)) {
        r.elapsedMs = (long long)(GetTickCount64() - start);
        r.summary   = L"连接失败";
        r.detail    = cerr;
        return r;
    }

    r.detail += L"TCP " + host + L":" + FormatI64(port) + L" 已连接。\r\n";

    if (p == L"SSH" || p == L"SFTP")      r = DoSsh(timeoutMs, start);
    else if (p == L"FTP")                 r = DoFtp(timeoutMs, start);
    else if (p == L"VNC")                 r = DoVnc(timeoutMs, start);
    else if (p == L"RDP") {
        // RDP 需要 TLS + CredSSP 协商，这里只做 TCP 连通性测试
        r.ok      = true;
        r.summary = L"RDP 端口可达（未做 TLS 协商）";
        r.detail += L"RDP 使用 TLS 协商，本程序只验证 TCP 可达性。";
    } else {
        r.ok      = true;
        r.summary = L"TCP 端口可达";
        r.detail += L"未指定具体协议，仅完成 TCP 连通性测试。";
    }

    r.elapsedMs = (long long)(GetTickCount64() - start);
    CloseSock();
    return r;
}
