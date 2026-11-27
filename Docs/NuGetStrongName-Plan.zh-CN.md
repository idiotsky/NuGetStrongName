# NuGetStrongName — 实施计划

本文为[英文权威计划](NuGetStrongName-Plan.md)的中文翻译，两份文档同步维护。

Status: in progress
Slice tag: `strongname`
Delivery: 按切片完成验收及独立评审，经明确授权提交实现和双语计划；等待提交保持 `in progress`。本次仅迁移计划，不授权实施、提交或发布。

## 目标与继续方式

构建通用、独立的跨平台 NuGet strong-name CLI，并发布可供其他工具使用的固定版本 GitHub Release。使用共享 `continue-plan`
技能，每次继续一个切片或交付收尾；使用 `pending`、`in progress`、`done`，记录实际命令、结果、阻塞和下一步。

## 切片

| # | 结果                              | Status      | 依赖 |
|---|-----------------------------------|-------------|------|
| 1 | 通用跨平台 .NET CLI、签名及包验证 | in progress | 无   |

### Slice 1 — 实现通用跨平台 .NET CLI

范围与分发：

- 在本仓库实现可测试的跨平台 .NET Console 工具，不引用消费方项目或 WPF。直接负责下载、检查、程序集引用改写、签名、重打包及结果报告。
- 使用经检查许可及固定版本的程序集元数据处理库（优先验证 Mono.Cecil），不使用第三方签名 CLI，也不自行实现 PE/CLI
  解析或密码算法。先验证现代 IL、引用程序集及两平台签名/验证能力。
- 以 `PackAsTool` 构建固定版本 .NET tool `.nupkg`，命令名 `nuget-strongname`，包 ID `NuGetStrongName.Tool`。初版目标为
  `net11.0`，明确记录 .NET 11 运行时及工具安装所需 SDK；跨平台不代表无需 .NET 环境。不引入多 RID 自包含发布或双重分发方案。
- 提供 `sign-package`、`verify-package`、`--help`、`--version`。签名输入为互斥的 `--package-id` 加 `--version`，或 `--input`
  本地包；另有显式 `--key-file`、`--output` 和依赖映射参数。包版本参数属于子命令，顶层 `--version`
  表示工具版本。已强命名检查完成前不要求密钥和输出参数，需要签名而缺少参数时明确失败，不弹交互提示；由调用方询问用户。
- 从目标仓库上下文解析有效 NuGet 配置、源映射及认证；允许显式传入仓库根目录/配置位置，不要求特定目录布局。密钥及输出目录来自调用方参数，工具不内置消费仓库的专用路径。
- 明确支持 Windows 和 Linux，不引用 WPF、WinForms、注册表，不依赖 Windows 专用 sn.exe/ildasm.exe/ilasm.exe 或
  PowerShell。路径使用平台 API，覆盖空格和大小写敏感性；前置条件缺失时明确诊断。
- CLI 源码、NUnit 测试及发布说明由本仓库维护；消费仓库仅使用固定发布版本，不添加跨仓库源码引用。
- 工具自身启用真实 strong-name 签名，使用本仓库独立的 `Key/NuGetStrongName.snk`，不复用消费仓库的密钥。该 2048
  位密钥已生成、导入验证并提交；后续项目加入 SignAssembly 和相对仓库根解析的
  AssemblyOriginatorKeyFile。所有自有程序集包括测试按共享配置签名；验证发布程序集及其公钥令牌，重新构建后身份保持一致。此工具构建密钥不作为处理第三方包的默认密钥，也不包含在任何发布产物中。
- 目标是在 GitHub 维护并发布工具：固定工具版本，记录可复现构建/pack 命令，准备 GitHub Release 的 .NET tool `.nupkg`、SHA-256
  清单及使用说明。标签使用 `v<version>`，资产名固定为 `NuGetStrongName.Tool.<version>.nupkg`，便于调用方按 URL 下载固定版本并校验
  SHA-256；已发布的 Release 不替换资产。完成本地和双平台验收后再按用户授权提交、推送、创建标签及 Release。NuGet.org
  发布不在当前范围。不得将密钥、凭据或输入包包含在发布产物中。

必须保留的行为：

