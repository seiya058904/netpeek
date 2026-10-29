<p align="center">
  <img src="assets/NetPeek.svg" width="88" height="88" alt="NetPeek 双筒镜标志" />
</p>
<h1 align="center">NetPeek</h1>
<p align="center"><strong>Your connections, at a glance.</strong></p>
<p align="center">一眼看清，哪些应用正在建立连接。<br>轻量、原生、本地运行的 Windows 应用连接查看器。</p>

<p align="center">
  <a href="https://github.com/seiya058904/netpeek/releases/latest"><img alt="Release v0.1.1" src="https://img.shields.io/badge/release-v0.1.1-28684F?style=flat-square" /></a>
  <img alt="Windows x64" src="https://img.shields.io/badge/Windows-x64-486650?style=flat-square" />
  <img alt="Native WPF" src="https://img.shields.io/badge/native-WPF-6A8071?style=flat-square" />
  <a href="LICENSE"><img alt="AGPL 3.0 or later" src="https://img.shields.io/badge/license-AGPL--3.0--or--later-7A8872?style=flat-square" /></a>
</p>
<p align="center">
  <a href="https://github.com/seiya058904/netpeek/releases/latest"><strong>下载 Windows 版</strong></a>
  &nbsp; · &nbsp; <a href="#preview">界面预览</a>
  &nbsp; · &nbsp; <a href="#get-started">开始使用</a>
  &nbsp; · &nbsp; <a href="#development">开发与验证</a>
</p>
<p align="center"><sub>普通权限 &nbsp; / &nbsp; 无需抓包驱动 &nbsp; / &nbsp; 无遥测 &nbsp; / &nbsp; 简体中文界面</sub></p>

<a id="preview"></a>
<p align="center">
  <img src="docs/screenshots/0.1.1/overview.png" width="100%" alt="NetPeek 应用列表：按应用分组显示当前网络端点" /><br>
  <sub>v0.1.1 最终程序 · 真实本机采样与 WPF 控件离屏渲染；不是物理桌面或高 DPI 验收截图。</sub>
</p>

## 看连接，也看清边界

<table>
  <tr>
    <td width="50%" valign="top"><h3>从应用开始</h3><p>按可核验的程序路径分组，展开查看进程、PID 与完整路径。同名但路径不同的应用分别展示。</p></td>
    <td width="50%" valign="top"><h3>连接细节，一步展开</h3><p>查看 TCP / UDP、IPv4 / IPv6、本地与远端地址、端口和状态。优先呈现地址、状态与复制操作。</p></td>
  </tr>
  <tr>
    <td width="50%" valign="top"><h3>安静地留在手边</h3><p>搜索、筛选、暂停与托盘恢复。差量刷新保留现有行对象；近期记录只保存在内存中。</p></td>
    <td width="50%" valign="top"><h3>只展示可靠的事实</h3><p>采集失败保留上次完整画面并注明时间；受限身份明确标为不确定。不会推测流量、网站归属或风险。</p></td>
  </tr>
</table>

<details>
<summary><strong>展开查看连接详情</strong></summary>

![真实回环连接的详情表](docs/screenshots/0.1.1/connections.png)

