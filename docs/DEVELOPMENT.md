# 开发文档

面向维护者的技术说明。使用方式见 [README](../README.md)。

## 项目结构

```text
ArchivePasswordTestTool/
├─ Program.cs        程序入口：启动模式路由 + 版本号（从程序集元数据读取）
├─ Cli/              参数解析与进程模式分流（Options / Arguments / Instance）
├─ Interop/          7-Zip 原生互操作：COM 契约与回调、库加载、格式检测、Windows API 汇集、
│                    内嵌 7z.dll 的提取（EmbeddedSevenZip）
├─ Services/         业务核心：Runner（主流程）、Planner（执行规划）、MultiProcess（多进程引擎）、
│                    SevenZip（环境定位）、Sync（在线共享交互）、PasswordDictionary、
│                    DictionaryEncoding（字典编码探测）、ResultsWriter、HttpTransport、
│                    Upgrade、OnlineService
├─ Ui/               界面输出与交互引导（Spectre.Console）
└─ Properties/       多语言资源（resx，en/zh-CN/ja/fr/ru）

ArchivePasswordTestTool.Tests/   xUnit 测试（样本自动生成）

build/                          构建期工具：BuildTasks.cs（csproj 里两个 UsingTask 的源码）
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
- **stderr 必须持续消费**：重定向后若无人读取，工作进程写满管道缓冲（未处理异常、OOM、
  failfast 都会写）就会阻塞在 Write，而主进程在 `stdout.ReadLine` 等回复——双方互等即死锁。
  `WorkerStderrTail` 异步读取并保留尾部 20 行：工作进程无回复退出时，这就是唯一的崩溃线索
  （否则用户只能看到"工作进程已退出"）。

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

| 字典 | 20 进程 | 10 进程 | 5 进程 | 1 进程  |
| ---- | ------- | ------- | ------ | ------- |
| 300  | 625ms   | 636ms   | 814ms  | 2482ms  |
| 1200 | 1245ms  | 1489ms  | 2349ms | 9571ms  |
| 5000 | 3825ms  | 5393ms  | 9245ms | 41320ms |

## 全量解密路径的并行度：按物理核数

`Planner.Plan` 的进程内并行度取 `CpuTopology.PhysicalCoreCount`，不是 `ProcessorCount - 1`。

全量解密（RAR / Zip / 小字典 7z）是内存带宽与 AES 吞吐密集型，把 SMT 逻辑核算成物理核会超订。
同一 1.2GB RAR5 样本、同一台 8 核 16 线程机器实测（200 密码，字典全错）：

| 并行度                   | 吞吐         | 说明                    |
| ------------------------ | ------------ | ----------------------- |
| 1                        | 1.9 /秒      |                         |
| 4                        | 6.7 /秒      |                         |
| **8（物理核）**          | **10.5 /秒** | 最优                    |
| 15（`ProcessorCount-1`） | 7.2 /秒      | 超订，比 8 线程慢约 30% |

7z 多进程路径**不受此影响**（那边瓶颈是 dll 内全局锁，进程越多越好），仍用逻辑核数。

### 还必须叠加进程亲和性掩码

`GetLogicalProcessorInformationEx` 和 `Environment.ProcessorCount` **都不看进程亲和性**。
实测（8C16T，把进程限制到 `0x3` = 2 个逻辑核）：

| 口径                                                      | 默认 | 亲和性=0x3     |
| --------------------------------------------------------- | ---- | -------------- |
| `Environment.ProcessorCount`                              | 16   | **16（不变）** |
| `GetLogicalProcessorInformationEx(RelationProcessorCore)` | 8    | **8（不变）**  |
| `GetProcessAffinityMask` 位数                             | 16   | **2**          |

因此探测结果是 `min(物理核, 亲和性掩码位数)`：任务管理器的「设置相关性」、
Job object 的 `JOB_OBJECT_LIMIT_AFFINITY`（Windows 容器限制 CPU 也走这个）都能被正确尊重。
修复前在 2 核限制下会开 8 个线程，超订 4 倍。

多处理器组（>64 逻辑核）时 `GetProcessAffinityMask` 只覆盖当前组，会低估可用核数，
这种场景直接退回 `Environment.ProcessorCount`（跨组机器极罕见，优先不误压并行度）。

### 为什么不用 WMI / 注册表取物理核

`GetLogicalProcessorInformationEx(RelationProcessorCore)` 就是微软文档给出的「数物理核」标准做法，
其余路径在耗时或可用性上都不成立（本机 8C16T 实测）：

| 方式                                                                       | 是否给物理核 | 实测耗时                        | Native AOT                              | 备注                                      |
| -------------------------------------------------------------------------- | ------------ | ------------------------------- | --------------------------------------- | ----------------------------------------- |
| **`GetLogicalProcessorInformationEx`**                                     | ✅           | **0.002 ms**                    | ✅                                      | 当前实现                                  |
| `GetSystemInfo` / `GetNativeSystemInfo`                                    | ❌ 逻辑核    | ~0.001 ms                       | ✅                                      | `dwNumberOfProcessors` 只覆盖当前处理器组 |
| `GetActiveProcessorCount(ALL)`                                             | ❌ 逻辑核    | ~0.001 ms                       | ✅                                      | 跨组总数，仍不区分 SMT                    |
| WMI `Win32_Processor.NumberOfCores`                                        | ✅           | **首次 116.3 ms / 后续 4.5 ms** | ❌ **抛 `TypeInitializationException`** | 要拉起 WMI 服务与提供程序                 |
| 注册表 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\NumberOfCores` | 不一定       | 0.34 ms                         | ✅                                      | 本机该值**不存在**，不可靠                |
| `__cpuid`（leaf 0x1F + 0x4 / AMD 0x80000008）                              | ✅           | ns 级                           | ✅                                      | 需自己处理厂商差异与 SMT 位数，易错       |

WMI 那行是决定性的：本项目对外只发 Native AOT 单文件，`System.Management` 依赖内置 COM，
AOT 编译期就报 `IL3053`（AOT 分析警告）+ 三个方法「will always throw because: Invalid IL」，
运行期直接异常。而 AOT 是发布形态，不是可选项。

`ProcessorCount / 2` 这类启发式也不行：SMT 不总是 2 路（部分 CPU 无 SMT），
多处理器组下 `ProcessorCount` 与物理核的对应关系还会错位。

### 已知未覆盖的场景

- **Intel P/E 异构核**：`RelationProcessorCore` 把 P 核与 E 核一并计数，取「全部物理核」
  作为并行度。E 核跑 AES/SHA 明显慢于 P 核，理论上按 P 核数更优，但需要
  `GetSystemCpuSetInformation` 的 `EfficiencyClass` 才能区分，收益未实测，暂不引入。
