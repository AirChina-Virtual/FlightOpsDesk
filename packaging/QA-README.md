# FlightOps Desk — QA 专用版本

此目录包含窗口自动化控制和存储故障注入入口，仅用于测试，不作为正式程序分发。

使用独立的 `VAMSYS_DATA_DIR` 数据目录，配合项目源码中的 `scripts/test-v3-verification-ui.ps1` 运行检查。正式程序由 `scripts/build.ps1 -Publish` 生成到 `artifacts/flightops-app`。

[使用指南](docs/USER-GUIDE.md) · [升级与恢复](docs/STORAGE-MIGRATION.md)
