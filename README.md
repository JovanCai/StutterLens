# StutterLens

[English](README.en.md) | 中文

StutterLens 是一款后台运行的 Windows 游戏卡顿诊断工具。它会自动识别正在玩的游戏，记录每一次卡顿，推测原因并给出处理建议。游戏结束后自动生成一份报告。

- **找原因**：把每次卡顿和当时的系统状态对照，指出问题出在驱动和后台程序这类系统层面，还是游戏本身。
- **给办法**：每个原因都附处理建议，按难度分为简单、中等和进阶。
- **不碰游戏**：不注入，也不读取游戏内存或安装驱动。检测到反作弊时自动切换到更保守的安全模式。
- **够轻量**：实测自身平均 CPU 占用约 0.2%，常驻内存约 85MB（i7-10700K，深度模式）。

报告示例：[docs/sample-report.html](docs/sample-report.html)（下载后用浏览器打开）

## 下载与使用

1. 到 [Releases](../../releases) 页面下载最新版，解压到任意文件夹。大多数电脑选 `win-x64`，骁龙等 ARM 处理器的笔记本选 `win-arm64`。不确定的话，在 设置 → 系统 → 系统信息 中查看“系统类型”。
2. 双击 `StutterLens.exe`，在弹出的 UAC 窗口中点“是”。读取帧数据需要管理员权限。
3. 正常打开游戏玩就行，StutterLens 会自动识别并开始记录。
4. 游戏退出后，报告会自动在浏览器中打开。中途想结束，在 StutterLens 窗口按 Ctrl+C 或直接关闭窗口，同样会生成报告。

报告保存在 `%LOCALAPPDATA%\StutterLens\Reports`，每次游戏一份 HTML 和一份 JSON。

> **首次运行时 Windows 可能提示“已保护你的电脑”。** 这是因为程序还没有代码签名，点“更多信息 → 仍要运行”即可。个别杀毒软件可能因为程序需要管理员权限并读取系统事件而误报，可以自行检查 [源码](src/StutterLens) 或从源码构建。

**系统要求**：64 位的 Windows 10（1809 及以上）或 Windows 11，x64 或 ARM64 处理器，需要管理员权限。

## 反作弊与账号安全

无论哪种模式，StutterLens 都**不会**：

- 打开游戏进程句柄（不调用 `OpenProcess`），不读写游戏内存，不注入 DLL
- 显示游戏内叠加层，不挂钩图形 API 或输入
- 安装内核驱动（不使用 WinRing0 之类的硬件访问驱动）

所有数据都来自 Windows 自身公开的接口。和 PresentMon 一样，用的是任务管理器和性能监视器也在用的那一类接口。

| 模式 | 读取的数据 | 何时使用 |
|---|---|---|
| 安全模式 | 性能计数器、进程列表、图形 Present 事件 | 检测到反作弊进程或驱动，或游戏在已知反作弊名单中 |
| 深度模式 | 在上面基础上增加内核 ETW（驱动 DPC/ISR、磁盘 IO） | 单机游戏，且游戏启动 15 秒后仍未发现反作弊 |

- 内核追踪只在玩单机游戏时开启，空闲时不开。
- 一旦出现反作弊进程或驱动，会立即关闭内核追踪，本局保持安全模式。
- 即使手动指定 `--mode full`，检测到反作弊也会强制切回安全模式。

> 没有任何第三方工具能拿到反作弊厂商的官方保证。StutterLens 能做的是只使用系统公开接口，并对反作弊采取最保守的策略。部分游戏会屏蔽 ETW，这时只是拿不到帧数据，不会带来风险。

## 隐私

StutterLens 完全在本地运行，**不联网，不收集或上传任何数据**。报告只保存在你自己的电脑上。JSON 报告里的文件路径会隐去 Windows 用户名，方便你放心地把报告发给别人求助。

## 能识别的卡顿原因

| 原因 | 判断依据 | 模式 |
|---|---|---|
| 驱动延迟过高（可定位到显卡、网卡、USB 等具体设备） | 单个 CPU 核心上的 DPC/ISR 耗时 | 深度 |
| 驱动中断占用偏高（不定位设备） | `% DPC Time` 与 `% Interrupt Time` | 安全 |
| 后台程序占用突增 | 卡顿那一秒的 CPU 占用和过去 30 秒相比 | 两者 |
| 新程序启动 | 进程列表变化 | 两者 |
| 硬盘读取阻塞 | 游戏读盘延迟（深度）或磁盘响应时间（安全） | 两者 |
| 内存不足换页 | 可用内存、提交率、游戏每秒硬缺页数 | 两者 |
| 显存已满 | GPU Adapter Memory | 两者 |
| CPU 满载或降频 | 处理器利用率与 `% Processor Performance` | 两者 |
| 窗口切换或弹窗打断 | 前台窗口变化 | 两者 |
| 游戏自身卡顿（多为着色器编译） | 排除法，结合游戏自身的 CPU 与读盘突增 | 两者 |
| 周期性卡顿、开局集中卡顿、CPU/GPU 瓶颈 | 整局统计 | 两者 |

