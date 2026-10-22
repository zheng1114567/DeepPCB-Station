# DeepPCB-Station · UI 一页范围（浅灰工位壳）

**状态：v2.0.0 壳 · 已按 `docs/ui-mockup-上位机.html` 1:1 落地**（自绘标题栏 + 菜单栏 + 工具栏 + 状态灯条 + 设备栏 + 三栏主体 + 底部页签 + 状态栏）。实际渲染见 `docs/ui-actual-mainwindow.png`。检测与配方逻辑未动。

## 视觉基准（唯一）

- 样张：`docs/ui-mockup-上位机.html`（颜色/间距/字号/控件形态以此为准，改版先改样张再改 XAML）。
- 实拍：`docs/ui-actual-mainwindow.png`（运行页）+ `docs/ui-pages/`（检测历史 / 统计报表 / 配方管理 / 告警记录 / 系统设置）。用 `DPCB_DEV_UI_SNAPSHOT=<png>` 启动 **Debug 版** exe 自动渲染；加 `DPCB_DEV_UI_PAGE=<页签标题>` 可指定页面。该快照分支整块在 `#if DEBUG` 内（会跳过登录起 Admin 会话），Release 构建里不存在——截图请用 Debug 构建。

## 闸门（必须遵守）

1. **浅灰 / 米灰底 + 深色字**；禁大面积纯黑、纯白 Material、青紫赛博高光、渐变假仪表盘；**禁主题库**。
2. **单窗口、先登录后进站**：登录窗（含锁定剩余时间 / 首次强制改密）+ 主窗 7 页（运行 / 历史 / 统计 / 配方 / 告警 / 用户 / 设置）。主窗为**自绘标题栏**（`WindowStyle=None` + `WindowChrome`）：标题栏深钢蓝 `#33465C`、右上角最小化/最大化/关闭，双击标题栏切换最大化，边框可拖拽缩放。角色门控：Operator 只读设置与配方，Critical 告警仅 Engineer/Admin 可确认，复判需登录且非只读角色。
3. **空闲 15 分钟遮罩锁屏**：检测不停、握手不退，输密码解锁；锁定态吞掉 F11 等全局快捷键。
4. **按钮**：**直角（0px）**；闸门上限仍是 ≤2px，当前实现一律 0；平涂实色；无渐变 / 外发光 / 大胶囊 / 悬浮阴影。确认换型 = **蓝灰实心**；单次 / 停止 = **灰底深字**。
5. **OK / NG**：绿 / 红 **实底 + 白字色块灯**；未判定 = **中性灰实底**；禁淡绿淡红幽灵圆圈。
6. **Ready**：朴素小圆点 + 状态字；禁产品页胶囊灯。
7. **文案**：空态「无图像」；去省略号机器腔；确认换型字清晰（微圆≤2 平涂）。
8. **顶栏不堆 hash**；握手细节 **只留底栏一行**。
9. **触发权只属一方**：UI 与 PLC 同一时刻只能一个在检；另一方立即接到拒绝（PLC 收到 `FAULT,-,BUSY`，不排队、不等满超时）。运行页状态条常显占用来源（空闲 / 操作员占用中 / PLC 占用中）。
10. **每条检测可追溯到人**：库里有 operator / shift / trigger_source / station_id；无登录会话时 PLC 触发直接 `FAULT,-,NO_OPERATOR`，不产出无归属记录。
11. **超时/断连的结果标 VOID**：留证据（`results/VOID/`）但不计良率，`parent_id` 回指故障行；统计口径中 OK/NG/FAULT 三分离，VOID 单列。
12. **最近结果**空时收矮 + 一句「尚无结果」，勿大片留白。
13. **可访问性**：键盘焦点框保留（禁 `FocusVisualStyle=null`）；正文 12px、微标签/表格 11px、数字一律 Consolas。主操作（单次/连续/停止）在**常驻工具栏**，高 30px、宽 ≥104px —— 与样张一致；原「48px 高」条款仅适用于页面内的独立大按钮，工具栏按钮不在其列（点击面积由宽度与 4px 间隙保证）。
14. **检测 / 配方 / Halcon / 金标一律不动**；只动 WPF 壳样式与空态文案。