- **Windows 进程隔离容器**：共享宿主机内核。若容器用 Job object 限制 CPU，能被亲和性掩码覆盖；
  若只是「CPU 速率限制」（`JOBOBJECT_CPU_RATE_CONTROL_INFORMATION`）而非核数限制，
  拓扑仍报宿主机核数，此时按宿主机并行度开线程是正确的（速率限制由调度器执行）。
- **Hyper-V 隔离容器 / VM**：看到的是虚拟机的拓扑，结果正确。

## RAR4 存储模式快速判错

### 问题：RAR4 没有密码校验字段

RAR5（WinRAR 5.21+）在压缩记录里带 `PswCheck`，密码错时 7-Zip 在 KDF 之后就能判定，
代价只有一次密钥派生。RAR4 没有这个字段，错误密码必须把**整个文件解完**、靠文件尾 CRC 才能判错。

实测 1.2GB 单文件 RAR4（store，仅数据加密）：

- 每个错误密码读取 1126–1147MB（`Win32_Process.ReadTransferCount` 采样），单次约 0.5s
- 7z.exe 单次耗时与文件大小严格线性：100MB=0.076s / 400MB=0.198s / 800MB=0.377s / 1200MB=0.54s
- 250 万字典 = 2.8PB 读取量，默认并行度约 100 小时

### 方案：只派生密钥，解首块判格式

存储模式（method 0x30）密文就是明文（CBC 补零），因此：

1. 用候选密码派生 RAR3 密钥（`Rar3KeyDerivation`）
2. 只解密归档起始的 16 字节块
3. 比对文件格式签名（`ContentSignature`）：MP4 要求首 4 字节是合理盒长且 offset 4 为
   `ftyp`/`moov`/`mdat`/`free`/`skip`/`wide`/`styp`
4. 命中的候选交给 7-Zip 完整解包确认

错误密码解出的首块是随机字节，命中 4 字节魔数约 2⁻³²（MP4 还叠加盒长合理性），
250 万候选期望误报 < 0.001 次；真密码必然命中，因此**不会漏掉正确答案**。
筛选结果永远不当结论用，只用来排除。

启用条件（任一不满足即 `TryCreate` 返回 null，退回完整解密）：RAR4、恰好 1 个文件、
加密但未加密文件名、method=0x30、UNP_VER ≥ 29、未分卷、未用 LHD_LARGE、
`packSize == ceil16(unpSize)`、扩展名在签名表内。

### 实测

同一台机器（Ryzen 7 7840HS，8C16T，硬件 SHA1）：

| 方案                         | 吞吐         | 250 万字典   |
| ---------------------------- | ------------ | ------------ |
| 完整解密，15 线程            | 7 /秒        | ≈100 小时    |
| 完整解密，8 线程（改后默认） | 10.5 /秒     | ≈66 小时     |
| **快速判错，16 线程**        | **1020 /秒** | **≈41 分钟** |

快速筛选用**逻辑核数**（16），与全量解密相反：KDF 是纯 CPU 且 SMT 有效
（实测 16 线程 650/秒 vs 8 线程 499/秒，另有 1000+ 的更高实测值随机器负载浮动）。

### 两个必须守住的实现细节

1. **KDF 的 16 个 IV 检查点在 7-Zip 里是「当前 SHA1 状态」的摘要末字节**
   （`Crypto/RarAes.cpp`：`if (i % (kNumRounds/16) == 0)` → `digest[19]`）。
   本实现用 CNG 的 `BCryptDuplicateHash` 复制中间状态——但哈希数据按 1024 条一批缓冲，
   **快照前必须先冲刷缓冲**，否则取到的是滞后最多 1024 条记录的状态，IV 全错
   （开发时踩过：密文解出来是乱码，7-Zip 报 CRC Failed）。
2. **密码字节是 UTF-16LE**（UNP_VER ≥ 29，7-Zip 逐 wchar 取低/高字节），不是 UTF-8。
   密码超过 28 个字符时记录超过 64 字节，会触发 RAR 3.5 兼容分支（就地改写密码缓冲），
   本实现不支持，`Probe` 返回 `Unsupported`，由调用方退回完整测密（绝不静默漏测）。

测试用 `Rar4ArchiveWriter` 合成已知密码的 RAR4 存档：它用生产代码的 KDF 加密，
再由 7-Zip 解密校验——若 KDF 与真实 RAR3 有偏差，7-Zip 就解不开，测试立即失败。

## ZipCrypto 快速判错

### 问题：1 字节校验带来 1/256 假阳性

传统 zip 加密（ZipCrypto）的 12 字节加密头末尾只有 **1 个字节**是校验值
（通用标志位 bit 3 未设置时取 CRC 的高字节，设置时取修改时间的高字节）。
错误密码通过校验的概率是 1/256，之后必须完整解压才能被文件 CRC32 否决。

200MB 单文件实测（20000 条全错误密码）：

| 加密方式       | 校验值     | 吞吐         |
| -------------- | ---------- | ------------ |
| WinZip AES-256 | 2 字节     | 3754 /秒     |
| **ZipCrypto**  | **1 字节** | **1215 /秒** |

### 方案：加密头 + 首块双校验

ZipCrypto 是流密码（逐字节异或），Store 模式下加密头之后就是明文。因此：

1. 用候选密码初始化密钥流（`ZipCryptoCipher`），解 12 字节加密头
2. 校验最后一字节 → 1/256 通过
3. 继续解 16 字节数据首块，比对文件格式签名 → 误判率降到约 2⁻³²
4. 命中的候选交给 7-Zip 完整解包确认

实测 20000 条全错误密码：**1215 → 45892 个/秒**（wall clock，含进程启动；测密本身约 40 万/秒）。

启用条件：zip、加密、非 AES（method ≠ 99）、某条目 method == 0（Store）、
该条目未使用 data descriptor、扩展名在签名表内。

### 必须守住的实现细节

1. **密码按 ASCII 字节直接使用**——实测与 7-Zip 一致（纯 ASCII 密码）。
   但非 ASCII 密码的字节编码取决于 7-Zip 的转换（UTF-8 / 系统代码页），
   本实现不做猜测：`Probe` 直接返回 `Unsupported`，由调用方退回完整测密。
   猜错编码会**静默漏掉正确答案**，这是不可接受的失败模式。
2. **data descriptor（bit 3）必须排除**：此时本地头里的压缩后大小为 0，
   既无法可靠定位下一条目，也无法判断加密头是否完整。
3. 校验值取 `CRC >> 24` 的前提是 bit 3 未设置——设置时应改用修改时间高字节，
   本实现通过排除 bit 3 回避了这个分支。

