# FlightOps Desk

Windows 11 x64 · 简体中文 / English

解压整个目录，运行 **FlightOpsDesk.exe**。程序已包含所需运行库，请保留同目录中的 DLL、Assets、PRI 和 XBF 文件。

1. 创建航空公司工作区，在“连接与设置”填写 Operations Client ID 和 Secret。
2. 测试连接并刷新数据，修改后到审阅页核对，再提交。
3. 没有 API 账号时，可以使用演示工作区或 CSV 导入导出。示例文件位于 examples。

[使用指南](docs/USER-GUIDE.md) · [升级与恢复](docs/STORAGE-MIGRATION.md)

数据默认保存在 `%LOCALAPPDATA%/VamSysBatch`。升级前关闭旧程序，将新版解压到独立目录；旧数据会在升级前自动备份。

目前尚未完成真实航空公司账号联调；文档未明确的操作仍然禁用。程序包不包含测试工具、QA 控制入口、开发文档或调试符号。
