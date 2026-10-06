// probe.h - 真实协议探测（WinSock2，无第三方库）
// 程星SSH客户端 (cx-ssh-client) 原生 C++ 版 / MPL-2.0
#pragma once

#include "common.h"

// 探测结果
struct ProbeResult {
    bool         ok = false;   // 是否探测成功
    std::wstring summary;      // 一行结论（用于 MessageBox 标题内容）
    std::wstring banner;       // 探测到的服务端 banner（可能为空）
    std::wstring detail;       // 过程细节 / 失败原因
    long long    elapsedMs = 0;
};

// 纯 TCP 连通性测试（非阻塞 connect + select 超时）
bool TcpConnectTest(const std::wstring& host, int port, int timeoutMs, std::wstring& err);

// 按协议探测。proto 取 SSH/SFTP/FTP/RDP/VNC（大小写不敏感）。
ProbeResult ProbeSession(const std::wstring& proto, const std::wstring& host, int port,
                         int timeoutMs = 5000);
