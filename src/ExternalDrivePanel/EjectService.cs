namespace ExternalDrivePanel;

public sealed record EjectTarget(string Root, uint DiskNumber, uint DevInst, string DeviceId, uint VolumeSerial);
public interface IEjectBackend
{
    EjectTarget Resolve(string root);
    EjectResult Request(EjectTarget target);
}

/// <summary>Ask Windows PnP to prepare removal; never force-lock/dismount a volume.</summary>
public sealed class EjectService(IEjectBackend? backend = null)
{
    private readonly IEjectBackend _backend = backend ?? new WindowsEjectBackend();
    public EjectResult TryEject(string root)
    {
        try
        {
            if (!IsDriveRoot(root)) return EjectResult.Failed("올바른 드라이브 루트가 아닙니다.");
            var first = _backend.Resolve(root);
            var current = _backend.Resolve(root);
            if (first != current) return EjectResult.Failed("준비 중 연결된 장치가 바뀌었습니다. 드라이브를 다시 확인해주세요.");
            var result = _backend.Request(current);
            DiagnosticLog.Record(result.Success ? "eject.success" : "eject.rejected");
            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Record("eject.failed", ex);
            return EjectResult.Failed(ex.Message);
        }
    }
    public static bool IsDriveRoot(string? root) => root is { Length: 3 } && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] is '\\' or '/';
}

public sealed record EjectResult(bool Success, string? FailureReason)
{
    public static EjectResult Succeeded() => new(true, null);
    public static EjectResult Failed(string reason) => new(false, reason);
}
