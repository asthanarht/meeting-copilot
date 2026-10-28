using System.Windows;
using System.Windows.Media;

namespace MeetingCopilot.Desktop;

public static class ButtonIcon
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached(
        "Data", typeof(Geometry), typeof(ButtonIcon), new PropertyMetadata(null));

    public static Geometry? GetData(DependencyObject value) => (Geometry?)value.GetValue(DataProperty);
    public static void SetData(DependencyObject value, Geometry? data) => value.SetValue(DataProperty, data);
}