报告里还会附上一份系统设置检查。除了电源计划和 Xbox 后台录制这类常见设置，也会检查虚拟内存和显示器刷新率，以及显卡驱动是否过旧、游戏是否装在机械硬盘上。不想玩游戏也可以单独运行 `StutterLens.exe check` 做这项检查。

**卡顿的判定方式**：以最近 3 秒帧时间的中位数为基线，某一帧超过基线的 2 倍（且至少多出 12ms、不少于 20ms）即算一次卡顿，连续的慢帧合并为一次。画面停顿超过 50ms 记为“明显卡顿”，其余记为“轻微顿挫”。持续性的掉帧会被基线吸收，不会被当成一连串卡顿。游戏启动的前 10 秒和游戏不在前台时不计入。

## 命令行

```bash
StutterLens.exe                          # 后台监控，自动识别游戏（默认）
StutterLens.exe check                    # 系统设置检查，不需要管理员权限
StutterLens.exe --process game.exe       # 只监控指定的游戏
StutterLens.exe --mode safe              # 强制使用安全模式
StutterLens.exe --include-background     # 游戏在副屏或不在前台时也计入卡顿
StutterLens.exe help                     # 查看全部选项
```

## 常见问题

**为什么需要管理员权限？**
帧时间来自 Windows 的事件跟踪（ETW），Windows 规定只有管理员才能开启 ETW 会话。

**为什么没识别到我的游戏？**
StutterLens 会把在前台、每秒提交 15 帧以上并持续 3 秒的程序当作游戏。窗口化玩或者放在副屏时，可以用 `--process 游戏名.exe` 加 `--include-background` 指定。

**某些游戏拿不到帧数据？**
少数游戏会主动屏蔽 ETW，这时 StutterLens 只能做系统层面的检查。

**支持 ARM 处理器的 Windows 电脑吗？**
支持，请下载 `win-arm64` 版本。ARM64 版还没有在真机上做过游戏实测，欢迎 ARM 设备用户反馈。另外，不少带反作弊的游戏本身还不支持 ARM 设备。

**报告里的“把握”是什么意思？**
原因是根据卡顿那一刻的系统状态推测出来的，属于相关性分析，“把握”反映推测的可信度。同一个原因反复出现、把握又高的话，基本可以确定。

## 和同类工具的区别

| 工具 | 擅长 | 不足 |
|---|---|---|
| PresentMon、CapFrameX、RTSS、FrameView | 帧时间采集和曲线 | 能看到什么时候卡，看不出为什么卡 |
| LatencyMon | 定位驱动延迟 | 只在空闲时测，和游戏帧对不上 |
| [Stuttometer](https://github.com/xdr0p/stuttometer) | ETW 抓帧并按概率排序原因 | 不给处理建议，也没说明反作弊兼容性 |
| [FrameScope](https://github.com/KaroqDave/FrameScope) | 原因加处理建议 | 依赖另装 PresentMon，反作弊兼容性只是声明 |

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet build StutterLens.sln -c Release
```

```bash
dotnet test StutterLens.sln -c Release
```

```bash
dotnet publish src/StutterLens -p:PublishProfile=win-x64
```

```bash
dotnet publish src/StutterLens -p:PublishProfile=win-arm64
```

发布命令会在 `artifacts/publish/win-x64/` 或 `artifacts/publish/win-arm64/` 生成自带运行时的单个 `StutterLens.exe`。ARM64 版可以在 x64 电脑上交叉编译。

代码结构：

```
src/StutterLens/
  Collectors/   ETW 帧与内核事件采集、性能计数器与进程快照、时序信号存储
  Detection/    游戏识别与帧时间统计、反作弊检测
  Analysis/     卡顿判定、原因推测与建议、系统设置检查
  Knowledge/    反作弊签名、游戏库、后台程序与驱动知识库
  Report/       HTML 和 JSON 报告
  Platform/     Win32/NT 只读调用
tests/          单元测试
```

## 反馈与贡献

遇到误判或漏判，欢迎 [提交 Issue](../../issues/new/choose)，附上报告的 JSON 文件会很有帮助（文件路径里的用户名已经隐去）。补充反作弊签名、后台程序或驱动的知识库条目，可以直接改 [KnowledgeBase.cs](src/StutterLens/Knowledge/KnowledgeBase.cs) 后提交 PR。

## 许可证

[MIT](LICENSE)
