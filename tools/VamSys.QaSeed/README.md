# VamSys.QaSeed — 人工查看的数据、打包检查与诊断回放

net10.0 控制台程序，只提供命令行入口，供 `scripts/open-datagrid-qa.ps1` 和打包脚本调用；不带参数或参数不对时提示用法并以退出码 2 结束。它引用启用 `VamSysTestSupport=true` 的存储与基础设施构建（输出在 artifacts/test-build），不进入正式包。[C# 窗口测试](../../tests/VamSys.UiTests/README.md)不经过这里，在进程内用 `DataUiFixture` 准备数据、直接读存储核对。

## 命令

| 命令 | 作用 |
|---|---|
| `--seed-data-ui <目录> <行数>` | 人工查看数据页用的合成航线 |
| `--verify-package <目录>`、`--verify-qa-package <目录>` | 打包检查（`PackageChecks`），`scripts/build.ps1 -Publish` 每次发布都会运行 |
| `--export-diagnostic-replay <JSON路径>` | 通过模拟 422/500 生成脱敏诊断与 SHA-256 |

所有 `--seed-*` 入口在写入前都会拒绝已有文件的目录。

~~~powershell
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --verify-package artifacts/flightops-app
dotnet run --no-build --project tools/VamSys.QaSeed -c Release -- --verify-qa-package artifacts/v3-qa
~~~

## 打包检查

`PackageChecks.Verify` 要求正式包和 QA 包都含有运行所需文件、不含开发目录与源文件、不含测试或工具程序集、文档链接有效；正式包不含调试符号、QA 入口（`CreateVerification` 等）、QA 钩子实现、`VAMSYS_QA_` 开关字符串、存储测试钩子和友元授权，QA 包则必须含有这些。检查本身的测试见 `IntegrationTests.Packaging.PackageChecksTests`。

编译服务不可用时，可先 `dotnet build tools/VamSys.QaSeed/VamSys.QaSeed.csproj -c Release -p:RestoreLockedMode=true -p:UseSharedCompilation=false -m:1 -nr:false`，再直接运行 `dotnet tools/VamSys.QaSeed/bin/Release/net10.0/VamSys.QaSeed.dll`。
