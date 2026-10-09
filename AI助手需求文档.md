# Hexa 新增「AI 助手」页 — 需求文档

## 目标
在 Hexa（WPF C# .NET9 windows，MVVM）新增一个「🤖 AI 助手」Page：**AI 语音伴侣**——用户说话/打字，AI（DeepSeek）对话，AI 通过 **function calling 全自动控制 OSR6**，AI 用 **TTS 语音回复**。参考现成 Python 项目 `https://github.com/zxdxzxdxzxdx/1/.../c.py`（功能参考，**不改其代码；运动执行复用 Hexa 现有 MotionEngine，不重造**）。

## 技术背景（Hexa 现有，必须复用）
- **App 静态单例**（`App.xaml.cs` 已定义，直接访问）：`App.Serial`(SerialService)、`App.Engine`(MotionEngine)、`App.Settings`(AppSettings)、`App.AudioReactive`、`App.RuleEngine` 等。
- **MotionEngine 现成能力**（`Services/MotionEngine.cs`，AI tools 直接映射到这些方法）：
  - `ApplyQuickPreset(string id)`：预设（gentle/daily/crazy/tease/wave/heartbeat/escalate/edging/ambush）→ 设 AutoPattern + Speed + 各轴幅度。
  - `StartAuto()` / `StopAuto()`：启停自动行为；`AutoPattern`(string)：如 organic_flow/gentle_wave/intense_thrust；`CurrentAutoPattern`/`CurrentAutoBpm`。
  - `StartRuleAuto(pattern)`（规则引擎接管时用）；`ToggleAutoBehaviorHold()`（保持/继续当前行为）；`NextAutoBehavior()`；`EaseAutoBehavior()`。
  - `Speed`(0.1–3.0)、`IntensityScale`(0.1–2.0)、`AdjustIntensity(delta)`。
  - `SetAxisAmp(int index, double value)`(0–100，6 轴 L0-L2/R0-R2 对应 index 0-5)、`GetAxisAmp()`。
  - `TrySendDirectAxes(double[] values)`(0-100，需 `CanAcceptDirectInput`)。
  - `EmergencyStop()`、`Home()`、`CanRun`、`SafetyStatus`、`IsRunning`、`ActiveMode`。
- **SerialService**：`App.Serial.SetSimulationMode(true)` 可无硬件模拟测试（TCode 进 `SimulationCommands`）；`Send/SendAxes` 已被 MotionEngine 封装，AI 助手一般只需调 Engine 方法。
- **MVVM 模板**：`ViewModels/` 有 PlaygroundViewModel/SettingsViewModel/StrokesViewModel（CommunityToolkit.Mvvm，ObservableProperty/RelayCommand）。Page 用 `Views/`。
- **MainWindow 导航**：`_navBtns` 数组 + 缓存 `_xxxPage = new()` + `Navigate(tag)` 用 `ContentFrame.Navigate(tag switch { ... })`。加新页 = ① MainWindow.xaml 加一个导航按钮（Tag="ai"，样式同其他）② MainWindow.xaml.cs 加 `_aiPage = new()` 字段 + `_navBtns` 加该按钮 + switch 加 `"ai" => _aiPage`。
- **主题**：暗色，主色 `#17B890`（App.xaml 资源字典）。新 Page 复用同主题。
- **音频**：`NAudio 2.2.1` 已引用（`PackageReference`），TTS 播放用它（WaveOutEvent + AudioFileReader 播 mp3）。
- **目标框架**：`net9.0-windows`，`Nullable enable`。

## 要加的文件
1. `Views/AiAssistantPage.xaml` + `AiAssistantPage.xaml.cs`
2. `ViewModels/AiAssistantViewModel.cs`
3. （可选）`Services/AiAssistantService.cs`：封装 DeepSeek API + function calling 执行 + TTS
4. MainWindow.xaml + MainWindow.xaml.cs：加导航
5. App.xaml.cs：若需注册单例（如 `App.AiAssistant`）——建议用 VM 静态单例方式（仿 `App.StrokesVm`）

## 功能拆解

### 1. AI 对话（DeepSeek/硅基流动 OpenAI 接口）
- 用 OpenAI 兼容接口 `chat.completions`（function calling）。`base_url=https://api.siliconflow.cn/v1`，模型 `deepseek-ai/DeepSeek-V4-Flash`（c.py 用），**API key 从 App.Settings 读，绝不硬编码**（AppSettings 加 `AiApiKey/AiApiBase/AiModel` 字段，默认空，设置页可填）。
- 可用 `System.Net.Http.HttpClient` 直接 POST JSON，或引 `OpenAI` NuGet 包（二选一，**优先 HttpClient 免加依赖**）。
- 多轮对话：保存 `messages` 列表（List of {role,content}），AI tool 调用后把 assistant 的 tool_calls 和 tool 返回值 append 进 messages（参考 c.py ai_process 逻辑）。

