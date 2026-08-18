using System.Windows;

namespace SnapWorkspace.App;

public partial class WindowPickerHintWindow : Window
{
    public WindowPickerHintWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
            Top = workArea.Top + 24;
        };
    }
}
