# 开发文档

面向维护者的技术说明。使用方式见 [README](../README.md)。

## 项目结构

```text
ArchivePasswordTestTool/
├─ Program.cs        程序入口：启动模式路由 + 版本号（从程序集元数据读取）
├─ Cli/              参数解析与进程模式分流（Options / Arguments / Instance）
├─ Interop/          7-Zip 原生互操作：COM 契约与回调、库加载、格式检测、Windows API 汇集
├─ Services/         业务核心：Runner（主流程）、Planner（执行规划）、MultiProcess（多进程引擎）、
│                    SevenZip（环境定位）、Sync（在线共享交互）、PasswordDictionary、
│                    ResultsWriter、HttpTransport、Upgrade、OnlineService
├─ Ui/               界面输出与交互引导（Spectre.Console）
└─ Properties/       多语言资源（resx，en/zh-CN/ja/fr/ru）

ArchivePasswordTestTool.Tests/   xUnit 测试（样本自动生成）
```

## 多进程测密（仅 7z）

### 为什么需要多进程

7-Zip 的 7zAES 密钥派生在 `7z.dll` 内部受进程级全局锁串行化（源码 `7zAes.cpp` 的
`PrepareKey`，注释自述 "BCJ2 threads use same password. So we use long lock."）。
线程再多，吞吐也锁死在单密码 KDF 成本上（默认参数约 16ms/密码 → 约 66/s）。

绕开方式只有隔离地址空间：对 7z 格式且字典超过阈值（256 行）时，主进程启动多个**自身副本**
作为无 UI 工作进程，每个副本加载独立的 dll 实例（独立全局锁），KDF 不再互相排队。
RAR5/Zip 的 KDF 不在全局锁内，仍走进程内线程模式，可正常线性扩展。

### 进程间通信

- 传输：`Process.Start` 重定向的 stdin/stdout 匿名管道
- 序列化：行式 JSON，源生成（`WorkerJsonContext`，IL 裁剪后可用）
- 协议：严格停等（写一批 → 阻塞等回复 → 再写下一批），控制帧 `READY` / `STOP` / `QUIT`
- 容错：工作进程中途退出时整批重投（宁可重测，不可漏测）

**传输层不需要更复杂的方案**（实测依据）：5000 密码 / 20 进程达 1318 个/秒，单进程 121 个/秒
→ 10.9× 线性扩展；单批 64 个密码约 1.2s 计算，一次管道往返 <1ms，开销占比 <0.1%。
换命名管道或共享内存的收益上限不足 1%。

### 批大小自适应

批大小决定调度粒度：固定批过大时，小字典只够少数进程瓜分，其余进程启动即空转退出。
实测 300 密码时 20 进程（905ms）比 5 进程（841ms）还慢——白付 15 个进程的启动成本。

```text
batchSize = clamp(ceil(count / (workers × 4)), 8, 64)
```

`TargetBatchesPerWorker = 4` 让每个进程约摊到 4 批；大字典自动饱和到 64（与原固定批实现等价），
只有小字典才收缩。实测 300 密码 905ms → 625ms，1200/5000 密码持平。

工作进程启动到 `READY` 的开销：Debug apphost 66–87ms，单文件发布版 144–152ms
（bundle 需解压），这也是小字典并行收益的下限。

### 实测吞吐（i7-12700F，字典全错误密码）

| 字典 | 20 进程 | 10 进程 | 5 进程 | 1 进程 |
|------|---------|---------|--------|--------|
| 300 | 625ms | 636ms | 814ms | 2482ms |
| 1200 | 1245ms | 1489ms | 2349ms | 9571ms |
| 5000 | 3825ms | 5393ms | 9245ms | 41320ms |

## 版本号与更新检查

**唯一真相源是 git tag**，源码里不存在版本常量。

- csproj 定义 `AppVersion`（默认 `0.0.0`）/ `AppVersionType`（默认 `Dev`），
  写入 `<AssemblyMetadata>`，同时 `<Version>$(AppVersion)</Version>` 让 exe 文件属性同步
- 发布时工作流解析 tag，用 `-p:AppVersion=x.y.z -p:AppVersionType=Type` 注入
- 运行时由 `Program.ResolveMetadata` 经 `AssemblyMetadataAttribute` 反射读取
  （裁剪 + 单文件下可用，IL 链接器默认保留程序集级特性，也不需要 `DynamicallyAccessedMembers`）
- **`publish` 必须重复传版本参数**：它会重新构建，漏传就产出 `0.0.0` 的包

两个必须守住的约束：

1. **版本号必须恰好三段 `x.y.z`**。`Upgrade.IsNewerVersion` 在段数与线上 tag 不一致时
   直接返回 false（"段数不同无法比较，视为不更新"）——静默失效，用户永远收不到更新提示。
   工作流与单元测试都会校验。
