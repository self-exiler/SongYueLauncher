# ComfyUI ROCm Launcher — 软件需求规格说明书（SRS）

| 项目     | 内容                                                                                                                                                                                     |
| -------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 文档版本 | v1.1                                                                                                                                                                                     |
| 日期     | 2026-10-07                                                                                                                                                                               |
| 变更说明 | v1.1：新增 3.3 解决方案结构（GUI / Core / Core.Test 三项目划分与依赖约束）；参数清单与 GPU 模板定为 Core 内嵌资源（EmbeddedResource）                                                |
| 状态     | 已评审（经 grill-me 两轮需求评审收敛）                                                                                                                                                   |
| 应用名称 | ComfyUI ROCm Launcher（暂定）                                                                                                                                                            |
| 技术栈   | C# / .NET 10 LTS + WinUI 3（Windows App SDK）                                                                                                                                            |
| 参考依据 | `docs/功能参考脚本/`（comfyui-rocm.bat、install.bat、rocm-pytorch-package-updater.bat、comfyui-rocm-updater.bat）、ComfyUI 上游 `comfy/cli_args.py`（`docs/cli_args_upstream.py`） |

---

## 1. 引言

### 1.1 目的

为 patientx 的 **comfyui-rocm 整合包**（Windows + AMD ROCm）提供一个图形化启动器，替代手写批处理的启动、参数配置、日志查看与环境维护工作。

### 1.2 范围

- **包含**：启动/停止 ComfyUI、启动参数配置、环境变量配置、实时日志、ROCm/PyTorch 组件更新、通用 Python 依赖安装、整合包程序更新。
- **不包含**：Python 环境的创建（用户自行用 uv 构建）、ComfyUI 本体的首次安装、NVIDIA/DirectML/CPU 等其他后端的支持、custom_nodes 的自由管理（仅更新脚本追踪的 3 个节点）。

### 1.3 定义

| 术语   | 含义                                                              |
| ------ | ----------------------------------------------------------------- |
| 整合包 | patientx-cfz/comfyui-rocm 仓库内容解压/更新后的 ComfyUI 目录      |
| venv   | 用户用 uv 构建的 Python 虚拟环境，默认位于`<ComfyUI目录>\.venv` |
| 模板   | 按 GPU 代际预设的一组环境变量 + 启动参数                          |
| 追踪包 | 启动前按 requirements.txt 校验的 6 个包（见 4.4.2）               |

---

## 2. 总体描述

### 2.1 产品定位

单用户、单机 Windows 桌面工具。启动器 exe 可位于任意位置，与 ComfyUI 目录解耦（全部使用绝对路径配置）。

### 2.2 运行环境

- Windows 10 1809+ / Windows 11，x64
- 已安装 comfyui-rocm 整合包（含 `main.py`、`requirements.txt`、`detect_gpu.py`）
- 用户已用 uv 创建 venv 并完成初始依赖安装
- 外部可执行依赖：`uv.exe`（环境管理）、`git.exe`（程序更新）、venv 内的 `rocm-sdk`

### 2.3 约束

- 配置文件存 `%APPDATA%\ComfyUI ROCm Launcher\config.json`；日志存 `%APPDATA%\ComfyUI ROCm Launcher\logs\`。
- 仅支持 ROCm；所有 ROCm 特有步骤（rocm-sdk init、HIP_PATH 推导、MIOpen 变量）默认启用，不做平台开关。
- 单实例运行。

### 2.4 全局状态机

```
Idle ──启动──▶ Starting ──就绪/超时──▶ Running ──停止──▶ Stopping ──退出──▶ Idle
  │                                                                      ▲
  └── 更新/安装任务 ──▶ BusyEnv ──完成/失败/取消──────────────────────────┘
```

- `Running / Starting / Stopping` 期间：禁用环境管理页全部操作。
- `BusyEnv` 期间：禁用启动按钮与环境管理页其他操作。
- 应用退出时若 ComfyUI 仍在运行，提示并执行停止流程。

---

## 3. 系统架构

### 3.1 分层

```
Views (WinUI 3 XAML)
 ├─ HomePage          主页
 ├─ ArgsPage          启动参数
 ├─ EnvVarsPage       环境变量（可并入 ArgsPage 为两个 Tab，实现时定）
 ├─ LogPage           日志
 └─ EnvManagerPage    环境管理
