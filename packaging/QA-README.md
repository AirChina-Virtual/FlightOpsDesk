# FlightOps Desk — QA 专用版本

此目录包含窗口自动化控制、测量命令和存储故障注入入口，仅用于测试，不作为正式程序分发。

请配合项目源码中的 C# 窗口测试（`tests/VamSys.UiTests`）运行，用环境变量 `VAMSYS_QA_EXE` 指向本目录中的 FlightOpsDesk.exe；每个测试都使用独立的数据目录。正式程序由 `scripts/build.ps1 -Publish` 生成到 `artifacts/flightops-app`。

[使用指南](docs/USER-GUIDE.md) · [升级与恢复](docs/STORAGE-MIGRATION.md)
