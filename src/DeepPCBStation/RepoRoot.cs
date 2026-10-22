using System.IO;
using System.Text;
using System.Windows;

namespace DeepPCBStation;

// 双击 bin\...\exe 时 cwd 在输出目录，需切到仓库根
public static class RepoRoot
{
    public static string? Current { get; set; }

    public static bool TryBootstrap()
    {
        var root = Find();
        if (root == null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("找不到仓库根目录（需同时存在 data\\samples 与 recipes）。");
            sb.AppendLine();
            sb.AppendLine("已探测起点：");
            sb.AppendLine("  BaseDirectory = " + AppContext.BaseDirectory);
            sb.AppendLine("  当前目录     = " + Directory.GetCurrentDirectory());
            sb.AppendLine();
            sb.AppendLine("建议：");
            sb.AppendLine("  1) 双击仓库根下的「启动主窗.bat」");
            sb.AppendLine("  2) 或先 cd /d E:\\demo\\DeepPCB-Station 再启动 exe");
            sb.AppendLine("  3) 确认 E:\\demo\\DeepPCB-Station\\data\\samples 与 recipes 存在");
            MessageBox.Show(sb.ToString(), "DeepPCB-Station · 无法定位仓库根",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        Directory.SetCurrentDirectory(root);
        Current = root;
        return true;
    }

    public static string? Find()
    {
        var starts = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            @"E:\demo\DeepPCB-Station"
        };

        foreach (var start in starts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            try
            {
                var dir = new DirectoryInfo(Path.GetFullPath(start));
                for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    if (LooksLikeRepoRoot(dir.FullName))
                        return dir.FullName;
                }
            }
            catch
            {
                /* next */
            }
        }
        return null;
    }

    public static bool LooksLikeRepoRoot(string path)
    {
        return Directory.Exists(Path.Combine(path, "data", "samples"))
               && Directory.Exists(Path.Combine(path, "recipes"));
    }

    public static string FormatException(Exception ex)
    {
        var root = ex.GetBaseException();
        var sb = new StringBuilder();
        sb.AppendLine("启动或运行异常（含 InnerException 根因）：");
        sb.AppendLine();
        sb.AppendLine(root.GetType().FullName);
        sb.AppendLine(root.Message);
        sb.AppendLine();
        if (!ReferenceEquals(ex, root))
        {
            sb.AppendLine("--- 外层 ---");
            sb.AppendLine(ex.GetType().FullName + ": " + ex.Message);
            sb.AppendLine();
        }
        sb.AppendLine("--- 堆栈 ---");
        sb.AppendLine(root.StackTrace ?? "(无)");
        sb.AppendLine();
        sb.AppendLine("仓库根=" + (Current ?? "(未设置)"));
        sb.AppendLine("cwd=" + Directory.GetCurrentDirectory());
        sb.AppendLine("BaseDirectory=" + AppContext.BaseDirectory);
        sb.AppendLine();
        sb.AppendLine("请用「启动主窗.bat」或从 E:\\demo\\DeepPCB-Station 启动。");
        return sb.ToString();
    }
}