ViewModels (MVVM, CommunityToolkit.Mvvm)
Services
 ├─ ConfigService      config.json 读写、默认值、迁移
 ├─ ProcessService     启动/停止/进程树管理、输出重定向
 ├─ LaunchEnvBuilder   固定注入 + rocm-sdk 推导 + 模板/自定义变量合成
 ├─ ArgsBuilder        结构化参数 + 额外参数 → 命令行
 ├─ LogService         内存环形缓冲(5000行) + ANSI 解析 + 落盘 + 磁盘保护
 ├─ UpdateService      程序更新 / ROCm-PyTorch 更新 / 依赖安装（子任务化、可取消）
 ├─ GpuInfoService     WMI 读取显卡型号；调 detect_gpu.py 读 arch（环境管理用）
 └─ ToolLocator     git.exe 发现
```

### 3.2 进程模型

- ComfyUI 以子进程启动：`FileName=<python.exe>`，`Arguments=main.py <args>`，`WorkingDirectory=<ComfyUI目录>`，`UseShellExecute=false`，重定向 stdout/stderr，`CreateNoWindow=true`，`CREATE_NEW_PROCESS_GROUP`（供 CTRL_BREAK 使用）。
- 所有长任务（更新/安装）同样以子进程流式输出，输出汇入环境管理页内嵌输出区。

### 3.3 解决方案结构

```
ComfyUiRocmLauncher.sln（根目录，经典 sln 格式）
├─ src/ComfyUiRocmLauncher.GUI         WinUI 3 主程序
│    net10.0-windows10.0.26100.0，UseWinUI，单项目 MSIX 打包
│    Views（五页 XAML）/ ViewModels / App.xaml / 导航 / 文件与目录选择器等 WinRT 互操作
├─ src/ComfyUiRocmLauncher.Core        服务层类库
│    net10.0，3.1 节全部服务 + 数据模型 + 状态机
│    Resources/cli-args.json、Resources/gpu-templates.json 以 EmbeddedResource 内置
└─ test/ComfyUiRocmLauncher.Core.Test  xUnit 测试项目（net10.0），仅引用 Core
```

**约束（NFR-5 的编译级保证）**

- 依赖方向固定为 `GUI → Core ← Core.Test`，不允许反向引用或跨层引用。
- Core 不得引用任何 `Microsoft.UI.*` / `Microsoft.Windows.App.*` 类型；服务与 UI 之间一律通过 `event` / `Action<T>` 回调通信，日志与进度由 GUI 侧切回 UI 线程渲染。
- 不引入独立的 Packaging 项目（模板已启用单项目 MSIX，FR-3 的 robocopy 更新流程不依赖打包工程）；ViewModels 不独立成程序集。
- 参数清单与 GPU 模板统一放在 Core 的内嵌资源中，GUI 与单元测试共用同一份数据；发版时对照上游 `cli_args.py` 更新（参见第 8 节风险表）。
- Core 目标框架保持 `net10.0`（不带 WinUI），以保证单元测试无需 WindowsAppSDK 即可在 CI 运行。

**开发期启动方式（v1.1 补充）**

- GUI 项目默认按**免打包（unpackaged）**方式启动：`UseUnpackaged=true` 时自动设置 `WindowsPackageType=None`、`EnableMsixTooling=false`、`EnableWinAppRunSupport=false`、`WindowsAppSDKSelfContained=true`，运行时随程序输出，**无需开启系统"开发人员模式"**。开发命令：`dotnet run --project src/ComfyUiRocmLauncher.GUI/ComfyUiRocmLauncher.GUI.csproj -p:Platform=x64`。
- 发布 MSIX 时切回打包模式：`dotnet publish -p:UseUnpackaged=false`（此模式下的调试部署仍要求系统开启开发人员模式）。
- 两条路径共用同一份 `Package.appxmanifest`、`app.manifest` 与代码，切换只影响启动/部署方式，不影响 FR-6.3 的 robocopy 更新流程。

---

## 4. 功能需求

### 4.1 主页（HomePage）

**FR-1.1 路径设置**

- ComfyUI 目录：文本框 + 浏览按钮。校验：目录存在且含 `main.py`。
- Python 解释器：文本框 + 浏览按钮。默认值：`<ComfyUI目录>\.venv\Scripts\python.exe`（ComfyUI 目录变更时若用户未手动改过则联动刷新）。校验：文件存在；点击"验证"执行 `<python> --version` 显示版本。
- uv.exe：自动从 PATH 发现；失败时显示手动配置项；仍失败则环境管理页操作给出引导性报错（不内置下载）。

**FR-1.2 操作按钮**：启动、停止、打开浏览器（按状态机启停）。

**FR-1.3 状态显示**

- 运行状态指示（停止/启动中/运行中/停止中）。
- 当前版本：读 `<ComfyUI目录>\.last_update_hash` 前 10 字符，无文件显示 `unknown`。
- GPU 型号：WMI `Win32_VideoController` 中 AMD/ATI 适配器的 Name；读取失败显示"未知"。

**FR-1.4 快捷目录**（资源管理器打开，目录不存在时按钮禁用）
`output`、`models`、`user\workflow`、`custom_nodes`、`input`

### 4.2 启动参数页（ArgsPage）

**FR-2.1** 以分组结构化控件枚举上游 `main.py` 全部参数（完整清单见**附录 A**）。控件形态：

- flag 型 → ToggleSwitch
- 带值型 → TextBox / NumberBox / 下拉框（枚举型如下拉：`--preview-method`、`--default-hashing-function`、`--fast` 多选、`--verbose` 级别）
- 互斥组（附录 A 中标注）→ 同组开启一个自动关闭其余，或组内 RadioButton

**FR-2.2** 每组可折叠；页面顶部提供搜索框按参数名过滤。

**FR-2.3 额外参数**：单行自由文本，原样拼接在结构化参数之后（放最后，允许覆盖前者）。

**FR-2.4 命令预览**：只读文本实时显示最终命令行 `<python.exe> main.py <args>`。

**FR-2.5** 参数随配置持久化；提供"应用模板"与"恢复默认（全不启用）"。

### 4.3 环境变量页（EnvVarsPage）

三层结构，自上而下合成最终环境：

**FR-3.1 固定注入层**（展示为只读列表，不可删除/修改）

| 变量                         | 值                                                       |
| ---------------------------- | -------------------------------------------------------- |
| `PYTHONUTF8`               | `1`                                                    |
| `PYTHONNOUSERSITE`         | `1`                                                    |
| `PATH`                     | `<venv目录>;<venv目录>\Scripts;` 前置                  |
| `HIP_PATH` / `ROCM_PATH` | 启动时执行`<venv>\Scripts\rocm-sdk path --root` 的输出 |

**FR-3.2 模板层**：下拉选择模板（RDNA1/2、RDNA3、RDNA4，内容见**附录 B**）→"应用模板"把模板中的变量与启动参数写入普通配置，之后与自定义项无区别、可自由编辑。纯手动选择，不做 GPU 自动检测推荐。

**FR-3.3 自定义层**：key-value 表格，增删改；key 与固定层冲突时禁止保存并提示。

**FR-3.4 启动前自动步骤**（复刻 comfyui-rocm.bat）：

1. 执行 `rocm-sdk init`（失败仅警告，不阻断）；
2. 执行 `rocm-sdk path --root` 推导 `HIP_PATH`/`ROCM_PATH`（失败警告）；
3. 确保 `TRITON_CACHE_DIR`、`PYTORCH_TUNABLEOP_CACHE_DIR` 指向的目录存在（默认 `<ComfyUI目录>\triton-cache`、`<ComfyUI目录>\tunableop-cache`，作为模板层变量可改）。

### 4.4 启动 / 停止 / 浏览器

**FR-4.1 启动序列**

```
校验 python.exe、main.py
合成环境变量（FR-3）
若启用"启动前同步追踪包"（默认开）：对 6 个追踪包执行
   uv pip install <pkg>==<requirements.txt中版本>（版本一致则跳过）
   追踪包 = comfyui-frontend-package, comfyui-workflow-templates,
            comfyui-embedded-docs, comfy-kitchen, comfy-aimdo, av
