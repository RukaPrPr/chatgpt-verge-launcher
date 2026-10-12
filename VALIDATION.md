# 1.0.6（codex-proxy-6）公开验证摘要

验证日期：2026-10-12。六个 EXE 文件版本均为 `1.0.6.0`。发布程序沿用已完成回归的 proxy6 二进制；发布整理仅调整公开文档和验证资料，不修改运行时代码。

## 本次检查

| 组别 | 通过数 | 公开证据 |
| --- | ---: | --- |
| 控制入口、程序包清单、hosts 事务、日志故障及进程退出诊断 | 73 | [core-checks.txt](validation/core-checks.txt) |
| 字节计数、异常保留、状态快照与诊断字段 | 13 | [relay-diagnostic-checks.txt](validation/relay-diagnostic-checks.txt) 前 13 项 |
| 独立回环代理与转发场景 | 11 | 同上后 11 项；[relay-diagnostics-results.json](validation/relay-diagnostics-results.json) |
| 合计 | 97 | [summary.json](validation/summary.json) |

基础套件未启用 `-InstalledPackage`，未重做四项已安装应用的激活/身份检查。历史 proxy5 的 154 项检查属于上一修订的验证，不计入本次 97 项；历史变化见 [CHANGELOG.md](CHANGELOG.md)。

网络测试仅创建自有短期 relay、临时回环服务器和假 CONNECT 代理，不使用生产 443/7896，不查询公网 DNS，不连接实际远端。测试中的 203.0.113.1 仅出现在 CONNECT 目标文本中，假代理不向该地址拨号。

- 双向 EOF 与 TCP reset：最先观察到的流向正确；ConnectionReset / 10054 与随后清理产生的 ObjectDisposedException 可区分。
- 大数据转发：1,048,593 字节上传、1,048,654 字节下载逐字节一致，成功写入计数匹配。
- CONNECT 407、提前 EOF、连接拒绝：保留数字状态、阶段和 Socket 错误码；不记录响应头或异常消息。
- 原有 5 秒 CONNECT 截止：测得失败记录出现在 5163 毫秒，没有延长连接截止时间。
- 已建立连接空闲超过 21 秒后仍可双向传输；显式停止时保存活动隧道快照。这只验证未新增空闲关闭，不代表真实应用永远不会出现 20 秒等待。
- 活动隧道的主日志路径不可写时，转发继续，最终摘要写入备用日志；启动器诊断日志失败不改变原有转发退出处理。
- 测试载荷和异常消息中的秘密标记未出现在诊断字段中。

## 运行行为与构建

相对于 proxy5，新增代码只进行诊断观测。转发仍使用原有双向 `CopyToAsync`、`WhenAny`、关闭两端、`WhenAll` 的顺序；目的流的计数包装只在成功写入后计数，不增加缓冲、重试、刷新或截止时间。DNS/DoH、TLS、代理选择、Node 预加载模块、规则范围、浏览器限制和恢复逻辑保持。

`build.ps1` 构建六个 EXE 成功。旧系统 MSBuild 同样构建成功，0 错误；本机缺少 .NET Framework 4.8 Targeting Pack 并使用 GAC 引用，产生 MSB3644、MSB3270 两条已知环境警告。正式资产来自脚本构建，没有为此安装或修改开发环境。

源码提供相同检查入口：

```powershell
pwsh -NoProfile -File .\build.ps1
pwsh -NoProfile -File .\tests\run.ps1 -BuildDirectory .\build -TestDirectory .\test-output
pwsh -NoProfile -File .\tests\run-relay-diagnostics.ps1 -BuildDirectory .\build -TestDirectory .\relay-diagnostic-output
```

诊断套件需要本机可用的 Node.js。测试目录应使用独立的空目录；断连诊断脚本要求目标目录尚不存在。

## 原会话保留与边界

构建和测试前后只读对照确认：11 项原有程序/配置文件的哈希、64 条专用防火墙规则记录、9 个原进程的 PID 和创建时间、3 条 durable TCP 记录均相同；自有测试进程残留为 0。测试没有重启 Codex、CUA、Chrome、沙箱服务或 Clash，没有修改商店包、全局代理或 Clash 配置。

公开资料仅保留检查名称、统计和非敏感的测试指标。本机用户名、工作目录、生产进程信息、配置快照及原始运行日志保留在本地，不包含在 Git 仓库和 Release 资产中。

发布不会自动切换当前应用。新日志需在下次正常退出 Codex 后从新目录启动才启用；本修订尚未进行正式会话的完整重启验收。没有新增真实 CUA 界面、语音、硬件断电或系统实际防火墙丢包测试。

转发器不读取 TLS/WebSocket 内容，不能单凭 TCP reset 判定本地代理、远端代理、服务器或中间网络的责任。字节计数代表已完成的写入，不是远端确认数。强杀、断电或主/备用路径均不可写时仍可能缺少最后记录；本次没有宣称消除 1006、attestation 或浏览器生命周期告警。