## 慢速完整解码的预警

### 问题：7z Copy 编码没有可校验结构

7z 的加密头里只有 `NumCyclesPower / Salt / IV`，**没有密码校验值**。能否快速判错
完全取决于压缩编码能否从垃圾数据里察觉异常。200MB 单文件实测：

| 场景                                     | 工具吞吐   | 判错依据                |
| ---------------------------------------- | ---------- | ----------------------- |
| **7z Copy(store) + AES，无 header 加密** | **36 /秒** | 文件 CRC → 解完整个文件 |
| 7z LZMA2 + AES                           | 835 /秒    | 压缩流结构 → 首块即失败 |
| 7z Copy + AES + `-mhe=on`                | 861 /秒    | Open 阶段验头部 CRC     |
| zip Store + AES256                       | 3754 /秒   | 2 字节校验值            |

关键点：**多进程救不了 Copy**（瓶颈不是 dll 内的 KDF 锁，而是内存带宽/IO）：

```
7z Copy+AES, 16 进程 → 36 个/秒
7z Copy+AES,  1 进程 → 34 个/秒
```

外推到 1.2GB：约 6 个/秒 → 250 万字典约 116 小时。

### 方案：开始前估算并提示

`ArchiveInfo.FailureRequiresFullDecode` 在打开归档时判定（读取每个加密条目的
`ItemPropId.Method`，全部为 `Copy`/`Store` 时才为 true），`FailurePathBytes` 记录
实际会解码的数据量（有快速测试目标时取该文件大小，否则取全部加密数据之和）。

`CostEstimate` 按 **2 GB/s 的乐观吞吐**估算总耗时（本机实测下限），
超过 **1 小时**才提示。乐观取值是刻意的：宁可少提示，也不要把正常场景说成"要跑几小时"。

实测验证：

| 场景                  | 字典   | 估算                 | 是否提示 |
| --------------------- | ------ | -------------------- | -------- |
| 7z Copy+AES 200MB     | 2000   | 3.3 分钟             | 否       |
| 7z Copy+AES 200MB     | 200000 | 5.4 小时             | **是**   |
| 7z LZMA2+AES 200MB    | 200000 | —（不标记）          | 否       |
| 7z Copy+AES+mhe 200MB | 200000 | —（header 加密豁免） | 否       |

提示只是一条黄色警告，不阻塞、不询问——用户仍可继续；目的是避免静默等待上百小时。

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

| 字段                  | 职责                                                                     |
| --------------------- | ------------------------------------------------------------------------ |
| `lastCheckUpgrade`    | **请求节流**，1 小时一次。GitHub 未认证配额 60 次/小时，1 小时远在限额内 |
| `lastNotifiedVersion` | **每个版本只提示（并下载）一次**，是缩短节流的必要配套                   |

早期实现只有「24 小时节流」一个状态，于是「发布紧跟在上次检查之后」的用户要整整一天
才可能看到提示（实测撞到过）。缩短节流会带来重复骚扰，因此必须配合 per-version 去重。
`lastCheckUpgrade` 在**发起请求时**就写入（无论成败），用来约束失败重试频率；
`lastNotifiedVersion` 只在**确实把事办完**（下载成功，或无 exe 资产已引导到发布页）后才写，
否则下载失败会把这个版本永久跳过。

### 自更新：启动即检查、并行下载 + 结束时应用并重启一次

单文件发布的 exe 运行期间无法覆盖自身，因此替换必然发生在**进程即将退出**的时刻：

1. **本次启动最早时机**（`Program.Main` 里自我替换之后、启动路由之前）发起后台检查。
   发现新版本就在同一个后台任务里**立刻下载**：传输与用户输入、测密全程重叠，
   结束前多半已下载完成，无需在退出前干等。下载到 `自身.exe.new.downloading`，
   按 GitHub 提供的 `sha256:` digest + 体积校验通过后改名为 `自身.exe.new`。
   测试结束时（`Runner` 的 `finally` 里、保存结果确认之前）只负责消费结果并提示：
   检查没跑完就最多等 3 秒；已进入下载阶段则先提示再等它收尾。
2. **测密结束或中断时**（`Runner.Run` 在备用屏幕退出之后）：`.new` 存在就
   `自身 → 自身.exe.old`、`自身.exe.new → 自身`，然后带 `-UpdateRestarted` 参数重启
   一次自身，当前进程随即返回。新进程删掉 `.old`、打印「已更新到 x.y.z」后退出，
   **不会重跑一遍测密**（重放用户的命令等于白测一次）。
   中断（Ctrl+C）走同一条路径：注册 `Console.CancelKeyPress`，有就绪更新时先替换重启
   再退出，没有则保持默认的立即终止。
3. **兜底**：`Program.Main` 启动时的 `PendingUpdate.TryApply` 仍然保留——上次没来得及
   应用（进程被强杀、目录当时不可写）时，下次启动照样会装上。

关键取舍：

- **检查与下载都要早**：晚到测试结束才下载，短测试会让用户白等一整个下载窗口，
  长测试（数小时）则把 12MB 传输完全堆到退出前；早期下载还能避免用户中途关窗口就永远拿不到更新
- **应用要等测密结束**：替换要重命名正在运行的 exe，不能打断测密；离开备用屏幕后再做，
  重启进程的输出才落在主屏幕上
- **重启必须带标记**：否则新进程会把用户的命令原样再跑一遍
- **输出重定向时不应用**：子进程的提示会混进调用方的数据流，自动化场景也不需要"立刻生效"，
  留给下次启动的兜底路径
- **「重命名自身」不需要额外 updater 进程**：Windows 允许重命名正在运行的 exe
- **落盘必须在同目录**：Windows 只允许同卷重命名，跨盘会退化成复制+删除，不再原子
- **先下到 `.downloading` 再改名**：避免半截文件在下个启动被当成有效更新
- **任何一步失败都回滚**到旧二进制，绝不把用户留在「两个文件都不完整」的状态
- **后台线程不得写控制台**：进度条正在主线程渲染，后台输出会把画面打乱；
  下载过程因此全程静默，提示统一留到结束前由主线程输出
- 无 exe 资产或下载失败时退回发布页引导，不阻塞退出

`Console.IsInputRedirected` 为 true（管道/无人值守）时既不提示也不下载：12MB 无人消费纯属浪费。

### 下载的候选地址矩阵

下载走「候选地址 × TLS 模式」的兜底矩阵，任一成功即返回：

| 顺序 | 地址                            | 说明                                                                                           |
| ---- | ------------------------------- | ---------------------------------------------------------------------------------------------- |
| 1    | `assets[].url`（**API 形式**）  | `api.github.com/.../releases/assets/{id}` + `Accept: application/octet-stream`，302 到下载 CDN |
| 2    | `assets[].browser_download_url` | `github.com/.../releases/download/...`                                                         |

