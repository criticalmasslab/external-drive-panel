namespace ExternalDrivePanel;

public interface IEjectParticipant
{
    Task SuspendForEjectAsync();
    void ResumeAfterEject();
}

public static class EjectWorkflow
{
    public static async Task<EjectResult> RunAsync(string root, EjectService service, IReadOnlyList<IEjectParticipant> participants)
    {
        // A physical USB disk may contain multiple drive letters. Quiesce all
        // our panels briefly, not just the clicked partition, before asking PnP.
        using var pause = FileOperationRegistry.Pause();
        try
        {
            await FileOperationRegistry.WaitForIdleAsync();
            foreach (var participant in participants) await participant.SuspendForEjectAsync();
            await ShellPreviewService.WaitForIdleAsync();
            return await Task.Run(() => service.TryEject(root));
        }
        catch (Exception ex) { DiagnosticLog.Record("eject.prepare.failed", ex); return EjectResult.Failed("안전 제거 준비에 실패했습니다. " + ex.Message); }
        finally
        {
            foreach (var participant in participants)
            {
                try { participant.ResumeAfterEject(); }
                catch (Exception ex) { DiagnosticLog.Record("eject.resume.failed", ex); }
            }
        }
    }
}
