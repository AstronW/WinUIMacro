# WinUIMacro

WinUIMacro 是一款面向 Windows 的轻量级键鼠宏工具，可以录制、编辑和回放键盘与鼠标操作，并通过全局按键或鼠标侧键触发宏。

## 功能概览

- 录制键盘、鼠标按键和滚轮操作
- 编辑录制序列并调整回放模式
- 支持单次、按住和切换三种回放模式
- 使用全局键盘按键或鼠标侧键触发宏
- 关闭主窗口后退出 UI 进程，宏引擎继续在系统托盘运行
- 将宏与触发绑定保存在程序目录，普通权限和管理员权限共享同一份数据

## 用户指南

### 系统要求

- Windows 10 1809（内部版本 17763）或更高版本
- x64 处理器与操作系统
- [Microsoft Visual C++ 可再发行组件（x64）](https://learn.microsoft.com/zh-cn/cpp/windows/latest-supported-vc-redist)

### 选择发布包

当前版本提供两种非打包的便携式发布包：

| 发布包 | 文件名 | 运行时要求 |
| --- | --- | --- |
| 非自包含版 | `WinUIMacro-1.1.1-win-x64.7z` | 需要安装 [.NET 10 Desktop Runtime（x64）](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)；已包含 Windows App SDK 运行时 |
| 自包含版 | `WinUIMacro-1.1.1-win-x64-self-contained.7z` | 已包含所需的 .NET 和 Windows App SDK 运行时，体积较大 |

### 安装与启动

1. 下载适合自己的发布包。
2. 将压缩包完整解压到一个具有写入权限的目录。不要直接在压缩包内运行，也不建议解压到 `Program Files`。
3. 运行目录中的 `WinUIMacro.exe`。

关闭主窗口后，`WinUIMacro.UI.exe` 会完全退出，`WinUIMacro.exe` 宏引擎继续在系统托盘中运行。可以通过托盘图标重新创建主窗口或彻底退出程序。

如果需要控制以管理员权限运行的程序，请右键以管理员身份运行 `WinUIMacro.exe`。不要直接启动 `WinUIMacro.UI.exe`。

### 创建和录制宏

1. 在宏列表中创建一个宏。
2. 根据需要选择回放模式。
3. 点击“开始录制”，然后执行需要记录的键盘和鼠标操作。
4. 检查录制序列并保存宏。

录制期间产生的操作只会在保存后持久化。离开存在未保存修改的宏时，程序会询问是否保存或放弃修改。

### 设置触发键

1. 打开“按键绑定”页面。
2. 选择一个支持的键盘按键或鼠标侧键。
3. 选择要绑定的已保存宏。

每个触发键只能绑定一个宏。删除宏时，与该宏关联的触发绑定也会被解除。

### 回放模式

| 模式 | 行为 |
| --- | --- |
| 单次 | 按下触发键后完整执行一次 |
| 按住 | 按住触发键时循环执行，松开后停止 |
| 切换 | 第一次按下开始循环，再次按下同一触发键停止 |

回放被停止或程序退出时，WinUIMacro 会尝试释放由宏保持按下状态的键盘按键和鼠标按键。

### 数据、备份与升级

用户数据位于：

```text
Data\Macro\
```

宏以 JSON 文件保存，触发绑定保存在 `bindings.toml` 中。

- 备份/恢复：退出 WinUIMacro 后复制程序目录中的 `Data`，恢复时放回原位置。
- 升级：解压新版本时保留原有 `Data`。
- 卸载：从托盘退出程序后删除程序目录。

### 使用提醒

- WinUIMacro 会在后台接收全局键鼠输入，并使用 Windows `SendInput` API 执行宏。
- 请勿录制密码、支付信息、恢复密钥等敏感内容。
- 某些游戏、远程桌面、安全软件或以更高权限运行的程序可能阻止输入录制或注入。
- 请先在非关键环境中确认宏行为，避免错误操作造成数据丢失。

## 开发者指南

### 开发环境

- Windows x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- WinUI 3 所需的 Windows SDK 和开发组件
- [7-Zip](https://www.7-zip.org/)，用于生成发布压缩包

Host 项目的发布目标会自动查找以下位置：

```text
C:\Program Files\7-Zip\7z.exe
C:\Program Files (x86)\7-Zip\7z.exe
```

如果 7-Zip 位于其他目录，可以在 `dotnet publish` 后追加参数：

```powershell
-p:SevenZipExecutable="D:\Tools\7-Zip\7z.exe"
```

### 项目边界

| 项目 | 应实现的内容 | 对外公开的内容 |
| --- | --- | --- |
| `WinUIMacro.Contracts` | 宏与输入数据模型、稳定键值映射、Host/UI IPC 消息和传输协议 | 仅跨进程或跨程序集共享的数据类型、消息类型与 `PipeConnection`；不包含存储、Win32 或 UI 行为 |
| `WinUIMacro.Engine` | 引擎线程、录制与回放、宏和绑定持久化、Raw Input、SendInput、托盘及本地 IPC 所需的 Win32 能力 | Host/UI 实际调用的运行时、存储结果和少量进程级 Win32 入口；录制器、回放控制器、消息窗口等实现保持 `internal` |
| `WinUIMacro.UI` | WinUI 启动、窗口与页面、编辑状态、展示逻辑和 Engine Pipe 客户端 | 仅 XAML 创建或绑定所需的应用、窗口、页面、ViewModel 和属性；IPC 客户端及 UI 内部协调保持 `internal` |
| `WinUIMacro` | 进程入口、单实例与权限边界、Engine 生命周期、工作区协调、IPC 路由和 UI 拉起 | 不提供库 API，宿主类型全部保持 `internal` |

依赖方向固定为 `Engine -> Contracts`、`UI -> Contracts + Engine`、`WinUIMacro -> Contracts + Engine`。Host 不引用 UI 程序集，只负责启动 `WinUIMacro.UI.exe`；Contracts 不反向依赖任何实现项目。

### 还原、构建与测试

```powershell
dotnet restore WinUIMacro.slnx
dotnet build WinUIMacro.slnx -p:Platform=x64
dotnet test WinUIMacro.Tests\WinUIMacro.Tests.csproj -p:Platform=x64
```

### 发布

| 发布配置 | 部署方式 | 发布目录 | 压缩包 |
| --- | --- | --- | --- |
| `win-x64` | 非自包含 | `artifacts\publish\WinUIMacro` | `WinUIMacro-1.1.1-win-x64.7z` |
| `win-x64-self-contained` | 自包含 | `artifacts\publish-self-contained\WinUIMacro` | `WinUIMacro-1.1.1-win-x64-self-contained.7z` |

生成非自包含发布包：

```powershell
dotnet publish .\WinUIMacro\WinUIMacro.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

生成自包含发布包：

```powershell
dotnet publish .\WinUIMacro\WinUIMacro.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64-self-contained
```

Host 项目会在发布过程中自动发布 UI、合并两个进程的输出，并在仓库根目录的 `artifacts` 目录生成对应的 7z 文件；不需要额外的 PowerShell 脚本。只需要发布目录而不生成压缩包时，可追加 `-p:CreateSevenZipArchive=false`。

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
