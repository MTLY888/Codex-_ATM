# Codex 额度（EXE 桌面版）

`CodexQuota` 是一个原生 Windows WPF 桌面程序，用于查看 Codex 周额度，并通过奥特曼能量灯提示任务状态。

## 使用方法

1. 克隆仓库：

```powershell
git clone git@github.com:MTLY888/Codex-_ATM.git
cd Codex-_ATM
```

2. 双击 `CodexQuota.exe` 即可运行。

> 程序不会启动 PowerShell 或浏览器。首次下载时，Windows 可能显示 SmartScreen 提示，请确认文件来自本仓库后选择继续运行。

## 使用要求

- Windows 10 或 Windows 11（64 位）
- 已安装 Codex CLI
- 已在 Codex CLI 中完成登录

## 功能

- 每 60 秒自动同步 Codex 周额度
- 每秒更新额度重置倒计时
- 订阅到期日可手动设置并保存在本机
- 启动后只显示小奥特曼
- 鼠标移入奥特曼后显示完整窗口
- 鼠标离开完整窗口后恢复为奥特曼
- 红灯：Codex 正在运行
- 绿灯：Codex 暂停并等待用户确认或点击 Allow
- 黄灯：任务已经结束，无需用户确认

## 项目文件

- `CodexQuota.exe`：可直接运行的主程序
- `CodexQuota.cs`：完整 C# 源码
- `CodexQuota.ico`：程序图标
- `sqlite3.exe`：状态检测所需的 SQLite 运行文件

## 说明

- 用户设置会保存到本地 `settings.ini`，该文件不会提交到 Git。
- 随项目提供的 SQLite 为 64 位 SQLite 3.45.3；SQLite 属于公有领域软件。
- 本项目不会上传 Codex 登录凭据、额度数据或本地任务记录。