### 2. function calling tools（映射到 App.Engine）
定义 7 个 tools，AI 调用时**映射到 MotionEngine 方法**（不要自己发串口）：
- `set_scene` {scene: idle|mild|excited|climax|riding|sitting_twist|fisting} → 映射 `Engine.ApplyQuickPreset`（建议：idle→Home归中 / mild→gentle / excited→daily / climax→ambush / riding→wave / sitting_twist→tease / fisting→heartbeat），或 `StartAuto()`+对应 AutoPattern。映射表写清楚。
- `set_speed` {speed: slow|medium|fast|full} → `Engine.Speed=` (slow=0.5, medium=1.0, fast=1.8, full=3.0)，可选 `Engine.IntensityScale` 同步。
- `stop` → `Engine.EmergencyStop()`（安全兜底，别裸停）。
- `pause` → `Engine.StopAuto()`。
- `resume` → `Engine.StartAuto()`。
- `tease` {move_sec,stop_sec,pause_sec} → 基础版：`Engine.ApplyQuickPreset("tease")` + `ToggleAutoBehaviorHold()`；做减法，不需精确动停循环，先能"调情停顿"即可。
- `set_motion` {L0..R2: {center,amplitude,speed}} → 用 `Engine.SetAxisAmp(index, amplitude)` + `Engine.StartAuto()` 或 `TrySendDirectAxes`；center 直接送轴（用 SendAxes 或 TrySendDirectAxes）。幅度 clamp 0-50，speed 映射 `Engine.Speed`。

### 3. TTS 语音回复
- 用 **edge-tts**（生成 mp3 到临时文件，`VOICE=zh-CN-XiaoyiNeural`, rate +30%），再用 **NAudio** `WaveOutEvent + AudioFileReader` 播放（Hexa 已带 NAudio）。`clean_text_for_tts` 参考 c.py（去 think 标签/括号/非中英文）。
- 异步后台线程播放（不阻塞 UI）；可加"停止说话"（StopSpeech 方法，复位 WaveOutEvent）。
- ⚠️ edge-tts 需网络；若时效/离线要求，可降级用 `System.Speech.SpeechSynthesizer`（内置，音质差）。**默认 edge-tts + NAudio**。

### 4. 语音识别（输入，可选/后置）
- P0 **先做文字输入**（TextBox + 发送按钮），保证对话+控制闭环。
- 语音识别（升级项）：`System.Speech.Recognition`（Windows 中文识别，`zh-CN`，需系统装了中文语音，`SpeechRecognitionEngine(zh-CN)`）或 `Windows.Media.SpeechRecognition`。**此步可后置/可选**，需求文档先标"可选"。

### 5. 角色卡（人格）
- AI system prompt 预设 1 个默认人格（标题如"AI 伴侣"），可下拉切换 2-3 个人格（如：温柔陪伴 / 调皮调教 / 冷静服务）。system prompt 说明"你是 AI 伴侣，陪用户并用工具控制设备，根据气氛调用合适的运动，语气亲密自然，中文回复"。
- 精简（做减法）：内置 2 个人格即可，不做复杂导入/导出。

### 6. UI（AiAssistantPage）
- 暗色主题，复用 App.xaml 资源。
- 主体：聊天消息列表（ItemsControl 或 ListBox，显示用户/AI 消息，AI 可显示头像/气泡）。
- 底部：输入框 + 「发送」按钮 + 「🎤 语音」按钮（可选）+ 「⏹ 停止说话」。
- 顶部/侧：设备连接状态（读 `App.Serial.IsOpen`/`App.Engine.SafetyStatus`）+ 角色卡下拉 + 连接指示。
- 导航栏加「🤖 AI 助手」按钮。
- 与现有页风格一致（卡片式、圆角、主色 #17B890）。

## 安全（必须）
- 复用 `MotionEngine` 内置 `MotionSafetyLimiter`（PhysicalMax 安全），**不绕过**。
- AI 工具调用限制强度上限：`Engine.IntensityScale`/`Speed` clamp 在安全范围（Engine 已 clamp）。
- 提供「急停」按钮（调 `Engine.EmergencyStop()`），AI 说的"停/stop"也映射到 EmergencyStop。
- 所有工具执行包裹 try/catch，失败 APPEND 一条 assistant 消息"出了点问题"并保持安全状态。

## 验收（dsh 交付后我来 build/review）
1. `dotnet build` 零错误。
2. 模拟模式（`App.Serial.SetSimulationMode(true)`）下，对话触发 AI 调 `set_scene` → `Engine` 状态改变（AutoPattern/轴幅变化可见）。
3. UI 能正常导航到「AI 助手」页，暗色主题一致，聊天能收发文字。
4. TTS 能生成+播放（在无硬件时也能测，先不接串口）。
5. 不改动/破坏现有页功能。

## 约束
- 全程 C#，MVVM（CommunityToolkit.Mvvm），代码风格与现有 Services/Views 一致（文件头 namespace `Hexa.Services`/`Hexa.ViewModels` 等）。
- 不引入不必要 NuGet（别装 OpenAI 包免得体积大；HttpClient 足够）。
- 运动执行一律走 `App.Engine`，**不要在 AiAssistantService 里直接 new SerialService/发串口**。
- API key 从 AppSettings 读，默认空则 AI 功能禁用并提示去设置，绝不打进代码。