2. **"开发构建"的判定必须依据显式标记 `VersionType == "Dev"`，不能依据"版本号是否为 0"**。
   后者在元数据意外缺失时会把发布版误判成开发版，从而静默关闭更新检查。
   CI 的发布产物实测会断言版本号等于注入值——这是唯一能覆盖裁剪路径的检查
   （单元测试跑的是未裁剪的 apphost）。

更新检查在后台异步进行，配置（`lastCheckUpgrade`、`lastNotifiedVersion`）保存在
`%LOCALAPPDATA%\ArchivePasswordTestTool\config.json`。

### 两个独立状态，各管一件事

| 字段 | 职责 |
|------|------|
| `lastCheckUpgrade` | **请求节流**，1 小时一次。GitHub 未认证配额 60 次/小时，1 小时远在限额内 |
| `lastNotifiedVersion` | **每个版本只提示（并下载）一次**，是缩短节流的必要配套 |

早期实现只有「24 小时节流」一个状态，于是「发布紧跟在上次检查之后」的用户要整整一天
才可能看到提示（实测撞到过）。缩短节流会带来重复骚扰，因此必须配合 per-version 去重。
`lastCheckUpgrade` 在**发起请求时**就写入（无论成败），用来约束失败重试频率；
`lastNotifiedVersion` 只在**确实把事办完**（下载成功，或无 exe 资产已引导到发布页）后才写，
否则下载失败会把这个版本永久跳过。

### 自更新：关闭前下载 + 下次启动应用

单文件发布的 exe 运行期间无法覆盖自身，因此拆成两次启动：

1. **本次运行结束前**（`Runner` 的 `finally` 里、保存结果确认之前）下载新版本到
   `自身.exe.new.downloading`，按 GitHub 提供的 `sha256:` digest + 体积校验通过后
   改名为 `自身.exe.new`。
2. **下次启动最早时机**（`Program.Main` 第一行，早于任何路由）：`自身 → 自身.exe.old`、
   `自身.exe.new → 自身`，再以原样参数启动新进程；当前进程立刻返回 0 退出。
   新进程启动时发现 `.old` 存在即顺手删除，并提示「已更新到 x.y.z」。

关键取舍：

- **「重命名自身」不需要额外 updater 进程**：Windows 允许重命名正在运行的 exe
- **落盘必须在同目录**：Windows 只允许同卷重命名，跨盘会退化成复制+删除，不再原子
- **先下到 `.downloading` 再改名**：避免半截文件在下个启动被当成有效更新
- **任何一步失败都回滚**到旧二进制，绝不把用户留在「两个文件都不完整」的状态
- 无 exe 资产或下载失败时退回发布页引导，不阻塞退出

`Console.IsInputRedirected` 为 true（管道/无人值守）时既不提示也不下载：12MB 无人消费纯属浪费。

## HTTP 传输

`HttpTransport` 是**唯一**的 `HttpClient` 构造点，统一装配 User-Agent
（`ArchivePasswordTestTool x.y.z-Type`）。两种客户端：

| 工厂 | TLS 行为 | 用途 |
|------|----------|------|
| `CreateClient` | 空 SNI，仅豁免证书域名不匹配（链与有效期仍校验） | 更新检查——部分网络环境按 SNI 域名嗅探并阻断 `github.com` |
| `CreateStandardClient` | 标准 SNI + 完整校验 | 在线服务、更新检查的回退路径 |

在线服务必须用标准客户端：其地址可由 `-Api` 指向任意端点，空 SNI 会豁免域名不匹配，
等于对任意地址放宽校验。

### 在线服务契约

一个压缩包哈希对应一个密码。

```text
查询  GET  {url}?hash={sha256hex}  → 200 + {"password":"..."}（404/空=无记录）
回传  POST {url}                   body {"hash":"{sha256hex}","password":"..."} → 2xx
```

部署实际服务后修改 `OnlineService.DefaultServiceUrl`（默认占位地址指向本机保留端口，
未修改时提示"未配置"并跳过，保证离线环境不会因超时拖慢启动）。

## 测试

```text
dotnet test ArchivePasswordTestTool.Tests\ArchivePasswordTestTool.Tests.csproj
```

- xUnit 2.9.2 + VSTest adapter，net10.0
- 样本夹具 `TestDataFixture` 幂等生成 `TestData/`（不入库）：需要 7-Zip CLI；
  RAR 系用例需要 WinRAR，未安装时自动跳过（此时样本生成失败会直接抛异常而非静默通过）
- 覆盖范围：格式识别（含 SFX 签名偏移）、分卷收集规则、字典消费语义（含并发出队）、
  多进程协议与批大小调度、在线服务交互（内置 fake HTTP server）、多语言资源一致性、
  源码编码与 CET 位、以及针对真实 exe 的端到端矩阵

### 断言必须与界面语言无关

- E2E 判据统一使用 ASCII 密码 `Correct123!`，绝不断言中文文案
- 需要断言"某提示未出现"时，**文案从各语言资源枚举**，不能写死一两种语言——
  写死会在其它 UI 语言下永不命中（假绿），失败被静默吞掉

### 关键守护测试

