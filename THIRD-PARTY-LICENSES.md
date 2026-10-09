# 第三方许可声明（THIRD-PARTY LICENSES）

本仓库包含或依赖以下第三方作品。按各自许可要求保留其版权声明。

## 一、随仓库分发的代码与数据

### osr-emu（MIT）
- 用途：SR6 六轴设备的 3D 网格（`Assets/Models/Sr6/*.obj`）、正/逆运动学移植（`Services/Sr6/Sr6Rig.cs`、`Sr6Assets.cs`）、黄金回归数据（`tests/Hexa.CoreTests/sr6_golden.json`）
- 来源：https://github.com/ayvajs/osr-emu （模型作者 soritesparadox）
- 许可：MIT License

```
MIT License

Copyright (c) 2021 ayvajs

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 二、NuGet 依赖（均为 MIT）

| 包 | 用途 |
|---|---|
| CommunityToolkit.Mvvm | MVVM 基础设施 |
| HelixToolkit.Wpf | 3D 预览视口 |
| Microsoft.ML.OnnxRuntime（含 native onnxruntime.dll） | 本机 ONNX 推理 |
| NAudio | 音频采集与播放 |
| System.IO.Ports / System.Speech | 串口与中文语音（.NET 运行时库） |

完整版本号见 `Hexa.csproj`。

## 三、运行时不随仓库分发的模型（用户按需下载，务必注意许可）

| 模型 | 用途 | 许可 | 说明 |
|---|---|---|---|
| `Xenova/yolov8n-pose`（ONNX 量化版） | 「画面跟随」的姿态检测 | **AGPL-3.0**（上游 Ultralytics YOLOv8 训练权重） | 本仓库**不包含**该权重，程序在你点「下载」时从 Hugging Face 镜像取回；**AGPL 的义务随使用者对模型的使用方式而定**。若你要在闭源/商业场景使用姿态功能，请自行替换为许可更宽松的模型 |
| `OwenElliott/image-safety-classifier-{s,m,l}` | 画面内容分类（可选） | 以模型页标注为准（作者注释称 MIT，请以 Hugging Face 模型卡为准） | 同上，不随仓库分发 |

## 四、协议与规范

- Buttplug v3 协议与 Intiface® 为相应权利人商标；本项目的 WebSocket 服务是**独立实现**，不含其代码。
- TCode 协议（v0.3）为社区约定，本项目按其公开格式实现。

## 五、待作者确认的来源

- `Assets/icon.ico`：程序图标（若来自图标站请在此补充来源与许可）。
- `Assets/Models/Sr6/sr6_modcase.obj`、`sr6_link.obj`：与 osr-emu 的 10 个几何模块不完全对应，请补充出处。