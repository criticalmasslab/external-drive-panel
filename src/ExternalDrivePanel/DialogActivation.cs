using System.Windows;

namespace ExternalDrivePanel;

public static class DialogActivation
{
    public static T Show<T>(Window owner, Func<T> showDialog)
    {
        var wasTopmost = owner.Topmost;
        try
        {
            // Explorer can still own foreground focus at drop time. Keep the owned
            // modal above normal windows for its entire lifetime, not just one tick.
            owner.Topmost = true;
            owner.Activate();
            return showDialog();
        }
        finally
        {
            owner.Topmost = wasTopmost;
        }
    }
}