每个地址都先试**空 SNI**、失败再试**标准 TLS**（与 `FetchLatestRelease` 对称）。

**为什么 API 形式必须排第一**（实测教训）：部分网络环境只阻断 `github.com` 的 TLS 握手，
`api.github.com` 却通畅。此时更新检查正常（能查到新版本），但 `browser_download_url` 指向
`github.com` → 握手失败 → 「发现得了却下不下来」。API 形式复用已经打通的那条通路。

两个配套细节：

- `Accept: application/octet-stream` **必须显式声明**：否则 API 把资产当元数据返回 JSON，
  200 却下错内容（会被 digest 校验拦住，但白跑一次）
- 下载用 `ConnectTimeout = 30s` + 整体 `Timeout = 10min` 分离设置：候选最多试 4 次，
  沿用整体超时会让一个静默丢包的地址把退出卡住半小时

## HTTP 传输

`HttpTransport` 是**唯一**的 `HttpClient` 构造点，统一装配 User-Agent
（`ArchivePasswordTestTool x.y.z-Type`）。两种客户端：

| 工厂                   | TLS 行为                                         | 用途                                                                 |
| ---------------------- | ------------------------------------------------ | -------------------------------------------------------------------- |
| `CreateClient`         | 空 SNI，仅豁免证书域名不匹配（链与有效期仍校验） | 更新检查与下载的首选——部分网络环境按 SNI 域名嗅探并阻断 `github.com` |
| `CreateStandardClient` | 标准 SNI + 完整校验                              | 在线服务、更新检查与下载的回退路径                                   |

两者都接受可选的 `connectTimeout`：大文件下载需要「短建连（30s）+ 长传输（10min）」，
避免静默丢包的候选地址把退出卡住。

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

> 输出编码：主程序在重定向时把 stdout/stderr 改为 **UTF-8 无 BOM**，Windows 交互终端
> 也把代码页切到 UTF-8（见 `Program.InitializeConsole` 与「Windows 控制台适配」）。
> 系统默认是 OEM 代码页（中文 Windows = 936/GBK），而界面支持 5 种语言——法语重音、
> 俄语西里尔在 GBK 下无法表示，会变成 `?`（数据损坏）。
> **任何 `RedirectStandardOutput` 的测试都必须显式设 `StandardOutputEncoding = UTF8`**，
> 否则读到的中文是乱码。

### 关键守护测试

| 测试                                                         | 守护的内容                                                                                           |
| ------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------- |
| `LocalizationTests`                                          | 五套 resx 键集一致、占位符一致、无空值、`ResourceManager` 能解析每种语言、`Designer` 属性覆盖所有键  |
| `CetCompatTests`                                             | apphost 的 `IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS(20)` 不含 CET 位                                  |
| `VersionMetadataTests`                                       | 版本号恰好三段、元数据注入生效、`Version` 确实来自元数据                                             |
| `SourceEncodingTests`                                        | 全仓源码文件为 UTF-8 无 BOM                                                                          |
| `ConsoleInitializationTests`                                 | 控制台初始化早于其它启动动作；备用缓冲区进入后重新修正字体；退出还原字体与代码页；字体写入有读回校验 |
| `MultiProcessBatchSizeTests`                                 | 每个工作进程至少分到一批活                                                                           |
| `DictionaryEncodingTests`                                    | 字典编码探测（GBK/UTF-8/BOM/截断尾字符）与真实密码可读性                                             |
| `ResultsWriterTests`                                         | 结果文件密码行三态；保存询问按"已探测"而非"已加密"守卫                                               |
| `EmbeddedSevenZipTests`                                      | 内嵌 `7z.dll` / 许可证存在性、提取落地尺寸、缓存复用、并发提取不互相踩踏                             |
| `EndToEndTests.EmbeddedSevenZipWorksWhenNoInstalledSevenZip` | 强制走内嵌 dll 时真的能解包破密（语言无关判据：exit 0 + ASCII 密码命中）                             |

## 字典编码

`DictionaryEncoding` 按文件探测编码，**不能硬定 UTF-8**：GBK 密码本（简中 Windows
记事本另存为 ANSI 的默认结果）整份读成乱码时，密码就在字典里也必然报"未找到"。

探测顺序：BOM → 严格 UTF-8 校验（64KB 窗口，容忍切在多字节字符中间）→ GBK(936) → 系统 ANSI。
探测到非 UTF-8 时界面会提示一行，避免用户对"怪字符"密码困惑。

两个 .NET 陷阱：

