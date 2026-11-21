# NuGetStrongName — 实施计划

本文为[英文权威计划](NuGetStrongName-Plan.md)的中文翻译，两份文档同步维护。

Status: pending
Slice tag: `strongname`
Delivery: 按切片完成验收及独立评审，经明确授权提交实现和双语计划；等待提交保持 `in progress`。本次仅迁移计划，不授权实施、提交或发布。

## 目标与继续方式

构建通用、独立的跨平台 NuGet strong-name CLI，并发布可供其他工具使用的固定版本 GitHub Release。使用共享 `continue-plan` 技能，每次继续一个切片或交付收尾；使用 `pending`、`in progress`、`done`，记录实际命令、结果、阻塞和下一步。

## 切片

| # | 结果 | Status | 依赖 |
|---|------|--------|------|
| 1 | 通用跨平台 .NET CLI、签名及包验证 | pending | 无 |

### Slice 1 — 实现通用跨平台 .NET CLI

范围与分发：
- 在本仓库实现可测试的跨平台 .NET Console 工具，不引用消费方项目或 WPF。直接负责下载、检查、程序集引用改写、签名、重打包及结果报告。
- 使用经检查许可及固定版本的程序集元数据处理库（优先验证 Mono.Cecil），不使用第三方签名 CLI，也不自行实现 PE/CLI 解析或密码算法。先验证现代 IL、引用程序集及两平台签名/验证能力。
- 以 `PackAsTool` 构建固定版本 .NET tool `.nupkg`，命令名 `nuget-strongname`，包 ID `NuGetStrongName.Tool`。初版目标为 `net11.0`，明确记录 .NET 11 运行时及工具安装所需 SDK；跨平台不代表无需 .NET 环境。不引入多 RID 自包含发布或双重分发方案。
- 提供 `sign-package`、`verify-package`、`--help`、`--version`。签名输入为互斥的 `--package-id` 加 `--version`，或 `--input` 本地包；另有显式 `--key-file`、`--output` 和依赖映射文件参数。包版本参数属于子命令，顶层 `--version` 表示工具版本。已强命名检查完成前不要求密钥和输出参数，需要签名而缺少参数时明确失败，不弹交互提示；由调用方询问用户。
- 从目标仓库上下文解析有效 NuGet 配置、源映射及认证；允许显式传入仓库根目录/配置位置，不要求特定目录布局。密钥及输出目录来自调用方参数，工具不内置消费仓库的专用路径。
- 明确支持 Windows 和 Linux，不引用 WPF、WinForms、注册表，不依赖 Windows 专用 sn.exe/ildasm.exe/ilasm.exe 或 PowerShell。路径使用平台 API，覆盖空格和大小写敏感性；前置条件缺失时明确诊断。
- CLI 源码、NUnit 测试及发布说明由本仓库维护；消费仓库仅使用固定发布版本，不添加跨仓库源码引用。
- 工具自身启用真实 strong-name 签名，使用本仓库独立的 `Key/NuGetStrongName.snk`，不复用消费仓库的密钥。该 2048 位密钥已生成、导入验证并提交；后续项目加入 SignAssembly 和相对仓库根解析的 AssemblyOriginatorKeyFile。所有自有程序集包括测试按共享配置签名；验证发布程序集及其公钥令牌，重新构建后身份保持一致。此工具构建密钥不作为处理第三方包的默认密钥，也不包含在任何发布产物中。
- 目标是在 GitHub 维护并发布工具：固定工具版本，记录可复现构建/pack 命令，准备 GitHub Release 的 .NET tool `.nupkg`、SHA-256 清单及使用说明。标签使用 `v<version>`，资产名固定为 `NuGetStrongName.Tool.<version>.nupkg`，便于调用方按 URL 下载固定版本并校验 SHA-256；已发布的 Release 不替换资产。完成本地和双平台验收后再按用户授权提交、推送、创建标签及 Release。NuGet.org 发布不在当前范围。不得将密钥、凭据或输入包包含在发布产物中。

