# 上游审计与技术取舍

审计日期：2026-10-29（Asia/Singapore）。这是实施前的真实源码审计与基线可行性检查；没有运行上游演示模式，也没有将模拟数据作为性能验收结果。

## 已核实的上游

- 仓库：[aixisstudio/Snitch](https://github.com/aixisstudio/Snitch)。浅克隆 `main`，实际 HEAD 为 `062f2a8c3ba95ac811988710579874100d667956`，与任务给出的参考一致。
- 只读审计副本位于本机临时目录。未修改上游受 Git 跟踪的文件，未安装其依赖、驱动或发布包；公开记录省略本机用户路径。
- [v1.6.4](https://github.com/aixisstudio/Snitch/releases/tag/v1.6.4)，GitHub Releases API 返回发布时间 `2026-10-23T10:58:15Z`；发布标签与审计 main 是不同证据，不能默认发布二进制恰好对应审计 HEAD。
- [`frontend/package.json`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/frontend/package.json)、[`electron/package.json`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/electron/package.json) 和 [`THIRD_PARTY_NOTICES.md`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/THIRD_PARTY_NOTICES.md) 指定 AGPL-3.0-or-later；根 `LICENSE` 是完整 AGPL v3 正文，已原样保留。

## 核心证据与改造决定

| 项目 | 当前上游证据 | NetPeek 决定 |
| --- | --- | --- |
| 进程归属 | [`sniffer.py:160–205`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/backend/capture/sniffer.py#L160-L205) 将 TCP/UDP 分开，但键只有本地端口；同协议同端口的不同地址/地址族记录可互相覆盖。[`399–408`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/backend/capture/sniffer.py#L399-L408) 用此表给包归属。 | 直接读取 Windows owner-PID TCP/UDP 表，保留地址族、完整端点和 PID；进程实例额外包含启动时间，路径可信时按路径分组，未知时保守隔离。 |
| “应用”汇总 | [`api/main.py:1086–1111`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/backend/api/main.py#L1086-L1111) 累积按进程名的字节、包数及历史；[`AppsView.jsx:30–45`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/frontend/src/components/AppsView.jsx#L30-L45) 再按名称跨节点汇总并按累计字节排序。 | 不把累计传输历史解释为当前连接；不把同名不同路径程序合并；不从整机流量分摊“应用速度”。 |
| 失败与清理 | [`sniffer.py:506–551`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/backend/capture/sniffer.py#L506-L551) 先启动连接轮询线程，后打开 pcap。`finally` 只关闭 pcap，轮询线程仅在显式 `stop()` 停止；[`main.py:1880–1906`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/backend/api/main.py#L1880-L1906) 的失败回调主要广播状态。 | 单一采集循环，异常内收敛，重试不创建并发循环；退出取消并回收；失败期间旧快照显式标为过期，恢复后更新。 |
| 详情旧响应 | [`AppsView.jsx:47–58`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/frontend/src/components/AppsView.jsx#L47-L58) 的异步历史请求没有取消、请求代次或选择一致性检查，较旧响应可能覆盖新选择。 | 同进程本地快照详情，不保留 HTTP 详情请求链；UI 以稳定身份选择并更新。 |
| 运行负担 | Electron + React/D3 + Python/FastAPI/WebSocket + pcap；`backend/data/geo` 两个压缩数据库共 **65,460,555 字节**。[`AuroraCurtain.jsx`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/frontend/src/components/AuroraCurtain.jsx#L376) 与力导向图有逐帧调度。 | 仅应用列表与渐进详情，WPF 单进程；删除整条无关功能依赖链，不迁入地图、地理库、图布局、检测、安全评分、进程控制或防火墙。 |
| Lite | [`README.md:94`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/README.md#L94) 明确 Lite 为 backend + browser UI，仍需 Npcap；[`ci.yml`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/.github/workflows/ci.yml#L179-L195) 构建相同后端的 Lite 安装包。 | Lite 无法消除驱动和后端采集门槛，因此替换采集和外壳，而不是只换浏览器承载方式。 |
| 身份、更新和 IPC | 上游 `appId=com.snitch.app`，`publish` 指向 `aixisstudio/Snitch`；[`main.js:157–239`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/electron/main.js#L157-L239) 有 updater；本地后端有 token 鉴权。 | 使用 NetPeek 独立名称/标识/数据目录；不带上游更新源。没有后端服务、监听端口和跨进程控制接口，因此没有需要移植的本地 HTTP token 链。 |

以上缺陷限于当前检查的具体路径；不声称上游所有功能或所有运行场景都错误。

## 实际执行的最小风险复核

在系统 Python 3.12 下通过 `python -B` 导入上游模块；不写字节码，不启动应用服务。

1. `capture.pcap.is_available()` 返回 **False**。`C:\Windows\System32\Npcap\wpcap.dll` 不存在，未发现 `npcap`/`npf` 服务。上游 [`electron/main.js:751–758`](https://github.com/aixisstudio/Snitch/blob/062f2a8c3ba95ac811988710579874100d667956/electron/main.js#L751-L758) 在此条件下显示驱动提示并直接返回，不能到达正常主界面。
2. **端口碰撞的单元复核**：替换 `psutil.net_connections`，提供同一 TCP 本地端口 `54321`、地址 `127.0.0.1` 与 `192.0.2.10`、PID `111/222` 两条记录；`ConnectionTable.lookup(54321, 'TCP')` 仅返回最后的 `(222, 'same.exe')`。这是受控测试数据，不是实际网络活动。
3. **失败清理的单元复核**：替换表刷新为空操作，令 `PcapSession` 构造抛出 `PcapError('injected open failure')`。直接运行 `_run()` 返回后 `_conn_table._running=True`，表线程仍存活。审计最后显式 `stop()` 并 `join()`，确认线程已退出。该测试证实清理缺口，未真实打开网络设备。
4. UI 旧响应问题通过源代码检查确认缺少保护；没有运行 React 页面复现竞态，不能将它称为浏览器实测。
5. 上游工作树最终 `git status --short` 为空。

## 实施前同机基线及限制

主机：Windows 11 企业版 64 位，版本 `10.0.22631`；AMD Ryzen 7 9700X，16 逻辑处理器；可见物理内存 31.61 GiB。.NET Framework 注册值 `Release=533320`，`Version=4.8.09032`。以上由 `Win32_OperatingSystem`、`Win32_Processor`、.NET 注册表读取，不由机器型号推算。该主机的已有 Windows/.NET 是共享系统运行时，不能将其成本说成零。

| 指标 | 上游 Snitch v1.6.4 基线 | 证据/限制 |
| --- | --- | --- |
| 完整版 Windows 下载包 | **198,471,268 B（189.28 MiB）** | GitHub Releases API 的 `Snitch-Setup-windows.exe` 资产元数据，非本地下载后测量。 |
| Lite Windows 下载包 | **78,216,423 B（74.59 MiB）** | 同 API 的 `Snitch-Lite-Setup-windows.exe`；并未含用户另装的 Npcap 或浏览器。 |
| Windows 后端压缩包 | **80,314,501 B（76.59 MiB）** | `snitch-backend-windows-latest.tar.gz`，不是完整桌面运行包。 |
| 安装后磁盘占用（含必要额外运行时） | **未测** | 未下载/安装上游包、Npcap；不可与 NetPeek 解压后目录直接作百分比比较。 |
| 启动至首个真实连接列表 | **未测 / 当前环境不可达** | Npcap 不可用；未以错误提示窗口或 demo 模式代替真实启动。 |
| 完整进程树内存 | **未测** | 正常采集进程树没有启动，不能用单个 Python/Electron 空进程冒充。 |
| 前台 CPU | **未测** | 同上。 |
| 后台/托盘 CPU | **未测** | 同上。 |

额外源码运行条件也不齐：`frontend/dist`、前后端 JavaScript `node_modules` 不存在；系统 Python 缺少 `maxminddb`、`aiofiles`。没有为获取一个不具备真实采集功能的基线临时安装依赖或驱动。此次只有发布资产大小可获得；**不能宣称已完成同机性能优化前后验收，也不能报告启动/CPU/内存提升百分比**。新版本的实际数据应由它自己的测试报告记录，不能填补上游的缺测值。

若未来在用户已准备好 Npcap 的环境补测：同一台机器、同权限、同连接负载，记录两者启动至首个真实快照的时间；先预热，分别测至少 30 秒前台和托盘状态。用父 PID 关系闭包枚举全部后代，求和 WorkingSet 和 PrivateMemory，采样累计 CPU 时间差，CPU 整机百分比 = ΔCPU 秒 / 墙钟秒 / 16 × 100（同时注明共享页重复计数）。Lite 必须把其浏览器运行成本纳入，或使用独立浏览器实例。分别报告压缩包、解压/安装占用、额外运行时占用；不能混用这三种大小。若权限要求不同，应作为限制单列。

## Windows 原生方案与覆盖边界

Microsoft 的 [`GetExtendedTcpTable`](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable) 支持 IPv4/IPv6 的 owner-PID 表；[`GetExtendedUdpTable`](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable) 支持两种地址族的本地 UDP 端点。选择系统 IP Helper + .NET Framework WPF 可去掉 packet capture 驱动、Python 服务、浏览器外壳及其 IPC，满足当前范围；不是对流量分析能力的等价替换。

- TCP 端点状态是采样时的系统事实；TCP Established 不证明此刻有传输。监听与 UDP 本地端点必须单独标识。
- UDP 表不提供完整远端；不猜测 QUIC/DNS 远端，不制造每应用流量、速率或“最近有传输”。
- 轮询会遗漏完全发生在采样间隙内的短连接。记录离开表的端点只能称“最近出现/历史”，不能称“仍在访问”。
- 域名不是必填信息；不做后台反向 DNS 查询。IP/端口可信；不将 PTR 名称当访问网站。
- 代理/VPN 时展示操作系统实际端点，无法自动还原隧道或代理背后的最终网站；回环不是公网访问。
- 按路径和进程启动时间改善身份可靠性；保护进程、权限受限、进程退出或竞态无法核实时应显示未知，不依靠名称猜测。
- 网卡变更、断网、休眠恢复和受限权限都需要单独的真实环境验收；本审计没有执行这些系统状态改变。

## 许可证与复用

保留 Snitch 的应用优先、目的地渐进展开、本地观察和错误显式反馈等设计贡献；新代码替换其名义聚合、包捕获、前后端协议和外壳。上游代码中的数据库、检测器、logo、地理数据与 UI 动画不复制。NetPeek 采用 AGPL-3.0-or-later，保留完整许可证、Snitch 及其 PCYBOX Orbis 来源署名和修改声明；二进制交付需附对应源码与构建说明。具体条款以仓库 `LICENSE` 为准，分发说明见 `THIRD_PARTY_NOTICES.md`。
