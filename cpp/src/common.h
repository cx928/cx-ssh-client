// common.h - 公共头文件：Windows 版本宏、通用工具函数声明
// 程星SSH客户端 (cx-ssh-client) 原生 C++ 版 / MPL-2.0
#pragma once

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601   // Windows 7 及以上
#endif

#include <windows.h>
#include <string>
#include <vector>

// ---------------------------------------------------------------------------
// 标准输出（本程序是 GUI 子系统，需要时手工接管父进程控制台）
// ---------------------------------------------------------------------------
void OutInit();                              // 初始化输出通道（进程启动时调用一次）
void OutW(const std::wstring& line);         // 输出一行
void OutF(const wchar_t* fmt, ...);          // 格式化后输出一行

// ---------------------------------------------------------------------------
// 字符串转换与 Win32 错误文本
// ---------------------------------------------------------------------------
std::string  WideToUtf8(const std::wstring& w);
std::wstring Utf8ToWide(const std::string& s);
std::wstring Win32ErrText(DWORD code);       // "错误 10061: 由于目标计算机积极拒绝..."
std::wstring Trim(const std::wstring& s);
std::wstring ToUpperAscii(const std::wstring& s);
std::wstring FormatI64(long long v);
bool         EqualsNoCase(const std::wstring& a, const std::wstring& b);
