using System.Windows;
using System.Windows.Media;

namespace ExternalDrivePanel;

public partial class DrivePanelWindow
{
    public Func<string, Task<EjectResult>>? EjectRequested { get; set; }
    private bool _ejectSuspended, _enabledBeforeEject;
    private FilePreview[] _suspendedPreviews = [];

    public async Task SuspendForEjectAsync()
    {
        if (_ejectSuspended) return;
        _ejectSuspended = true; _enabledBeforeEject = IsEnabled;
        IsEnabled = false; _searchClosed = true;
        _favoriteMonitor.Suspend(); _fileCommands.Suspend();
        _suspendedPreviews = DescendantPreviews(ContentArea).ToArray();
        foreach (var preview in _suspendedPreviews) preview.Suspend();
        await Task.WhenAll(PrepareForShutdownAsync(), CapacityBar.SuspendAsync());
    }
    public void ResumeAfterEject()
    {
        if (!_ejectSuspended) return;
        _ejectSuspended = false;
        if (!IsVisible) { _suspendedPreviews = []; return; }
        _searchClosed = false;
        void Restore(Action resume)
        {
            try { resume(); } catch (Exception ex) { DiagnosticLog.Record("eject.panel.resume.failed", ex); }
        }
        try
        {
            Restore(_favoriteMonitor.Resume); Restore(_fileCommands.Resume); Restore(CapacityBar.Resume);
            foreach (var preview in _suspendedPreviews) Restore(preview.Resume);
        }
        finally { _suspendedPreviews = []; IsEnabled = _enabledBeforeEject; }
    }
    private static IEnumerable<FilePreview> DescendantPreviews(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FilePreview preview) yield return preview;
            else foreach (var nested in DescendantPreviews(child)) yield return nested;
        }
    }
}
