using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Taste.ViewModels;

namespace Taste.Controls;

public static class ListBoxDragDropBehavior
{
    private const string DragFormat = "PinnedFolder";

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ListBoxDragDropBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static bool IsDragging { get; private set; }

    private static Point _startPoint;
    private static PinnedFolderViewModel? _draggedItem;
    private static InsertionAdorner? _adorner;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb) return;
        if ((bool)e.NewValue)
        {
            lb.AllowDrop = true;
            lb.PreviewMouseLeftButtonDown += Lb_PreviewMouseLeftButtonDown;
            lb.PreviewMouseMove           += Lb_PreviewMouseMove;
            lb.DragOver                   += Lb_DragOver;
            lb.DragLeave                  += Lb_DragLeave;
            lb.Drop                       += Lb_Drop;
        }
        else
        {
            lb.AllowDrop = false;
            lb.PreviewMouseLeftButtonDown -= Lb_PreviewMouseLeftButtonDown;
            lb.PreviewMouseMove           -= Lb_PreviewMouseMove;
            lb.DragOver                   -= Lb_DragOver;
            lb.DragLeave                  -= Lb_DragLeave;
            lb.Drop                       -= Lb_Drop;
        }
    }

    private static void Lb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint  = e.GetPosition(null);
        _draggedItem = GetItemFromPoint((ListBox)sender, e.GetPosition((ListBox)sender));
    }

    private static void Lb_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _startPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _startPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _draggedItem;
        _draggedItem = null;
        IsDragging   = true;
        DragDrop.DoDragDrop((ListBox)sender, new DataObject(DragFormat, item), DragDropEffects.Move);
        IsDragging   = false;
    }

    private static void Lb_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not PinnedFolderViewModel) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        var lb  = (ListBox)sender;
        int idx = GetTargetIndex(lb, e.GetPosition(lb));
        ShowAdorner(lb, idx);
        e.Handled = true;
    }

    private static void Lb_DragLeave(object sender, DragEventArgs e)
    {
        var lb  = (ListBox)sender;
        var pos = e.GetPosition(lb);
        if (pos.X < 0 || pos.Y < 0 || pos.X > lb.ActualWidth || pos.Y > lb.ActualHeight)
            RemoveAdorner(lb);
    }

    private static void Lb_Drop(object sender, DragEventArgs e)
    {
        var lb = (ListBox)sender;
        RemoveAdorner(lb);

        if (e.Data.GetData(DragFormat) is not PinnedFolderViewModel dragged) return;
        if (lb.ItemsSource is not ObservableCollection<PinnedFolderViewModel> items) return;

        int oldIdx = items.IndexOf(dragged);
        int newIdx = GetTargetIndex(lb, e.GetPosition(lb));

        if (oldIdx < 0) return;
        if (newIdx > oldIdx) newIdx--;
        if (oldIdx == newIdx) return;

        items.Move(oldIdx, Math.Max(0, Math.Min(newIdx, items.Count - 1)));
        e.Handled = true;
    }

    private static PinnedFolderViewModel? GetItemFromPoint(ListBox lb, Point point)
    {
        var element = lb.InputHitTest(point) as DependencyObject;
        while (element != null)
        {
            if (element is ListBoxItem lbi && lbi.DataContext is PinnedFolderViewModel vm) return vm;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static int GetTargetIndex(ListBox lb, Point point)
    {
        for (int i = 0; i < lb.Items.Count; i++)
        {
            if (lb.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var bounds = container.TransformToAncestor(lb).TransformBounds(new Rect(container.RenderSize));
            if (point.Y < bounds.Top + bounds.Height / 2) return i;
        }
        return lb.Items.Count;
    }

    private static void ShowAdorner(ListBox lb, int targetIndex)
    {
        var layer = AdornerLayer.GetAdornerLayer(lb);
        if (layer == null) return;
        RemoveAdorner(lb);
        _adorner = new InsertionAdorner(lb, targetIndex);
        layer.Add(_adorner);
    }

    private static void RemoveAdorner(ListBox lb)
    {
        if (_adorner == null) return;
        AdornerLayer.GetAdornerLayer(lb)?.Remove(_adorner);
        _adorner = null;
    }
}

internal sealed class InsertionAdorner : Adorner
{
    private readonly int _targetIndex;
    private static readonly Pen _pen = new(new SolidColorBrush(Color.FromArgb(0xCC, 0xC0, 0xC0, 0xC0)), 1.5)
        { DashStyle = DashStyles.Solid };

    public InsertionAdorner(ListBox lb, int targetIndex) : base(lb)
    {
        _targetIndex     = targetIndex;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var lb = (ListBox)AdornedElement;
        double y;

        if (_targetIndex >= lb.Items.Count)
        {
            if (lb.Items.Count == 0) { y = 0; }
            else
            {
                if (lb.ItemContainerGenerator.ContainerFromIndex(lb.Items.Count - 1) is not FrameworkElement last) return;
                y = last.TransformToAncestor(lb).TransformBounds(new Rect(last.RenderSize)).Bottom;
            }
        }
        else
        {
            if (lb.ItemContainerGenerator.ContainerFromIndex(_targetIndex) is not FrameworkElement c) return;
            y = c.TransformToAncestor(lb).TransformBounds(new Rect(c.RenderSize)).Top;
        }

        dc.DrawLine(_pen, new Point(8, y), new Point(lb.ActualWidth - 8, y));
    }
}
