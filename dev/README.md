# dev —— 调试与测试脚本

这些脚本不是给最终用户用的，是开发和验收时用的。

| 文件 | 用途 |
| --- | --- |
| `cdp.mjs` | 通过 Chrome DevTools 协议操作客户端播放器窗口：`list` / `eval` / `shot` / `click` / `setrate` / `front` |
| `state.js` | 读取 `<video>` 的倍速、播放状态、当前时间 |
| `probe-player.js` | 读取播放器页面结构（video 尺寸、窗口位置、控制条位置等） |
| `context-info.js` | 判断播放器页面有没有 iframe、事件计数器状态 |
| `install-counters.js` / `read-counters.js` | 在页面里挂 mousedown/mouseup/click/dblclick 计数器，用来验证"普通点击没被工具破坏" |
| `test-longpress.ps1` | 端到端测试：模拟真实鼠标长按 / 点击 / 拖动，验证倍速与点击行为 |

## 前置条件

1. 客户端带调试端口运行：
   ```powershell
   & "<客户端目录>\哔哩哔哩.exe" --remote-debugging-port=9222
   ```
   （如果客户端已经在运行，先完全退出，否则新实例只会把参数交接给旧实例。）
2. 播放器窗口里已经打开了一个视频。
3. `node` 在 PATH 里（Node.js 22+，脚本用了内置的 `WebSocket` / `fetch`）。

## 用法

```powershell
# 看有哪些页面 target
node .\dev\cdp.mjs list

# 在播放器页面里执行 JS（表达式也可以写成 @文件 形式，避免引号被 shell 吃掉）
node .\dev\cdp.mjs eval player.html "@.\dev\state.js"
node .\dev\cdp.mjs setrate player.html 1.75

# 端到端测试（会短暂重启托盘工具，并模拟真实鼠标操作）
powershell -ExecutionPolicy Bypass -File .\dev\test-longpress.ps1
```

> 测试期间请**不要动鼠标**：脚本用 `SendInput` 模拟输入，
> 真实的鼠标移动会触发工具里的"拖动取消"逻辑，导致结果不可信。
