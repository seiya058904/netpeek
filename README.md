# NetPeek

**Your connections, at a glance.**

一个简体中文 Windows 桌面连接查看器。打开即看应用，展开查看进程、可获取的完整路径、PID、TCP/UDP、IPv4/IPv6、本地与远端 IP/端口及状态。界面只呈现可核验的连接事实。

![NetPeek 0.1.1 最终 EXE 的真实采样与 WPF 控件离屏渲染](docs/screenshots/0.1.1/overview.png)

## 使用

解压 `NetPeek_0.1.1_windows_x64.zip`，双击文件夹中的 **NetPeek.exe**。无需安装、无需 Npcap，普通用户权限即可读取连接表。需要 **Windows 10/11 x64 与 .NET Framework 4.8 或更高版本**；较旧的 Windows 10 如未安装该运行时，须由用户自行安装。运行时不随包重复打包，也不会自动下载。

- 点击应用行，展开路径与进程；再展开进程，查看端点明细。多个相同路径的进程属于同一个应用；同名不同路径分别展示。图标来自本机可读取的可执行文件，否则显示名称首字母。
- 搜索支持名称、路径、PID、IP、端口和协议。筛选支持全部、有已建立连接、本地/其他端点、仅历史出现。
- 搜索框右侧可一键清空；无匹配时可直接恢复全部应用。窄窗优先保留远端地址、状态和复制；较小工作区将侧栏操作收进页头，说明与警告可独立滚动。
- “复制”复制连接远端 `IP:端口`；监听与 UDP 复制本地端点。IPv6 使用 `[地址%scope]:端口` 形式。“复制路径”复制完整程序路径。表格可横向滚动，路径可以选中文字或横向滚动。
- “暂停刷新”冻结画面，后台继续尝试采集并保留有界近期记录。恢复后显示最新完整快照。托盘状态继续同样的采集；不绘制隐藏窗口。
- “收起到托盘”隐藏窗口，双击托盘图标或再次打开 NetPeek 恢复。同一 Windows 用户会话仅运行一个界面实例。**窗口关闭按钮、Alt+F4 和“退出 NetPeek”均完全退出。**
- “这些数据意味着什么”解释观察范围和快捷键。采集失败保留上次完整画面并注明时间，自动重试；“立即重试”不会叠加工作线程。

| 快捷键 | 操作 |
| --- | --- |
| Ctrl+F | 聚焦搜索 |
| Ctrl+P | 暂停 / 继续界面刷新 |
| F5 | 请求立即采集，暂停画面仍保持暂停 |
| Esc | 清空搜索、收起说明 |
| Tab / Shift+Tab | 遍历控件 |
| Space / Enter | 展开或收起聚焦的应用/进程 |
| Ctrl+C | 复制选中的端点，文本框内保持常规复制 |

## 准确理解数据

**TCP 已建立不等于正在传输。** TCP 监听表示程序正在等待连接，不证明访问互联网。UDP 系统表只给出本地绑定端点，不提供可靠的远端，所以明确显示未知。NetPeek 不提供未经可靠测量的网速、累计流量、“最近有传输”、域名或网站归属，也不会平均分摊整机流量。

每轮采样完成后等待 1.5 秒再采集；耗时较长时实际间隔会略长。两轮之间发生并结束的短连接可能遗漏。成功观察过但之后未再出现的端点保留 **60 秒、最多 12,000 条历史**，仅保存在内存中；当前端点不受历史限额裁剪。历史以最后观察时间为准，并不等于精确关闭时间。无法核验进程启动时间的端点仅显示当前快照，不推测其历史归属。

当前端点的“最后出现”显示“本次快照”，精确采样时间见窗口右下角；转为历史后显示最后观察时间。无变化快照保留原有行对象，不反复切换“当前/历史”。身份无法核验的 PID 展示行可以保留展开与选中状态，这只延续界面交互，不表示已确认是同一个进程，也不会延续可信历史。

系统原生表自带 PID，NetPeek 在读表前后核验进程启动时间，再按规范化可执行路径分组。无法确定归属时显示“未知进程”，不会把相同名称当作同一应用。受保护进程的路径和图标可能不可读；无默认提权提示。操作系统并不提供四张表与全部进程的原子快照，这仍是一项采样观察。

地址范围表示地址本身的类别，不等于路由、安全性或真实目的地。回环、私有、链路本地、共享地址及基准测试地址分别标注。**代理/VPN/Fake-IP 可能只显示本机代理、隧道或映射端点**；例如 `198.18.0.0/15` 可能被本机代理用于映射。仅凭连接表不能还原真实网站或穿透隧道。

