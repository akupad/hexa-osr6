# Hexa · OSR6 六轴控制器（Windows / WPF）

**中文** ｜ [English](#english)


> 用一个桌面软件把「六轴舵机设备」变成能玩的东西：游戏联动、脚本播放与编排、声音响应、画面识别、手动动轴与 3D 实时预览。

**English TL;DR** — Hexa is a Windows (WPF, .NET 10) controller for TCode-based multi-axis servo devices (developed on a 6-axis OSR6: 6×30kg + 1×9kg servos). It speaks TCode v0.3 over serial, hosts a Buttplug/Intiface v3 WebSocket bridge for games, plays and edits funscripts, reacts to audio, and can drive the device from screen content or an LLM. Issues and PRs are very welcome — see 「想请大家帮忙的地方」.

## 它能做什么

- **3D 实时预览**：六轴姿态跟着真实下发值同步（不是估算）。
- **游戏桥**：本机起一个 Buttplug v3 WebSocket 服务（默认 `ws://127.0.0.1:12345`），游戏侧填这个地址即可；支持单桥（L0+R0）/ 双桥 / **单桥·六轴直驱**（游戏能按轴点名 L0/L1/L2/R0/R1/R2）。
- **脚本播放**：funscript 载入/播放/暂停/定位、A-B 循环、章节跳转、倍率、媒体同步（mpv / MPC-HC / VLC 时间码跟随）。
- **脚本制作**：编排（拖动作段到时间轴）＋ 微调波形（逐轴关键帧、波形生成、撤销栈）。
- **声音响应**：按音量/律动驱动，带节拍检测与事件化动作。
- **画面跟随**：屏幕内容识别（可选本机 ONNX 模型），把画面信号映射成动作强度。
- **手动动轴 + 正弦摆动**：默认关的安全闸，勾上才动；六轴滑杆与幅度/速度发生器。
- **AI 助手**：接 DeepSeek/硅基流动等 OpenAI 兼容接口，用自然语言下动作指令（下发前有二次确认）。
- **安全设计**：急停一键可达；所有运动只经唯一的限速输出出口；每个驱动源（脚本/桥/声音/手柄/编排/AI）必须认领「控制权」，界面永远显示**谁在驱动设备**。

## 运行要求

- Windows 10 1809+ / Windows 11（本项目在 Windows 11 + .NET 10 上开发）
- .NET 10 SDK（自行编译）或直接用发布好的自包含包
- 一台 TCode v0.3 设备（本机开发用的是 ESP32-C3 原生 USB CDC，115200）

## 快速开始（自行编译）

```powershell
dotnet build Hexa.csproj -c Debug          # 编译（应 0 错误）
dotnet run --project tests/Hexa.CoreTests  # 单元测试（257+ 项）
./tools/release.ps1                        # 发布：构建 → 单测 → 自检 → 打包到 dist_hexa/
```

自检（会在界面里跑一遍功能并写报告）：

```powershell
$env:HEXA_SELFTEST="$env:TEMP\hexa-selftest.txt"; dotnet run --project Hexa.csproj -c Debug
```

## 上手三步

1. 打开 Hexa → **设置** 页选串口 → 点「立即连接」（连接不上先点「刷新设备」看串口有没有被别的程序占用）。
2. 想玩游戏：**游玩 → 🌉 游戏桥** 打开桥，把游戏里的 Buttplug/Intiface 地址填成 `ws://127.0.0.1:12345`。**顺序是先开 Hexa、再开游戏。**
3. 想直接玩：**脚本库**载入一个 funscript 播放，或 **手动动轴**页勾上安全闸自己拉滑杆。

## 常见故障（先看这三条）

| 现象 | 处置 |
|---|---|
| 机器突然不动、日志里全是写超时 | **拔插一次 USB 线**，然后点侧栏的「⬆ 全部归中」解锁输出（软件复位救不回来，只有断电重插能救） |
| 界面写「输出已锁定」 | 同上：点侧栏「⬆ 全部归中」（会先问一次），它会解除急停并重新使能输出 |
| 游戏连上了但设备不动 | 见「游玩 → 游戏桥」面板的指令监视：如果游戏只发 VibrateCmd，本机没有振动附件；换成发 LinearCmd/RotateCmd 的内容，或到测试台开「接收原始 TCode」走 OSR-TCode |

## 画面跟随要先下模型

「画面跟随」用的是本机 ONNX 推理，**仓库里不含模型**：第一次用得在「测试台 → 画面信号」里点「下载模型」（识别模型与姿态模型分别下，几 MB～几十 MB）。没下模型时它会退化成手工特征判断，不是坏了。

⚠️ **许可提醒**：姿态模型用的是 `Xenova/yolov8n-pose`，其上游（Ultralytics YOLOv8）权重默认 **AGPL-3.0**；本项目不随仓库分发该权重，由你在界面上按需下载，用于闭源/商业场景请自行替换。详见 `THIRD-PARTY-LICENSES.md`。

## 适用人群

本软件用于驱动成人向互动设备（六轴机械臂 / 线性设备），包含 18+ 用途。请在合法合规、自愿的前提下使用。

## 已知限制（也正是想请大家帮忙的地方）

1. **设备档案写死在代码里**：轴名、引脚、设备名（`OSR6 6-Axis`）目前是硬编码的，只支持 TCode v0.3 的位置舵机设备。**最想要的一个 PR：把设备参数抽成可配置的「设备档案」**，这样别人的机器也能用。
2. **只在一台机器 + 一台设备上验证过**：多客户端仲裁、设备热插拔等场景还没做真机回归。
3. **没有安装包**：目前是自包含目录 + 桌面快捷方式，没有版本号/签名/自动更新。
4. **协议覆盖**：Buttplug 侧的 `ScalarCmd`（振动/往复的规范表达）、`RawWriteCmd`、`SensorReadCmd` 还没实现（会明确回 `Error`，不会静默丢弃）。对照表见 `docs/游戏桥-意图覆盖表.md`。
5. **诊断界面还在产品里**（日志查看、固件设置、设备模拟器）—— 目前一律折叠收起。

## 想请大家帮忙的地方

- 把**设备档案做成可配置**（上面第 1 条）——这是「别人也能用」的最大门槛。
- Buttplug 协议的**规范化补齐**：`ScalarCmd` 的语义映射（振动/往复 → 位置轴）。
- 其他 TCode 固件/控制板的适配（不同引脚、不同轴数、带位置反馈的机型）。
- 界面与文案（目前全中文，欢迎英文/日文翻译 PR）。
- 真机测试反馈：任何「我这台机器上会这样」的现象都请开 issue，附 `%LOCALAPPDATA%\Hexa\app.log` 与操作步骤。

## 安全提醒

- 这是一款**会真的让机械动起来**的软件。请在无人、无障碍、随时能断电的环境里试。
- 所有会驱动设备的开关默认关闭；「锁定急停」在任何页面都够得到（含悬浮窗）。
- 首次使用请先把行程限位收窄（设置 → 🎚 轴限位），别一上来就跑全程。

## 许可

MIT License，见 `LICENSE`；第三方作品（含 SR6 网格与运动学来自 osr-emu）的版权声明见 `THIRD-PARTY-LICENSES.md`。

## 目录结构

```
Hexa.csproj            程序本体（WPF，net10.0-windows）
Services/              设备/协议/引擎/桥/音频/画面/AI 等服务
Views/                 各页面与悬浮窗
ViewModels/            MVVM 层
Models/                设置与数据模型
Diagnostics/           自检与 UX 量测
tools/release.ps1      发布流程
docs/                  设计与协议文档（含游戏桥意图覆盖表）
tests/Hexa.CoreTests/  单元测试
```


---

## English

> **Hexa** is a Windows desktop app (WPF, .NET 10) that turns a TCode multi-axis servo device (OSR6 / SR6 class) into something you can actually play with: game integration, funscript playback and authoring, audio-reactive motion, screen-aware behaviour, manual axis control and a live 3D preview.

### Features

- **Live 3D preview** — the model follows the *actual* values being sent to the device.
- **Game bridge** — hosts a Buttplug v3 WebSocket server (default `ws://127.0.0.1:12345`) for games and plugins. Three presentation modes: single (L0+R0), dual, or **single · 6-axis direct** (games can address L0/L1/L2/R0/R1/R2 explicitly). It re-interprets game intent for position servos: rotation becomes a back-and-forth sweep, a constant position is rendered as an oscillation around that level, and vibration can be mapped to rotation or a small fast stroke.
- **Script playback** — funscript load / play / pause / seek, A-B loop, chapter jump, playback rate, media sync (mpv / MPC-HC / VLC timecode following).
- **Script authoring** — a timeline composer plus a per-axis waveform editor with undo.
- **Audio reactive** — volume and rhythm driven, with beat detection and event-based actions.
- **Screen aware** — optional on-device ONNX inference maps screen content to motion intensity.
- **Manual control + sine generator** — behind an explicit safety toggle: six sliders plus a per-axis amplitude/speed oscillator.
- **AI assistant** — any OpenAI-compatible endpoint (DeepSeek etc.), natural-language motion commands with a confirmation step before anything moves.
- **Safety model** — one-click e-stop on every page; all motion goes through a single rate-limited output path; every driver (script / bridge / audio / gamepad / composer / AI) has to claim ownership, and the UI always shows **who is currently driving the device**.

### Requirements

- Windows 10 1809+ / Windows 11 (developed on Windows 11 + .NET 10)
- .NET 10 SDK (or use a published self-contained build)
- A TCode v0.3 device (reference setup: ESP32 native USB CDC at 115200)

### Build & test

```powershell
dotnet build Hexa.csproj -c Debug          # build
dotnet run --project tests/Hexa.CoreTests  # unit tests (262 checks)
./tools/release.ps1                        # build -> tests -> self-test -> package into dist_hexa/
```

### Quick start

1. Open Hexa -> **Settings** -> pick the serial port -> Connect. If it fails, hit Refresh devices to see whether another program holds the port.
2. To play a game: **Playground -> Game bridge** -> enable it, then point the game's Buttplug/Intiface address at `ws://127.0.0.1:12345`. **Start Hexa first, then the game.**
3. To play directly: load a funscript in the **Script library**, or enable the safety switch on the **Manual axes** page and drag the sliders.

### Troubleshooting

| Symptom | What to do |
|---|---|
| Device suddenly stops; log full of write timeouts | **Unplug and replug the USB cable**, then click Center all in the sidebar to unlock the output. A software reset does not recover it. |
| UI says output locked | Same: Center all (it asks once) clears the e-stop and re-enables output. |
| Game is connected but the device does not move | Check the bridge command monitor: if the game only sends `VibrateCmd`, note that this device has no vibrator. Use content that sends `LinearCmd` / `RotateCmd`, or enable receive-raw-TCode in the test lab. |

### Screen-follow needs a model download

The screen-follow feature runs local ONNX inference and **no model ships with this repo**: download it from Test lab -> Screen signal. The pose model is `Xenova/yolov8n-pose`; upstream Ultralytics YOLOv8 weights are **AGPL-3.0**, and we do not redistribute the weights. See `THIRD-PARTY-LICENSES.md`.

### Known limitations (and where help is most welcome)

1. **The device profile is hard-coded** — axis names, pins and the advertised device name (`OSR6 6-Axis`) are baked in, and only TCode v0.3 position-servo devices are supported. **The single most wanted PR: make the device profile configurable.**
2. **Validated on one machine and one device only** — multi-client arbitration and hot-plug scenarios still need real-hardware regression.
3. **No installer** — self-contained folder plus a desktop shortcut; no versioning, signing or auto-update yet.
4. **Protocol coverage** — `ScalarCmd`, `RawWriteCmd` and `SensorReadCmd` are not implemented yet (they return an explicit `Error`, never a silent drop).
5. **The UI is Chinese-only for now** (an English build is planned).

### Safety

- This software **moves real machinery**. Use it with no people or obstacles nearby and a way to cut power.
- Every device-driving switch defaults to off; the e-stop is reachable from every page (including the floating overlay).
- Narrow the travel limits first (Settings -> Axis limits).

### License

MIT — see `LICENSE`. Third-party notices (the SR6 mesh and kinematics come from osr-emu, etc.) are in `THIRD-PARTY-LICENSES.md`.

### Contributing

Issues and PRs are very welcome — especially the device-profile work above. When reporting a bug, please attach `%LOCALAPPDATA%\Hexa\app.log` and the steps to reproduce.