- 检查相关托管编译/运行程序集和引用身份；已满足强命名且无需改写时，退出码 0、JSON `status: already-strong-named`、原包
  ID/版本、`outputPath: null`。不读取私钥、不要求签名工具、不创建输出。不能将 NuGet 包签名或仅有令牌等同于有效 strong
  name；考虑引用程序集签名约定。
- 将每组兼容 TFM/RID 资产内相互引用的 DLL 作为一个集合，先建立精确的程序集身份映射，再改写所有受影响 AssemblyRef
  并用匹配密钥签名；不只逐个签名或按名称前缀猜测，不跨 TFM 混合相同名称的 DLL。支持跨包显式依赖映射，必要时处理
  InternalsVisibleTo；不自动递归重签全部第三方依赖。
- 签名覆盖包内所有托管程序集：`lib/`、`ref/`、`runtimes/*/lib/` 下的每个 TFM/RID 都要处理，不能只处理当前平台或最新 TFM。所有
  TFM 使用同一密钥，同名程序集签名后的公钥令牌一致，程序集版本不变。`already-strong-named` 按全部 TFM 判断：任一 TFM
  未签名即需处理未签名部分，并保留已有的有效签名。JSON 报告按 TFM/RID 列出逐程序集结果。
- QuickBooksApi（`IppDotNetSdkForQuickBooksApiV3`）作为必需真实回归案例：其多个未签名且相互引用的 DLL
  必须一起处理，包括传递引用；若实际存在环，也必须正确处理。仅签名 DLL 而留下旧的空令牌引用不算成功。
- 保留有效原厂签名；若改写已签名程序集需要无法取得的匹配私钥，明确失败，不静默更换供应商身份。保留全部
  TFM/RID、ref/lib、资源、原生资产、构建文件和许可证，不签原生 DLL。
- 按调用方显式约定生成自制包，例如同 ID、`X.Y.Z-signed`；不把特定项目约定强加给所有调用方。正确更新映射中的包依赖及程序集引用，移除因重打包失效的原
  NuGet 包签名，不新增 NuGet 包签名。
- 唯一临时目录、输入不可变、不覆盖已有输出、验证完成后才生成最终产物。CLI stdout 为 JSON，stderr 为诊断，退出码 0 成功、1
  处理/验证失败、2 参数错误；正确传播底层失败、超时及带空格路径，报告版本、哈希和逐程序集结果，不泄露凭据或密钥。

验收与验证：

- 按 TDD 验证新增 CLI 行为；覆盖已签名无操作、混合/无效签名、相互引用、多 TFM/ref/RID、缺失依赖、空格路径、工具失败、超时、冲突及输入完整性。
- 真实 QuickBooksApi 原包经过下载、集合引用改写、签名及重打包后，逐项检查变更引用的目标令牌与实际 DLL
  一致，所有必需签名有效；从隔离本地源干净还原，编译并运行不调用远程 QuickBooks 服务的代表性消费者。
- 使用多 TFM 包（QuickBooksApi 若含多个 TFM 则直接使用）验证每个 TFM/RID 下的每个 DLL 均已签名，且同名程序集公钥令牌一致。
- 在 Windows 和 Linux 上分别执行同一套确定性测试及真实 QuickBooksApi 下载/重签/重打包/消费者验证，检查包布局及依赖身份的一致性，不要求
  ZIP 字节哈希跨平台相同。Linux 验收未执行或失败时明确标记阻塞，不以 Windows 通过替代。Windows 专用包资产保留完整，但不因此承诺其可在
  Linux 运行。
- 验证其他所需自制包所覆盖的资产类型，并安排独立评审。记录 CLI 所用库及许可、固定版本、确切命令、实际结果和验证限制。不因 CLI
  运行结束就认为产物验收通过；消费仓库自行管理旧工具退役。

Decisions:

- 依赖库：使用 Mono.Cecil 0.11.6（MIT）读取、改写和签名程序集。可行性已于 2026-11-21 验证：在 .NET 11 上通过
  `WriterParameters.StrongNameKeyBlob` 签名，无需 `StrongNameKeyPair`。NuGet.Protocol 与 NuGet.Credentials
  8.0.0（Apache-2.0）负责配置、源映射、凭据和下载。NuGet.Packaging 8.0.0 会传递引入有漏洞的 System.Security.Cryptography.Pkcs
  8.0.1，因此以传递固定方式改用 10.0.14。测试使用 NUnit 5.0.0、NUnit3TestAdapter 6.3.1 和 Microsoft.NET.Test.Sdk
  18.12.0。所有固定版本的发布时间都早于当前两周以上。SDK 由 `global.json` 固定为 11.0.100（`latestFeature`）。
- 签名验证：Cecil 不提供验证功能，nuget.org 上也没有跨平台验证库。`StrongNameIdentity` 基于 System.Reflection.Metadata 解析的
  PE 头计算 ECMA-335 II.6.2.1.3 签名区域哈希，并用 BCL RSA 校验；`StrongNameKey` 负责解析 CAPI `.snk`
  数据块。这是有意设定的窄范围例外：没有自写密码原语，也没有自写元数据解析器。Windows 测试会用 `sn.exe -vf` 交叉核对验证结果。
- CLI 约定：依赖映射通过可重复的 `--map-package <id>=<newId>@<version>` 与 `--map-assembly <name>[=<token>]`
  参数提供，不使用映射文件；不带令牌的映射表示使用 `--key-file` 的令牌。`--output` 是目录，输出文件名为
  `<output-id>.<output-version>.nupkg`。签名时必须提供 `--output-version`，确保不会遮蔽上游身份；`--output-id` 默认与输入
  ID 相同。另提供 `--repository`、`--configfile`、`--timeout`（默认 600 秒）。`--help` 与 `--version` 输出纯文本，其余每条命令的结果都是一份
  JSON 文档。下载得到的输入报告为 `inputPath: null` 并附带 `inputSource`。
- 强命名规则：实现程序集需要有效签名，或使用 ECMA 平台密钥。位于 `ref/` 或标记 `[ReferenceAssembly]`
  的程序集只需带有公钥（遵循公开签名约定）。只改写空令牌引用。名称解析依次查找同一资产组、同 TFM 的 `lib/`
  组；只有全部候选最终令牌一致时，才在整个包范围内解析。附属资源程序集会签名，但不参与身份映射。有效签名逐字节保留；改写或重签只能使用该程序集自身的密钥。
- 符号文件：portable 与 embedded PDB 随程序集一起改写。Cecil 无法跨平台写入 Windows 原生 PDB（QuickBooksApi 附带的就是这种
  PDB），因此原样保留，并在报告中给出警告。

Verification（Windows 11 与 Ubuntu 22.04，.NET SDK 11.0.100，2026-11-21）：

- 独立评审（code-reviewer 代理，静态阅读代码）：0 个严重，2 个高，7 个中，9 个低。除以下三项外，其余问题均已修复，并在
  `ReviewRegressionTests` 中补充了回归测试：
    - 已修复 H1：增加兜底异常处理，确保输出 JSON 并以退出码 1 结束；nuget.config 或清单格式错误时会正常报错失败，不再因
      `Single`/`First` 崩溃。
    - 已修复 H2：需要签名的混合模式或 ReadyToRun 映像会直接失败，不做改写。
    - 已修复 M1：已保留程序集引用包外未签名程序集时，签名和验证两条路径都统一报告为警告。
    - 已修复 M2：拒绝不安全的条目名（`..`、`.`、空段、`:`、非法字符）。
    - 已修复 M3：在解析参数阶段校验 ID 和版本（包括 `--map-package` 中的版本范围），不合法时以退出码 2 结束。
    - 已修复 M4：截断的 PE 报告为 `Invalid`，不会崩溃。
    - 已修复 M5：损坏的托管映像会报错失败，不再被当作原生映像。
    - 已修复 M6：解压上限为 100,000 个条目、4 GiB，读取 XML 时禁用 DTD 处理。
    - 已修复 M7 与 L8：移除 Authenticode 签名和 `.signature.p7s` 时会给出警告。
    - 已修复 L3：记录日志前对源 URL 做脱敏处理。
    - 已修复 L4：在 Unix 上以 0700 权限创建工作目录。
    - 已修复 L5：拒绝仅大小写不同的条目。
    - 已修复 L6：只改写 `_rels/.rels` 和 core-properties 下的 `.psmdcp`；`.rels` 无法完成重命名时直接失败。
    - 已修复 L9：解压过程会检查超时。
    - 延后到 Carry-over 的有三项：L1（增强型强命名密钥的哈希算法优先级）、L2（在签名前而不是最终验证时，识别部分 TFM 由厂商签名、其余
      TFM 未签名的包）、L7（已保留程序集中的 IVT）。M7 的 Authenticode 警告目前没有测试夹具覆盖。
    - 变异检查：移除 ILOnly 防护后有 1 个测试失败；移除条目名校验后有 4 个测试失败。