断网时监听端点和 UDP 本地绑定仍可能存在，TCP 状态由 Windows 决定。休眠后请求重新采样，超过 6 秒未更新的可见画面明确标为过期。网卡切换、完整断网/休眠与 Windows 10 跨机验收的未测部分见 [v0.1.1 验收报告](docs/VALIDATION_0.1.1.md)。

## 本地、轻量、独立

运行期只有一个 NetPeek 进程；WPF、系统 IP Helper API 与 Windows Forms 托盘均来自 Windows/.NET。没有本地 HTTP 服务、开放的网络监听端口、抓包驱动、浏览器外壳、AI、遥测或自动更新。应用不会主动发起网络请求；网络位置的应用不尝试读取图标。单实例激活使用按用户命名的本机会话事件，不传递任意命令。

正常使用不写入连接日志；只有未处理异常会覆盖 `%LOCALAPPDATA%\NetPeek\last-error.txt`。诊断命令仅在显式提供输出路径时写文件，可能含本机应用路径和网络端点，分享前请自行检查。

## 构建与测试

在 Windows PowerShell 5.1 或 PowerShell 7 中，从源码根目录运行：

```powershell
.\build.ps1
.\tests\run-core-tests.ps1
.\tests\run-viewmodel-tests.ps1
.\tests\run-icon-cache-tests.ps1
.\tests\run-layout-tests.ps1
.\tests\run-controller-state-tests.ps1
.\tests\IntegrationTests.ps1
```

无需 SDK、NuGet、npm、Python 或额外第三方库；使用已安装 .NET Framework 的 `csc.exe`。`build.ps1` 会检查 4.8 运行时，输出 `dist\NetPeek\NetPeek.exe`。若本机 PowerShell 策略阻止运行本地脚本，可只对本次进程使用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`，不修改系统策略。

核心默认是离线测试。下列**显式选项**会创建受控网络套接字，个别系统可能弹出防火墙提示；NetPeek 正常运行不需要放行入站网络：

```powershell
.\tests\run-core-tests.ps1 -LiveSockets
.\tests\run-core-tests.ps1 -LiveSockets -InterfaceSockets
.\tests\IntegrationTests.ps1 -LiveSockets -ExternalHost github.com
.\scripts\Measure-NetPeek.ps1
.\build.ps1 -Package
```

性能脚本预热一次后测量三次暖启动，每次前台/最小化各采样15秒；由应用的显式测试模式完成最小化和退出。本轮保留 0.1.0 与候选版的总览、展开详情、后台对比；最终列宽补丁后的完整复测尚未完成。各二进制的实测数据、方法、宿主开销和未验证项见 [v0.1.1 验收报告](docs/VALIDATION_0.1.1.md)。[v0.1.0 验收记录](docs/VALIDATION.md) 与 [上游审计](docs/UPSTREAM_AUDIT.md) 保留供追溯。原始本机证据放在被 Git 忽略的 `artifacts/`；可分享的脱敏汇总随源码提供。

诊断接口：

```powershell
.\dist\NetPeek\NetPeek.exe --snapshot C:\Temp\netpeek_snapshot.json
.\dist\NetPeek\NetPeek.exe --smoke-test C:\Temp\netpeek_smoke.json
```

`--snapshot` 读取一次真实系统表后退出，完整快照退出码0、不完整快照2、启动失败1。`--smoke-test` 启动真实窗口、采样并在5秒后退出：成功必须应用过完整快照（允许零端点），UI 异常退出1，始终无有效快照退出2，已有实例占用测试退出3。报告包含本轮 run ID、PID、时间和最终完成状态；出现报告文件不等于成功。集成测试还会显式注入“报告后 UI 异常”“持续采集失败”和“零端点快照”，确认结果并检查收尾。故障注入仅由 `--smoke-test --test-fault` 显式开启，正常运行不会触发。

`--benchmark <报告路径> [5到120的秒数]` 是性能测试接口：不提供秒数时窗口保持运行；提供后按采样阶段自动最小化和退出。版本变更见 [变更记录](docs/CHANGELOG.md)。

## 源码与许可证

GitHub 仓库：[seiya058904/netpeek](https://github.com/seiya058904/netpeek)。使用独立的 NetPeek 名称、程序集、会话标识和数据目录，不会查询 Snitch 更新渠道。

源自对 [aiXis Studio / Snitch](https://github.com/aixisstudio/Snitch) 的产品思路与源码审计，Windows 采集器和界面已独立重写。许可证 **AGPL-3.0-or-later**，保留上游及 PCYBOX Orbis 的署名，详见 [第三方说明](THIRD_PARTY_NOTICES.md) 与 [LICENSE](LICENSE)。

便携包附 `NetPeek_source.zip`，包含完整对应源码、构建脚本、测试、许可证、说明和截图。对外分发时请一并保留。当前版本未做代码签名；这是本地验证构建，不代表已完成 Windows 10、多显示器或所有 VPN 产品的兼容性认证。
