# Codex 多账号管理器设计文档

版本：v1.1  
日期：2026-05-07  
作者：Codex

## 1. 背景

当前 Codex CLI 在 Windows 本地默认使用 `C:\Users\Administrator\.codex` 作为主数据目录，账号鉴权、会话索引、线程状态、日志和本地配置都落在该目录下。

在多账号轮换使用场景中，存在以下痛点：

1. 账号额度耗尽后，需要手动退出并重新登录另一个账号。
2. 不同账号产生的本地会话分散，无法统一检索、归档和导出。
3. 缺少一个集中视图来查看每个账号当前的 Codex 可用状态。

本设计文档定义一个本地程序，用于实现：

1. 一键切换账号：切换本地生效凭据，无需重新登录。

## 2. 目标与非目标

### 2.1 目标

- 支持多账号档案管理，避免重复登录。
- 支持快速切换当前生效账号。

### 2.2 非目标

- 不做 OpenAI 账号之间的云端历史同步。
- 不承诺读取 OpenAI 后台的官方精确 credits 余额。
- 不篡改 OpenAI 服务端会话归属。
- 不依赖未公开、未稳定的官方私有接口作为核心能力。
- 第一阶段不覆盖 macOS / Linux，优先 Windows。


### 3 当前状态是手动切换
当前生效的账号信息：
C:\Users\Administrator\.codex\auth.json

我手动保存的账户信息：
C:\Users\Administrator\.codex\auth-Eli.json
C:\Users\Administrator\.codex\auth-zorA.json

## 4. 官方资料核对结论

### 4.1 Codex 认证与配置边界

OpenAI 官方 Codex 文档确认：

- Codex CLI 是本地运行的编码 agent，第一次运行时会提示登录，可使用 ChatGPT 账号或 API key。
- Codex CLI、IDE Extension 使用同一套本地配置层；用户级配置位于 `~/.codex/config.toml`，项目级配置可位于项目内 `.codex/config.toml`。
- 配置优先级为：CLI flags / `--config` 覆盖 > `--profile` > 项目配置 > 用户配置 > 系统配置 > 内置默认值。
- CLI 支持 `--profile <name>`，但这是配置 profile，不是账号 profile；官方尚未提供一等多账号切换参数。
- Codex 支持两类 OpenAI 登录方式：ChatGPT 登录用于订阅额度，API key 登录用于按 API 计费。
- `cli_auth_credentials_store = "file" | "keyring" | "auto"` 可控制 CLI 凭据缓存位置；当前本机默认仍能看到文件型 `auth.json`。

源码层面进一步确认：

- `CODEX_HOME` 环境变量可覆盖 Codex home；未设置时默认使用 `~/.codex`。
- `CODEX_HOME` 若设置，路径必须已经存在且必须是目录。
- Codex 的 `Config` 结构把 `codex_home` 描述为“所有 Codex 状态的目录”，默认 `~/.codex`，可由 `CODEX_HOME` 覆盖。
- `auth.json` 中的 ChatGPT 模式包含 `access_token`、`refresh_token`、`id_token`、`account_id` 等敏感字段。

官方链接：

- https://developers.openai.com/codex/cli
- https://developers.openai.com/codex/auth
- https://developers.openai.com/codex/config-basic
- https://developers.openai.com/codex/config-reference
- https://raw.githubusercontent.com/openai/codex/main/codex-rs/utils/home-dir/src/lib.rs
- https://raw.githubusercontent.com/openai/codex/main/codex-rs/core/src/config/mod.rs
- https://raw.githubusercontent.com/openai/codex/main/codex-rs/login/src/token_data.rs

### 4.2 关键设计判断

不要把“复制多个 `auth-*.json` 到默认 `auth.json`”作为核心方案。它可以作为向后兼容能力，但不应是主路径。

主方案应改为：每个账号档案拥有一个独立的 `CODEX_HOME`，管理器通过设置进程环境变量 `CODEX_HOME=<profile-home>` 来启动 `codex`、`codex login`、`codex login status`、`codex app-server` 等命令。这样每个账号的认证文件、会话、日志、模型缓存和状态库都自然隔离，避免多个账号共享或反复覆盖同一个默认目录。

`auth.json` 是敏感凭据，不是稳定的多账号公共 API。应用只做本地隔离、备份、导入、启动和状态探测，不读取或调用 OpenAI 未公开的私有接口，不承诺显示官方精确余额。

## 5. 推荐实现方案

### 5.1 Profile Home 模型

管理器维护自己的应用目录：

```text
%LOCALAPPDATA%\CodexAccountManager\
  app.db
  logs\
  backups\
  profiles\
    Eli\
      profile.json
      codex-home\
        auth.json
        config.toml
        sessions\
        log\
        state_*.sqlite
    zorA\
      profile.json
      codex-home\
        auth.json
        config.toml
        sessions\
        log\
        state_*.sqlite
```

`profile.json` 只保存非敏感展示信息，例如 profile id、显示名、创建时间、最后验证时间、导入来源、备注、是否默认档案。账号邮箱、订阅类型等信息如果来自 `id_token`，只能作为本地展示缓存，标注为“本地 token 声明”，不要当作实时服务端状态。

### 5.2 账号导入与登录

支持三种入口：

