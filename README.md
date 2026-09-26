# ChatGPT Verge Launcher 1.0.4

一个仅用于当前 Windows 用户的一次性启动器。它自动读取 Clash Verge Rev 当前的 `mixed-port`，让微软商店版 ChatGPT 使用对应的本地 HTTP mixed 代理，例如：

```text
http://127.0.0.1:7896
```

启动器不会修改 Windows 系统代理，不会启用 TUN，也不会读取 Verge 的订阅、节点或密钥。启动器启动应用后退出；控制代理组件仅随 Codex 的控制运行时存活。

程序内嵌原创的彩虹编织结应用图标，源图及多分辨率 ICO 位于 `assets` 目录。

## 使用方法

1. 启动 Clash Verge Rev。可以关闭 Verge 的“系统代理”。
2. 如果 ChatGPT 已经运行，请先在系统托盘中完全退出 ChatGPT。
3. 解压并保留完整发布目录，包括 `ChatGPT-Verge-Launcher.exe` 和 `ChatGPT-Verge-ControlProxy.exe`，双击前者。
4. 启动器自动读取并验证 `mixed-port`，从商店包清单读取 ChatGPT 的 AppID，以程序包身份启动桥接进程；桥接进程再设置 `--proxy-server`、进程级代理和 `CODEX_NODE_REPL_PATH` 后启动 ChatGPT。
5. Codex 启动控制组件时，辅助程序为原版 `node_repl.exe` 补齐代理，再转发控制协议。首次启动会在程序目录生成只含本地端口的 `control-proxy.port`，此目录需可写。

建议将本启动器固定到任务栏，并关闭 ChatGPT 自身的开机启动，避免 ChatGPT 在没有代理参数的情况下提前运行。

## 1.0.4 更新兼容性

新版 ChatGPT 会使用要求 Windows 程序包身份的接口。1.0.3 直接运行安装目录下的 `ChatGPT.exe`，可能出现 **ChatGPT failed to start / 该进程没有程序包标识符**。本版改为通过 Windows 自带 Appx 模块的 `Invoke-CommandInDesktopPackage` 启动短暂的桥接进程，再由它启动安装包内的 ChatGPT。

程序包激活不会继承外部启动器的进程环境，因此代理变量在桥接进程内设置。启动器会确认桥接与 ChatGPT 的程序包身份，通过当前用户专用的本地命名管道接收启动结果，并报告激活失败或应用立即退出。它不会在失败后回退到缺少程序包身份的直接启动方式。

升级时保留完整目录中的两个 EXE；也可用新发布目录替换旧目录内的全部发布文件，原有快捷方式即可继续使用。更新文件后，仍需从托盘完整退出 ChatGPT，再运行启动器。

## 安全行为

- 只读取 Clash Verge Rev 配置中的顶层 `mixed-port`，不会解析或输出订阅节点。
- 只连接本机回环地址 `127.0.0.1` 上检测到的端口。
- 当前配置读取失败时回退检查 `7896`，但仍以实际端口连通性为准。
- 动态寻找当前安装的 `OpenAI.Codex` / ChatGPT 商店包，升级后不依赖旧版本路径。
- 如果发现普通方式启动的 ChatGPT 已在运行，只显示提示，不会强制结束进程。
- 已运行的应用必须完整退出后才能重新应用启动配置；不会仅凭现有 `--proxy-server` 参数误判控制代理已经生效。
- 不要求管理员权限。
- 不启用程序包调试模式，不修改程序包清单、用户环境变量或应用数据。

## 已知限制

- Codex 会重新生成受管理的 MCP 配置，直接编辑其中的 `env_vars` 可能在启动时被覆盖。本版使用当前应用已提供的 `CODEX_NODE_REPL_PATH` 覆盖入口，不修改 Codex 安装文件或超时设置。
- 控制运行时从 Codex 提供的 `NODE_REPL_NODE_PATH` 同目录寻找原版 `node_repl.exe`，不固定安装版本。若未来 Codex 改变覆盖入口或运行时目录结构，辅助程序会明确报错，需要适配。
- 代理环境变量只影响本次启动的进程树，不写入系统或用户环境变量。辅助程序不修改控制组件权限或授权流程。
- 程序包桥接依赖 Windows PowerShell 5.1 内置的 Appx 模块，仅支持清单声明为 `Windows.FullTrustApplication` 的 ChatGPT 桌面应用，不适用于任意 UWP 应用。Windows 对此命令只保证程序包身份与虚拟资源上下文；后续系统或应用更新仍可能需要适配。参见 [Microsoft 命令说明](https://learn.microsoft.com/en-us/powershell/module/appx/invoke-commandindesktoppackage)。

- 自动识别适用于 Clash Verge Rev 的标准 Windows 配置目录；非常规便携版目录可能需要后续增加支持。
- HTTP 代理覆盖 ChatGPT 的 TCP/HTTPS/WebSocket 流量。实时语音等 UDP 功能不保证经过该端口。
- EXE 未进行商业代码签名，首次运行时 Windows 可能显示 SmartScreen 提示。

## 构建

使用 Windows 自带的 .NET Framework 64 位 C# 编译器，无第三方依赖：

```powershell
pwsh -NoProfile -File .\build.ps1
```

入口位于 `src\Program.cs`；程序包发现和桥接位于 `src\PackagedApp.cs`、`src\PackageLaunch.cs`。

构建同时生成启动器、诊断程序和控制代理组件，无需另装 .NET 10。可运行 `ChatGPT-Verge-Launcher-Diagnostics.exe --diagnose` 检查程序包全名、AppID、安装路径、代理端口与辅助程序是否齐全；诊断不会启动 ChatGPT，静态诊断不代表实际网页验收通过。

运行隔离集成测试：

```powershell
pwsh -NoProfile -File .\tests\run.ps1 -BuildDirectory .\build -TestDirectory .\test-output
```

测试覆盖代理缺失后的恢复、端口更新、带空格/引号/尾反斜杠的参数、UTF-8 协议流、标准错误隔离、子进程退出码、无效端口、缺失运行时、清单 AppID 解析和程序包身份检查。

在已安装 ChatGPT 的 Windows 当前用户会话下增加 `-InstalledPackage`，可验证真实 Windows 程序包激活、桥接代理继承和失败回传：

```powershell
pwsh -NoProfile -File .\tests\run.ps1 -BuildDirectory .\build -TestDirectory .\test-output -InstalledPackage
```

该测试使用独立探针运行环境变量检查；对真实 ChatGPT 仅创建新的挂起进程、读取程序包身份后销毁，不执行应用代码，不关闭现有会话。实际界面与联网验收仍需完整退出 ChatGPT，用新版启动器启动后检查页面、工具及浏览器联网。