spawn: <python.exe> main.py <ArgsBuilder输出>
日志开始落盘；状态 → Starting
检测到就绪标志（日志出现 "To see the GUI go to" 或端口可连接）→ Running
   若开启"启动后自动打开浏览器"则打开浏览器
```

**FR-4.2 停止序列**

```
附加到子进程组发送 CTRL_BREAK_EVENT
等待最多 5 秒（写死）
未退出 → 强杀整个进程树（taskkill /PID <pid> /T /F 等价实现）
状态 → Idle
```

**FR-4.3 浏览器地址推导**

- 从结构化参数与额外参数文本解析 `--port`（默认 8188）、`--listen`（默认 127.0.0.1）；
- `--listen` 无值（const `0.0.0.0,::`）或为 `0.0.0.0`/`::` 时，浏览器地址用 `127.0.0.1`；
- 同时配置 `--tls-keyfile` 与 `--tls-certfile` 时用 `https://`；
- "启动后自动打开浏览器"开关默认关（ComfyUI 自身 `--auto-launch` 参数不在此处干预）。

### 4.5 日志页（LogPage）

**FR-5.1** 实时滚动显示子进程 stdout/stderr 合并流；ANSI 颜色码解析为彩色文本渲染。
**FR-5.2** 内存环形缓冲上限 5000 行，超出丢弃最旧。
**FR-5.3** 每次启动 ComfyUI 落盘一份日志：`logs\comfyui_yyyy-MM-dd_HH-mm-ss.log`（写入原始文本，含 ANSI 码可接受）。
**FR-5.4 磁盘保护**：仅保留最近 30 份；且总大小超过 200 MB 时从最旧开始删除直至达标。
**FR-5.5** 按钮：清空、复制全部、打开日志目录、自动滚动开关。