## 七页统一规则（v2.0.0）

- 面板标题统一为 24px `PanelHead` 条（表/明细类）或 3px 竖条 + 12.5px 标题（表单分组），不再出现 16px 页内大标题。
- 统计页 KPI 改为**两条密集数值带**（16px 等宽字 + 11px 标签），取消 24px 大数字卡片。
- 表格：行高 24、表头 24、全网格线、列宽固定、数字 Consolas；筛选条内边距 10,8、卡间距 8。
- 颜色只允许出现在**状态**上（OK/NG/FAULT 灯、告警条、缺陷类型字），其余一律灰阶。

## 本闸改动面

| 文件 | 作用 |
|------|------|
| `src/DeepPCBStation/Resources/Theme.xaml` | v2.0.0：令牌与样张同源（标题栏 #33465C、菜单 #F0F1F3、工具栏 #F4F5F7、灯条 #E7EAED、边 #C9CFD5、钢蓝 #2C5C8F、圆角全 0）；新增 ToolButton / TitleButton / Lamp / PanelHead / DeviceRow / ImageToolButton / 经典 TabItem |
| `scripts/check-ui-gate.ps1` | 新增静态闸：圆角 >2 / 渐变 / 阴影 / `FocusVisualStyle=Null` / 缺菜单栏 一律非零退出码 |
| `src/DeepPCBStation/MainWindow.xaml(.cs)` | v2.0.0：自绘标题栏 + 菜单栏（文件/视图/运行/定位/帮助）+ 工具栏（单次/连续/停止/复位视图/结果目录/复判/导出 + 配方下拉 + 确认换型 + 操作员）+ 7 段状态灯条 + 208px 设备栏 + 底部四页签 + 状态栏 + 锁屏遮罩 + F11 记忆还原 |
| `src/DeepPCBStation/Views/RunPage.xaml(.cs)` | v2.0.0：图像工具条（适应窗口/1:1/十字线/partId）+ 深灰视口（#333639）+ 右侧判定三灯 + 缺陷明细表（真实缺陷行）+ 本次参数表；图像页脚显示 partId/inspectionId/耗时/引擎 |
| `src/DeepPCBStation/ViewModels/RunViewModel.cs` | 新增 `LastDefects` / `ParamRows` / `CameraText` / `StationIdText` / `ShiftText`（全部取真实结果与当前配方，不做占位数据） |
| `src/DeepPCBStation/LoginWindow.xaml(.cs)`、`ChangePasswordWindow.xaml(.cs)` | 登录、锁定剩余时间、首次强制改密 |
| `src/DeepPCBStation/Views/RunPage.xaml(.cs)` | 占用状态条、单次/连续/停止、最近结果“查看”、日志 6 行 |
| `src/DeepPCBStation/Views/HistoryPage.xaml` | 机器/终判双列、待复判筛选、复判面板 |
| `src/DeepPCBStation/Views/StatsPage.xaml` | 两行 KPI（含 FPY/伪点率/DPPM/作废）、目标线绑配置 |
| `src/DeepPCBStation/Views/SettingsPage.xaml` | 设备/存储/数据库/快捷目录（含“关于”） |

## 验收

1. Release 能编过；`启动主窗.bat` 能开登录窗 → 主窗。
2. 空态：米灰底、配方下拉非白框、OK/NG 中性灰色块；判定后绿/红实底白字。
3. 首登强制改密 → 登录 → 确认换型 → 单次/连续 → 空闲锁屏后解锁 → 复判一条 → 统计页数字自洽。
4. 自测全绿：`--handshake-selftest` / `--recipe-gate-selftest` / `--db-selftest` / `--concurrency-selftest`。
5. 静态闸全绿：运行 `scripts/check-ui-gate.ps1`，输出 `UI GATE: PASS`。
6. 一屏放下运行页全部要素，不需滚动；隔 1.5m 可辨 OK/NG/FAULT 三灯。