- 修复后执行 `dotnet test NuGetStrongName.slnx`：53 个通过，0 个失败（修复前为 34 个通过，0
  个失败）。测试覆盖已签名包无操作；混合签名、无效签名、外部密钥签名；含循环的相互引用；IVT 与 `typeof` 改写；多
  TFM、ref、RID、附属程序集和原生资产；缺失依赖与已映射依赖；带空格路径；损坏输入；无响应源超时；包不存在；源映射；输出冲突；输入不可变；输出
  ID 重命名；portable PDB。`sn.exe` 对照测试实际执行，没有被跳过。测试与实现同步编写，并非严格先写失败测试；作为补偿做了变异检查：移除引用改写后
  5 个测试失败，移除 IVT 改写后 1 个测试失败。
- `dotnet pack src/NuGetStrongName.Tool/NuGetStrongName.Tool.csproj -c Release -o artifacts` 生成
  `NuGetStrongName.Tool.0.1.0.nupkg`，包内不含 `.snk`。随后用
  `dotnet tool install --tool-path <tmp> NuGetStrongName.Tool --version 0.1.0 --add-source artifacts` 安装；`--version`
  输出 `0.1.0`。
- 工具身份：工具及测试程序集均通过 `sn -vf`，令牌为 `43203feb910a45dc`（来自 `Key/NuGetStrongName.snk`）。用
  `--no-incremental` 重新构建 Release，输出与之前逐字节相同。
- QuickBooksApi：运行
  `nuget-strongname sign-package --package-id IppDotNetSdkForQuickBooksApiV3 --version 14.7.1.6 --key-file "<tmp>/keys/consumer test.snk" --output "<tmp>/local feed" --output-version 14.7.1.6-signed`
  ，退出码 0，耗时约 4.5 秒。使用 `sn -k 2048` 生成的一次性测试密钥（令牌 `887f7ba4a4ba02b9`）签名了 48 个程序集（16 个 ×
  net461/net472/netstandard2.0），并改写了全部 `Intuit.*` 引用；对保留的原生 PDB 给出警告。
  `verify-package --public-key-token 887f7ba4a4ba02b9` 退出码 0，48/48 个 DLL 通过 `sn -vf`。除移除的 `.signature.p7s`
  外，条目列表与上游一致。上游包不含循环引用、`ref/` 或 `runtimes/` 资产，这些情况由测试夹具覆盖。
- 消费者：一个强命名的 `net48;net11.0` 项目（启用 `TreatWarningsAsErrors`，出现 CS8002 即构建失败）通过源映射和独立的
  `globalPackagesFolder`，从隔离本地源还原 `14.7.1.6-signed`。项目构建成功，并在不调用远程服务的情况下运行了
  `ServiceContext`、`DataService`、`QueryService<Customer>` 和 `Customer`。两个 TFM 上加载的所有 `Intuit.Ipp.*` 程序集令牌均为
  `887f7ba4a4ba02b9`，没有任何空令牌引用。过程中遇到两个环境问题，均不属于工具缺陷：上游 SDK 的 NU1902/NU1904
  审计警告只在消费者项目中豁免；为避开 Windows MAX_PATH 限制，将运行目录移到了更短的临时路径。
- 评审修复后，在 `<tmp>` 中用 Release 构建重新执行验收，结果相同：退出码 0，48 个程序集已签名。`verify-package` 报告
  `verified`，`sn -vf` 48/48 通过，消费者在 net11.0 和 net48 上均输出 `CONSUMER OK`。报告中现在包含 `.signature.p7s`
  移除警告。在消费者仓库内运行工具时，因该仓库的源映射只允许从本地源获取此包，工具正确地以 "not found" 报错失败。
