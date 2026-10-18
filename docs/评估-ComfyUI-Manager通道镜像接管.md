# 评估：ComfyUI-Manager 的 raw.githubusercontent.com 通道接入镜像

评估日期：2026-10-18
证据来源：浅克隆 `Comfy-Org/ComfyUI-Manager@65dafe5`（2026-10-18）实读源码 + 本机 `curl` 实测镜像
结论：**可行，成本约 0.5 人日，风险低**。但要接受 3 个硬边界（见 §3 / §6 / §7），并且触发时机必须设计成「每次启动前同步」，不能是「设置页点一次永久生效」。

---

## 1. 现状为什么接管不到（源码证据）

启动器目前只在环境变量层注入 `GITHUB_ENDPOINT`（`LaunchEnvBuilder.cs:63-67` → `MirrorPolicy.GithubEndpoint`），而 ComfyUI-Manager 消费它的地方只有一处：

```python
# glob/git_utils.py:83-84
def get_url_for_clone(url):
    url = normalize_url(url)
    if GITHUB_ENDPOINT is not None and url.startswith('https://github.com/'):
        url = GITHUB_ENDPOINT + url[18:]   # 去掉 https://github.com
    return url
```

判定条件是 `startswith('https://github.com/')`，**`https://raw.githubusercontent.com` 永远不匹配**。所以现在的实际效果是：

| 链路 | 是否已被镜像 | 依据 |
| --- | --- | --- |
| 安装/更新节点时 clone 节点仓库（`https://github.com/…`） | ✅ 已覆盖 | `git_utils.py:83-84` |
| Manager 拉取节点/模型数据库 JSON（`raw.githubusercontent.com`） | ❌ 未覆盖 | 见下 |
| Manager 自身更新（git pull） | ✅ 已覆盖 | 同 clone 路径 |

未覆盖的数据通道有**两个来源**，不是只有一个文件：

```python
# glob/manager_core.py:51
DEFAULT_CHANNEL = "https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main"

# glob/manager_core.py:1938（config.ini 读取）
'channel_url': default_conf.get('channel_url', DEFAULT_CHANNEL),

# glob/manager_core.py:3064 等（实际消费）
uri = channel_url + '/' + filename     # custom-node-list.json / model-list.json
                                       # extension-node-map.json / github-stats.json
                                       # alter-list.json / extras.json
```

即：
1. **`channels.list`** 的 6 行通道 URL（下文原文）；
2. **`config.ini` 的 `[default] channel_url`** —— 所有「调用时没显式指定通道」的请求都走它（`manager_server.py:797 / 1101 / 1173 / 2048` 等多处）。

只改 `channels.list` 会漏掉第二类请求；只改 `config.ini` 会漏掉按通道枚举的请求。**两个都要改。**

`channels.list.template` 原文（6 行，全部指向 raw）：

```
default::https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main
recent::https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/node_db/new
legacy::https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/node_db/legacy
forked::https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/node_db/forked
dev::https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/node_db/dev
tutorial::https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/node_db/tutorial
```

---

## 2. 要改哪些文件、在哪个目录（源码确认）

```python
# glob/manager_migration.py:45-54
def get_manager_path(user_dir):
    if has_system_user_api():                     # hasattr(folder_paths, 'get_system_user_directory')
        return os.path.join(user_dir, '__manager')          # ComfyUI ≥ 0.3.76
    else:
        return os.path.join(user_dir, 'default', 'ComfyUI-Manager')   # 旧版
```

- `<user>` = `folder_paths.get_user_directory()`，默认 `ComfyUI/user`，可被 `--user-directory` 覆盖（**启动器参数清单里已有该参数**，`cli-args.json:27`，可直接读 `Settings.Args`）。
- `channels.list` 缺失时由 Manager **自己**从模板复制（`manager_core.py:1695-1696`）——所以不预写就会被 raw 填满。
- `config.ini` 没有模板文件；键缺失时直接取默认值（含 `DEFAULT_CHANNEL`）。
- 缓存目录 `<manager_files_path>/cache`，缓存文件名 = `simple_hash(uri) + '_' + filename`（`manager_core.py` 内 `get_data_by_mode`）→ **换 URL 即换缓存键**，不会读到旧的 raw 缓存，但首启会全量重下几 MB JSON。

---

## 3. 无法接管的一个点（必须接受）

```python
# glob/manager_server.py:143（get_risky_level）
json_data2 = await core.get_data_by_mode('cache', 'custom-node-list.json',
              channel_url='https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main')
```

这处**硬编码 raw**，既不读 `channels.list` 也不读 `config.ini`，用于安装节点前判定风险等级（high / middle / block）。

