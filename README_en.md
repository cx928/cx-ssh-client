# cx-ssh-client

An SSH client tool developed by Chengxing Studio, supporting SSH remote connection and SFTP file management, with **WinUI / .NET CLI / native C++** implementations.

[简体中文](README_cns.md) | [繁體中文](README.md) | [English](README_en.md)

⚠️ Preview release, intended mainly for feature validation and testing.

## 📦 Version

- Current version: **v1.0.0**
- Website: <https://www.cxbk.cc>
- Downloads: <https://github.com/cx928/cx-ssh-client/releases>

## 🧩 Implementations

| Implementation | Folder | Stack | Notes |
|---|---|---|---|
| **WinUI GUI** (recommended) | `winui/` | WinUI 3 + .NET 8 | SSH/SFTP/FTP/RDP/VNC, encrypted vault, account sync |
| **.NET CLI** | `dotnet/` | .NET 8 | Manage sessions, run commands, transfer files in a terminal |
| **Native C++** | `cpp/` | Win32 API (MinGW) | Zero third-party dependencies, protocol probing |
| **Python (legacy v0.0.3)** | `legacy/` | Python 3 + paramiko | Early preview, kept for reference |

## 🚀 Quick start

```powershell
# GUI
dotnet build winui/CxSshClient.csproj -c Release -p:Platform=x64 -r win-x64

# CLI
dotnet build dotnet/CxSshClient.Cli.csproj -c Release -r win-x64
cx list
cx exec myserver "uname -a"
```

## ☁️ Optional sync server

`server/` contains a zero-dependency (Python standard library) sync server for Linux. The server stores **ciphertext only**; sessions and passwords are encrypted client-side with AES-256-GCM (PBKDF2-derived key). See `server/README-server.md`.

## 📄 License

[Mozilla Public License 2.0](LICENSE)