- `Encoding.Default` 在 .NET Core 起**恒为 UTF-8**，不是系统 ANSI
- `Encoding.GetEncoding(936)` 需先 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`
  否则抛 `NotSupportedException`。该包在 .NET 10 内置（`NU1510` 会提示无需显式引用），
  但**引用 `CodePagesEncodingProvider` 会让裁剪器保留整份代码页数据：实测 +494 KB（+4%）**。
  这是"能破解 GBK 字典 vs 不能"的必要代价。

## 多语言资源约定

- 五套 resx（`Resources.resx` 中性 + `ResourcesZhCn` / `ResourcesJa` / `ResourcesFr` /
  `ResourcesRu`）必须**键集一致、占位符一致**：缺键会在运行时静默回退英文，
  丢 `{0}` 会丢参数
- **不生成卫星程序集**：各语言 resx 的文件名去掉了 `.Culture` 段，再用 `LogicalName`
  指定 `{BaseName}.{culture}.resources` 直接编进主程序集。原因是 Native AOT 无法打包
  卫星程序集，否则产物必然是「exe + 语言目录」，与「单 exe 下载 + 自更新替换单文件」冲突
- `Resources.Designer.cs` 是手工维护的（无 VS 设计器自动同步），新增 resx 键后
  必须同步添加属性，否则代码里无法强类型引用。所有属性都走 `EmbeddedResourceManager`，
  不能用默认的 `ResourceManager`——后者只在卫星程序集里找 culture 资源，
  资源编进主程序集后它只会返回英文
- `EmbeddedResourceManager` 的 culture 参数可能为 **null**（`Designer` 传进来的是
  `resourceCulture`）：必须回退到 `CultureInfo.CurrentUICulture`，否则全部界面都是英文
- `zh` / `zh-Hans` / `zh-CHS` / `zh-SG` 等别名统一归到 `zh-CN`；
  `zh-Hant` 系（繁体）不在支持列表内，按 culture 链回退到英文

## Windows 控制台适配

界面完全基于 Spectre.Console（面板、Rule、Progress），而 Spectre 按 Unicode East Asian
Width 计算列宽。Windows 控制台有若干不归程序管的宿主属性，却会直接破坏布局，启动时必须处理
（见 `Program.InitializeConsole`）：

| 处理                          | 位置                                                                                 | 为什么必须做                                                                                                                                       |
| ----------------------------- | ------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| 代码页切 UTF-8                | `WindowsNativeMethods.TryEnableUtf8Console` + `Console.OutputEncoding/InputEncoding` | 控制台代码页跟随系统语言（中文 Windows = 936/GBK）。界面有 5 种语言，法语重音与俄语西里尔字母在 GBK 下被编成 `?`（数据损坏，不是显示问题）         |
| `Capabilities.Unicode = true` | `Program.InitializeConsole`                                                          | 代码页已是 UTF-8，直接启用 Unicode 边框；不依赖 Spectre 对旧代码页的探测                                                                           |
| 歧义宽度字体修正              | `WindowsNativeMethods.EnsureNarrowAmbiguousWidth`                                    | 中文 Windows 默认字体（新宋体 8x16）把 `± → … · °` 画成两格，Spectre 按一格计算 → 面板/进度条折行、备用缓冲区逐帧错位                              |
| 进入备用缓冲区后再修正一次    | `Ui.RunInAlternateScreenWhenSupported`                                               | conhost 的备用缓冲区有独立的一份字体：先设 Consolas 再进缓冲区，字体又变回新宋体                                                                   |
| **退出时还原字体与代码页**    | `Program.Main` 的 `finally` + `ProcessExit` → `WindowsNativeMethods.RestoreConsole`  | 从终端启动时窗口是 shell 的，用户只借给我们用；不还原的话退出后终端一直停在程序换的字体（实测：新宋体被换成 Courier New，后续 shell 输出字体全变） |

字体修正是「写一个 `±` 看光标前进几格」实测，已经是一格就不动，不覆盖用户调好的字体。
候选只取各国 Windows 都预装的等宽字体（Consolas → Lucida Console → Courier New）；
拉丁字体没有 CJK 字形，中文由 conhost 自行回退渲染，不会变方块。

### 还原字体时的两个 conhost 陷阱（实测）

1. **`SetCurrentConsoleFontEx` 返回 `true` 不代表改成功**：它可能只应用了字号，
   字体名原样不动。因此每次写入后都要用 `GetCurrentConsoleFontEx` 读回比对，
   不一致就换另一种结构体再写一次（`TrySetConsoleFont` → `IsConsoleFontApplied`）。
2. **两种结构体声明的有效方向不同**：内联 `fixed char[32]` 能把「新宋体」切成 Courier New，
   却切不回 MS Gothic；`ByValTStr` 定长字符串相反——用它才能读到完整名字
   （`fixed` 版读回来只有第一个字符）并完成另一方向的切换。两者都保留。

代码页必须在**任何 `Console.OutputEncoding` 赋值之前**记录（`CaptureConsoleCodePage`）：
该属性的 setter 自己就会改原生代码页，顺序反了退出时「还原」成的是 UTF-8。

字体结构体在 Native AOT 下均已实测可用（ByValTStr 的封送会被 AOT 编译器静态展开）。
`ConsoleInitializationTests` 守护初始化顺序、备用缓冲区二次修正、退出还原与读回校验。

## 构建与发布

```text
dotnet build -c Release

# 形态 A：单文件 + 裁剪 + bundle（CI 构建验证，不对外发布）
dotnet publish ArchivePasswordTestTool\ArchivePasswordTestTool.csproj -c Release `
  -p:PublishProfile=Properties\PublishProfiles\win-x64.pubxml

# 形态 B：Native AOT（默认自动用 UPX 压缩；upx.exe 自动获取）—— **对外发布就是这个**
dotnet publish ArchivePasswordTestTool\ArchivePasswordTestTool.csproj -c Release `
  -p:PublishProfile=Properties\PublishProfiles\win-x64-aot.pubxml -o dist\aot
```

**为什么命令里要写 csproj**：`-p:PublishProfile` 的路径相对**项目目录**解析，不指定项目就在仓库根
发布了整个解决方案——那个配置文件在测试项目里不存在，会多出一条 NETSDK1198 警告，并在
`ArchivePasswordTestTool.Tests\bin\Release\net10.0\publish\` 里凭空产出一份无意义的发布。
测试项目已设 `IsPublishable=false`（`Publish` 目标的条件就是它），所以从根目录发布也不会再出这些噪音；
显式写 csproj 是为了让意图明确、也避免误发布将来的其他项目。

两种形态都只产出一个 exe。单文件落在默认目录 `bin\Release\net10.0\win-x64\publish\`，AOT 在 CI 里被 `-o` 重定向到 `dist\aot\`。

**为什么 AOT 必须单独指定输出目录**：两种形态默认写同一个 `publish\`，顺序执行会互相覆盖（先构建的产物被后构建的抹掉）。CI 的 `Publish single file` 与 `Publish Native AOT` 是连续两步，不分开就必然踩到。

**为什么叫 `dist` 而不是 `artifacts`**：`artifacts/` 在 .NET 8+ 是 SDK 托管输出目录的名字（`--artifacts-path` / `UseArtifactsOutput`），而本仓库实测 `--artifacts-path` 会让 `TestPaths.FindRepoRoot()` 找不到仓库根、大量测试假失败。用同名目录会误导后来的人以为是那个东西；`dist`（distribution）语义也更准确——这里是最终分发产物，不是中间产物。

| 形态                   | 体积     | 启动（热，交替 10 次） |
| ---------------------- | -------- | ---------------------- |
| 单文件 + 裁剪 + bundle | 13.31 MB | 335 ms                 |
| Native AOT             | 11.30 MB | 130 ms                 |
| AOT + UPX              | 4.17 MB  | 108 ms                 |

均含内嵌的 `7z.dll`（+1.92 MB 未压缩；内嵌前后逐形态增量见「内嵌 7z.dll → 体积代价」）。

几个刻意的取舍：

- **`PublishTrimmed` + `BuiltInComInteropSupport`**：裁剪默认禁用内置 COM 互操作，
  必须显式启用，否则运行时抛 "Built-in COM has been disabled via a feature switch"。
  COM 部分用 `[LibraryImport]` 源生成 P/Invoke，AOT 另需
  `[GeneratedComInterface]` / `[GeneratedComClass]`（详见下文「Native AOT」）。
- **不启用 `InvariantGlobalization`**：不变全球化会把 `CurrentUICulture` 固定为
  InvariantCulture，ResourceManager 只探测中性资源，中文卫星资源完全失效
  （实测卫星 dll 就在 exe 旁也回退英文）。体积收益仅约 0.16MB。
- **单文件形态不用 UPX**：与 `PublishSingleFile` 的 bundle 机制不兼容——host 启动需按
  原始布局从磁盘读自身文件提取运行时组件，改写布局即失败。bundle 内置 deflate 已是等效手段。
  **AOT 形态可以**：产物是普通原生 PE，没有 bundle 自解压过程。

### 构建期 MSBuild 任务

csproj 里的 `EnsureResvgToolTask` / `EnsureUpxToolTask` / `EnsureSevenZipDllTask` / `PackAppIconTask` 都是 `RoslynCodeTaskFactory` 内联任务，但**源码不在 csproj 里**——`<Code Source=...>` 指向仓库根的 `build/`：`BuildTasks.cs`（前三个「定位或下载外部工具」共用同一份 `BuildTaskHelpers`）与 `AppIconTask.cs`（把 resvg 渲染出的 PNG 帧装进 ICO）：

```xml
<UsingTask TaskName="EnsureUpxToolTask" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll">
  <ParameterGroup> ... </ParameterGroup>
  <Task>
    <Code Type="Class" Language="cs" Source="$(MSBuildThisFileDirectory)..\build\BuildTasks.cs" />
  </Task>
