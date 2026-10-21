using System.Windows;
using System.Windows.Controls;

namespace ExternalDrivePanel;

public enum NewDriveChoice { Skip, Register, Ignore }

public sealed class NewDriveWindow : Window
{
    public NewDriveChoice Choice { get; private set; } = NewDriveChoice.Skip;
    public Button RegisterButton { get; }
    public Button SkipButton { get; }
    public Button IgnoreButton { get; }

    public NewDriveWindow(Window owner, DriveSnapshot drive)
    {
        Owner = owner;
        Title = "새 외장하드 연결";
        Width = 580;
        Height = 240;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true; // This modal must also stay above an Explorer popup/menu.
        var placement = new PhysicalWindowPlacement(this);
        Loaded += (_, _) => placement.CenterOnOwner(580, 240);
        var content = new StackPanel { Margin = new Thickness(18) };
        var name = drive.Root.TrimEnd('\\', '/') + (string.IsNullOrWhiteSpace(drive.Label) ? "" : " " + drive.Label);
        content.Children.Add(new TextBlock { Text = $"'{name}' 외장하드가 연결되었습니다.\n등록해서 패널로 관리할까요?", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        Button Add(string text, NewDriveChoice choice)
        {
            var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
            button.Click += (_, _) => { Choice = choice; DialogResult = true; };
            buttons.Children.Add(button);
            return button;
        }
        RegisterButton = Add("등록", NewDriveChoice.Register);
        RegisterButton.IsDefault = true;
        SkipButton = Add("이번만 건너뛰기", NewDriveChoice.Skip);
        SkipButton.IsCancel = true;
        IgnoreButton = Add("이 드라이브는 다시 묻지 않기", NewDriveChoice.Ignore);
        content.Children.Add(buttons);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
