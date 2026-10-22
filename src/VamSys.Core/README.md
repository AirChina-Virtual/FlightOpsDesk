# 原 VamSys.Core 项目

R2 已将该项目拆入 [VamSys.Domain](../VamSys.Domain/README.md) 和 [VamSys.Application](../VamSys.Application/README.md)，数据目录解析移至 [Infrastructure](../VamSys.Infrastructure/README.md)。本目录仅保留旧文档链接的入口，不再生成程序集。

现有类型继续使用 `VamSys.Core` CLR 命名空间；程序集改为 `VamSys.Domain` 和 `VamSys.Application`。更新程序时应使用完整的新包，不混用不同版本的 DLL。数据库格式仍为 v4。
