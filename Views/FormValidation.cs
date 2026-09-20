using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AwsManager.Views;

public static class FormValidation
{
    public static bool HasErrors(DependencyObject parent)
    {
        if (Validation.GetHasError(parent)) return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (HasErrors(VisualTreeHelper.GetChild(parent, index))) return true;
        return false;
    }
}