# ArchivePasswordTestTool

利用 7-Zip（7z.dll）对加密压缩包进行密码测试的工具，从密码字典中批量尝试密码，支持多线程并行测试与快速测试（优先测试最小的加密文件以加快验证速度）。

## 功能特性

- 支持格式：7z、Zip、RAR（RAR4 / RAR5）
- 支持分卷压缩包：`xxx.7z.001`、`xxx.zip.001`、`xxx.part1.rar`（测试时传入第一分卷）
- 支持自解压格式（SFX）：exe 封装的压缩包，自动在 PE stub 之后定位压缩数据
- 支持 header 加密压缩包（如 7z `-mhe=on`、RAR `-hp` 密码模式）
- 自动查找系统中已安装的 7-Zip，也可手动指定 7z.dll 路径；未安装时给出官方下载引导（可选一键打开浏览器）
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

数据模型：**一个压缩包哈希对应一个密码**。压缩包哈希计算完成后立即向在线服务查询
该哈希对应的共享密码：存在则置于密码队列最前优先测试（不匹配再退回本地字典）；
成功找到密码后自动把 哈希+密码 回传服务端供他人共享。两个功能默认开启、
尽力而为（网络失败只提示不阻断），可用 `-NoQuery` / `-NoReport` 分别关闭。

服务端契约：

```text
查询  GET  {url}?hash={sha256hex}  → 200 + {"password":"..."}（404/空=无记录）
回传  POST {url}                   body {"hash":"{sha256hex}","password":"..."} → 2xx
```

部署实际服务后，把 `ArchivePasswordTestTool\Services\OnlineService.cs` 中的 `DefaultServiceUrl` 改为真实地址即可（默认地址为占位，未改时提示未配置并跳过）。

示例：

```text
ArchivePasswordTestTool.exe -D dict.txt -F secret.7z
ArchivePasswordTestTool.exe -D dict.txt -F split.7z.001
ArchivePasswordTestTool.exe -D dict.txt -F installer.exe -lib "C:\Program Files\7-Zip\7z.dll"
```

测试完成时间与结果会直接输出；正式版还会询问是否保存测试结果文件（`<压缩包名>[results].txt`）。

## 环境变量

| 变量 | 说明 |
|------|------|
| `APTT_PARALLELISM` | 限制并行度（进程内线程数/工作进程数共用的总并发上限），设为 `1` 表示单线程执行 |
| `APTT_WORKERS` | 指定 7z 测密的工作进程数（默认 = 逻辑核心数，取值 1 时退回进程内线程模式） |
| `APTT_TRACE` | 设为 `1` 时将 COM 回调跟踪日志输出到 stderr，用于问题诊断 |

## 多进程测密（仅 7z）

7-Zip 的 7zAES 密钥派生在 7z.dll 内部受进程级全局锁串行化（源码 `7zAes.cpp` 的 `PrepareKey`），线程再多吞吐也锁死在单密码 KDF 成本（默认参数下约 66/s）。因此对 7z 格式且字典超过阈值（256 行）时，工具会自动启动多个自身副本作为无 UI 工作进程，密码经管道逐批分发，每个副本持有独立的 dll 实例（独立锁），吞吐随进程数接近线性（实测 19 进程可达 640/s，为线程模式的 6.6 倍）。RAR5/Zip 无此锁，仍走进程内线程模式。工具允许多开，各实例独立测试互不干扰。

## 运行要求

- Windows x64 与 .NET 10 运行时（单文件发布已内置）
- 已安装 7-Zip，或程序目录下存在 64 位 `7z.dll`；两者皆无时给出 7-Zip 官方下载引导
- 若程序目录存在 `ArchivePasswordTestToolGUI.exe`，启动命令行版时会自动拉起 GUI

## 构建

```text
dotnet build -c Release
```

## 发布（打包单文件）

```text
dotnet publish -c Release -p:PublishProfile=Properties\PublishProfiles\win-x64.pubxml
```

- 产物：`ArchivePasswordTestTool\bin\Release\net10.0\win-x64\publish\ArchivePasswordTestTool.exe`
- 单文件约 12 MB：自包含 .NET 运行时 + IL 裁剪 + 启用内置 COM 互操作（`BuiltInComInteropSupport`），关闭不使用的运行时特性（调试器/热重载/EventSource 等）
- 刻意不启用 `InvariantGlobalization`（不变全球化会把界面语言固定为中性回退，卫星资源失效）：本工具按系统 UI 语言自动切换界面语言；卫星语言经 `SatelliteResourceLanguages` 收敛为 zh 系 + ja/fr/ru 以减小体积
- 关于 UPX：实测与 `PublishSingleFile` 的 bundle 机制不兼容（host 启动需按原始布局从磁盘读取捆绑内容，改写文件布局即失败），故不采用；bundle 内置压缩已是受支持的等效手段
- `7z.dll` 按设计保持外部依赖：自动查找已安装的 7-Zip，或用 `-lib` 参数指定路径
- 若需其他平台/框架依赖发布，调整 `win-x64.pubxml` 中的 `RuntimeIdentifier` 与 `SelfContained`

## 项目结构

```text
ArchivePasswordTestTool/
├─ Program.cs        程序入口：启动模式路由
├─ Cli/              参数解析与进程模式分流（Options / Arguments / Instance）
├─ Interop/          7-Zip 原生互操作：COM 契约与回调、库加载、格式检测、Windows API 汇集
├─ Services/         业务核心：Runner（主流程）、Planner（执行规划）、MultiProcess（多进程引擎）、
│                    SevenZip（环境定位）、Sync（在线共享交互）、PasswordDictionary、ResultsWriter
├─ Ui/               界面输出与交互引导（Spectre.Console）
└─ Properties/       多语言资源（resx，en/zh-CN/ja/fr/ru）

ArchivePasswordTestTool.Tests/   xUnit 测试（样本自动生成）
```



## 测试

```text
dotnet test
```

- 测试项目：`ArchivePasswordTestTool.Tests`（xUnit，54 个用例）
- 样本夹具自动生成到 `TestData/`（幂等，缺失才重建；已加入 `.gitignore` 不入库），需要已安装 7-Zip；RAR 系用例需要已安装 WinRAR，未安装时自动跳过
- 覆盖范围：格式识别（含 SFX 签名偏移）、分卷收集规则、字典消费语义、多进程协议、在线服务交互（内置 fake HTTP server）、以及针对真实 exe 的端到端矩阵（7z/Zip/RAR5 交叉 单卷/分卷/SFX/header 加密，含错误密码与非压缩包负例）