- 影响：装节点时仍有一次 raw 请求；raw 不通时 `get_data_by_mode` 内部有 `try/except` 兜底到本地 JSON，**不会崩**，但风险判定会缺少"default 通道"这一半数据 → 在 `security_level = normal` 下可能把本可安装的节点判成 high 而拦截。
- 缓解手段：无（除非 patch Manager 源码，不建议——会被 Manager 自更新覆盖，且属于改第三方代码）。
- 建议：接受，并在 UI 文案/文档里写明"装节点时的风险判定仍有一次直连 GitHub"。

---

## 4. 生命周期：为什么必须每次启动重放

```python
# glob/manager_migration.py:165-206
"""Migrate ONLY config.ini to new __manager path if needed.
   IMPORTANT: Only config.ini is migrated. Other files (snapshots, cache, etc.) are NOT migrated"""
```

- ComfyUI 升级到 0.3.76+ 后，Manager 会把旧 `default/ComfyUI-Manager` 整个挪成 `__manager/.legacy-manager-backup`，**`channels.list` 不随之迁移** → 新目录重新从模板生成一份全 raw 的 channels.list。
- 因此「设置页点一次」这种一次性写入必然在某次 ComfyUI 升级后失效。

**结论：必须做成"每次启动 ComfyUI 前同步一次"（幂等、内容不变则不写盘）。** 这也是最省心的实现——不需要用户理解任何东西。

### 4.1 一个必须绕开的坑：不要预创建 `config.ini`

`migrate_legacy_config` 的分支判定是「新旧 `config.ini` **同时存在**」：

```python
# glob/manager_migration.py:196-198
if os.path.exists(legacy_config) and os.path.exists(new_config):
    _handle_first_update_migration(user_dir, legacy_dir, manager_files_path)
```

该分支会：① 用 `sys.executable` 跑一次 `pip install -r ComfyUI/requirements.txt`；② 把旧目录挪成 `.legacy-manager-backup`。

如果启动器在 Manager 首次初始化之前就**凭空创建** `__manager/config.ini`，会伪造出"首次升级"场景，触发上面两个副作用。

→ 硬规则：**`channels.list` 可以预创建（安全）；`config.ini` 只能在已存在时改写，绝不新建。**

---

## 5. 方案对比

| 方案 | 覆盖面 | 成本 | 可逆 | 结论 |
| --- | --- | --- | --- | --- |
| **A. 改写 `channels.list` + `config.ini: channel_url`** | 除 §3 那处硬编码外的全部数据通道 | Core 一个服务 + 启动钩子，~180 行 | 是（mirror 置空即还原） | ✅ **推荐** |
| B. 只靠环境变量（现有 `GITHUB_ENDPOINT`） | 0（对 raw 无效） | — | — | ❌ 不可行（见 §1） |
| C. `network_mode = private` + 自建 node DB | 全覆盖 | 要自建并长期维护 node DB，远超收益 | 是 | ❌ 性价比差；且 private 模式仅对 comfyregistry 有额外限制（`manager_server.py:2100`），与 raw 无关 |
| D. 本地 HTTP 反代（127.0.0.1 起服务，把 raw 请求转给镜像） | 全覆盖（含 §3 硬编码点） | 常驻进程 + 端口占用 + 本地证书/HTTP 通道（`http_channel_enabled` 默认关闭）+ 出错面大 | 难 | ❌ 不建议 |

---

## 6. 推荐实现（可直接照做）

### 6.1 Core 新增 `ComfyManagerChannelService`

纯函数与 IO 分离，便于在 `Core.Test` 里覆盖（不需要 WinUI）：

| 成员 | 职责 |
| --- | --- |
| `ResolveChannelFiles(AppConfig)` | 返回要改的文件列表。user 目录 = `--user-directory`（`Settings.Args`）→ 否则 `<ComfyUiDir>/user`；目录判定：`__manager` 存在 → 收；`default/ComfyUI-Manager` 存在 → 也收（旧版共存时两个都改）；都不存在 → 读 `<ComfyUiDir>/folder_paths.py` 是否含 `def get_system_user_directory`（与 Manager 的 `has_system_user_api` 同判据）决定写哪个 |
| `RewriteChannelList(string content, string mirror)` | 逐行解析 `name::url`；**仅当 url 命中 `https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager`** 才改；`mirror` 非空 → `mirror.TrimEnd('/') + "/" + url`；`mirror` 为空 → 在 url 中定位 `https://raw.githubusercontent.com` 并截断还原；已带目标前缀则原样返回（幂等）。**其他行（用户自加通道）逐字保留**，注释行保留 |
| `RewriteConfigIni(string content, string mirror)` | 只在 `[default]` 段内维护 `channel_url`，值规则同上；不动其他键、不重排、保持原换行风格（CRLF/LF） |
| `Sync(AppConfig)` | 编排：读 → 改写 → 内容不变则跳过写盘 → 首次改写前留 `<file>.launcher.bak` → 返回 `(Changed[] , Message)`；IO/权限异常降级为警告字符串，**绝不抛进启动流程** |