### 4.6 环境管理页（EnvManagerPage）

页面上方三个任务卡片，下方内嵌输出区（等宽字体、滚动、清屏按钮）；任务进行中显示进度状态，支持取消（取消 = 终止当前子进程）。

**FR-6.1 ROCm / PyTorch 更新**（复刻 rocm-pytorch-package-updater.bat）

```
1. 校验 python.exe 存在
2. 执行 detect_gpu.py 取 arch（stderr 落 gpu_detect_debug.log；失败中止并展示）
3. 组装卸载清单：rocm rocm-sdk-devel rocm-sdk-core rocm-sdk-libraries
   torch torchaudio torchvision + pip freeze 中匹配 ^rocm/^amd-torch/^amd-torchvision 的包
   → uv pip uninstall <清单>
4. 按 arch 分支安装：
   - gfx942 / gfx950（保留分支，模板不做）：rocm[devel,libraries] 与 torch 系
     使用 https://rocm.nightlies.amd.com/v2-staging/<arch>-dcgpu/
   - gfx1250 / gfx110x / gfx115x：提示"暂无可用 PyTorch 构建"，中止
   - 其他：uv pip install "torch[device-<arch>]" "torchvision[device-<arch>]"
           torchaudio rocm-sdk-devel --pre
           --index-url https://nightly.repo.amd.com/rocm/whl-next/
5. rocm-sdk init；rocm_kpack.dll 缺失时从 _rocm_sdk_core 复制到 _rocm_sdk_devel
6. 验证：python -c 输出 torch 版本 / ROCm 可用性 / HIP 版本 / 设备名
```

注：所有 pip 操作统一为 `uv pip ... --python <python.exe>`。

**FR-6.2 通用依赖安装**

- 从 `requirements.txt` 过滤掉 `torch`/`torchvision`/`torchaudio` 行（正则 `^\s*(torch|torchvision|torchaudio)\s*([=<>!~]|$)`，忽略大小写），生成临时 `requirements_filtered.txt`；
- `uv pip install -r requirements_filtered.txt --python <python.exe>`；
- 完成后对比前后 `pip freeze`，输出变更包列表（`pkg: old -> new`）；
- 支持使用国内镜像源加速。

**FR-6.3 程序更新**（复刻 comfyui-rocm-updater.bat，robocopy 覆盖模式）

- 更新源：`https://github.com/patientx-cfz/comfyui-rocm`
- 模式选择（RadioButton）：**仅代码** / **代码 + Python 包**（默认仅代码）

