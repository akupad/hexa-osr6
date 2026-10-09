# Hexa · OSR6 六轴控制器（Windows / WPF）

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

MIT License，见 `LICENSE`。第三方协议与素材的归属见各文件头部注释。

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