1. 从现有文件导入：把当前的 `C:\Users\Administrator\.codex\auth-Eli.json`、`auth-zorA.json` 导入到各自 `codex-home\auth.json`。
2. 新建 ChatGPT 档案：先创建 profile home，再以该 home 运行 `codex login` 或 `codex login --device-auth`。
3. 新建 API key 档案：以该 home 运行 `codex login --with-api-key`，从标准输入传入 API key，不把 key 放在命令行参数里。

导入时必须校验 JSON 形状，但不能输出 token 内容。可读取字段存在性和 `id_token` 的非敏感 claims；所有原始 token 均视为 secret。

### 5.3 账号切换语义

推荐把“切换账号”定义为切换管理器的当前默认 profile，然后由管理器启动 Codex：

```powershell
$env:CODEX_HOME = "$env:LOCALAPPDATA\CodexAccountManager\profiles\Eli\codex-home"
codex
```

对用户体验而言，可以提供：

- “打开 Codex CLI”：启动 Windows Terminal / PowerShell，注入当前 profile 的 `CODEX_HOME`。
- “登录/重新登录”：对当前 profile 运行 `codex login`。
- “检查状态”：对当前 profile 运行 `codex login status`，显示 logged in / not logged in / error。
- “打开 profile 目录”：方便高级用户检查会话和日志。

### 5.4 兼容模式：替换默认 auth.json

为了兼容用户直接在普通终端运行 `codex` 的习惯，可以保留“写入默认 home”的显式高级功能：

1. 检测当前是否存在运行中的 `codex.exe`、Codex App、IDE extension 相关进程，用于决定是否提示重启。
2. 备份当前 `C:\Users\Administrator\.codex\auth.json` 到带时间戳的备份目录。
3. 使用原子替换把选中 profile 的 `auth.json` 写入默认 home。
4. 记录切换审计日志。

此模式必须有明显风险提示：它只替换默认凭据，不隔离会话、日志和 sqlite 状态；如果 Codex 正在运行，应用仍写入全局 `auth.json`，但提示用户重启 Codex 后生效。MVP 默认不启用自动替换。

### 5.5 状态展示边界

可展示：

- profile 是否存在 `auth.json`。
- `codex login status` 的结果。
- 最近一次登录/刷新时间，即 `last_refresh`。
- 本地 `id_token` claims 中的 email、plan type、account id。
- 本地会话数量、最近会话时间、日志路径。

不可承诺：

- ChatGPT / Codex credits 精确余额。
- 服务端真实可用额度。
- 跨账号云端历史同步。
- 绕过 OpenAI 登录、安全策略或企业管理策略。

## 6. 推荐技术栈

### 6.1 首选：.NET 8/9 + WinUI 3 + Windows App SDK

这是第一阶段最佳技术栈，因为产品明确优先 Windows，核心能力是本地文件、进程、环境变量、Windows 权限、终端启动、凭据保护和可靠安装。

推荐组合：

- UI：WinUI 3 / Windows App SDK。
- 语言：C#。
- 架构：MVVM。
- 本地数据库：SQLite，用于 profile 索引、审计日志、备份记录，不存明文 token。
- 敏感辅助数据：Windows DPAPI / `ProtectedData` 以 CurrentUser scope 加密。
- 文件操作：`System.IO` + 原子替换 + ACL 校验。
- 进程操作：`System.Diagnostics.Process`，按 profile 注入 `CODEX_HOME`。
- 打包：MSIX 或 winget 友好的安装包。

选择理由：

- WinUI 3 是微软当前用于 Windows 桌面应用的现代原生 UI 框架，随 Windows App SDK 发布。
- .NET 对 Windows 文件、进程、DPAPI、SQLite、日志和安装链路支持成熟。
- 该项目第一阶段不需要跨平台，原生 Windows 栈比 Electron/Tauri 更少桥接层。

官方链接：

- https://learn.microsoft.com/windows/apps/winui/winui3/
- https://learn.microsoft.com/dotnet/standard/security/how-to-use-data-protection


## 7. MVP 功能清单

第一阶段：

- profile 列表、创建、重命名、删除。
- 从 `auth-*.json` 导入 profile。
- 以 profile-scoped `CODEX_HOME` 启动 `codex`。
- 对单个 profile 运行 `codex login`、`codex login --device-auth`、`codex login status`。
- 显示本地登录状态、邮箱/plan claim、最近刷新时间。
- 对 profile home 设置当前用户可读写 ACL，避免 Everyone/Users 广泛读权限。
- 备份和恢复 profile 的 `auth.json`。

第二阶段：

- 会话索引：扫描各 profile 的 `session_index.jsonl`、`sessions\`、sqlite 状态，做统一搜索。
- 一键导出 profile：导出前默认排除 token，用户明确选择时才包含加密凭据包。
- 高级兼容模式：把某个 profile 写入默认 `C:\Users\Administrator\.codex\auth.json`。
- 可选 App Server 集成：通过 `codex app-server` 读取丰富的 thread / approval / event 流，构建更深的 Codex 客户端体验。

## 8. 验收标准

- 新建两个 profile 后，分别运行 `codex login status` 能得到互相独立的结果。
- 使用 profile A 启动 Codex 不会修改 profile B 的 `auth.json`、`sessions` 或 sqlite 状态。
- 默认模式不改写 `C:\Users\Administrator\.codex\auth.json`。
- 导入、备份、恢复操作不在日志和 UI 中输出 token。
- `CODEX_HOME` 目录不存在时，应用先创建目录，再启动 Codex。
- 运行中的 Codex 进程存在时，兼容模式仍覆盖默认 `auth.json`，并提示用户重启 Codex 后生效。
