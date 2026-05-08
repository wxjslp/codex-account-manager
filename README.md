# Codex 多账号管理器

Codex 多账号管理器是一个面向 Windows 的本地桌面工具，用来管理多个 Codex CLI 账号档案，并在不同账号之间快速切换。它的核心目标是把每个账号的 Codex 本地数据隔离到独立的 `CODEX_HOME` 中，避免手动复制、覆盖 `auth.json` 带来的混乱和风险。

本项目使用 .NET 8、WinUI 3 和 SQLite 构建，发布形态为 win-x64 自包含便携程序。

## 下载

最新 Windows x64 便携版请从 GitHub Releases 下载：

- [下载 CodexAccountManager-win-x64-portable.zip](https://github.com/wxjslp/codex-account-manager/releases/latest/download/CodexAccountManager-win-x64-portable.zip)
- [查看全部版本](https://github.com/wxjslp/codex-account-manager/releases)

## 适用场景

- 你在同一台 Windows 电脑上使用多个 Codex / ChatGPT 账号。
- 你希望保留多个账号的本地登录状态，减少反复登录。
- 你希望按账号隔离 Codex 的 `auth.json`、会话、日志、状态数据库和配置文件。
- 你希望查看每个账号的本地凭据状态、会话数量、最近会话时间和可用额度窗口。
- 你偶尔仍需要把某个账号同步回默认的全局 `~\.codex` 目录，兼容普通终端直接运行 `codex` 的习惯。

## 核心能力

### 账号档案管理

- 新建账号档案。
- 从任意 `auth.json` 文件导入账号。
- 从当前全局 `.codex` 目录导入正在使用的账号。
- 扫描并导入全局 `.codex` 下的 `auth-*.json` 备份文件。
- 重命名账号档案。
- 删除账号档案及其本地 profile 目录。
- 显示账号总数、可启动账号数、缺少凭据账号数和当前账号数。

### 独立 CODEX_HOME

每个账号都会拥有独立的 profile home：

```text
%LOCALAPPDATA%\CodexAccountManager\profiles\<profile-key>\codex-home
```

管理器会在启动 Codex 或读取账号信息时，为目标账号注入：

```powershell
$env:CODEX_HOME = '<profile-home>'
```

这样 Codex 会在该账号自己的目录中读写认证文件、会话、日志和状态数据，而不是共用默认的 `C:\Users\<用户名>\.codex`。

### 凭据导入、备份和恢复

- 导入 `auth.json` 时会复制到账号自己的 `codex-home\auth.json`。
- 导入时会读取非敏感元数据，例如账号 ID、邮箱、套餐类型、认证模式和最近刷新时间。
- token、API key 等敏感内容不会显示在界面日志中。
- 可手动备份当前账号的 `auth.json`。
- 恢复备份前会自动先备份当前 `auth.json`，降低误操作成本。

### 全局同步模式

默认推荐使用独立 `CODEX_HOME` 模式启动 Codex。  
如果你需要让普通终端里直接运行的 `codex` 使用某个账号，可以使用“同步到全局”：

1. 备份当前全局 `.codex` 中的关键文件。
2. 将选中账号的 `auth.json` 原子复制到全局 `.codex\auth.json`。
3. 如果检测到 Codex 正在运行，会提示重启 Codex 后生效。

全局同步是一种兼容模式，不是首选模式。它会改写默认 `.codex` 下的凭据，因此只应在你明确需要兼容全局 Codex 环境时使用。

### 官方额度信息

“刷新额度”会通过 `codex app-server --listen stdio://` 与本机 Codex 通信，并读取账号与 rate limit 信息。界面会展示：

- 额度名称。
- 主额度 / 备用额度。
- 窗口周期。
- 已用百分比。
- 剩余百分比。
- 重置时间。

额度展示依赖本机 Codex 当前可返回的数据。它用于辅助判断，不等同于 OpenAI 后台账单页面或服务端最终结算结果。

### 本地会话信息

管理器会扫描账号 profile home 下的 `sessions` 目录，并显示：

- 会话数量。
- 最近会话时间。
- profile home 路径。
- auth 文件路径。
- 推荐启动命令。

当前版本不会把不同账号的云端历史合并，也不会修改 OpenAI 服务端会话归属。

## 安装与运行

### 环境要求

使用便携发行包时：

- Windows 10 1809 或更高版本。
- x64 系统。
- 已安装或可访问 Codex CLI。

从源码构建时：

- Windows 10 1809 或更高版本。
- .NET 8 SDK。
- Visual Studio 2022 或可用的 .NET CLI。
- Codex CLI，建议确保 `codex` 在 `PATH` 中可找到。

如果管理器找不到 Codex，可设置环境变量：

```powershell
$env:CODEX_EXE = 'C:\path\to\codex.exe'
```

也支持指向 `codex.cmd` 或 `codex.ps1`。

### 直接运行

在项目根目录执行：

```bat
run.bat
```

如果 `dist\CodexAccountManager-win-x64\CodexAccountManager.exe` 不存在，脚本会先自动发布便携版本，然后启动程序。

### 运行已发布程序

可执行文件位置：

```text
dist\CodexAccountManager-win-x64\CodexAccountManager.exe
```

双击该文件即可启动。

## 快速上手

### 1. 导入当前账号

如果你已经在默认 Codex 环境中登录过账号：

1. 打开软件。
2. 点击“导入当前 .codex”。
3. 管理器会读取默认 `.codex\auth.json`，复制到新的账号 profile 中。
4. 导入成功后，账号会出现在左侧账号列表。

默认全局目录通常是：

```text
C:\Users\<用户名>\.codex
```

如果当前进程设置了 `CODEX_HOME`，软件会把该目录视为全局 Codex home。

### 2. 导入 auth 文件

如果你手上已经有多个备份文件，例如：

```text
C:\Users\<用户名>\.codex\auth-Eli.json
C:\Users\<用户名>\.codex\auth-zorA.json
```

可以点击“导入 auth 文件”，逐个选择这些 JSON 文件。导入后，每个账号都会复制出独立的 `codex-home\auth.json`。

### 3. 批量导入 auth-* 备份

点击“导入 auth-* 备份”后，软件会扫描全局 `.codex` 目录下匹配 `auth-*.json` 的文件，并批量导入。

### 4. 选择当前账号

选中账号后点击“设为当前”，会把它标记为管理器当前账号。  
这只影响管理器内部状态，不会改写全局 `.codex\auth.json`。

### 5. 同步到全局

选中账号后点击“同步到全局”，会把该账号的凭据同步到默认 Codex home。  
如果你习惯在普通终端直接运行：

```powershell
codex
```

这个功能可以让普通终端使用选中的账号。

### 6. 查看和刷新额度

选中账号后点击“刷新额度”。软件会用该账号的独立 `CODEX_HOME` 启动 Codex app-server，读取账号和额度窗口信息。

如果失败，请先确认：

- 该账号存在 `auth.json`。
- Codex CLI 可被找到。
- 该账号仍处于有效登录状态。
- 当前 Codex 版本支持 `app-server` 与账号读取能力。

### 7. 备份与恢复

选中账号后：

- 点击“备份”创建当前 `auth.json` 备份。
- 在备份列表中选择记录后点击“恢复”。
- 恢复前软件会自动创建一次 `before-restore` 备份。

备份文件保存在：

```text
%LOCALAPPDATA%\CodexAccountManager\backups
```

## 数据目录

软件默认数据根目录：

```text
%LOCALAPPDATA%\CodexAccountManager
```

典型结构：

```text
%LOCALAPPDATA%\CodexAccountManager\
  app.db
  logs\
  backups\
  profiles\
    <profile-key>\
      profile.json
      codex-home\
        auth.json
        config.toml
        sessions\
        log\
        state_*.sqlite
```

说明：

- `app.db`：SQLite 数据库，保存账号索引、操作日志、备份记录和额度快照。
- `profiles`：每个账号独立的 Codex home。
- `backups`：账号凭据备份和全局同步前备份。
- `profile.json`：账号的非敏感描述信息。
- `codex-home\auth.json`：该账号的本地 Codex 凭据文件，属于敏感文件。

创建 profile home 时，软件会尽量收紧目录 ACL，仅保留当前用户、Administrators 和 SYSTEM 的访问权限。ACL 收紧是 best-effort 操作，在部分受限系统上失败时不会阻断 profile 创建。

## 安全边界

本软件是本地账号档案管理工具，不是 OpenAI 官方账号后台。

它会做：

- 本地隔离不同账号的 Codex home。
- 导入、备份和恢复本机 `auth.json`。
- 启动 Codex 时注入对应账号的 `CODEX_HOME`。
- 读取本地 token 中的非敏感展示字段。
- 调用本机 Codex app-server 获取可展示的账号和额度窗口信息。
- 对日志和命令输出做敏感信息脱敏。

它不会做：

- 不绕过 OpenAI 登录、安全策略、企业策略或额度限制。
- 不修改 OpenAI 服务端会话归属。
- 不同步不同账号的云端历史。
- 不承诺显示官方精确 credits 余额。
- 不把 token、refresh token、API key 明文输出到 UI 日志。
- 不依赖手动覆盖全局 `auth.json` 作为默认工作方式。

请把 `auth.json`、备份目录和便携包中的账号数据都当作敏感资料处理。

## 开发与构建

### 项目结构

```text
.
├─ CodexAccountManager.sln
├─ src\
│  ├─ CodexAccountManager.App\       WinUI 3 桌面应用
│  └─ CodexAccountManager.Core\      账号、文件、数据库、Codex 运行服务
├─ tests\
│  └─ CodexAccountManager.Core.Tests\ 核心逻辑测试
├─ scripts\
│  ├─ build-and-test.bat
│  └─ publish-portable.bat
├─ run.bat
├─ package.bat
├─ LICENSE
└─ README.md
```

### 还原、测试和构建

运行完整验证：

```bat
scripts\build-and-test.bat
```

等价核心命令：

```bat
dotnet restore CodexAccountManager.sln
dotnet test tests\CodexAccountManager.Core.Tests\CodexAccountManager.Core.Tests.csproj
dotnet build CodexAccountManager.sln -p:Platform=x64 --no-restore
```

### 打包便携版

```bat
package.bat
```

或直接运行底层发布脚本：

```bat
scripts\publish-portable.bat
```

发布输出：

```text
dist\CodexAccountManager-win-x64\CodexAccountManager.exe
dist\CodexAccountManager-win-x64-portable.zip
```

发布脚本会执行：

1. `dotnet restore`
2. 核心测试
3. `dotnet publish`
4. 压缩便携发行包

## 关键实现说明

### Profile Home 模型

每个账号拥有独立的 `codex-home`。导入账号时，软件会复制：

- `auth.json`
- 常见配置文件，例如 `config.toml`、`hooks.json`、`version.json`
- 常见静态目录，例如 `agents`、`prompts`、`rules`、`skills`、`vendor_imports`

这可以让新 profile 尽量继承当前 Codex 环境中的配置和本地扩展，同时仍然保持认证与状态隔离。

### Codex 可执行文件查找

软件按以下顺序解析 Codex 可执行文件：

1. `CODEX_EXE` 环境变量。
2. `PATH` 中的 `codex.cmd`、`codex.exe`、`codex.ps1`、`codex`。
3. 用户目录下常见位置，例如 npm 全局目录和 Codex sandbox bin。
4. VS Code 扩展目录中与 OpenAI 相关的 `codex.exe`。

找不到时会提示设置 `CODEX_EXE`。

### 全局同步文件

“同步到全局”目前会处理这些文件：

```text
auth.json
cap_sid
.codex-global-state.json
```

同步前会备份全局目录中的已有文件。若 Codex 正在运行，部分辅助文件可能因为占用而延后，界面日志会给出提示。

### 敏感信息脱敏

核心服务会对状态输出、错误信息和操作日志进行脱敏，避免常见 token 或密钥片段出现在界面日志中。  
但这不代表 `auth.json` 本身不敏感。请不要把 profile 目录、备份目录或发行包中的账号数据上传到公共位置。

## 常见问题

### 软件提示“未找到 codex 可执行文件”

请确认 Codex CLI 已安装，并且可以在终端中运行：

```powershell
codex --version
```

如果终端可运行但软件仍找不到，可设置：

```powershell
$env:CODEX_EXE = 'C:\path\to\codex.cmd'
```

重新启动软件后再试。

### 点击“刷新额度”失败

可能原因：

- Codex CLI 版本不支持所需的 app-server 账号接口。
- 当前账号凭据已过期，需要重新登录。
- 本机网络或 Codex 后端返回异常。
- `CODEX_EXE` 指向的不是预期 Codex 可执行文件。

可以先在普通终端中检查 Codex 状态：

```powershell
codex login status
```

如果要检查某个 profile，可以使用界面中显示的启动命令，先设置对应 `CODEX_HOME`。

### “设为当前”和“同步到全局”有什么区别

“设为当前”只改变管理器内部的当前账号标记，不修改默认 `.codex`。  
“同步到全局”会把选中账号的 `auth.json` 写入默认 `.codex\auth.json`，用于兼容普通终端直接运行 `codex` 的场景。

推荐日常使用“设为当前”和独立 `CODEX_HOME`。只有确实需要让全局 Codex 使用某个账号时，再使用“同步到全局”。

### 删除账号会删除 OpenAI 账号吗

不会。删除只影响本机管理器中的 profile 记录和本地 profile 目录，不会删除或注销 OpenAI / ChatGPT 账号。

### 能否显示精确余额

当前软件展示的是 Codex app-server 返回的 rate limit / quota 窗口信息，适合做本地辅助判断。它不是 OpenAI 官方账单页，也不承诺展示精确 credits 余额。

## 版权与许可

版权所有：清远市启宇科技有限公司

官方网站：http://www.qyqiyu.com/

使用反馈：wxj124@qq.com

本项目使用 MIT License 开源，详见 [LICENSE](LICENSE)。

## 当前状态

该项目处于本地 Windows 桌面工具阶段，重点覆盖：

- 多账号本地档案隔离。
- `auth.json` 导入、备份、恢复。
- 管理器当前账号和全局同步。
- 本机 Codex app-server 额度读取。
- 便携版发布。

后续可继续扩展统一会话搜索、加密导出、更多诊断面板和更完整的 Codex 客户端体验。