详情图使用受控回环连接。完整 IPv6 地址可查看并复制；路径支持选中文字和横向滚动。[更多布局与验收证据](docs/VALIDATION_0.1.1.md#交互布局与截图)。

</details>

<a id="get-started"></a>
## 三步开始

1. 从 **[GitHub Releases](https://github.com/seiya058904/netpeek/releases/latest)** 下载 Windows x64 便携包。
2. 解压整个文件夹，双击 **NetPeek.exe**，无需安装或管理员权限。
3. 点击应用展开进程与端点；搜索名称、PID、IP、端口或协议，按需复制地址。

| 下载 | 用途 |
| --- | --- |
| [NetPeek_0.1.1_windows_x64.zip](https://github.com/seiya058904/netpeek/releases/download/v0.1.1/NetPeek_0.1.1_windows_x64.zip) | Windows 便携版，包含对应源码与许可证 |
| [NetPeek_0.1.1_source.zip](https://github.com/seiya058904/netpeek/releases/download/v0.1.1/NetPeek_0.1.1_source.zip) | 对应源码、构建脚本、测试和文档 |
| [SHA256SUMS.txt](https://github.com/seiya058904/netpeek/releases/download/v0.1.1/SHA256SUMS.txt) | 下载文件校验值 |

**运行环境：** Windows 10/11 x64，已安装 .NET Framework 4.8 或更高版本。实机验证环境为 Windows 11；Windows 10 跨机覆盖尚未完成。程序未做代码签名，运行时不随包重复分发，也不会自动下载。

<details>
<summary><strong>常用操作与快捷键</strong></summary>

- **搜索 / 筛选：** 按名称、路径、PID、IP、端口或协议查找。无匹配时可一键恢复全部应用。
- **复制：** 连接复制远端地址；监听和 UDP 复制本地端点。IPv6 格式为 `[地址%scope]:端口`；“复制路径”保留完整程序路径。
- **暂停：** 冻结画面，后台继续采集；恢复后显示最新完整快照。
- **托盘：** “收起到托盘”隐藏窗口；双击托盘图标或再次启动程序恢复。同一 Windows 用户会话只有一个界面实例。
- **退出：** 关闭窗口、Alt+F4 和“退出 NetPeek”都会完全退出。

| 快捷键 | 操作 |
| --- | --- |
| Ctrl+F / Ctrl+P | 搜索 / 暂停与继续 |
| F5 / Esc | 请求采样 / 清空搜索、收起说明 |
| Tab / Shift+Tab | 遍历控件 |
| Space / Enter | 展开或收起聚焦的条目 |
| Ctrl+C | 复制选中端点；文本框保留常规复制 |

</details>

## 这些数据意味着什么

**TCP 已建立不等于正在传输。** 监听只说明程序等待连接；UDP 系统表只提供本地绑定，远端明确显示未知。NetPeek 是连接查看器，不是流量计、防火墙或网站识别工具。

| 可以确认 | 需要保留的边界 |
| --- | --- |
| Windows 连接表中的端点、PID 与状态 | 四张表与进程信息不是原子快照，短连接可能遗漏 |
| 读表前后可核验的进程启动时间 | 身份不可核验的 PID 行只延续界面交互，不延续可信历史 |
| 当前与过去 60 秒内观察到的端点 | 最多保留 12,000 条历史；最后观察时间不是精确关闭时间 |
| 地址本身的范围 | 代理 / VPN / Fake-IP 可能只显示代理、隧道或映射端点，不能还原真实网站 |

每轮采样完成后等待 1.5 秒再采集。当前端点显示“本次快照”，精确采样时间见页脚；历史条目显示最后观察时间。画面超过 6 秒未更新时会标明过期。

## 本地运行，普通权限

一个原生 WPF 进程，直接读取 Windows 连接表。无需 Npcap、浏览器外壳、后台 HTTP 服务或账号；无遥测、无自动更新，不主动发起网络请求，也不读取网络位置的应用图标。

普通使用不写连接日志。未处理异常会覆盖 `%LOCALAPPDATA%\NetPeek\last-error.txt`；显式诊断命令输出可能包含本机路径与端点，分享前应检查。

<a id="development"></a>
## 开发与验证

**C# / WPF · .NET Framework 4.8 · Windows IP Helper API**

无需额外 SDK、NuGet、npm 或 Python。构建使用系统安装的 .NET Framework `csc.exe`：

```powershell
.\build.ps1
.\tests\run-core-tests.ps1
.\tests\run-viewmodel-tests.ps1 -Executable .\dist\NetPeek\NetPeek.exe
```

<details>
<summary><strong>完整检查、诊断与打包命令</strong></summary>

在 Windows PowerShell 5.1 或 PowerShell 7 中执行：

```powershell
.\tests\run-core-tests.ps1 -Executable .\dist\NetPeek\NetPeek.exe
.\tests\run-icon-cache-tests.ps1 -Executable .\dist\NetPeek\NetPeek.exe
.\tests\run-layout-tests.ps1 -Executable .\dist\NetPeek\NetPeek.exe
.\tests\run-controller-state-tests.ps1 -Executable .\dist\NetPeek\NetPeek.exe
.\tests\IntegrationTests.ps1
.\build.ps1 -Package -SkipBuild
```

`-SkipBuild` 复用已测 EXE；`-Executable` 模式从指定 EXE 加载业务代码。构建输出位于被 Git 忽略的 `dist/`，正式分发以 GitHub Releases 为准。

显式套接字检查可能触发系统防火墙提示；正常运行 NetPeek 不需要放行入站网络：

```powershell
.\tests\IntegrationTests.ps1 -LiveSockets -StressConnections 256 -ExternalHost github.com
.\scripts\Measure-NetPeek.ps1
```

诊断命令仅在显式指定路径时写报告：

```powershell
.\dist\NetPeek\NetPeek.exe --snapshot C:\Temp\netpeek_snapshot.json
.\dist\NetPeek\NetPeek.exe --smoke-test C:\Temp\netpeek_smoke.json
```

完整快照可包含零端点。Smoke 验收核验本轮身份、时间、有效快照和最终状态；报告文件存在不等于成功。显式故障注入覆盖“报告后 UI 异常”“始终采集失败”与“零端点完整快照”。

若本地执行策略阻止脚本，可使用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`，只作用于该次进程。

</details>

**v0.1.1 最终 EXE：125 项针对性检查、70 项集成检查通过。** 完整交互、高 DPI 与三场景性能记录来自最后列宽补丁前的候选版；最终版追加复测未全部完成。Windows 10、多屏、网卡切换及休眠等也未验证，详见下列记录，不据此宣称兼容性或性能保证。

[变更记录](docs/CHANGELOG.md) · [v0.1.1 验收与性能](docs/VALIDATION_0.1.1.md) · [v0.1.0 归档](docs/VALIDATION.md) · [上游审计](docs/UPSTREAM_AUDIT.md)

## 致谢与许可证

源自对 [aiXis Studio / Snitch](https://github.com/aixisstudio/Snitch) 的产品思路与源码审计，Windows 采集器与界面已独立重写。使用独立的 NetPeek 名称、程序集、会话标识与数据目录，不连接 Snitch 的更新渠道。

采用 **[AGPL-3.0-or-later](LICENSE)**，保留上游及 PCYBOX Orbis 的署名，详见 [第三方说明](THIRD_PARTY_NOTICES.md)。便携包内附完整对应源码；再次分发时请一并保留源码与许可证。
