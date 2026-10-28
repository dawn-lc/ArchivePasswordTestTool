# ArchivePasswordTestTool

利用 7-Zip（7z.dll）对加密压缩包进行密码测试的工具，从密码字典中批量尝试密码，支持多线程并行测试与快速测试（优先测试最小的加密文件以加快验证速度）。

## 功能特性

- 支持格式：7z、Zip、RAR（RAR4 / RAR5）
- 支持分卷压缩包：`xxx.7z.001`、`xxx.zip.001`、`xxx.part1.rar`（测试时传入第一分卷）
- 支持自解压格式（SFX）：exe 封装的压缩包，自动在 PE stub 之后定位压缩数据
- 支持 header 加密压缩包（如 7z `-mhe=on`、RAR `-hp` 密码模式）
- **自带 7-Zip 引擎**：下载单个 exe 即可使用，无需预装 7-Zip（也可用系统已安装的版本或 `-lib` 指定）
- 自动查找系统中已安装的 7-Zip，也可手动指定 7z.dll 路径；三者全无时给出官方下载引导（可选一键打开浏览器）
- 界面语言按系统自动切换：English / 简体中文 / 日本語 / Français / Русский

## 使用方法

```text
ArchivePasswordTestTool.exe -D <字典文件> -F <压缩包文件>
```

| 参数 | 说明 |
|------|------|
| `-D` | 密码字典文件路径，默认为 `PasswordDictionary.txt`，每行一个密码 |
| `-F` | 要测试的压缩包路径，分卷包请传入第一个分卷 |
| `-lib` | 可选，指定 7z.dll 路径，默认自动查找已安装的 7-Zip |
| `-Api` | 可选，覆盖在线密码共享服务地址（默认 `OnlineService.DefaultServiceUrl`） |
| `-NoQuery` | 关闭"测试前按哈希请求在线共享密码"（默认开启） |
| `-NoReport` | 关闭"找到密码后自动回传"（默认开启） |

无任何参数直接启动（如双击 exe）时进入交互引导：把密码本与压缩包拖入窗口即自动识别路径开始测试（无需回车）；
拖放后控制台窗口自动回到前台，无需再手动点击窗口。带参启动则完全按参数行事（缺失必需参数报错退出）。
双击启动出现错误时（如未安装 7-Zip），窗口会等待按键确认后才关闭，并给出 7-Zip 下载页引导；从终端运行则直接退出不等待。

## 在线密码共享

数据模型：**一个压缩包哈希对应一个密码**。计算完压缩包哈希后会立即向在线服务查询该哈希的共享密码，
命中则优先测试；找到密码后自动回传供他人共享。两个功能默认开启、尽力而为
（网络失败只提示不阻断），可用 `-NoQuery` / `-NoReport` 分别关闭。

示例：

```text
ArchivePasswordTestTool.exe -D dict.txt -F secret.7z
ArchivePasswordTestTool.exe -D dict.txt -F split.7z.001
ArchivePasswordTestTool.exe -D dict.txt -F installer.exe -lib "C:\Program Files\7-Zip\7z.dll"
```

测试完成时间与结果会直接输出；正式版还会询问是否保存测试结果文件（`<压缩包名>[results].txt`）。

字典编码自动识别：UTF-8、UTF-16（有 BOM）以及 GBK/ANSI（简中 Windows 记事本另存的默认编码）都能正确读取，无需手动转码。

## 环境变量

| 变量 | 说明 |
|------|------|
| `APTT_PARALLELISM` | 限制并行度（进程内线程数/工作进程数共用的总并发上限），设为 `1` 表示单线程执行 |
| `APTT_WORKERS` | 指定 7z 测密的工作进程数（默认 = 逻辑核心数，取值 1 时退回进程内线程模式） |
| `APTT_TRACE` | 设为 `1` 时将 COM 回调跟踪日志输出到 stderr，用于问题诊断 |
| `APTT_FORCE_EMBEDDED_7Z` | 设为 `1` 时跳过系统已安装的 7-Zip，强制用内置的 `7z.dll`（系统那份版本过旧/损坏时可用） |
| `APTT_NO_UPGRADE_CHECK` | 设为 `1` 时跳过启动时的后台更新检查（离线/内网环境用） |

