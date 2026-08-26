# AIGeekTuner

基于 .NET 8 WPF 与本地 Ollama 的 AI 硬件故障诊断工具。

导入故障日志，结合本机真实硬件信息，由本地大模型生成结构化诊断结论——全部计算在本机完成，不需要任何云端 API Key。

## Features

- 硬件信息采集（CPU / GPU / 内存 / 磁盘 / 操作系统，基于 WMI）
- 实时传感器读取（温度 / 功耗 / 频率，基于 LibreHardwareMonitor，可选能力）
- 故障日志导入（.txt / .log，UTF-8 / UTF-16 / GB18030 自动识别）
- 本地 Ollama 结构化诊断（JSON mode + 解析校验 + 一次自动修复）
- SafetyGuard 安全复核（危险电压 / 危险操作 / 过度自信拦截）
- 证据分层展示（事实 Fact 与推测 Inference 分开）
- 设置持久化（服务地址 / 模型 / 超时 / 输入上限等，保存后立即生效）
- 历史记录（成功与失败均留痕：模型、耗时、失败原因）+ Markdown 报告导出
- 本地优先：默认诊断流程完全在本机完成

## Architecture

```mermaid
flowchart TD
    A[UI: WPF Pages] --> B[DiagnosisViewModel]
    B --> C[Hardware Detection WMI]
    B --> D[Fault Log Reader]
    B --> E[System Context]
    B --> F[Knowledge Keywords]
    B --> G[DiagnosisService]
    G --> H[Readiness Preflight]
    G --> I[DiagnosisPromptBuilder]
    I --> J[Ollama /api/chat JSON mode]
    J --> K[JSON Parser + Validate]
    K -- parse fail --> L[One-time Repair Retry]
    L --> K
    K --> M[SafetyGuard Rules]
    M --> N[Result Page]
    N --> O[History JSON + Markdown Export]
```

## Reliability & Safety

### Anti-hallucination

Prompt 明确要求：AI 只能引用日志原文或真实采集到的硬件字段作为“事实”，禁止编造未采集的温度、电压、功耗、BIOS、超频状态；推测必须标注为 Inference 并给出验证建议；证据不足时输出固定句式并降低 confidence。

### Structured Output

- 强类型 DiagnosticResult schema + 必填字段 / 枚举 / confidence 范围校验
- Ollama 原生 `format: "json"` 模式
- 平衡扫描器从模型输出中提取首个合法 JSON 对象（容忍 code fence / 前后噪声 / think 标签）
- 校验失败自动发起最多一次 repair 重试，携带原输出与结构错误反馈

### SafetyGuard

本地规则层对 AI 结论复核：

- 异常电压建议（如 >1.70V）→ 拦截
- 禁用保护机制 / 绕过安全限制类建议 → 拦截
- 低置信度 + 绝对化措辞 → 警告展示

### Failure handling

Ollama 未启动、模型缺失、请求超时、AI 输出无效、settings.json / records.json 损坏——全部有明确的用户提示与自愈策略，不会崩溃或永久不可用。

## Optional Telemetry Sources (V2-M1)

AIGeekTuner 可读取可用的外部硬件监控数据，并保留数据来源；外部软件为可选项，未安装时内置传感器功能完全正常。

| 来源 | 方式 | 说明 |
| --- | --- | --- |
| LibreHardwareMonitor | 内置 | 默认来源，无需额外安装 |
| AIDA64 | WMI（Root\WMI\AIDA64_SensorValues） | 可选；需在 AIDA64 External Applications 中启用 |
| HWiNFO | Shared Memory（7.0+ SM2 接口，官方已完全公开） | 可选；需用户安装 HWiNFO 并启用 Shared Memory Support。免费版连续共享约 12 小时后自动停用、需手动重开；AIGeekTuner 不捆绑 HWiNFO，也不会以任何方式规避该时限 |

- AIDA64 / HWiNFO 需用户自行安装、启动并启用数据导出；AIGeekTuner 不捆绑、不下载、不自动修改它们的设置。
- 同一物理设备的识别基于证据（强 ID / 单例 / 归一化名称唯一匹配）；证据不足的设备保留来源本地身份，不做跨源回退。
- 同一指标按固定优先级（HWiNFO → AIDA64 → LibreHardwareMonitor）选择单一来源，不做多源平均，并保留来源溯源。
- Hardware 页与 Settings 页可查看各数据源状态。

## Tests

运行：

```bash
dotnet test
```

共 182 个自动化测试，覆盖：JSON Parser、Prompt 构建、SafetyGuard 规则、Ollama repair 与 JSON mode、设置持久化、运行时快照隔离、FileReader 编码、History 存储与损坏恢复、页面构造冒烟，以及 V2-M1 遥测域 / 单位归一 / AIDA64 映射与 Provider 状态机 / TelemetryHub 优先级回退。未声明覆盖率指标。

## Quick Start

### Requirements

- Windows 10 / 11
- [Ollama](https://ollama.com) 已安装并运行
- 推荐模型：`ollama pull qwen3:8b`
- .NET 8 Desktop Runtime（仅 framework-dependent 发布产物需要）

### 从源码运行

```bash
git clone <repo>
cd AIGeekTuner
dotnet run --project AIGeekTuner/AIGeekTuner.csproj
```

### 发布产物

发布与演示说明见 [docs/DEMO.md](docs/DEMO.md)。

## Privacy

默认诊断流程完全在本机完成：硬件信息、日志文本与推理请求都只发送给本机 Ollama 服务，不要求云端 API Key。操作系统与用户环境本身不在项目可控范围内。

## Limitations

- 当前仅支持 Ollama Provider
- 诊断建议为辅助参考，不替代专业硬件维修
- WMI / LibreHardwareMonitor 的部分字段取决于硬件、驱动与管理员权限，读不到就以“未检测到”呈现，不会伪造
- 不执行任何 BIOS / 超频 / 电压修改操作
- 不解析 Minidump 二进制文件
- AI 可能判断错误：请结合 confidence、事实与推测分区自行判断
- V2 后续模块（Recorder / PresentMon 会话 / 语音总结等）尚未实现
