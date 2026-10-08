# 哔哩哔哩桌面端 · 长按左键倍速

在哔哩哔哩 Windows 桌面端的播放画面上**按住鼠标左键**，播放速度临时变成你设定的倍速（默认 2x）；
**松开立刻恢复**原来的倍速（1x、1.25x、1.5x 都会原样恢复）。

> 这是给哔哩哔哩 PC 客户端用的第三方小工具，和哔哩哔哩官方无关。

## 功能

- **长按倍速**：按住左键超过设定时长（默认 300 毫秒）才生效，普通点击完全不受影响
- **只在视频画面生效**：底部控制条 / 进度条 / 弹幕输入框上长按不会触发
- **拖动不误触**：按住后移动超过 12 像素视为拖动，自动取消
- **倍速随时可换**：托盘右键菜单里选 1.25x / 1.5x / 1.75x / 2x / 2.5x / 3x / 4x，点一下立即生效并写回配置
- **恢复准确**：恢复的是你按下前的实际倍速，中途在播放器里改过倍速也不会还原错
- 支持多播放窗口、全屏、任意窗口大小
- 单文件 `exe`（约 50 KB），只依赖系统自带的 .NET Framework 4.x，不需要装运行时

## 快速开始

1. 下载 `dist/BiliLongPress.exe`，放到任意目录（例如 `D:\BiliLongPress\`），双击运行
2. 第一次运行：如果客户端没开，工具会自己带调试端口把客户端拉起来
3. 打开任意视频，在画面上**长按左键**试试

托盘会出现一个粉色 `2x` 图标，右键有菜单：

| 菜单项 | 说明 |
| --- | --- |
| 状态：… | 当前是否已就绪、识别到几个播放窗口 |
| **长按倍速（当前 2x）** | 选 1.25x / 1.5x / 1.75x / 2x / 2.5x / 3x / 4x，点一下立即生效并写回配置文件 |
| **触发时长（当前 300 毫秒）** | 选 200 / 250 / 300 / 400 / 500 毫秒，同样立即生效 |
| 重启客户端并启用长按倍速 | 客户端已在运行但没开调试端口时用这个 |
| 启动哔哩哔哩（带倍速支持） | 手动拉起客户端 |
| 开机自动运行 | 勾选后开机自启（写入 `HKCU\...\CurrentVersion\Run`）|
| 打开配置文件 / 查看日志 | 排查问题用 |
| 退出 | 关闭工具 |

## 配置

首次运行会在 exe 同目录生成 `config.json`（也可以参考 `config.example.json`）。
菜单里改的值会立即覆盖写回这个文件；想用菜单之外的数值（比如 2.2x），直接编辑即可，它也会出现在菜单里。

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `debugPort` | `9222` | Chromium 调试端口，只监听本机回环 |
| `clientExe` | 自动探测 | 客户端 `哔哩哔哩.exe` 的完整路径 |
| `holdMs` | `300` | 长按多少毫秒触发 |
| `speed` | `2.0` | 长按时的倍速 |
| `clickMode` | `swallowUp` | 见下方「三种点击模式」 |
| `dragCancelPx` | `12` | 按住后移动超过这么多像素就取消 |
| `bottomExcludeCssPx` | `100` | 取不到控制条位置时，底部排除多少像素 |
| `topExcludeCssPx` | `0` | 顶部排除像素（留出窗口标题栏） |
| `pollMs` | `400` | 轮询播放器状态的间隔 |
| `autoLaunchClient` | `true` | 启动工具时若客户端没开，自动带调试端口启动 |
| `autoRestartClient` | `false` | 客户端没开调试端口时自动重启它（会打断当前播放） |
| `showNotifications` | `true` | 显示气泡提示 |
| `resumeWhenPaused` | `false` | 视频暂停时，长按是否顺便开始播放 |
| `restorePauseState` | `true` | 松开后校正播放/暂停状态 |
| `verboseLog` | `false` | 输出详细日志 |

### 三种点击模式

| 模式 | 行为 | 取舍 |
| --- | --- | --- |
| `swallowUp`（默认）| 长按触发后**吞掉鼠标抬起**，播放器收不到这次点击 | 普通点击零改动；不会误触发暂停 |
| `passthrough` | 完全不干预点击，只在松开后校正播放/暂停状态 | 最保守，但可能出现极短的暂停闪烁 |
| `block` | 按下时就拦住，短按由工具用 SendInput 回放 | 长按期间播放器完全无感知；依赖输入注入 |

## 工作原理

1. 桌面端是 Electron 应用（安装目录里有 `resources\app.asar`），播放器是一个带 `<video>` 的网页
   （`bilipc.bilibili.com/player.html`）。用 `--remote-debugging-port=9222` 启动后，
   本机就能通过 Chromium DevTools 协议直接控制它。
2. 工具轮询每个播放窗口，读取 `<video>` 的位置、当前倍速、播放状态，以及控制条的位置，
   换算成屏幕物理像素后得到「可长按热区」。
3. 全局低级鼠标钩子（`WH_MOUSE_LL`）监听左键：命中热区且按住超过 `holdMs` →
   用 `Runtime.evaluate` 把 `video.playbackRate` 设为 `speed`；松开还原成按下前的值。
4. 钩子里只做极轻量的判断（热区命中 + 该点是不是哔哩哔哩的窗口），
   真正的协议调用全部丢给后台线程，避免低级钩子超时被系统摘掉。

因为直接改的是播放器自己的 `playbackRate`，所以不依赖播放器有没有倍速快捷键，
任何分辨率、任何窗口大小、全屏都有效。

## 常见问题

**Q：提示「客户端未开启调试端口」？**
客户端必须带 `--remote-debugging-port=9222` 启动。托盘右键 →「重启客户端并启用长按倍速」，
或把 `autoRestartClient` 设为 `true`。

**Q：会不会影响我正常点视频？**
不会。短按完全按原样传给播放器（`dev/test-longpress.ps1` 里用页面事件计数器验证过：
短按仍会产生 mousedown/mouseup/click）。只有「长按触发倍速」那一次会吞掉鼠标抬起，
避免播放器把这次长按当成点击。

**Q：会不会占资源？**
单个约 50 KB 的 exe，常驻内存约 20 MB；鼠标钩子只在左键按下/抬起时被调用，
默认 400 毫秒轮询一次，几乎不占 CPU。

**Q：日志在哪？**
`%LOCALAPPDATA%\BiliLongPress\log.txt`

**Q：怎么卸载？**
退出托盘图标，删掉程序目录；如果勾过开机自启，先在托盘里取消勾选
（或删掉注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `BiliLongPress`）。

## 开发者

```
src/BiliLongPress.cs   单文件源码（C# / .NET Framework 4.x，无第三方依赖）
build.ps1              用系统自带 csc.exe 编译（不需要装 SDK）
dist/                  已编译好的 exe
dev/                   CDP 调试脚本与端到端测试脚本
```

重新编译：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

命令行参数：

```powershell
.\BiliLongPress.exe --probe          # 输出热区、窗口矩形、识别到的播放器（写 probe-report.txt）
.\BiliLongPress.exe --speedtest      # 不经过鼠标，直接测倍速通道（前值 → 设定值 → 恢复值）
.\BiliLongPress.exe --verbose        # 详细日志
.\BiliLongPress.exe --setspeed=1.75  # 等价于托盘菜单选 1.75x（立即存盘）
.\BiliLongPress.exe --sethold=250    # 等价于托盘菜单选 250 毫秒
```

端到端测试（模拟真实鼠标长按/点击/拖动，需要 node 在 PATH 里，且播放器里有视频）：

```powershell
powershell -ExecutionPolicy Bypass -File .\dev\test-longpress.ps1
```

## License

MIT
