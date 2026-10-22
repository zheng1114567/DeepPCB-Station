using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeepPCBStation.Controls;

public class ZoomBorder : Border
{
    private UIElement? _child;
    private Point _origin;
    private Point _start;

    public ZoomBorder()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // 命中测试需要
        Loaded += (_, _) => InitChild();
    }

    private void InitChild()
    {
        if (_child != null) return;
        _child = Child;
        if (_child == null) return;

        var group = new TransformGroup();
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        group.Children.Add(scale);
        group.Children.Add(translate);
        _child.RenderTransform = group;
        _child.RenderTransformOrigin = new Point(0, 0);

        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
    }

    public void Reset()
    {
        if (_child?.RenderTransform is TransformGroup g)
        {
            ((ScaleTransform)g.Children[0]).ScaleX = 1;
            ((ScaleTransform)g.Children[0]).ScaleY = 1;
            ((TranslateTransform)g.Children[1]).X = 0;
            ((TranslateTransform)g.Children[1]).Y = 0;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_child?.RenderTransform is not TransformGroup g) return;
        var scale = (ScaleTransform)g.Children[0];
        var translate = (TranslateTransform)g.Children[1];

        var zoom = e.Delta > 0 ? 1.2 : 1 / 1.2;
        var newScale = scale.ScaleX * zoom;
        if (newScale < 1 || newScale > 32)
        {
            e.Handled = true;
            return;
        }

        var rel = e.GetPosition(_child);
        var abs = e.GetPosition(this);

        scale.ScaleX = newScale;
        scale.ScaleY = newScale;

        var targetX = abs.X - rel.X * newScale;
        var targetY = abs.Y - rel.Y * newScale;
        translate.X = targetX;
        translate.Y = targetY;
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            Reset();
            e.Handled = true;
            return;
        }
        if (_child?.RenderTransform is not TransformGroup g) return;
        var scale = (ScaleTransform)g.Children[0];
        if (scale.ScaleX <= 1) return; // 未放大时不平移，保留点击语义

        _start = e.GetPosition(this);
        _origin = new Point(((TranslateTransform)g.Children[1]).X, ((TranslateTransform)g.Children[1]).Y);
        CaptureMouse();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        if (_child?.RenderTransform is not TransformGroup g) return;
        var t = (TranslateTransform)g.Children[1];
        var p = e.GetPosition(this);
        var v = p - _start;
        t.X = _origin.X + v.X;
        t.Y = _origin.Y + v.Y;
    }
}
