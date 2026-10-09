# Hexa AI 助手 — 功能补齐需求文档（对齐 c.py）

## 背景
Hexa 已实现「AI 助手」页（`Services/AiAssistantService.cs` + `ViewModels/AiAssistantViewModel.cs` + `Views/AiAssistantPage.xaml`），核心 AI 对话 + function calling 自动控 OSR6 + TTS 已就绪。现参照现成 Python 项目 `C:/Users/w1365/c_proj.py`（1472 行，经验验证过的逻辑），补齐 4 个缺失/简化的功能。**目标：对齐 c.py 的完整体验。**

## 复用约束（务必遵守）
- 运动控制一律走 `App.Engine`（MotionEngine），绝不 `new SerialService`/发串口。
- API key 从 `AppSettings` 读（已有 `AiApiKey/AiApiBase/AiModel`），不硬编码。
- MVVM（CommunityToolkit.Mvvm：`ObservableProperty`/`RelayCommand`），代码风格与现有 `Services/`、`ViewModels/` 一致。
- 运动执行复用 `MotionEngine` 现成方法；`App.Dispatch` 调度到 UI 线程（已有模式）。
- 目标框架 `net9.0-windows`，`Nullable enable`。

## 要补的 4 个功能

### ① 语音识别输入（对齐 c.py 前端 Web Speech）
- 在 `AiAssistantPage` 底部输入框旁边加一个「🎤 语音」按钮，点击开始/停止语音识别。
- 用 **System.Speech**（Windows 内置，`SpeechRecognitionEngine(language: "zh-CN")`）做中文语音识别。需在 `Hexa.csproj` 加 `<PackageReference Include="System.Speech" Version="..." />`（若缺失；确认 net9.0-windows 可用，若装不上可做成"识别引擎初始化失败→按钮禁用+提示"降级）。
- 流程：点「🎤」→ 开始异步监听（后台线程/async）→ 识别到文字 → `App.Dispatch` 回 UI 线程填入 `InputText`（或识别到一句话自动调用 Send）→ 结束监听。
- UI：按钮点击切换"🔴 正在听…/🎤 语音"状态；识别出错/无麦克风时按钮禁用并提示（`IsSpeechAvailable`）。
- 参考 c.py `recognition`（Web Speech）语义：说话变成用户消息发给 AI。

### ② 高潮处理（对齐 c.py `force_climax_reply`）
- 在 `SendAsync`/`ai_process` 里，检测用户输入是否命中关键词：`"1"`、`"射了"`、`"高潮"`、`"要到了"`、`"要出来了"`、`"出来了"`、`"憋不住了"`、`"快到了"` 等。
- 命中 → 调用高潮逻辑：**把 L0 降到最低位（中心 15）并停所有轴**——用 `_engine` 实现（可 `Engine.EmergencyStop()` 兜底，或调 `SetAxisAmp` 清零 + 归中；尽量复用 Engine 的"停+降位置"能力），并把这句作为用户消息发给 AI（"我射了"），让 AI 回一句收尾话（参考 c.py）。
- 返回 AI 的收尾回复（如"呼…终于结束了…"），不控制运动。

### ③ 敏感词运动补充（对齐 c.py contact_kw）
- 在 `SendUserAsync` 拿到 AI 文字回复后（且**本轮没有任何 tool_calls**），检测回复是否含身体动作敏感词：`蹭/贴住/含/握/揉/捏/套/舔/撸/夹/吸/顶/磨/套弄/吞吐/包裹/摩擦/抽插/骑/龟头/下面/那里/性器/生殖器/阴茎/肉棒/穴/阴/射/高潮/插` 等。
- 命中 → 追加一条 system/user 指令（如"你刚才描述了身体动作，请调用 set_motion 生成对应模拟动作，不要回复文字"）再请求一次 AI，若返回 tool_calls 则执行（复用 `SendUserAsync` 的循环逻辑，或单独方法）。
- 参考 c.py 1041-1053 逻辑。

### ④ tease 精确动停循环（对齐 c.py `start_teasing`）
- 当前 `tease` 只是切预设+hold。要改成**精确"动 N 秒→停 N 秒→歇 N 秒"循环**，用 `move_sec/stop_sec/pause_sec` 参数（1-10/1-15/1-15）。
- 做法建议（二选一，选侵入最小的）：
  1. 在 `MotionEngine` 新增一个 `TeasingMode`（定时器：动 N 秒按 tease 预设发运动、停/歇 N 秒不发或降幅），暴露 `StartTease(move,stop,pause)` / `StopTease()`。
  2. 或在 `AiAssistantService` 内部用一个循环定时器驱动（动→停→歇），命中时调 `Engine.ApplyQuickPreset("tease")` 动、`Engine.ToggleAutoBehaviorHold()`/`StopAuto()` 停——**但最好在 MotionEngine 层做，复用其定时/安全机制**。
- `tease` 工具调用时，从参数读取 move_sec/stop_sec/pause_sec，启动该循环；再次调用可调整；`stop`/`EmergencyStop` 时清掉。
- 安全：循环在 Engine 层、受 `MotionSafetyLimiter` 约束，不绕过；强度有上限。

## 文件改动预期
- `Services/AiAssistantService.cs`：加 ForceClimax + 敏感词补充 + tease 精确循环调度（或调 Engine 新方法）。
- `Services/MotionEngine.cs`（可选）：加 TeasingMode（若选 Engine 层实现）。
- `ViewModels/AiAssistantViewModel.cs`：加语音识别命令 + 高潮关键词检测（在 SendAsync 里）+ 状态。
- `Views/AiAssistantPage.xaml` + `.cs`：加「🎤 语音」按钮 + 识别状态 + 高潮/忙状态指示。
- `Hexa.csproj`：加 `System.Speech` 引用（若需要）。

## 验收（dsh 交付后我 build+review）
1. `dotnet build` 0 错误。
2. 语音按钮：能启动识别（麦克风可用时），识别到话回填输入框/发送；无麦克风则禁用并提示。
3. 输"射了/高潮/1"等 → 触发停轴降位 + AI 收尾话。
4. AI 回复含身体动作词但没调工具 → 自动补一次 set_motion。
5. tease 用参数做"动N/停N/歇N"循环，stop/急停可清。
6. 不破坏现有功能（AI 对话/场景/速度/TTS/急停）。
