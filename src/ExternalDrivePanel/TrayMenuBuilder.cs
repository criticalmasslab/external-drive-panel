using System.Windows.Controls;

namespace ExternalDrivePanel;

public static class TrayMenuBuilder
{
    public static void Populate(ContextMenu menu, DriveCoordinator coordinator, AppConfig config,
        Action openSettings, Action<uint> register, Action<string> toggleHidden, Action<string> unregister, Action exit)
    {
        menu.Items.Clear();
        MenuItem Item(string label, Action click)
        {
            var item = new MenuItem { Header = new TextBlock { Text = label } };
            item.Click += (_, e) => { e.Handled = true; click(); };
            return item;
        }
        menu.Items.Add(Item("화면 영역 설정…", openSettings));
        var candidates = new MenuItem { Header = "외장하드 등록" };
        foreach (var drive in coordinator.ManualCandidates)
            candidates.Items.Add(Item(coordinator.DisplayName(DriveCoordinator.Key(drive.Serial)), () => register(drive.Serial)));
        if (candidates.Items.Count == 0)
            candidates.Items.Add(new MenuItem { Header = "등록할 수 있는 외장하드가 없습니다", IsEnabled = false });
        menu.Items.Add(candidates);
        menu.Items.Add(new Separator());
        foreach (var key in coordinator.RegisteredOrder)
        {
            // Underscores in user volume labels are literal, not WPF access keys.
            var entry = new MenuItem { Header = new TextBlock { Text = coordinator.DisplayName(key) } };
            entry.Items.Add(Item(config.Drives[key].Hidden ? "패널 다시 보이기" : "패널 숨기기", () => toggleHidden(key)));
            entry.Items.Add(Item("등록 해제", () => unregister(key)));
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("종료", exit));
    }
}
