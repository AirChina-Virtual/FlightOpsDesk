# FlightOps Desk

Windows 11 x64 · 简体中文 / English

解压整个目录后运行 **FlightOpsDesk.exe**。程序已包含所需运行库，请保留同目录中的 DLL、Assets、PRI 和 XBF 文件。

1. 在“工作区”新建航空公司工作区，在“连接与设置”填写 Operations Client ID 和 Secret，测试连接。
2. 点击“从 API 刷新”读取数据，在“数据管理”中修改。单元格点击后即可编辑，批量操作只处理已选记录。
3. 在“变更审阅”中核对修改并提交，在“任务中心”查看结果。

没有 API 账号时，可以使用演示工作区或 CSV 导入导出，示例文件位于 examples。

[使用指南](docs/USER-GUIDE.md) · [升级与恢复](docs/STORAGE-MIGRATION.md)

数据默认保存在 `%LOCALAPPDATA%/VamSysBatch`。升级前请关闭旧程序，再将新版解压到单独的目录；旧数据会在升级前自动备份。

目前尚未用真实航空公司账号完成联调；官方文档未说明清楚的操作仍然禁用。程序包不包含测试工具、QA 控制入口、开发文档或调试符号。