- Linux 验收：本工作站无法运行 Docker，因此改用用户提供的一台远程 Ubuntu 22.04 x64 主机（通过 SSH），并用 `dotnet-install.sh`
  在私有目录中安装 SDK 11.0.100。将工作区打包，连同验收脚本（保存在被 git 忽略的 `artifacts/linux-acceptance/`
  中）一起，在带空格的工作路径下执行以下步骤，并与 Windows 产物比较：
    - `dotnet test NuGetStrongName.slnx -c Release`。
    - 打包，然后从全新的包目录安装工具。
    - 从 nuget.org 下载 QuickBooksApi 14.7.1.6，用同一个测试密钥签名。
    - `verify-package --public-key-token 887f7ba4a4ba02b9`。
    - 运行 net11.0 消费者（net48 无法在 Linux 上运行）。
    - 与 Windows 产物比较：状态、输入 SHA-256、输出 ID/版本、令牌、各程序集资产组、依赖变更、条目列表以及每个条目的 SHA-256。
- 第一次 Linux 运行全部通过，但 `.nuspec` 和 `.psmdcp` 与 Windows 版本仅在换行符上不同：上游文件使用 CRLF，而
  `XDocument.Save` 写出的是当前平台的换行符，因此 Linux 上变成了 LF。已修复：`ManifestEditor` 改为通过 `XmlWriter`
  保存，并沿用文件原有的换行符。回归测试 `Edited_manifests_keep_their_line_endings_on_every_platform`（CRLF 与 LF
  两种情况）在旧的保存方式下会失败。该修复是在独立评审之后完成的，尚未经过独立评审。
- 修复后，Windows 上 `dotnet test` 为 55 个通过、0 个失败。在 Windows 上重新执行 QuickBooksApi 签名和验证（退出码
  0），产出的条目与之前的 Windows 产物逐字节相同，因此之前的 `sn -vf` 和消费者结果仍然有效。Linux 重新运行全部通过：
    - 测试：54 个通过、0 个失败、1 个跳过（仅限 Windows 的 `sn.exe` 对照测试）。
    - 包状态为 `signed`，共 48 个程序集，并给出原生 PDB 和 `.signature.p7s` 警告；`verify-package` 报告 `verified`。
    - 消费者输出 `CONSUMER OK`，加载的 8 个 `Intuit.Ipp.*` 程序集令牌均为 `887f7ba4a4ba02b9`。
    - 比较结果所有字段一致，全部 149 个条目逐字节相同（0 个不同），依赖身份也一致。
- 未验证：其他自制包的资产类型（尚未指定具体包）；凭据提供程序插件访问需认证的源；Release 发布。

Resume：为本切片（实现、测试和两份计划译本）申请提交授权；也可以先请人独立评审 `ManifestEditor.SaveXml` 中的换行符修复。提交后将切片
1 标记为完成。

Carry-over:

- 只有在双平台验收通过并获得明确授权后，才发布 GitHub Release `v0.1.0`（`NuGetStrongName.Tool.0.1.0.nupkg` + `SHA256SUMS`
  ）。README 中的安装 URL 暂留 `<owner>` 占位符，待仓库 remote 建立后填写。
- 消费方需要指出还有哪些自制包的资产类型需要覆盖。
- 延后处理的低严重度评审项：
    - L1：按 CLR 的哈希算法优先级，验证增强型强命名（SHA-256 签名密钥）。
    - L2：让规划器在签名前就识别出同名程序集在部分 TFM 中由厂商签名、在其他 TFM 中未签名的包；目前要到最终验证时才会失败。
    - L7：确定已保留程序集中的 IVT 友元是否需要处理。
    - 为 Authenticode 移除警告补充测试夹具。

## 验证与交接记录

从消费仓库的升级计划迁入 CLI 开发要求，消费方工具退役留在原仓库。`Key/NuGetStrongName.snk`
已生成、导入验证，并与本计划一同提交。保留已有密钥，不重新生成。

2026-11-21：切片 1 已实现，并在 Windows 和 Linux 上通过验收（见切片 1 的 Verification）。改动尚未提交，正在等待提交授权。下一步：切片
1 的 Resume。