更新检查在后台异步进行（每小时最多请求一次，同一版本只提示一次），仅当有新版本时才提示，不阻塞测密。发现新版本时会自动下载到程序目录，**下次启动自动完成替换并重启**，无需手动覆盖；下载失败（目录只读、断网等）只提示不重试阻塞，下个周期自动重试。配置（含检查时间戳与已提示版本）保存在 `%LOCALAPPDATA%\ArchivePasswordTestTool\config.json`，不会写入程序目录。

## 性能

测密会自动选择并行方式，无需配置：

- **7z + 大字典**：自动启动多个工作进程绕开 7z.dll 内部的密钥派生全局锁，吞吐随进程数接近线性
  （实测 20 进程约 1300 个/秒）
- **RAR / Zip**：进程内多线程即可线性扩展
- 工具允许多开，各实例独立测试互不干扰

原理、实测数据与已知取舍见[开发文档](docs/DEVELOPMENT.md#多进程测密仅-7z)。

## 运行要求

- Windows x64 与 .NET 10 运行时（单文件发布 / Native AOT 已内置）
- 兼容未打全累积更新的旧版 Windows 10 / 11（宿主不启用 CET 影子栈，避免 .NET 10 启动自检直接退出）
- **不要求预装 7-Zip**：程序自带 7-Zip 官方 `7z.dll`（构建时内嵌，首次运行解压到
  `%LOCALAPPDATA%\ArchivePasswordTestTool\native`）。若系统已安装 7-Zip，优先用系统那份；
  也可用 `-lib` 指定自己的 `7z.dll`
- 若程序目录存在 `ArchivePasswordTestToolGUI.exe`，启动命令行版时会自动拉起 GUI

## 构建与发布

```text
dotnet build -c Release

# Native AOT + UPX（约 4.2 MB）—— **对外发布用的就是这个**
dotnet publish -c Release -p:PublishProfile=Properties\PublishProfiles\win-x64-aot.pubxml

# 单文件 + 裁剪（约 13.3 MB，兼容形态，CI 仍会构建验证）
dotnet publish -c Release -p:PublishProfile=Properties\PublishProfiles\win-x64.pubxml
```

两种形态都只产出一个 `ArchivePasswordTestTool.exe`，免安装 .NET、免装 7-Zip，自更新替换单文件。
**Release 只挂 AOT 那一个 exe**（体积与启动都更优；且 `Upgrade` 取「第一个 exe 资产」，
挂两个会让自更新在两种形态间不确定地切换）。
AOT 需本机装有 MSVC 链接器（Visual Studio「使用 C++ 的桌面开发」）。

**7z.dll 自动内嵌**：首次构建会从 7-Zip 官方 GitHub 发布点（`github.com/ip7z/7zip`，即
7-zip.org 下载页指向的仓库）取最新版，缓存到 `tools\7zip\` 并编进程序集。缺网也能构建
（只是不内嵌，运行时回退到「需自行安装 7-Zip」）。固定版本用 `-p:SevenZipVersion=26.05`，
关闭内嵌用 `-p:EnableEmbeddedSevenZip=false`；CI 与发布额外传 `-p:RequireEmbeddedSevenZip=true`
让「拿不到 dll」变成构建失败，而不是静默产出不自包含的包。

**UPX 默认开启**：把 UPX 发行包里的 `upx.exe` 放到 `tools\upx\` 即可（或用 `-p:UpxPath=...`），
之后每次 AOT 发布都会自动压缩；没放则打警告并跳过（产物约 11.3 MB 未压缩）。
`-p:EnableUpx=false` 可显式关闭。

构建选项、体积/启动实测数据与取舍见[开发文档](docs/DEVELOPMENT.md#构建与发布)。

## 开发

架构说明、测试、多语言资源约定、CI 与发布流程：
见 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)。

```text
dotnet test
```

## 许可证

见 [LICENSE](LICENSE)。
