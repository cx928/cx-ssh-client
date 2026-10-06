// session.h - 会话数据模型、本地加密存储(DPAPI)、CSV 导入导出
// 程星SSH客户端 (cx-ssh-client) 原生 C++ 版 / MPL-2.0
#pragma once

#include "common.h"

// 一条会话记录
struct Session {
    std::wstring name;        // 名称
    std::wstring protocol;    // 协议：SSH / SFTP / FTP / RDP / VNC
    std::wstring host;        // 主机
    int          port = 22;   // 端口
    std::wstring username;    // 用户名
    std::wstring password;    // 密码（仅本地存储，DPAPI 加密）
    std::wstring note;        // 备注
};

// 协议工具
std::wstring NormalizeProtocol(const std::wstring& raw);  // 返回大写协议名，未知返回空串
int          DefaultPortFor(const std::wstring& proto);
const wchar_t* const* ProtocolList();                      // 以 nullptr 结尾的协议名数组

// 存储文件：%LOCALAPPDATA%\cx-ssh-client\sessions.dat
std::wstring StorageFilePath();

// 读取/保存（整块 DPAPI 加密）。首次运行文件不存在时返回 true 且列表为空。
bool LoadSessions(std::vector<Session>& out, std::wstring& err);
bool SaveSessions(const std::vector<Session>& list, std::wstring& err);

// 数据文件损坏时，把它改名备份为 sessions.dat.bad-<时间戳>，返回备份路径
std::wstring BackupCorruptStore();

// 文本序列化（UTF-8，制表符分隔，便于排查；写盘前整体加密）
std::string SessionsToText(const std::vector<Session>& list);
bool        TextToSessions(const std::string& text, std::vector<Session>& out, std::wstring& err);

// CSV：列顺序 name,protocol,host,port,username,password,note，UTF-8 带 BOM，支持引号字段
bool ExportCsv(const std::wstring& path, const std::vector<Session>& list, std::wstring& err);
bool ImportCsv(const std::wstring& path, std::vector<Session>& out, std::wstring& err);
