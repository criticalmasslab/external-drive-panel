using System.IO;

namespace ExternalDrivePanel;

public sealed record EjectNode(uint DevInst, string DeviceId, string Service, uint Capabilities);
public static class EjectSafetyPolicy
{
    public static bool IsBoundary(string id, string service) => id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("HTREE\\", StringComparison.OrdinalIgnoreCase)
        || service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase);

    public static EjectNode Select(IEnumerable<EjectNode> ancestors, Func<uint, bool> containsOtherDisk)
    {
        var visited = new HashSet<uint>();
        foreach (var node in ancestors.Take(12))
        {
            if (!visited.Add(node.DevInst) || IsBoundary(node.DeviceId, node.Service) || (node.Capabilities & 8) != 0) break;
            if ((node.Capabilities & 4) == 0) continue; // CM_DEVCAP_REMOVABLE
            if (containsOtherDisk(node.DevInst))
                throw new IOException("여러 물리 디스크를 공유하는 장치입니다. Windows의 하드웨어 안전 제거 메뉴를 사용해주세요.");
            return node;
        }
        throw new IOException("Windows가 이 장치를 제거 가능한 외장 장치로 식별하지 못했습니다. 하드웨어 안전 제거 메뉴를 확인해주세요.");
    }
}
