# DeepPCB 小样

来源：[tangsanli5201/DeepPCB](https://github.com/tangsanli5201/DeepPCB)

- 成对：`*_temp.jpg`（模板）/ `*_test.jpg`（待检），640×640
- 标注：同 stem 的 `.txt`，行格式 `x1,y1,x2,y2,type`（1=open，3=mousebite）
- 许可：非商用可用；商用须联系作者。大数据不要提交进 git。

拉取更多小样：

```powershell
pwsh -File scripts/fetch-samples.ps1
```
