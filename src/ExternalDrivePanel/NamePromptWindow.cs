using System.Windows;
using System.Windows.Controls;

namespace ExternalDrivePanel;

internal sealed class NamePromptWindow : Window
{
    private readonly TextBox _name = new() { Margin = new Thickness(0, 8, 0, 8), MinWidth = 280 };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    public string Value => _name.Text;

    public NamePromptWindow(Window owner, string title, string initial, bool selectBaseName = false, Func<string, string>? validate = null)
    {
        Owner = owner; Title = title; Width = 420; Height = 210; ResizeMode = ResizeMode.NoResize;
        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(new TextBlock { Text = title });
        _name.Text = initial;
        layout.Children.Add(_name); layout.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 70, Margin = new Thickness(4) };
        var ok = new Button { Content = "확인", IsDefault = true, MinWidth = 70, Margin = new Thickness(4) };
        ok.Click += (_, _) =>
        {
            try { (validate ?? ShellFileOperations.ValidateName)(Value); DialogResult = true; }
            catch (ArgumentException ex) { _error.Text = ex.Message; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(ok); layout.Children.Add(buttons); Content = layout;
        var placement = new PhysicalWindowPlacement(this);
        Loaded += (_, _) =>
        {
            placement.CenterOnOwner(420, 210); _name.Focus();
            if (selectBaseName && System.IO.Path.GetExtension(initial).Length > 0)
                _name.Select(0, System.IO.Path.GetFileNameWithoutExtension(initial).Length);
            else _name.SelectAll();
        };
    }
    public static string? Ask(Window owner, string title, string initial, bool selectBaseName = false, Func<string, string>? validate = null)
    {
        var window = new NamePromptWindow(owner, title, initial, selectBaseName, validate);
        return DialogActivation.Show(owner, () => window.ShowDialog()) == true ? window.Value : null;
    }
}
