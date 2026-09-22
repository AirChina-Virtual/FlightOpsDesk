# FlightOps Desk 名称与图标

软件显示名称统一为 **FlightOps Desk**，中文及英文界面均使用该名称。程序入口为 FlightOpsDesk.exe，自包含目录为 artifacts/flightops-app。

## 图标

原创几何标志由汇聚航路、调度节点及方向箭头组成。采用 vaMSYS 登录页在 2026-09-22 的配色方向：primary-500 #E7515A、gray-800 #232637，辅以白色。[配色来源](https://vamsys.io/login)。此处仅借鉴配色，不复制其标志，也不表示官方发布或认可。

- 可编辑源文件：src/VamSys.App/Assets/FlightOpsDesk.svg
- 标题栏图像：FlightOpsDesk.png，256 × 256，透明边角
- EXE／窗口图标：FlightOpsDesk.ico，包含 16、20、24、32、40、48、64、128、256 像素的 32 位图像
- Windows 下执行 scripts/build-icon.ps1 可从 SVG 重新生成 PNG 和 ICO；使用系统 WPF，无第三方绘图库依赖。

图标同时嵌入 EXE，并随目录发布供窗口和标题栏使用。请保留完整解压目录。

## 兼容

保留原 %LOCALAPPDATA%/VamSysBatch 数据目录、VAMSYS_DATA_DIR 环境变量、核心命名空间及存储结构。此次品牌更新不触发数据库迁移，不改变 CSV／API 契约。请关闭旧版后，从新的独立目录启动，不覆盖混用两套程序文件。

## 本轮验证

- Windows x64 自包含发布成功，无编译错误。
- 实际窗口中双向语言切换后名称仍为 FlightOps Desk；未保存设置、草稿、筛选、选择、机型选择、删除和恢复验证通过。
- Windows 文件属性 ProductName／FileDescription 均为 FlightOps Desk；EXE 图标提取和窗口图标检查通过。
- 已检查实际窗口截图与 32 像素提取图标；ICO 包含九个尺寸。
- 本轮为品牌更新，执行针对性构建和界面验证；此前 151 项业务回归结果见测试报告，本轮未重复运行完整业务测试。
- 不涉及真实 API 实例联调。

日志及截图位于 artifacts/branding，不提交本地测试数据库。
