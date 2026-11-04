# 字体与第三方资源许可（Hexa）

> 结论先说：**本仓库与发布目录里没有任何字体文件**，界面只用字体名去调用 Windows 自带的字体。
> 因此不存在「把字体文件随软件分发」这件事，也就不涉及字体再分发授权。

审计时间：2026-11-04　审计范围：`Hexa/`（源码）+ `dist_hexa/`（发布）

## 1. 事实核查

```
glob **/*.{ttf,otf,woff,woff2}   → 源码树 0 个、dist_hexa 0 个
glob dist_hexa/**/LICENSE|NOTICE → 0 个
Assets 里只有 12 个 .obj 模型（最大 0.55 MB）与 icon.ico
```

## 2. 界面实际引用的字体

| 用在哪 | 字体名（WPF 里的写法） | 归属 | 授权状态 |
|---|---|---|---|
| 全局默认 `App.xaml:42` `App.xaml:478` | `Segoe UI Variable Text, Segoe UI, sans-serif` | Microsoft | 专有字体，随 Windows 分发；**不单独商用授权、不可再分发字体文件**；在 Windows 应用界面里按名字使用是允许的 |
| 等宽数字/日志（多个 View） | `Consolas` / `Consolas, Cascadia Mono, monospace` | Microsoft / Microsoft | 同上；Cascadia Mono 是 **SIL OFL 1.1**，可自由内嵌 |
| 脚本库网页 `Services/LibraryWebService.cs:203` | `'Segoe UI','Microsoft YaHei',sans-serif` | Microsoft | 同上（网页只是引用名字，未内嵌字体文件） |
| 中文回退 | 由系统按语言链回退到微软雅黑等 | Microsoft | 同上 |

出处：
- Cascadia Code/Mono 许可证原文：<https://raw.githubusercontent.com/microsoft/cascadia-code/main/LICENSE>（"licensed under the SIL Open Font License, Version 1.1"）
- 微软官方对 Segoe 商用的答复：<https://learn.microsoft.com/en-us/answers/questions/5622419/sego-font-for-commercial-use>（"proprietary typeface owned by Microsoft … free to use Segoe within Microsoft products (documents, presentations, user interfaces)"）

**结论**：以「只用字体名」的方式使用，随包分发不产生字体授权问题；这是 Windows 桌面应用的常规做法。

## 3. 以后要注意的规矩

1. **禁止**把 `C:\Windows\Fonts` 里的字体（segoeui.ttf、consola.ttf、msyh.ttc 等）复制进 `Assets/` 或 `dist_hexa/` —— 那才是「分发字体文件」，需要单独授权。
2. 要新增字体，只能用 SIL OFL / Apache-2.0 / CC0 这类可商用可再分发的，并把许可证文件一起放进来（例如 `Assets/Fonts/OFL.txt`）。
3. 真要摆脱专有字体依赖（跨平台或保证字形一致），推荐内嵌：
   - 中文：**Noto Sans SC** 或 **思源黑体 Source Han Sans**（均为 SIL OFL 1.1）
   - 等宽：**Cascadia Mono** / **JetBrains Mono**（SIL OFL 1.1）
   内嵌后在 `App.xaml` 的 FontFamily 里写成 `pack://application:,,,/Assets/Fonts/#字体族名`，并保留系统字体作为回退。
4. 网页（LibraryWebService 生成的那几个页面）同理：只用字体名，不要 `@font-face` 指向本地字体文件。

## 4. 第三方依赖许可（随包 DLL）

| 组件 | 版本 | 许可证 |
|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| HelixToolkit.Wpf | 3.1.2 | MIT |
| Microsoft.ML.OnnxRuntime | 1.30.0 | MIT |
| NAudio | 3.1.0 | MIT |
| System.IO.Ports | 10.0.12 | MIT |
| System.Speech | 10.0.12 | MIT |

均为 MIT，允许商用与再分发（分发时保留版权与许可声明即可；.NET 自带的 DLL 由 .NET 运行时许可覆盖）。
