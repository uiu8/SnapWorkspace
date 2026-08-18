using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnapWorkspace.App.Controls;

public sealed class SmoothComboBox : ComboBox
{
    private Popup? _popup;
    private UIElement? _popupRoot;
    private ScrollViewer? _dropDownScrollViewer;

    public override void OnApplyTemplate()
    {
        DetachPopupHandlers();
        base.OnApplyTemplate();
        _popup = GetTemplateChild("PART_Popup") as Popup;
        if (_popup is not null)
        {
            _popup.Opened += Popup_Opened;
            _popup.Closed += Popup_Closed;
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (IsDropDownOpen && ScrollDropDown(e.Delta))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    internal (bool Handled, double Before, double After) RunSyntheticWheelTest(int delta)
    {
        if (_popupRoot is null || _dropDownScrollViewer is null)
        {
            return (false, -1, -1);
        }

        _dropDownScrollViewer.ScrollToTop();
        _dropDownScrollViewer.UpdateLayout();
        var before = _dropDownScrollViewer.VerticalOffset;

        var wheelEvent = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent,
            Source = _popupRoot
        };
        _popupRoot.RaiseEvent(wheelEvent);
        return (wheelEvent.Handled, before, _dropDownScrollViewer.VerticalOffset);
    }

    private void Popup_Opened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            DetachPopupRootHandler();
            _popupRoot = _popup?.Child as UIElement;
            _dropDownScrollViewer = _popupRoot is null
                ? null
                : FindVisualChild<ScrollViewer>(_popupRoot);
            _popupRoot?.AddHandler(
                Mouse.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(PopupRoot_PreviewMouseWheel),
                handledEventsToo: true);
        }, DispatcherPriority.Loaded);
    }

    private void Popup_Closed(object? sender, EventArgs e) => DetachPopupRootHandler();

    private void PopupRoot_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Popup 使用独立的 HWND/视觉树。必须在这里消费滚轮，否则事件会回落到编辑页。
        ScrollDropDown(e.Delta);
        e.Handled = true;
    }

    private bool ScrollDropDown(int delta)
    {
        if (_dropDownScrollViewer is null)
        {
            return false;
        }

        _dropDownScrollViewer.ScrollToVerticalOffset(
            Math.Clamp(
                _dropDownScrollViewer.VerticalOffset - delta * 0.85,
                0,
                _dropDownScrollViewer.ScrollableHeight));
        _dropDownScrollViewer.UpdateLayout();
        return true;
    }

    private void DetachPopupHandlers()
    {
        DetachPopupRootHandler();
        if (_popup is not null)
        {
            _popup.Opened -= Popup_Opened;
            _popup.Closed -= Popup_Closed;
            _popup = null;
        }
    }

    private void DetachPopupRootHandler()
    {
        if (_popupRoot is not null)
        {
            _popupRoot.RemoveHandler(
                Mouse.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(PopupRoot_PreviewMouseWheel));
        }

        _popupRoot = null;
        _dropDownScrollViewer = null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
