# AI-GeekTuner

一个使用本地 Ollama 辅助分析 PC 硬件故障日志的 .NET 8 WPF 应用。

## 🧭 Overview

应用围绕一条完整但克制的本地诊断流程展开：读取 Windows 硬件信息，导入或粘贴故障日志，构造带有系统上下文的 Prompt，调用本机 Ollama，然后解析结构化诊断结果并交给 SafetyGuard 复核。程序不会自动修改 BIOS、电压、超频参数或系统保护设置。

硬件静态信息来自 WMI，实时温度、负载、频率与功耗等数据来自 LibreHardwareMonitor。传感器读取是可选能力；驱动、权限或硬件不支持时，界面会保留缺失状态，不会用模拟值填充。AI 输入和诊断历史默认保存在本机，不依赖云端账号或外部数据库。

## 📷 Screenshots

当前目录没有可公开使用的真实运行截图，因此 README 不放置生成图或界面 mockup。建议截图页面和隐私检查项记录在 [`docs/screenshots/README.md`](docs/screenshots/README.md)，发布前可在实际运行环境补充。

## Tech Stack

| 部分 | 实际使用的技术 |
| --- | --- |
| Runtime / UI | .NET 8、C#、WPF |
| UI 组织 | MVVM-style ViewModel、Command、Frame 导航 |
| 硬件信息 | WMI、`System.Management` |
| 实时传感器 | LibreHardwareMonitorLib |
| 本地 AI | Ollama `/api/tags`、`/api/chat` |
| 本地存储 | JSON 历史记录、JSON 设置、Markdown 报告 |

## 🧩 Architecture

`Views` 只负责 WPF 页面与少量生命周期事件，界面状态和操作集中在 `ViewModels`。`Services` 按硬件检测、日志读取、AI、诊断编排、安全检查、历史记录、报告导出和设置拆分；`Models` 保存这些服务之间传递的结构化数据。

```text
AIGeekTuner/
├─ Commands/             # 同步与异步 ICommand
├─ Configuration/        # Ollama、输入长度和 SafetyGuard 默认配置
├─ KnowledgeBase/        # 按日志关键词匹配的本地参考条目
├─ Models/               # 硬件、日志、诊断与安全结果
├─ Services/
│  ├─ AI/                # Ollama HTTP 调用
│  ├─ Diagnosis/         # Prompt、解析与诊断编排
│  ├─ Hardware/          # WMI 与传感器读取
│  ├─ Safety/            # 本地规则复核
│  ├─ History/           # JSON 历史记录
│  └─ Reports/           # Markdown 报告导出
├─ ViewModels/
└─ Views/
```

## 🔍 Core Workflow

诊断页接收 `.txt`、`.log` 或粘贴文本。文件读取器限制文件类型和大小，并识别 UTF-8、UTF-16 LE 与 UTF-16 BE；过长日志在进入 Prompt 前保留开头和结尾，并明确标记中间内容已省略。

诊断服务先确认本地 Ollama 可用，再把真实硬件字段、辅助系统上下文、匹配到的本地知识条目和故障日志序列化为输入。Prompt 要求模型区分 `Fact` 与 `Inference`，并只返回指定 JSON。解析器会移除常见 `<think>` 块、提取第一个有效 JSON 对象，并检查置信度、枚举和必需字段。

SafetyGuard 是本地规则层，不替代专业硬件判断。当前规则会拦截明显异常的电压建议和禁用保护机制的表达；低置信度结果若使用绝对确定性措辞，会以警告状态展示。被拒绝的结果不会在 UI 中显示建议操作。

诊断完成后可自动保存历史记录，也可导出 Markdown 报告。默认本地数据目录为 `%LOCALAPPDATA%\AI-GeekTuner`，旧版本 `%APPDATA%\AI-GeekTuner` 下的设置与历史只做一次兼容迁移。

## 🚀 Getting Started

需要 Windows 10/11、.NET 8 SDK 和本机 Ollama。默认模型为 `qwen3:8b`，默认服务地址为 `http://localhost:11434`。

```powershell
ollama pull qwen3:8b
ollama serve
```

另开终端恢复、构建并运行应用：

```powershell
dotnet restore .\AIGeekTuner.sln
dotnet build .\AIGeekTuner.sln -c Release
dotnet run --project .\AIGeekTuner\AIGeekTuner.csproj
```

若 Ollama 已由桌面应用或系统服务启动，不需要重复执行 `ollama serve`。设置页可以测试连接，并显示当前模型、超时和日志输入限制；这些参数当前来自代码中的默认配置，尚未提供 UI 编辑入口。

## Notes and Limitations

应用只支持 Windows，WMI 或硬件传感器能否返回数据取决于设备、驱动和权限。它读取文本日志，不解析二进制 Minidump，也不调用 WinDbg。内置知识库只是关键词召回的辅助上下文，不是本机检测结论。

本地模型输出具有不确定性。结构化 Prompt、JSON 校验和 SafetyGuard 能减少明显问题，但不能保证诊断正确，也不能代替厂商检测或专业维修。项目目前没有接入正式测试框架；源码中的 `*Test.cs` 是手动验证辅助类，不应视为自动化测试套件。