</UsingTask>
```

`<ParameterGroup>` 必须留在 csproj（MSBuild 靠它做参数校验与类型转换），只有 `<Code>` 换成了引用。

为什么不用 CDATA 内嵌：

- CDATA 里写 C# 没有语法高亮、补全、重构和错误提示，改错只能靠构建时报错发现；
- 两个任务原先各自复制了一份 `GetBytesWithRetry` / `Describe` / TLS 设置，改一处漏一处；现在统一为 `BuildTaskHelpers`；
- 不受 XML 转义限制（不必担心 `&&`、`<`，也不会被 `]]>` 意外截断）。

两点注意：

- `build/` 在仓库根，**不在任何项目的 `**/\*.cs` 通配范围内**，不会被编进程序集（`dotnet msbuild -getItem:Compile`可验证）。因为这些文件被多个`UsingTask`引用，所以没有 namespace ——`RoslynCodeTaskFactory`按`TaskName` 全名匹配类型。
- `RoslynCodeTaskFactory` 的编译参考集较旧：没有 `ProcessStartInfo.ArgumentList`（只能手工拼 `Arguments` 并自行加引号，见 `BuildTaskHelpers.Quote`），也缺部分 .NET Core 后才有的 API。

### Native AOT

AOT 产物没有 apphost / bundle 概念，COM 与资源两条链路都需要专门处理（7z.dll 见下文专章）：

- **COM**：运行时 CCW（`[ComVisible]` + `ClassInterface`）依赖内置封送，AOT 下直接报 `COM Interop requires ComWrapper instance registered for marshalling`。接口改 `[GeneratedComInterface]`、回调类改 `[GeneratedComClass]`，用 `ComInterfaceMarshaller<T>.ConvertToManaged` 取代 `Marshal.GetObjectForIUnknown`。事件不会因为 AOT 而少——反而更严：指针参数必须是 `byte*` / `ulong*` 形式。
- **资源**：AOT 无法把卫星程序集打进 exe（产物会变成「exe + 语言目录」，与单 exe 自更新替换冲突）。因此各语言 resx 去掉文件名里的 `.Culture` 段、用 `LogicalName` 直接编进主程序集，由 `EmbeddedResourceManager` 按 culture 链查找。默认的 `ResourceManager` 只在卫星程序集里找 culture 资源，这里会静默回退英文。
- **符号**：`DebugType` / `DebugSymbols` 管不到 ILCompiler 的原生符号，由 `CopyOutputSymbolsToPublishDirectory` 控制（默认 true，会多出一个 35 MB 的 .pdb）。
- **UPX**：在 csproj 的 `UpxCompressNativeBinary` target 里做，挂在 `AfterTargets="CopyNativeBinary"`。不能挂在 `Publish` 上——`CopyNativeBinary` 本身也是 `AfterTargets="Publish"`，两个 target 相对顺序不确定，UPX 跑在前时压缩的是中间产物，随后被「删除 + 复制」覆盖回未压缩文件（日志会显示 "Packed 1 file" 但输出目录仍是 9.4 MB，就这么来的）。`UpxPath` 为空则整个 target 跳过。

注意：UPX 压缩过的 exe 是杀软误报的经典模式，而本工具本身是密码破解器，发布前必须在开启实时防护的机器上实测确认。

### 程序图标

唯一真相源是 `build/AppIcon.svg`，仓库里不存 `.ico`。构建期 `GenerateAppIcon` target（`BeforeTargets="CoreCompile"`）分两步：resvg 按 8 档尺寸分别 `--width/--height` 渲染 PNG，再由 `PackAppIconTask` 把这些 PNG 原样装进 ICO 容器，路径通过 `ApplicationIcon` 交给 Csc 的 `/win32icon`；AOT 用 `--win32resourcemodule` 把托管 PE 的 Win32 资源整段搬进原生产物，图标自动跟过去。

几个不那么直观的决定：

- **不用自写渲染器**：内联任务不参与 NuGet 还原，引不进任何图形库，`System.Drawing` 也只在 WindowsDesktop 共享框架里；自己画则要放弃贝塞尔、transform、渐变、mask 等全部标准 SVG 特性。resvg 是单个静态链接的可执行文件，且是完整 SVG 渲染器，代价只是首次构建下载一次（之后命中 `tools/resvg/` 缓存）。
- **resvg 缺失直接失败，不降级**（与 UPX / 7z.dll 不同）：图标是产物的一部分，少一个图标或不报错地画错，都比构建失败难发现。
- **每一帧都原样嵌 PNG，不做 DIB 转换**：ICO 自 Vista 起允许任意尺寸用 PNG 帧。早期实现把 <256 的帧转 32 位 DIB，理由是「System.Drawing 读不了 ≥64px 的 PNG 帧」；实测该顾虑不成立（全 PNG 与 DIB 版在 System.Drawing 下读取结果相同、Shell 位图摘要逐像素一致，两者请求 256 都退回 64），而 DIB 转换要多写 PNG 解码与反滤波约 300 行、ICO 体积也大 3 倍（51.6 KB vs 17.7 KB）。本项目最低要求 Windows 10，故全部直嵌。
- **仍校验帧尺寸**：只读 PNG 签名与 IHDR 的宽高。resvg 若没按 `--width/--height` 出图，ICO 目录项记的尺寸会与真实像素不符，得到一个「能打开但显示错乱」的图标——这类问题极难发现。
- **`ApplicationIcon` 必须在 target 里赋值**：项目求值阶段 `$(IntermediateOutputPath)` 还不带 `$(TargetFramework)`，提前取会指到 `obj\Debug\` 而不是 `obj\Debug\net10.0\`，Csc 找不到文件就报 CS7064。
- **不挂 `Inputs`/`Outputs`**：那样 target 在增量构建时会被整体跳过，里面给 `ApplicationIcon` 赋值的 `PropertyGroup` 也跟着不执行，Csc 就收不到 `/win32icon`。增量判定改在 `PackAppIconTask` 内部按内容比对（内容没变就不写文件，避免无谓触发重新编译）。

## 内嵌 7z.dll

### 为什么内嵌

原先 `7z.dll` 是硬性外部依赖：没装 7-Zip 的用户双击即报错退出。「下载单个 exe 即可用」因此不成立。内嵌后零依赖，多进程工作进程也能拿到同一份 dll。

### 构建期：自动获取官方件

7-Zip 官方**不提供**独立的 `7z.dll` 下载，只有安装包。因此走「官方件自举」：

1. 下载 `7zr.exe`（602 KB，官方独立解包器）
2. 下载 `7z<版本>-x64.exe`（官方安装包，本质是 7z SFX）
3. 用 `7zr` 解包，取出 `7z.dll` + `License.txt`

三个文件全部来自 `github.com/ip7z/7zip`。**该仓库是 7-Zip 官方发布点**（不是第三方镜像），
证据：7-zip.org 下载页的每一个下载链接都指向 `github.com/ip7z/7zip/releases/download/...`
（含首页主下载按钮）；该仓库所有提交作者均为 `Igor Pavlov`；官网版权声明同为 Igor Pavlov。

落地在 `tools\7zip\`（`.gitignore` 排除），csproj 的 `EnsureSevenZipDllAsset` target 在
`PrepareForBuild` 前把 `7z.dll` / `License.txt` 以 `LogicalName` 加入 `EmbeddedResource`。
**必须用 target 动态添加**：项目求值阶段文件可能还不存在（首次构建要现下载），
字面 `Include` 一个不存在的文件会直接让构建报错。

版本探测走 `releases/latest` 的 **302 重定向**读 tag，不查 GitHub API——未认证配额
60 次/小时，本地反复构建很容易耗尽（实测撞过 403）。下载失败只警告不失败：
没有内嵌时程序退回「需自行安装 7-Zip」的原行为。

| 参数                              | 作用                                                |
| --------------------------------- | --------------------------------------------------- |
| `-p:SevenZipVersion=26.05`        | 固定版本（产物可重现，CI 推荐）                     |
| `-p:SevenZipForceUpdate=true`     | 忽略缓存重新探测最新版                              |
| `-p:EnableEmbeddedSevenZip=false` | 不内嵌                                              |
| `-p:SevenZipAutoDownload=false`   | 禁止下载（本地已有 `tools\7zip\7z.dll` 时仍会内嵌） |

### 运行期：解压到用户缓存再加载

`EmbeddedSevenZip.TryExtractDll()` 把资源写到
`%LOCALAPPDATA%\ArchivePasswordTestTool\native\7z.dll`，再交给 `SevenZipLibrary`
按普通文件加载。

**为什么不直接在内存里映射 PE**：`7z.dll` 有大量内部导入与 TLS，内存加载需要自实现
PE 映射和导入修复，风险远高于收益。落地成文件还带来两个好处：用户可以用自己的
`7z.dll` 覆盖（满足 LGPL 的可替换要求），也能继续用 `-lib` 显式指定。

**并发安全**：多进程测密一次拉起 N 个工作进程，它们几乎同时走到这里。做法是
「写唯一临时名 → 原子改名」，并接受「别人已经写好」的结果（目标文件尺寸一致即视为成功）。

**开销**（AOT+UPX 产物实测）：冷启动 149 ms（含解压 1.9 MB），热启动 117 ms——
与内嵌前 121 ms 基本持平，热路径零开销。

### 解析优先级

| 顺序 | 来源                            | 说明                                                                                        |
| ---- | ------------------------------- | ------------------------------------------------------------------------------------------- |
| ①    | 系统已安装的 7-Zip              | 不写盘；用户自己更新过就跟着用新版                                                          |
| ②    | `-lib` 指定 / 程序目录 `7z.dll` | 便携场景；`-lib` 指向的文件不存在时**直接报错**，不静默改用内嵌版——否则会掩盖用户的配置错误 |
| ③    | 内嵌版                          | 「没装 7-Zip」的兜底                                                                        |
| ④    | 都没有                          | 报错 + 官方下载引导 + 等待确认退出                                                          |

诊断开关 `APTT_FORCE_EMBEDDED_7Z=1` 跳过 ①，强制走内嵌版：既可验证内置 dll 本身可用，
也供「系统那份 7-Zip 版本过旧/损坏」的用户临时绕开。

### 许可证（必须遵守）

`7z.dll` 采用 **GNU LGPL 2.1+**（含 unRAR 限制）+ 部分 BSD，**不是公有领域**。
官方 `license.txt` 明确要求「以二进制形式再分发时必须附带相关许可证信息」，因此：

- `License.txt` 与 dll 一起内嵌，运行期写到解压目录旁
- 构建期若解包结果里**没有** `License.txt`，直接放弃内嵌（宁可不内嵌也不能不合规）
- 落地成独立文件而非内存映射，客观上也满足了 LGPL 对「可替换库」的要求

### 体积代价（实测）

| 形态                   | 内嵌前   | 内嵌后   | 增量                          |
| ---------------------- | -------- | -------- | ----------------------------- |
| 单文件 + 裁剪 + bundle | 12.46 MB | 13.31 MB | +0.81 MB                      |
| Native AOT（未压缩）   | 9.38 MB  | 11.30 MB | +1.92 MB（正好等于 dll 大小） |
| AOT + UPX              | 3.59 MB  | 4.17 MB  | +0.55 MB                      |

### 已知不足

7-Zip 官方**不提供**发布包的 GPG 签名或独立哈希列表（release 正文只有更新日志），
因此构建期下载只依赖 HTTPS 传输安全，没有密码学级来源校验。要更稳就在 CI 里
固定版本并缓存审核过的 `tools\7zip\`。

> 取证坑：想用 SourceForge 交叉对比哈希是**无效**的——`sourceforge.net/.../download`、
> `downloads.sourceforge.net/...`、`master.dl.sourceforge.net/...` 三条路径都返回
> 120 KB 左右的 HTML 拦截页。只看哈希会误判成「文件被篡改」，必须先检查首两字节是否 `MZ`。

### 旧版 Windows 兼容（CET）

.NET 9 起 SDK 默认给 apphost / singlefilehost 打 `/CETCOMPAT`。旧版 Win10（累积更新未打全）、
旧 Win11 等在启用影子栈后缺少特殊用户态 APC，.NET 10 会在**托管 Main 之前** fail-fast
（`Your Windows doesn't fully support CET`，0x80131506）——`try/catch` 捕不到，用户只看到闪退。

