using System.Windows;

namespace ExternalDrivePanel;

public interface IUpdateDialogs
{
    bool ConfirmRestart(Window owner);
    void ShowError(Window owner, string message);
}

internal sealed class UpdateDialogs : IUpdateDialogs
{
    public bool ConfirmRestart(Window owner) => DialogActivation.Show(owner, () => MessageBox.Show(owner,
        "업데이트를 설치하고 패널을 다시 시작할까요?\n진행 중인 파일 작업은 완료될 때까지 기다립니다.",
        "업데이트 후 다시 시작", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) == MessageBoxResult.Yes;
    public void ShowError(Window owner, string message) => DialogActivation.Show(owner, () => MessageBox.Show(owner,
        message, "업데이트", MessageBoxButton.OK, MessageBoxImage.Warning));
}