### 6.2 挂点

`LaunchService.StartCoreAsync`，在 `SetState(LaunchState.Starting)` 之后、`_process.Start(...)`（`LaunchService.cs:97`）之前：

```
[启动器] ComfyUI-Manager 通道已指向镜像 ghproxy（2 个文件）
[启动器] ComfyUI-Manager 尚未初始化，下次启动自动接管   ← 目录不存在时的降级
[启动器] 警告：改写 channels.list 失败：拒绝访问          ← 不阻断启动
```

### 6.3 GUI

`EnvUpdatePage` 的 GitHub 镜像下拉下加一行说明：「该镜像同时接管 ComfyUI-Manager 的数据通道（channels.list / config.ini），下次启动 ComfyUI 生效」。**不新增按钮**——启动时自动，直连时自动还原。

### 6.4 SRS 需要同步的三处

1. §3「三分法」一行（`SRS.md:33`）：GitHub 镜像作用域从「git clone 与 GitHub release 轮子」扩为「+ ComfyUI-Manager 数据通道」。
2. FR-8.2 正文（`SRS.md:272`）与 §4 的镜像作用域表（`SRS.md:386`）。
3. 建议新增 **FR-8.9「ComfyUI-Manager 数据通道镜像接管」**，写清：接管范围、每次启动重放、`config.ini` 不新建、§3 硬编码点不接管、失败降级。

### 6.5 测试（Core.Test，6–8 条纯函数用例）

直连 → 镜像；镜像 → 直连还原；重复应用幂等；自定义通道行不被改动；CRLF 保留；`config.ini` 缺 `channel_url` 时在 `[default]` 内新增；非 `[default]` 段的同名键不误改；目录判定（`__manager` / 旧路径 / 都不存在）。

---

## 7. 风险与边界

1. **镜像对 raw 的可用性（已实测）**：`ghproxy.net` 与 `gh-proxy.com` 对 `raw.githubusercontent.com` 返回 200 且内容正确；`ghfast.top` 本次请求为 `000`（无响应）。→ 镜像不可用时 Manager 会回退本地/缓存 JSON，但启动会多等一次超时。因此**只在用户显式选了镜像时接管，直连时还原**，默认仍是官方源。
2. **缓存键变化**：换 URL 后 `simple_hash(uri)` 变，旧缓存不再命中，首启重下全部 JSON（几 MB）。切回直连同理。可接受。
3. **安全面（重要）**：Manager 对通道 JSON **没有任何签名或哈希校验**（全仓 grep 只有 `share_3rdparty.py` 用 sha256，且与通道无关），而这些 JSON 决定了"装哪个仓库"。把数据通道交给三方代理，等于把"可信来源"扩展到代理方。建议：内置镜像条目文案里注明这点，并保持「直连」为默认值。
4. **Manager UI 观感**：Manager 界面的 Channel 下拉会显示带镜像前缀的 URL（能用但难看）；用户在 UI 里改通道也会被下次启动覆盖 → 文案里说明"由启动器接管"即可。
5. **只读目录 / 杀软锁文件**：降级为日志警告，不影响启动。
6. **用户自建通道**：只改 raw ltdrdata 前缀行，用户自己加的通道（含指向自己 fork 的 raw 行）**不会被改**——这是正确行为，需在文档中说明。

---

## 8. 工作量

| 项 | 估算 |
| --- | --- |
| `ComfyManagerChannelService`（含目录判定、两种改写、幂等/备份/降级） | ~180 行 |
| `LaunchService` 钩子 + 日志 | ~10 行 |
| Core.Test 纯函数用例 | ~100 行 / 8 条 |
| GUI 一行说明文案 | ~5 行 |
| SRS v1.13（三分法、FR-8.2、新 FR-8.9） | 文档 |

**合计约 0.5 人日**，风险低：不改启动参数、不改环境变量、失败可降级、效果可逆。

---

## 9. 需要你拍板

1. **触发时机**：每次启动自动同步（推荐）／仅「环境与更新」页手动按钮。
2. **直连时是否自动还原** channels.list（推荐还原，符合"镜像空 = 直连"的既有语义）。
3. **是否顺带开放「自定义镜像前缀」**：SRS FR-8.2 写的是「直连 / 内置候选 / **自定义前缀**」，但 `MirrorCatalog` 目前是不可编辑下拉、只有 4 个固定项——这是既存 SRS 与实现不一致。若要支持自定义前缀，本次接管逻辑顺带就能兼容（`mirror` 是任意字符串）。
4. **是否接受 §3 那处无法接管**（装节点时仍有一次 raw 请求，只影响风险判定，不影响安装本身）。
