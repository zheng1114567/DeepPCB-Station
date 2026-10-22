# 首期冒烟 · 质量债清单（金标前）

状态：反驳终裁 — 冒烟五条债 **已清**（提交 `a0dafd8`）。

金标后正式停拧结论与冻结配方见 **`docs/QUALITY_DEBT.md`**（勿再在本页开拧参）。

## 1. 分型口径（非可靠分类器）

- Pred 上的 `OPEN` / `MOUSEBITE` **TypeCode 仅来自外接矩长宽比启发式**（细长偏 open，紧凑偏 mousebite）。
- **不是**可靠分类器；**禁止**写分类精度 / 分型成绩 / 按错分型刷金标。
- 金标主看：**定位命中 / 过杀**；Pred 分型标签不作门槛依据。

## 2. Overlay 噪声框

- 首期差分 + Blob 的 overlay **可能出现噪声候选框**。
- **禁止**把噪声框当成过杀已控。过杀数字只认正式金标报表（见 `QUALITY_DEBT.md`）。

## 3. 匹配失败兜底

- **匹配失败或分数过低**时：模板 **恒等拷贝**，依赖 DeepPCB 预对齐。
- **风险**：有旋转/平移时差分假差↑；冒烟通 ≠ 对齐已验证。

## 4. Manifest 与 txt

- `00041000`：`hasOpen=true`、`hasMousebite=true`（与 txt type=1 / type=3 一致）。

## 5. Git remote

见根 README「仓库与 remote」：本地仓；`origin` 已配，推送仍可能受凭据/网络阻塞。
