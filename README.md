# ChatGPT Verge Launcher 1.0.2

一个仅用于当前 Windows 用户的一次性启动器。它自动读取 Clash Verge Rev 当前的 `mixed-port`，让微软商店版 ChatGPT 使用对应的本地 HTTP mixed 代理，例如：

```text
http://127.0.0.1:7896
```

启动器不会修改 Windows 系统代理，不会启用 TUN，不会读取 Verge 的订阅、节点或密钥，也不会在后台常驻。

程序内嵌原创的彩虹编织结应用图标，源图及多分辨率 ICO 位于 `assets` 目录。

## 使用方法

1. 启动 Clash Verge Rev。可以关闭 Verge 的“系统代理”。
2. 如果 ChatGPT 已经运行，请先在系统托盘中完全退出 ChatGPT。
3. 双击 `ChatGPT-Verge-Launcher.exe`。
4. 启动器从 Verge 的本地运行配置自动读取 `mixed-port`，验证端口后向 ChatGPT 传入 `--proxy-server`，随后立即退出。

建议将本启动器固定到任务栏，并关闭 ChatGPT 自身的开机启动，避免 ChatGPT 在没有代理参数的情况下提前运行。

## 安全行为

- 只读取 Clash Verge Rev 配置中的顶层 `mixed-port`，不会解析或输出订阅节点。
- 只连接本机回环地址 `127.0.0.1` 上检测到的端口。
- 当前配置读取失败时回退检查 `7896`，但仍以实际端口连通性为准。
- 动态寻找当前安装的 `OpenAI.Codex` / ChatGPT 商店包，升级后不依赖旧版本路径。
- 如果发现普通方式启动的 ChatGPT 已在运行，只显示提示，不会强制结束进程。
- 如果 ChatGPT 已经由本启动器运行，再次双击会尝试激活现有窗口。
- 不要求管理员权限。

## 已知限制

- 自动识别适用于 Clash Verge Rev 的标准 Windows 配置目录；非常规便携版目录可能需要后续增加支持。
- HTTP 代理覆盖 ChatGPT 的 TCP/HTTPS/WebSocket 流量。实时语音等 UDP 功能不保证经过该端口。
- EXE 未进行商业代码签名，首次运行时 Windows 可能显示 SmartScreen 提示。

## 构建

使用 Windows 自带的 .NET Framework 64 位 C# 编译器，无第三方依赖：

```powershell
powershell -NoProfile -File .\build.ps1
```

源代码位于 `src\Program.cs`。
