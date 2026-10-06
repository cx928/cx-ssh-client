// common.cpp - 通用工具实现（字符串转换、Win32 错误文本、格式化）
// 程星SSH客户端 (cx-ssh-client) 原生 C++ 版 / MPL-2.0
//
// 这些函数主程序和 tools\gen.cpp（内存/存储测试工具）都要用，所以单独成一个编译单元。
#include "common.h"

#include <cstdio>

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
    if (code == 0) return L"成功";
    std::wstring msg = L"错误 " + FormatI64((long long)code);
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
    while (e > b && (s[e - 1] == L' ' || s[e - 1] == L'\t' ||
                     s[e - 1] == L'\r' || s[e - 1] == L'\n')) --e;
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
