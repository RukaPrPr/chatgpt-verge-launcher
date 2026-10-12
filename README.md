# ChatGPT Verge Launcher 1.0.6

修订标识：`codex-proxy-6`；文件版本：`1.0.6.0`。

用于 Windows 上 OpenAI.Codex 商店程序包的专用启动器，通过 Clash Verge Rev 的本地 mixed-port 为桌面应用、内置浏览器和专用控制组件配置代理。1.0.6 汇总 durable 转发、事务恢复、专用防直连规则、内置浏览器限制与 Node 标准流修复；本次发布采用最终的 proxy6 修订。

本修订仅补充 **durable 转发的断连诊断日志**：记录两侧连接端点、CONNECT 状态、连接持续时间、双向字节数、最先观察到的结束方向、EOF/异常及 Windows Socket 错误码；启动器主动结束转发进程和观察到转发进程退出时，也记录原因与 PID。

保留 proxy5 的 Node 标准流修复及全部既有网络行为，不调整代理选择、DNS/TLS、CONNECT 超时、重试、关闭顺序、规则范围或浏览器限制。构建与测试不切换当前 Codex 会话，新包单独保存，**下次正常退出 Codex 后从新目录启动才生效**。

## 使用

1. 从 [GitHub Release](https://github.com/RukaPrPr/chatgpt-verge-launcher/releases/tag/v1.0.6) 下载程序 ZIP 并完整解压，保留六个 EXE 与 `proxy-preload.cjs`。运行 Clash Verge Rev，确认本地 mixed-port 可用，例如 `7896`。
2. 准备切换时，从托盘正常退出旧 Codex。无需关闭或修改外部 Chrome。不要把新文件覆盖到仍在使用的旧目录。
3. 直接双击 `ChatGPT-Verge-Launcher.exe`，同意一次 UAC。不要手动选择“以管理员身份运行”。提权助手负责 Codex 专用规则与 hosts；应用、CLI、Node、转发程序保持普通权限。
4. 启动器先检查恢复状态，再发现当前程序包、验证代理和运行时、安装专用规则、验证后台 TLS，最后启动 Codex。规则安装或验证失败时不启动新应用。
5. 后续浏览器控制只提供 `iab` 和 `mcpapps`。Chrome、Edge、远程 CDP 后端被运行时拒绝；外部浏览器及扩展安装、注册信息不变。已存在的旧会话不会被热切换。

启动器自行引入的提权流程只有一次。Codex 自身的安装、更新或首次沙箱设置属于应用自己的权限流程，不能用启动器的承诺替代。现有 Codex 沙箱服务不被停止、重启或修改启动方式。

发布文件：

- `ChatGPT-Verge-Launcher.exe`：主入口。
- `ChatGPT-Verge-Launcher-Diagnostics.exe`：只读诊断及原有恢复入口。
- `ChatGPT-Verge-ControlProxy.exe`：恢复 node_repl 的代理环境。
- `ChatGPT-Verge-NodeProxy.exe`：恢复过滤后的环境，加载专用代理预加载模块。
- `ChatGPT-Verge-CodexProxy.exe`：在桌面程序覆盖环境之后，恢复 MCP 的 Node 入口。
- `ChatGPT-DurableRelay.exe`：保留原有 durable WebSocket 转发。
- `proxy-preload.cjs`：只由上述专用 Node 入口加载，不安装到系统 Node。

`proxy-runtime.json` 和 `control-proxy.port` 在首次正常启动时生成。Node 和 CLI 路径取自当前选择的商店程序包，不猜测旧缓存，也不使用系统共享 Node。请勿复制其他机器或旧版本的运行时清单。

## 网络处理

| 组件 | 处理 |
| --- | --- |
| Codex 界面、内置浏览器 | Chromium 代理参数指向本地 mixed-port；禁用 QUIC，限制 WebRTC 非代理 UDP，外部 DNS 解析失败并保留本地地址 |
| durable WebSocket | 原 URL、SNI、TLS 和认证保留，经本地 443 转发，再经 mixed-port 的 CONNECT |
| 控制组件与 Code Review 等使用 MCP Node 入口的插件 | 恢复 `NODE_USE_ENV_PROXY=1`，外部 HTTP/HTTPS 与 bundled ws 强制 CONNECT；覆盖自定义 Agent/agent=false 的直连路径 |
| Node fetch、原生 WebSocket | 在实际 Node 启动前恢复环境代理；本机测试代理已验证 |
| Node 裸 TCP、UDP、显式 DNS 查询 | 在预加载模块覆盖的标准接口中拒绝外部访问；不做直接回退 |
| 已枚举的 Codex 专用 EXE | Windows 防火墙对该具体路径设置入站、出站非回环阻止规则，覆盖 TCP、UDP、IPv4、IPv6；回环 IPC/代理保留 |

HTTPS 仍校验证书和主机名。不安装根证书，不设置 `NODE_TLS_REJECT_UNAUTHORIZED=0`。原转发器通过代理进行 DoH 查询和隧道连接，代理不可用时失败。

规则仅涵盖当前 Codex 程序包 `app` 目录、`%LOCALAPPDATA%\OpenAI\Codex\bin` / `runtimes`、默认 `%USERPROFILE%\.codex\plugins\cache` 中枚举到的具体 EXE；排除缓存及程序包内的外部 Chrome 扩展宿主。目录重解析点不被当作扩大范围的通道。不会给 Chrome、系统 Node、Git、Python、PowerShell、Clash 或其他商店应用添加规则，也不会修改它们的配置、进程或文件。

只修改启动器自身文件、进程级环境，以及下次启动时创建的 Codex 专用规则；不改商店包、系统代理、Clash 配置，不启用 TUN、不安装 Proxifier、驱动、服务或计划任务。

## 范围与限制

这不是“任意 Codex 用法均无法绕过”的完整网络隔离方案。

- 完整启动流程只适用于从此启动器启动的会话。直接从开始菜单或其他路径运行 Codex、直接运行内部辅助入口，不等同于已完成网络保护检查。
- 共享 Git、Python、PowerShell、系统 Node、外部自定义 MCP、WSL、Docker、SSH/远程主机中的程序不受这些按专用路径建立的规则约束。继承代理环境只对遵守该环境的客户端有效。按共享 EXE 设置规则会影响其他软件，因此没有采用。
- 本机会话开始后新增或移动到其他路径的可执行文件，不自动获得已安装的规则；安装/更新插件、运行时或商店应用后应正常退出并从启动器重启。自定义 `CODEX_HOME` 外部插件目录不自动纳入默认目录范围。
- 回环用于代理与本机 IPC，并非只允许 7896 一个端口。规则不提供对恶意插件、本机网络代理服务或系统服务代发请求的完整隔离；原生程序通过系统 DNS 服务发出的查询也不能仅靠 EXE 防火墙规则证明全部受控。
- 不支持代理的功能可能失败，不会保证语音、任意 UDP 协议、任意自建网络库都能工作。WebRTC/真实语音需要重启后验收。
- 请求到达 7896 后仍遵循用户现有的 Clash 分流规则；Clash 自身选择 DIRECT 与程序绕过本地代理是不同情况。本修订不修改 Clash 分流。
- 云端工具或远程运行环境的出站连接不属于本机代理范围。

## durable 的原有单域名映射

```text
# BEGIN ChatGPT-Verge-DurableRelay v1
127.0.0.1 codex-cloud-backend.chatgpt.com
# END ChatGPT-Verge-DurableRelay v1
```

此原有机制仍是系统 hosts 中的单域名映射，会影响本机任何访问该域名的程序；它不是按进程的 DNS 映射。127.0.0.1:443 必须可用。只添加和移除启动器自己的原样区块，不覆盖已有同域名条目或其他软件的 hosts 内容。加映射前后验证正常 TLS；异常时保留证据并按事务恢复。

## 退出和恢复

- 普通启动器退出：独立提权助手继续监控。应用退出后撤销 hosts，再结束转发。
- 专用 Node/CLI 入口被强杀：Windows Job Object 结束该入口拥有的运行时后代；不枚举杀死共享程序或外部浏览器。
- 仍有普通 Codex 受保护进程运行：保留其规则。再次启动遇到这些进程时拒绝解除保护，要求先正常退出 Codex。
- Codex 自带的常驻沙箱服务：保留它的两条规则，下一会话验证并复用；不停止服务，不短暂移除它的保护。服务仍运行时不会自动删除这两条规则。
- 提权助手被强杀、断电或系统重启：防火墙规则持续存在。下次运行启动器并同意 UAC 后恢复自己的规则和 hosts 事务；没有后台开机恢复服务。
- 规则名称/路径不匹配、事务损坏或无法确认外部修改：停止并保留记录，不覆盖不明数据。系统防火墙关闭或策略不可用时，拒绝宣称防直连保护已就绪，也不会修改全局防火墙设置。

原有 `--restore-hosts` 恢复入口也会处理自己的旧网络规则，仍保留活跃服务的规则。先退出普通 Codex 会话后使用：

```powershell
.\ChatGPT-Verge-Launcher-Diagnostics.exe --restore-hosts
```

日志位于 `%USERPROFILE%\.chatgpt-verge-launcher\logs\<会话 GUID>`，分 `launcher-<PID>.log`、`hosts-<PID>.log`、`relay-<PID>.log`。记录实际文件句柄路径，避免商店关联环境的 AppData 视图错位。运行中主日志失败会尝试发布目录 `diagnostics` 并输出 stderr；两者均不可写时不保证有磁盘记录。日志故障不终止当前连接。

## 新增断连诊断

同一转发进程内使用 `tunnel=<序号>` 关联 `CONNECT_OK`、`CLOSED`、`FAILED` 和 `SHUTDOWN_SNAPSHOT`。日志自身带有时间、进程和会话信息，可与应用的 durable WebSocket 日志按时间对照。

| 字段 | 含义 |
| --- | --- |
| `client` / `listener` / `upstreamLocal` / `target` | 本地客户端、监听端、连接代理的本地端口与 CONNECT 目标 IP；代理端口沿用 LISTEN 和 CONNECT_OK 记录 |
| `phase` / `connectStatus` | 失败或结束时的阶段，以及已收到的 CONNECT 数字状态；0 表示未取得状态 |
| `connectMs` / `elapsedMs` / `connectedMs` | 建立隧道耗时、从接受连接到取样的总时长、隧道建立后时长；未建立时 connectMs/connectedMs 为 0 |
| `upBytes` / `downBytes` | 本地到代理、代理到本地的已成功完成写入字节数，不含 CONNECT 头；不是远端确认收到的字节数 |
| `firstObserved` / `upAtClose` / `downAtClose` | 关闭两端前最先观察到的方向及当时状态；两方向已完成时记 `both`，不推断先后 |
| `upOutcome` / `downOutcome` | 清理后取样的方向状态：eof、faulted、canceled、pending 或 not_started |
| `*ErrorOperation` / `*ErrorType` / `*HResult` / `*SocketError` / `*NativeError` | 写入或读取/复制异常、异常类型及数字错误码；包括内层 SocketException，例如 ConnectionReset / 10054 |
| `cleanupReason` / `closeObservedMs` | 进入清理的分类，以及首次观察到转发完成时距接受连接的毫秒数；尚未观察为 -1 |

`local_to_proxy` 表示从本地客户端读取并向代理写入的方向，`proxy_to_local` 表示反向。应结合 ErrorOperation 判断出错操作，不能把方向直接等同于责任方。`upAtClose=pending` 而最终 `upOutcome=faulted` 可能是随后关闭两端引起的，不能单凭最终异常判定原始断连原因。并发取样不能精确确定网络事件的物理先后顺序。

`RELAY_KILL_REQUESTED` 记录启动器已有清理路径准备结束转发进程的原因；`RELAY_EXIT_OBSERVED` 记录应用仍运行时观察到的转发 PID、应用 PID 和退出码。显式 stop 事件结束转发时，`SHUTDOWN_SNAPSHOT` 尽力保留活动隧道状态。没有新增停止等待、后台监控、心跳或重试。

日志不记录数据正文、CONNECT 头、异常消息、Cookie 或令牌。TLS 保持端到端加密，因此转发器不能读取 WebSocket close frame，也不能仅凭 TCP 重置判断是 Clash、远端代理、服务器还是中间网络发起。进程强杀、系统断电及主/备用日志路径均不可写时，不能保证最后一条快照落盘。

## 验收与构建

只读诊断不会启动应用、安装规则或弹 UAC：

```powershell
.\ChatGPT-Verge-Launcher-Diagnostics.exe --diagnose
```

它区分当前 `RunningWithExpectedProxy`、新参数 `RunningWithProxy4Arguments` 以及实际 `InstalledCodexGuardRuleCount`，不会把新发布包的配置描述当作当前会话已经生效。

本修订通过 97 项检查：73 项控制入口、程序包清单、hosts 事务和日志回归，13 项诊断单元检查，11 项独立回环网络场景。覆盖双向 EOF/TCP 重置、CONNECT 拒绝/提前关闭/连接拒绝、原有 5 秒 CONNECT 截止、超过 20 秒空闲后继续传输、主动停止快照、运行中日志故障及备用保存。测试未使用当前 443 或 7896 连接。

发布前测试保留原有 proxy5 会话，没有启动新主入口、弹出 UAC、操作正式 hosts/防火墙或控制浏览器。**新日志尚未在正式 Codex 会话中重启验收，不能把隔离测试当作真实断连原因已经查明。**历史 proxy5 的 154 项检查未重复计入本次通过数。公开验证摘要见 [VALIDATION.md](VALIDATION.md)；本机原始日志、进程信息和配置快照不随公开包分发。

使用 Windows .NET Framework 编译器，无 NuGet 依赖：

```powershell
pwsh -NoProfile -File .\build.ps1
pwsh -NoProfile -File .\tests\run.ps1 -BuildDirectory .\build -TestDirectory .\test-output
pwsh -NoProfile -File .\tests\run-relay-diagnostics.ps1 -BuildDirectory .\build -TestDirectory .\relay-diagnostic-output
# 需要已安装程序包的额外检查时，给 run.ps1 添加 -InstalledPackage；本次未启用。
# 另外运行 tests/run-proxy4.ps1 和 tests/run-transports.ps1；参数见脚本。
# 历史 proxy5 的真实 REPL 回归，TestDirectory 必须是尚不存在的隔离目录：
# pwsh -NoProfile -File .\tests\run-proxy5.ps1 -BuildDirectory .\build -TestDirectory .\stdio-test-output -PackageExecutable '<当前商店包的 app\ChatGPT.exe 完整路径>'
```

EXE 未进行商业代码签名。升级请解压到新目录，在正常退出旧 Codex 后启动；旧版本目录可保留以便回退。
