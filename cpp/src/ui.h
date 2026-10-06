// ui.h - Win32 界面：会话管理主窗口 + 会话编辑对话框
// 程星SSH客户端 (cx-ssh-client) 原生 C++ 版 / MPL-2.0
#pragma once

#include "common.h"
#include "session.h"

// 运行主窗口（阻塞至退出），返回进程退出码
int RunMainWindow(HINSTANCE hInst, int nCmdShow);
