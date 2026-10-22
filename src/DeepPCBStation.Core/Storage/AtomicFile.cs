using System.IO;
using System.Text;

namespace DeepPCBStation.Core.Storage;

// 先写同目录 .tmp 再 Replace，崩溃不留半截 JSON
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, Encoding encoding)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, encoding);
        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }
}