项目用 `<CetCompat>false</CetCompat>` 退出该标记，使进程不启用影子栈，代价是失去 CET 加固。
CET 位**不在** `OptionalHeader.DllCharacteristics`，而在调试目录
`IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS(20)` 指向的 ushort 低位 `0x1`。

## 持续集成与发布

两个工作流都在 `windows-latest` 上运行（项目 Windows-only：COM 互操作 + Win32 API），
共用 `.github/actions/build-verify`：Restore → Build → Test → Publish 单文件 → Publish AOT → 冒烟测试（两种形态共用一份断言）。

| 工作流    | 触发                                    | 内容                                            |
| --------- | --------------------------------------- | ----------------------------------------------- |
| `CI`      | 推送 `master`、PR、手动                 | 上述全部，两种形态各留一份 artifact             |
| `Release` | 推送 `v*` 或 `[0-9]*` tag、手动指定 tag | 上述全部 + 把 **AOT 产物**上传到 GitHub Release |

发布新版本只需打 tag（版本号由 tag 注入，不需要改源码）：

```text
git tag -a 2.1.0-Release -m "2.1.0-Release"
git push origin 2.1.0-Release
```

- **必须用 `-a`（annotated）**：`2.0.0` 起的正式 tag 都是 annotated，带 tagger 与日期，
  可 GPG 签名。lightweight（`git tag` 不带 `-a`）只是 commit 指针——凡依赖 tag 对象的
  工具都会看不到它（`git describe` 默认忽略、`git push --follow-tags` 不会推、`git show`
  或 `git tag -n` 也没有标签消息）。发布流程本身不受影响（版本号取自 tag **名字**，
  `--verify-tag` 与 `checkout` 都正常），但会留下与历史不一致、且无法事后补救的记录——
  已发布的 tag 删掉会连带删掉 GitHub Release 并重跑整个工作流。