```
公共步骤：
1. 校验 git.exe、python.exe
2. git ls-remote <repo> HEAD 取远程 hash；读 .last_update_hash 比对
   一致 → 跳过核心更新，直接进入可选步骤
3. git clone --depth 1 到 <ComfyUI目录>\_update_temp
4. robocopy 覆盖：<temp> → <ComfyUI目录> /E
   排除目录：.venv、models、output、input、user、custom_nodes、_update_temp
   排除文件：comfyui-user.bat
   （robocopy 退出码 0–7 视为成功，≥8 报错）
5. 清理 _update_temp；写入 .last_update_hash
"仅代码"追加：
6. 更新 3 个追踪 custom_nodes（存在 .git → git pull --ff-only；
   目录存在但非 git → 跳过并提示；不存在 → git clone）：
   ComfyUI-Manager、ComfyUI-INT8-Fast-ROCM、comfyui-h3-sla-attention-rocm
"代码 + Python 包"再追加：
7. sageattention wheel 强制更新（--force-reinstall --no-deps）：
   arch=gfx1201 → thehybrid1337/sageattention-rocm-gfx1201-win releases latest
   其他        → patientx/sageattention-autotune releases latest
8. 执行 FR-6.2 的过滤 requirements 安装
```

- updater 自身的"脚本自更新 relaunch"逻辑不迁移（本启动器通过应用商店/侧载包自身更新）。

**FR-6.4 运行中保护**：状态机非 Idle 时本页全部操作禁用（含反向：BusyEnv 时禁止启动 ComfyUI）。

---

## 5. 数据模型

### 5.1 config.json（`%APPDATA%\ComfyUI ROCm Launcher\config.json`）

```json
{
  "version": 1,
  "comfyUiDir": "D:\\AI\\comfyui-rocm",
  "pythonExe": "D:\\AI\\comfyui-rocm\\.venv\\Scripts\\python.exe",
  "uvExe": null,
  "autoOpenBrowser": false,
  "syncTrackedPackagesOnStart": true,
  "args": {
    "structured": { "port": 8188, "cache-lru": 20, "enable-manager": true },
    "extra": ""
  },
  "envVars": {
    "custom": { "MIOPEN_FIND_MODE": "2" }
  },
  "log": { "retainCount": 30, "retainTotalMB": 200 }
}
```

- 启动时读取，保存时原子写入（临时文件 + 替换）；解析失败备份为 `config.broken.json` 并用默认值重建。
- `args.structured` 仅持久化"非默认"的参数。

### 5.2 其他文件

- `<ComfyUI目录>\.last_update_hash`：整合包版本 hash（只读 + 更新后写入）。
- `logs\comfyui_yyyy-MM-dd_HH-mm-ss.log`：会话日志。

---

## 6. 外部接口

| 依赖                            | 用途                                           | 发现/容错                              |
| ------------------------------- | ---------------------------------------------- | -------------------------------------- |
| `uv.exe`                      | 全部 pip 操作（`uv pip ... --python <exe>`） | PATH → 手动配置 → 环境管理页报错引导 |
| `git.exe`                     | 程序更新（ls-remote/clone/pull）               | PATH；缺失时该任务禁用并提示安装 Git   |
| `<venv>\Scripts\rocm-sdk.exe` | `init` 与 `path --root`                    | 缺失时警告并跳过推导                   |
| `<ComfyUI目录>\detect_gpu.py` | arch 检测（环境管理）                          | 缺失时对应任务报错                     |
| WMI`Win32_VideoController`    | 主页 GPU 型号                                  | 失败显示"未知"                         |

---

## 7. 非功能需求

- **NFR-1 性能**：UI 线程不阻塞；日志渲染 5000 行内滚动流畅（虚拟化列表）；启动器冷启动 < 3s。
- **NFR-2 可用性**：界面中文；深色/浅色跟随系统；所有危险操作（更新、强杀）有状态反馈；报错信息给出可执行的下一步引导。
- **NFR-3 可靠性**：子进程输出读取异常不崩溃；配置损坏可重建；更新任务中断后目录不留半成品（\_update_temp 可重入清理）。
- **NFR-4 安全/隐私**：无外传数据；网络访问仅限 GitHub、AMD nightly 源、PyPI（经 uv）。
- **NFR-5 可维护性**：MVVM 分层；服务层无 UI 依赖可单测；参数清单（附录 A）以数据驱动方式维护，便于跟随上游 ComfyUI 变更。

---

## 8. 风险与缓解