| 测试 | 守护的内容 |
|------|-----------|
| `LocalizationTests` | 五套 resx 键集一致、占位符一致、无空值、`ResourceManager` 能解析每种语言、`Designer` 属性覆盖所有键 |
| `CetCompatTests` | apphost 的 `IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS(20)` 不含 CET 位 |
| `VersionMetadataTests` | 版本号恰好三段、元数据注入生效、`Version` 确实来自元数据 |
| `SourceEncodingTests` | 全仓源码文件为 UTF-8 无 BOM |
| `MultiProcessBatchSizeTests` | 每个工作进程至少分到一批活 |

## 多语言资源约定

- 五套 resx（`Resources.resx` 中性 + `zh-CN` / `ja` / `fr` / `ru` 卫星）必须
  **键集一致、占位符一致**：缺键会在运行时静默回退英文，丢 `{0}` 会丢参数
- `Resources.Designer.cs` 是手工维护的（无 VS 设计器自动同步），新增 resx 键后
  必须同步添加属性，否则代码里无法强类型引用
- 单文件发布经 `SatelliteResourceLanguages` 只打包 zh 系 + ja/fr/ru

## 构建与发布

```text
dotnet build -c Release
dotnet publish -c Release -p:PublishProfile=Properties\PublishProfiles\win-x64.pubxml
```

单文件约 12 MB（自包含运行时 + IL 裁剪 + bundle 压缩）。几个刻意的取舍：

- **`PublishTrimmed` + `BuiltInComInteropSupport`**：裁剪默认禁用内置 COM 互操作，
  必须显式启用，否则运行时抛 "Built-in COM has been disabled via a feature switch"。
  COM 部分用 `[LibraryImport]` 源生成 P/Invoke + `Marshal.GetObjectForIUnknown`，
  因为裁剪会移除运行时封送 stub。
- **不启用 `InvariantGlobalization`**：不变全球化会把 `CurrentUICulture` 固定为
  InvariantCulture，ResourceManager 只探测中性资源，中文卫星资源完全失效
  （实测卫星 dll 就在 exe 旁也回退英文）。体积收益仅约 0.16MB。
- **不用 UPX**：与 `PublishSingleFile` 的 bundle 机制不兼容——host 启动需按原始布局
  从磁盘读自身文件提取运行时组件，改写布局即失败。bundle 内置 deflate 已是等效手段。
- **`7z.dll` 保持外部依赖**（按设计）：自动查找已安装的 7-Zip，或用 `-lib` 指定。

### 旧版 Windows 兼容（CET）

.NET 9 起 SDK 默认给 apphost / singlefilehost 打 `/CETCOMPAT`。旧版 Win10（累积更新未打全）、
旧 Win11 等在启用影子栈后缺少特殊用户态 APC，.NET 10 会在**托管 Main 之前** fail-fast
（`Your Windows doesn't fully support CET`，0x80131506）——`try/catch` 捕不到，用户只看到闪退。

项目用 `<CetCompat>false</CetCompat>` 退出该标记，使进程不启用影子栈，代价是失去 CET 加固。
CET 位**不在** `OptionalHeader.DllCharacteristics`，而在调试目录
`IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS(20)` 指向的 ushort 低位 `0x1`。

## 持续集成与发布

两个工作流都在 `windows-latest` 上运行（项目 Windows-only：COM 互操作 + Win32 API），
共用 `.github/actions/build-verify`：Restore → Build → Test → Publish → 对产物实测解出密码。

| 工作流 | 触发 | 内容 |
|--------|------|------|
| `CI` | 推送 `master`、PR、手动 | 上述全部 |
| `Release` | 推送 `v*` 或 `[0-9]*` tag、手动指定 tag | 上述全部 + 上传产物到 GitHub Release |

发布新版本只需打 tag（版本号由 tag 注入，不需要改源码）：

```text
git tag 2.1.0-Release
git push origin 2.1.0-Release
```

- tag 格式 `v2.0.0` / `2.0.0` / `2.0.0-Release` 均可；`Upgrade` 会剥掉 `v` 前缀，
  无后缀按 `Release` 处理，同版本号时按 `-Type` 后缀比较等级
- 工作流硬校验版本号为三段 `x.y.z`，不合法直接失败
- 发布产物只有一个 exe，校验值由 GitHub Release 自身提供，不额外生成 sha256
- CI 也跑 publish：裁剪 + 内置 COM 的问题只在发布路径暴露，普通构建与 E2E 都覆盖不到

## 编码约定

- 源码统一 **UTF-8 无 BOM**（根目录 `.editorconfig` 声明 `charset = utf-8`），行尾 CRLF
- 命名：类型/方法/属性 PascalCase，私有字段 `_camelCase`，参数 camelCase，**不使用下划线命名**
- 修改 UTF-8 文件时避免使用 PowerShell 5.1 的 `Get-Content/Set-Content` 往返
  （按 ANSI/GBK 解码会破坏中文），需要脚本处理时用 .NET 的
  `[System.IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)` 或 Python `newline=''`
