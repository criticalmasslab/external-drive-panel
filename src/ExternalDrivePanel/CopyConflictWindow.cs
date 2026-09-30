using System.Windows;
using System.Windows.Controls;

namespace ExternalDrivePanel;

public sealed class CopyConflictWindow : Window
{
    public CopyConflictChoice Choice { get; private set; } = CopyConflictChoice.Cancel;

    public CopyConflictWindow(Window owner, string source, string target)
    {
        Owner = owner;
        Title = "같은 이름의 항목이 있습니다";
        Width = 520;
        Height = 280;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var placement = new PhysicalWindowPlacement(this);
        Loaded += (_, _) => placement.CenterOnOwner(520, 280);
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock
        {
            Text = $"원본: {source}\n\n대상: {target}\n\n폴더 덮어쓰기는 내용을 합칩니다. 내부 이름 충돌은 다시 확인합니다.\n취소하면 남은 복사를 중단하며, 이미 복사한 항목은 유지됩니다.",
            TextWrapping = TextWrapping.Wrap
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, choice) in new[] { ("덮어쓰기", CopyConflictChoice.Overwrite), ("건너뛰기", CopyConflictChoice.Skip), ("취소", CopyConflictChoice.Cancel) })
        {
            var button = new Button { Content = label, MinWidth = 85, Margin = new Thickness(4, 16, 4, 0), Padding = new Thickness(8, 5, 8, 5) };
            button.Click += (_, _) => { Choice = choice; DialogResult = true; };
            button.IsCancel = choice == CopyConflictChoice.Cancel;
            buttons.Children.Add(button);
        }
        content.Children.Add(buttons);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