| 风险                                 | 影响         | 缓解                                                                                           |
| ------------------------------------ | ------------ | ---------------------------------------------------------------------------------------------- |
| MSIX 下附加控制台发 CTRL_BREAK 受限  | 优雅停止失败 | 始终有 5s 后强杀兜底；实现时优先验证 AttachConsole/FreeConsole + GenerateConsoleCtrlEvent 方案 |
| 上游 ComfyUI 参数变更                | 参数页过时   | 参数清单数据驱动（内置 JSON），发版时同步`cli_args.py`                                       |
| AMD nightly 源 URL 变动              | 更新失败     | URL 集中在常量/配置，错误信息含实际 URL 便于排查                                               |
| robocopy 覆盖时文件被占用            | 更新不完整   | 运行中禁止更新；robocopy 退出码 ≥8 明确报错                                                   |
| uv 版本差异导致 pip 子命令参数不兼容 | 环境管理失败 | 验证步骤输出 uv 版本；文档注明最低 uv 版本                                                     |
| 用户 venv 无 pip 且 uv 缺失          | 无法装依赖   | 报错引导安装 uv（winget install astral-sh.uv）                                                 |

---

## 附录 A：启动参数完整清单

来源：ComfyUI 上游 `comfy/cli_args.py`（2026-10 快照，`docs/cli_args_upstream.py`）。分组即页面分组；`[互斥N]` 表示同一 mutually exclusive 组。**加粗** = comfyui-rocm.bat 默认启用。

**网络**

| 参数                 | 类型          | 默认                           | 说明                        |
| -------------------- | ------------- | ------------------------------ | --------------------------- |
| --listen             | 可选值 IP     | 127.0.0.1（无值时 0.0.0.0,::） | 监听地址，逗号分隔多地址    |
| --port               | int           | 8188                           | 监听端口                    |
| --tls-keyfile        | str           | —                             | TLS 私钥（需配合 certfile） |
| --tls-certfile       | str           | —                             | TLS 证书                    |
| --enable-cors-header | 可选值 ORIGIN | 无（无值时 *）                 | 开启 CORS                   |
| --max-upload-size    | float         | 100                            | 上传大小上限 MB             |

**目录**

| 参数                                                      | 类型          | 说明                                                     |
| --------------------------------------------------------- | ------------- | -------------------------------------------------------- |
| --base-directory                                          | str           | 覆盖 models/custom_nodes/input/output/temp/user 的基目录 |
| --extra-model-paths-config                                | str（可多次） | 加载 extra_model_paths.yaml                              |
| --output-directory / --temp-directory / --input-directory | str           | 分别覆盖对应目录                                         |
| --user-directory / --models-directory                     | str（须存在） | 覆盖 user / models 目录                                  |

**设备与后端**

