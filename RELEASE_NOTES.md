# HikWarranty v1.0.0

**Hikvision warranty batch checker — 海康威视维保批量查询工具（Windows 原生 · 43 KB · 免安装）**

## 亮点 · Highlights

- **单文件原生程序**：GUI 43 KB / CLI 25 KB，无需安装任何运行时（Windows 10 1903+ / Windows 11 自带 .NET Framework 4.8）
- **批量并发查询**：默认并发 8，30 条序列号 **4.5~5.5 秒** 完成（串行约 23 秒）
- **零依赖构建**：使用系统自带 C# 编译器编译，无需 Visual Studio / .NET SDK / NuGet
- **导出友好**：CSV（UTF-8 带 BOM，Excel 直接打开）、剪贴板（可直接粘贴表格）；CLI 支持表格 / JSON 与脚本退出码
- **中文界面**，按屏幕 DPI 精确缩放（100%–200% 显示比例下比例一致）

## 新增 · Added

- `HikWarranty.exe` 图形界面：批量输入（自动去重、自动转大写）、实时进度、在保 / 已过保 / 未查到 三色标识、详情窗口（完整字段 + 原始 JSON + 一键打开产品图片）
- `hikq.exe` 命令行：表格 / JSON / CSV 输出，可配置并发（`-c`），脚本友好退出码（0/1/2）
- 并发调度器：可取消、结果保持输入顺序、单条超时与失败隔离
- 性能优化：启动 TLS 预热、每主机连接上限 2→64（.NET 默认值会卡住并发）、默认并发 8（实测最优）
- 稳健性：响应缺少 charset 时按 UTF-8 字节显式解码、超时 / 429 / 5xx 自动重试（最多 3 次）
- 设置记忆：窗口大小与并发数保存在 `%LOCALAPPDATA%\HikWarranty\ui.ini`

## 修复 · Fixed

- 首个公开版本，无历史问题修复。

## 已知问题 · Known issues

- **exe 未做代码签名**：首次运行可能出现 SmartScreen「未知发布者」提示（属正常现象，见下方说明）
- 并发开到 16 反而比 8 慢（服务端排队），不建议超过 8
- 依赖官方接口字段：如官方调整接口，需更新 `src/Api.cs` 后重新编译（编译仅需数秒）
- 仅支持 Windows 10/11 x64（依赖系统自带的 .NET Framework 4.8）

## 附件 · Assets

| 文件 | 用途 | 系统要求 |
|---|---|---|
| `HikWarranty-v1.0.0-win64.exe` | 图形界面（双击即用） | Windows 10/11 x64 |
| `HikWarranty-CLI-v1.0.0-win64.exe` | 命令行版 `hikq`（脚本/批处理） | Windows 10/11 x64 |
| `checksums.txt` | 上述两个 exe 的 SHA-256 校验值 | — |

## SHA-256

```
2846faf49bdd3efc685995ece5bb25a377e663655bc11c1bf3cc1ff5807d4b56  HikWarranty-v1.0.0-win64.exe      (43,520 字节)
245591eaaf207f0f1ca8aeecbf09aa4c11cf7ad4b8282238d66c14c9f7f572f1  HikWarranty-CLI-v1.0.0-win64.exe  (25,600 字节)
```

## 快速开始

1. 下载 `HikWarranty-v1.0.0-win64.exe`，双击运行；
2. 粘贴序列号（每行一个，或空格 / 逗号分隔）→ 按 `Enter` 或 `F5` 查询；
3. 右键查看详情 / 打开产品图片，点击「导出 CSV」或「复制结果」带走数据。

命令行示例：

```
HikWarranty-CLI-v1.0.0-win64.exe LA0000000
HikWarranty-CLI-v1.0.0-win64.exe -f 序列号.txt -o 结果.csv -c 8
HikWarranty-CLI-v1.0.0-win64.exe -f 序列号.txt -j
```

## 未签名说明 · Unsigned binaries

这些 exe 没有代码签名证书，Windows SmartScreen 可能提示「Windows 已保护你的电脑 / 未知发布者」。这是未签名开源程序的正常现象：点击 **「更多信息 → 仍要运行」** 即可。如需确认文件在下载过程中未被篡改，请比对上面的 SHA-256（或 `checksums.txt`）：

```powershell
Get-FileHash .\HikWarranty-v1.0.0-win64.exe -Algorithm SHA256
```

## 注意事项

- 查询接口为官方公开接口，无需登录；请保持温和的请求频率，勿用于高频抓取
- 查询结果仅供参考，正式业务请以官方平台页面为准