- tag 格式 `v2.0.0` / `2.0.0` / `2.0.0-Release` 均可；`Upgrade` 会剥掉 `v` 前缀，
  无后缀按 `Release` 处理，同版本号时按 `-Type` 后缀比较等级
- 工作流硬校验版本号为三段 `x.y.z`，不合法直接失败
- 校验值由 GitHub Release 自身提供，不额外生成 sha256

### 为什么发布 AOT 而不是单文件

| 形态          | 体积     | 启动（热） | 是否发布         |
| ------------- | -------- | ---------- | ---------------- |
| AOT + UPX     | 4.17 MB  | 108 ms     | ✅ Release 资产  |
| 单文件 + 裁剪 | 13.31 MB | 335 ms     | 仅在 CI 构建验证 |

- **只能挂一个 exe**：`Upgrade.MainAsset` 取「第一个 `.exe` 资产」，两个 exe 会让自更新在
  两种形态间不确定地切换（用户装的是 13 MB 版，更新后变成 4 MB 版，或反之）
- 单文件形态仍在 CI 里构建，因为 `PublishTrimmed` + 内置 COM 的坑只在裁剪路径暴露，
  砍掉这一步等于放弃对该形态的回归保护（用户仍可自行用 pubxml 构建）
- AOT 没有 bundle 自解压步骤，旧版 Windows 上受 CET / 安全软件干扰的概率更低

### CI 的三条硬门禁

这三条都是「漏了以后本地/CI 全绿但产物是坏的」的典型，因此必须显式断言：

1. **`-p:RequireEmbeddedSevenZip=true`**：内嵌 7z.dll 拿不到时**构建失败**而不是警告。
   本地默认仍是警告（缺网也能开发），但 CI 上必须严格——否则下载失败只会产生一条 warning，
   而 smoke test 因为 runner 装了 7-Zip 会走「已安装」分支照样通过，
   于是不自包含的产物静默上传，恰好是本功能要消除的那个问题。
2. **强制内嵌的 smoke test**：`APTT_FORCE_EMBEDDED_7Z=1` + 先清
   `%LOCALAPPDATA%\ArchivePasswordTestTool\native`。不清缓存就命中上次解压的文件，
   证明不了「资源真的在产物里」。
3. **UPX 必须真的压缩过**：直接问 `upx -l <exe>` 退出码，而不是比对体积阈值
   （阈值会随 .NET 版本漂移）。压缩器没跑时产物仍能运行，只有体积变成 11 MB——
   光看「能跑通」测不出来。

### 冒烟测试矩阵（一份断言，两种形态）

`Smoke test published binaries` 一步跑完 **{单文件, AOT} × {默认路径, 强制内嵌}** 四次真实解密：

|        | 默认路径 | 强制内嵌 |
| ------ | -------- | -------- |
| 单文件 | ✅       | ✅       |
| AOT    | ✅       | ✅       |

- **两种形态共用同一份断言**（退出码 / 密码 / 版本号 / 内嵌 dll 落地）。原先各写一份，
  结果是版本号断言只加在了单文件那份里，AOT 那份漏了——典型“改一处漏一处”。
  合并后新增一形态只需在 `$products` 里加一行。
- **每形态跑两条 dll 解析路径**是有意的不对称消除：只测内嵌版看不出“用户装了 7-Zip”
  这个最常见场景是否正常；只测默认路径则永远测不到内嵌资源（runner 自带 7-Zip，
  优先级在已安装版上）。
- 形态专属检查单独做：两种形态都必须是单文件（语言资源已内嵌），且 **AOT** 还必须被 UPX 压缩过
  （单文件形态不能用 UPX，见上文“单文件形态不用 UPX”）。

另外构建期依赖版本在 `ci.yml` / `release.yml` 的 `env` 里**固定**（`SEVEN_ZIP_VERSION`、
`UPX_VERSION`）而不是跟 latest：同一 commit 两次构建要能拿到同一份 7z.dll 与 UPX，
否则产物不可重现。改版本即触发 `actions/cache` 的 key 变化，下次重新下载。

## 编码约定

- 源码统一 **UTF-8 无 BOM**（根目录 `.editorconfig` 声明 `charset = utf-8`），行尾 CRLF
- 命名：类型/方法/属性 PascalCase，私有字段 `_camelCase`，参数 camelCase，**不使用下划线命名**
- 修改 UTF-8 文件时避免使用 PowerShell 5.1 的 `Get-Content/Set-Content` 往返
  （按 ANSI/GBK 解码会破坏中文），需要脚本处理时用 .NET 的
  `[System.IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)` 或 Python `newline=''`