| 参数                                                        | 类型         | 说明                                 |
| ----------------------------------------------------------- | ------------ | ------------------------------------ |
| --cuda-device                                               | str          | 可见设备 id 列表或 all               |
| --default-device                                            | int          | 默认设备 id                          |
| --cuda-malloc / --disable-cuda-malloc                       | flag [互斥1] | cudaMallocAsync 开关                 |
| --directml                                                  | 可选 int     | torch-directml（本项目不用，仅枚举） |
| --oneapi-device-selector                                    | str          | oneAPI 设备（同上）                  |
| --supports-fp8-compute                                      | flag         | 声称支持 fp8 计算                    |
| --enable-triton-backend /**--disable-triton-backend** | flag         | comfy-kitchen Triton 后端开关        |

**精度**

| 参数                                                                                                             | 类型         | 说明                        |
| ---------------------------------------------------------------------------------------------------------------- | ------------ | --------------------------- |
| --force-fp32 / --force-fp16                                                                                      | flag [互斥2] | 强制全局精度                |
| --fp32-unet / --fp64-unet / --bf16-unet / --fp16-unet / --fp8_e4m3fn-unet / --fp8_e5m2-unet / --fp8_e8m0fnu-unet | flag [互斥3] | UNet 精度                   |
| --fp16-vae / --fp32-vae / --bf16-vae                                                                             | flag [互斥4] | VAE 精度                    |
| --cpu-vae                                                                                                        | flag         | VAE 跑 CPU                  |
| --fp8_e4m3fn-text-enc / --fp8_e5m2-text-enc / --fp16-text-enc / --fp32-text-enc / --bf16-text-enc                | flag [互斥5] | 文本编码器精度              |
| --fp16-intermediates                                                                                             | flag         | 节点间中间张量 fp16（实验） |
| --force-channels-last                                                                                            | flag         | channels_last 推理          |

**缓存与内存**

| 参数                              | 类型                     | 说明                              |
| --------------------------------- | ------------------------ | --------------------------------- |
| --cache-ram                       | 0~2 个 float             | RAM 压力缓存（默认模式），阈值 GB |
| --cache-classic / --cache-none    | flag [互斥6]             | 旧式缓存 / 不缓存                 |
| **--cache-lru**             | int [互斥6]（bat 值 20） | LRU 缓存 N 条结果                 |
| --high-ram                        | flag [互斥6]             | 高内存模式                        |
| --reserve-vram                    | float                    | 为系统保留显存 GB                 |
| --vram-headroom                   | float                    | DynamicVRAM 额外余量 GB           |
| --disable-nvml-pressure           | flag                     | 用 CUDA 代替 NVML 测压            |
| **--disable-smart-memory**  | flag                     | 激进 offload 到内存               |
| **--disable-pinned-memory** | flag                     | 禁用 pinned memory                |

**显存模式**

| 参数                                                   | 类型                  | 说明             |
| ------------------------------------------------------ | --------------------- | ---------------- |
| --gpu-only / --highvram / --lowvram / --novram / --cpu | flag [互斥7]          | 显存使用档位     |
| --enable-dynamic-vram / --disable-dynamic-vram         | flag（bat 用 enable） | 动态显存         |
| --async-offload                                        | 可选 int（默认2）     | 异步权重卸载流数 |
| --disable-async-offload                                | flag                  | 关异步卸载       |
| --fast-disk / --disable-fast-disk                      | flag                  | 磁盘后备动态加载 |

**注意力**

| 参数                                                                                                                                                         | 类型         | 说明                                            |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------ | ----------------------------------------------- |
| --use-split-cross-attention / --use-quad-cross-attention / --use-pytorch-cross-attention / --use-sage-attention / --use-flash-attention / --use-ck-attention | flag [互斥8] | 注意力实现（bat：RDNA1/2 用 quad，RDNA4 用 ck） |
| --disable-xformers                                                                                                                                           | flag         | 禁用 xformers                                   |
| --force-upcast-attention / --dont-upcast-attention                                                                                                           | flag [互斥9] | 注意力 upcast                                   |

**Manager 与节点**

| 参数                                                       | 类型          | 说明                             |
| ---------------------------------------------------------- | ------------- | -------------------------------- |
| **--enable-manager**                                 | flag          | 启用 ComfyUI-Manager             |
| --disable-manager-ui /**--enable-manager-legacy-ui** | flag [互斥10] | Manager UI 形态                  |
| --disable-all-custom-nodes                                 | flag          | 禁用全部自定义节点               |
| --whitelist-custom-nodes                                   | str（多个）   | 白名单节点                       |
| --disable-partner-nodes                                    | flag          | 禁用付费 API 节点                |
| --offline                                                  | flag          | 前端断网（CSP）                  |
| **--disable-api-nodes**                              | flag          | --offline 的废弃别名（bat 在用） |

**性能与编译**

| 参数                                                                     | 类型                                                                      | 说明                               |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------- | ---------------------------------- |
| --disable-cuda-graphs / --disable-comfy-compiler / --assert-graph-breaks | flag                                                                      | CUDA graph / 编译器控制            |
| --force-non-blocking                                                     | flag                                                                      | 非阻塞张量操作                     |
| --fast                                                                   | 0~n 个枚举（fp16_accumulation / fp8_matrix_mult / cublas_ops / autotune） | 实验性优化                         |
| --deterministic                                                          | flag                                                                      | 确定性算法                         |
| --debug-hang                                                             | flag                                                                      | Ctrl-C 时 dump 栈                  |
| --mmap-torch-files / --disable-mmap                                      | flag                                                                      | ckpt/pt mmap / safetensors 禁 mmap |

**日志与其他**

| 参数                                     | 类型                       | 说明                                              |
| ---------------------------------------- | -------------------------- | ------------------------------------------------- |
| --verbose                                | 可重复；LEVEL [FILE]       | 日志级别 DEBUG/DETAIL/INFO/WARNING/ERROR/CRITICAL |
| --log-stdout                             | flag                       | 输出走 stdout                                     |
| --dont-print-server                      | flag                       | 不打印服务输出                                    |
| --quick-test-for-ci                      | flag                       | CI 快测                                           |
| --windows-standalone-build               | flag                       | 独立版便利行为                                    |
| --auto-launch / --disable-auto-launch    | flag                       | 自动开浏览器（启动器侧另有开关，互不影响）        |
| --disable-metadata                       | flag                       | 不存 prompt 元数据                                |
| --multi-user                             | flag                       | 多用户存储                                        |
| --front-end-version                      | str                        | 前端版本 repoOwner/repoName@version               |
| --front-end-root                         | str（须存在）              | 本地前端目录                                      |
| --enable-compress-response-body          | flag                       | 压缩响应体                                        |
| --comfy-api-base                         | str                        | API base URL                                      |
| --database-url                           | str                        | 数据库 URL                                        |
| --enable-assets / --enable-asset-hashing | flag                       | 资产系统                                          |
| --feature-flag                           | KEY[=VALUE]（可多次）      | 服务器特性开关                                    |
| --list-feature-flags                     | flag                       | 打印特性开关并退出                                |
| --default-hashing-function               | md5/sha1/sha256/sha512     | 文件哈希算法                                      |
| --preview-method                         | none/auto/latent2rgb/taesd | 采样预览方式                                      |
| --preview-size                           | int（默认512）             | 预览尺寸                                          |

> 实现方式：固化为 Core 内嵌资源 `Resources/cli-args.json`（EmbeddedResource，见 3.3）驱动 UI，发版时对照上游 `cli_args.py` 更新。

## 附录 B：GPU 模板定义

**B.1 三模板共有环境变量**

```
COMFYUI_ENABLE_MIOPEN=0
FLASH_ATTENTION_TRITON_AMD_ENABLE=TRUE
MIOPEN_FIND_ENFORCE=1
MIOPEN_FIND_MODE=2
MIOPEN_DEBUG_DISABLE_FIND_DB=0
MIOPEN_SEARCH_CUTOFF=1
MIOPEN_ENABLE_LOGGING=0
MIOPEN_LOG_LEVEL=0
MIOPEN_ENABLE_LOGGING_CMD=0
TRITON_PRINT_AUTOTUNING=0
TRITON_CACHE_AUTOTUNING=0
TRITON_CACHE_DIR=<ComfyUI目录>\triton-cache
PYTORCH_TUNABLEOP_CACHE_DIR=<ComfyUI目录>\tunableop-cache
MIOPEN_SYSTEM_DB_PATH=<venv>\Lib\site-packages\_rocm_sdk_devel\bin
ROCBLAS_TENSILE_DB_PATH=<venv>\Lib\site-packages\_rocm_sdk_devel\bin\rocblas
ROCBLAS_TENSILE_LIBPATH=<venv>\Lib\site-packages\_rocm_sdk_devel\bin\rocblas\library
```

（RDNA3/gfx1100 按原脚本不设 MIOPEN_SYSTEM_DB_PATH 与 ROCBLAS_* 三项——模板应用时按对应代际处理。）

**B.2 RDNA1/2（gfx101x / gfx103x）**

- 追加环境变量：`TORCH_BACKENDS_CUDA_FLASH_SDP_ENABLED=0`、`TORCH_BACKENDS_CUDA_MEM_EFF_SDP_ENABLED=0`、`TORCH_BACKENDS_CUDA_MATH_SDP_ENABLED=1`
- 参数：基础参数组 + `--use-quad-cross-attention`

**B.3 RDNA3（gfx110x）**

- 追加环境变量：`TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL=1`
- 参数：基础参数组

**B.4 RDNA4（gfx120x）**

- 追加环境变量：`TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL=1`
- 参数（按原脚本整组替换）：`--enable-manager --enable-manager-legacy-ui --use-ck-attention`

**B.5 基础参数组**（RDNA1/2、RDNA3 模板用）

```
--disable-api-nodes --cache-lru 20 --disable-smart-memory --disable-pinned-memory
--enable-manager --enable-manager-legacy-ui --disable-triton-backend --enable-dynamic-vram
```

## 附录 C：参考脚本 → 功能映射

| 脚本                             | SRS 条目                                                      |
| -------------------------------- | ------------------------------------------------------------- |
| comfyui-rocm.bat                 | FR-3（环境变量/模板）、FR-4.1（启动序列/追踪包同步）、附录 B  |
| rocm-pytorch-package-updater.bat | FR-6.1                                                        |
| comfyui-rocm-updater.bat         | FR-6.3                                                        |
| install.bat                      | 不迁移（环境由用户用 uv 自建）；其验证思路用于 FR-6.1 第 6 步 |
