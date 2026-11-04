# FlightOps Desk — QA 专用版本

此目录包含窗口自动化控制、测量命令和存储故障注入入口，仅用于测试，不作为正式程序分发。

请使用独立的 `VAMSYS_DATA_DIR` 数据目录，配合项目源码 scripts 目录中的窗口脚本运行，例如 `test-data-performance-ui.ps1`、`test-baseline-ui.ps1` 和 `test-v3-verification-ui.ps1`，用 `-Executable` 指向本目录中的 FlightOpsDesk.exe。正式程序由 `scripts/build.ps1 -Publish` 生成到 `artifacts/flightops-app`。

[使用指南](docs/USER-GUIDE.md) · [升级与恢复](docs/STORAGE-MIGRATION.md)
