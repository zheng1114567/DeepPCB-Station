using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using DeepPCBStation.Core.Diagnostics;

namespace DeepPCBStation.Services;

// 记住窗口位置：下次启动回到用户挪过的位置
public static class WindowPlacement
{
    private sealed class State
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepPCBStation");

    private static string FilePath => Path.Combine(Dir, "window.json");

    public static void Restore(Window window, string key)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, State>>(File.ReadAllText(FilePath));
            if (map == null || !map.TryGetValue(key, out var s) || s.Width < 100 || s.Height < 100) return;

            // 换过显示器/分辨率后，窗口可能落在屏幕外，忽略失效记录
            var visible = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!visible.IntersectsWith(new Rect(s.Left, s.Top, s.Width, s.Height))) return;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = s.Width;
            window.Height = s.Height;
            window.Left = s.Left;
            window.Top = s.Top;
            if (s.Maximized)
                window.WindowState = WindowState.Maximized;
        }
        // 布局记忆失败不影响启动，但要留痕：否则「每次启动窗口都跑偏」永远查不出原因。
        catch (Exception ex) { StationLog.Warn(nameof(WindowPlacement), "窗口布局恢复失败: " + ex.Message); }
    }

    public static void Save(Window window, string key)
    {
        try
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
            if (bounds.Width < 100 || bounds.Height < 100) return;

            var map = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, State>>(File.ReadAllText(FilePath)) ?? new()
                : new Dictionary<string, State>();
            map[key] = new State
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = window.WindowState == WindowState.Maximized
            };

            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { StationLog.Warn(nameof(WindowPlacement), "窗口布局保存失败: " + ex.Message); }
    }
}