必须保留的行为：
- 检查相关托管编译/运行程序集和引用身份；已满足强命名且无需改写时，退出码 0、JSON `status: already-strong-named`、原包 ID/版本、`outputPath: null`。不读取私钥、不要求签名工具、不创建输出。不能将 NuGet 包签名或仅有令牌等同于有效 strong name；考虑引用程序集签名约定。
- 将每组兼容 TFM/RID 资产内相互引用的 DLL 作为一个集合，先建立精确的程序集身份映射，再改写所有受影响 AssemblyRef 并用匹配密钥签名；不只逐个签名或按名称前缀猜测，不跨 TFM 混合相同名称的 DLL。支持跨包显式依赖映射，必要时处理 InternalsVisibleTo；不自动递归重签全部第三方依赖。
- 签名覆盖包内所有托管程序集：`lib/`、`ref/`、`runtimes/*/lib/` 下的每个 TFM/RID 都要处理，不能只处理当前平台或最新 TFM。所有 TFM 使用同一密钥，同名程序集签名后的公钥令牌一致，程序集版本不变。`already-strong-named` 按全部 TFM 判断：任一 TFM 未签名即需处理未签名部分，并保留已有的有效签名。JSON 报告按 TFM/RID 列出逐程序集结果。
- QuickBooksApi（`IppDotNetSdkForQuickBooksApiV3`）作为必需真实回归案例：其多个未签名且相互引用的 DLL 必须一起处理，包括传递引用；若实际存在环，也必须正确处理。仅签名 DLL 而留下旧的空令牌引用不算成功。
- 保留有效原厂签名；若改写已签名程序集需要无法取得的匹配私钥，明确失败，不静默更换供应商身份。保留全部 TFM/RID、ref/lib、资源、原生资产、构建文件和许可证，不签原生 DLL。
- 按调用方显式约定生成自制包，例如同 ID、`X.Y.Z-signed`；不把特定项目约定强加给所有调用方。正确更新映射中的包依赖及程序集引用，移除因重打包失效的原 NuGet 包签名，不新增 NuGet 包签名。
- 唯一临时目录、输入不可变、不覆盖已有输出、验证完成后才生成最终产物。CLI stdout 为 JSON，stderr 为诊断，退出码 0 成功、1 处理/验证失败、2 参数错误；正确传播底层失败、超时及带空格路径，报告版本、哈希和逐程序集结果，不泄露凭据或密钥。

验收与验证：
- 按 TDD 验证新增 CLI 行为；覆盖已签名无操作、混合/无效签名、相互引用、多 TFM/ref/RID、缺失依赖、空格路径、工具失败、超时、冲突及输入完整性。
- 真实 QuickBooksApi 原包经过下载、集合引用改写、签名及重打包后，逐项检查变更引用的目标令牌与实际 DLL 一致，所有必需签名有效；从隔离本地源干净还原，编译并运行不调用远程 QuickBooks 服务的代表性消费者。
- 使用多 TFM 包（QuickBooksApi 若含多个 TFM 则直接使用）验证每个 TFM/RID 下的每个 DLL 均已签名，且同名程序集公钥令牌一致。
- 在 Windows 和 Linux 上分别执行同一套确定性测试及真实 QuickBooksApi 下载/重签/重打包/消费者验证，检查包布局及依赖身份的一致性，不要求 ZIP 字节哈希跨平台相同。Linux 验收未执行或失败时明确标记阻塞，不以 Windows 通过替代。Windows 专用包资产保留完整，但不因此承诺其可在 Linux 运行。
- 验证其他所需自制包所覆盖的资产类型，并安排独立评审。记录 CLI 所用库及许可、固定版本、确切命令、实际结果和验证限制。不因 CLI 运行结束就认为产物验收通过；消费仓库自行管理旧工具退役。

Resume: 先确认元数据处理库可行性，并为 CLI 添加多 DLL 引用改写的失败测试；尚未实现工具、运行签名。

## 验证与交接记录

从消费仓库的升级计划迁入 CLI 开发要求，消费方工具退役留在原仓库。`Key/NuGetStrongName.snk` 已生成、导入验证，并与本计划一同提交；未创建项目，未执行构建、测试、签名包转换或发布。下一步：实施获请求后从切片 1 开始。保留已有密钥，不重新生成。
