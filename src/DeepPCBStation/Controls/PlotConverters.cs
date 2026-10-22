using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DeepPCBStation.ViewModels;

namespace DeepPCBStation.Controls;

// 顶部预留 16px 放数值标签
public sealed class PlotBarHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var pct = ToDouble(values.Length > 0 ? values[0] : null);
        var height = ToDouble(values.Length > 1 ? values[1] : null);
        if (height <= 0) return 2d;
        var usable = Math.Max(0, height - 16);
        return Math.Max(6d, Math.Round(usable * pct / 100.0));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static double ToDouble(object? v)
    {
        if (v is double d) return d;
        if (v is float f) return f;
        if (v is int i) return i;
        if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return 0d;
    }
}

public sealed class PlotBarLabelMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var pct = PlotBarHeightConverter.ToDouble(values.Length > 0 ? values[0] : null);
        var height = PlotBarHeightConverter.ToDouble(values.Length > 1 ? values[1] : null);
        if (height <= 0) return new Thickness(0, 0, 0, 6);
        var usable = Math.Max(0, height - 16);
        var bar = Math.Max(6d, Math.Round(usable * pct / 100.0));
        return new Thickness(0, 0, 0, bar + 3);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PlotBarWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var cell = PlotBarHeightConverter.ToDouble(values.Length > 0 ? values[0] : null);
        if (cell <= 0) return 22d;
        return Math.Max(10d, Math.Min(52d, Math.Round(cell * 0.5)));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PlotYieldDotMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var pct = PlotBarHeightConverter.ToDouble(values.Length > 0 ? values[0] : null);
        var height = PlotBarHeightConverter.ToDouble(values.Length > 1 ? values[1] : null);
        if (height <= 0) return new Thickness(0, 0, 0, 0);
        var pctClamped = Math.Max(0, Math.Min(100, pct));
        var bottom = height * pctClamped / 100.0 - 3.5;
        return new Thickness(0, 0, 0, Math.Max(0, Math.Min(height - 7, bottom)));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// 目标线：values[0]=图高（ActualHeight），values[1]=目标百分比（来自 StatsConfig）
public sealed class PlotTargetBandConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var height = PlotBarHeightConverter.ToDouble(values.Length > 0 ? values[0] : null);
        var pct = PlotBarHeightConverter.ToDouble(values.Length > 1 ? values[1] : null);
        if (pct <= 0 || pct >= 100) pct = 95;
        var margin = height * (100 - pct) / 100.0;
        return new Thickness(0, Math.Max(0, margin), 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DailyYieldLineConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var points = new PointCollection();
        if (values.Length < 3) return points;
        var days = values[0] as IEnumerable;
        var width = PlotBarHeightConverter.ToDouble(values[1]);
        var height = PlotBarHeightConverter.ToDouble(values[2]);
        if (days == null || width <= 0 || height <= 0) return points;

        var yields = new List<double>();
        foreach (var item in days)
        {
            if (item is DailyBarVm bar) yields.Add(bar.YieldPct);
        }
        if (yields.Count == 0) return points;

        for (var i = 0; i < yields.Count; i++)
        {
            var x = (i + 0.5) * width / yields.Count;
            var pct = Math.Max(0, Math.Min(100, yields[i]));
            var y = height - pct / 100.0 * height;
            points.Add(new Point(x, Math.Max(1, Math.Min(height - 1, y))));
        }
        return points;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
