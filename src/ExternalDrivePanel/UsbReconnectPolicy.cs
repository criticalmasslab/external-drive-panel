using System.IO;

namespace ExternalDrivePanel;

public sealed record UsbReconnectNode(string Id, string Service, uint Status, uint Problem, uint Port, uint Capabilities);
public static class UsbReconnectPolicy
{
    public static UsbReconnectTarget Select(IReadOnlyList<UsbReconnectNode> ancestors,
        Func<string, bool> hasActiveDisk, Func<string, string, uint, bool> portMatches)
    {
        if (ancestors.Count == 0 || (ancestors[0].Status & 0x400) == 0 || ancestors[0].Problem != 47)
            throw new IOException("안전 제거 대기 상태가 아닙니다.");
        for (var i = 0; i + 1 < ancestors.Count; i++)
        {
            var usb = ancestors[i]; var hub = ancestors[i + 1];
            if (!usb.Id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)
                || usb.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                || !(usb.Service.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase) || usb.Service.Equals("UASPStor", StringComparison.OrdinalIgnoreCase))) continue;
            var target = new UsbReconnectTarget(ancestors[0].Id, usb.Id, hub.Id, usb.Port);
            if ((usb.Capabilities & 8) != 0 || !DeviceReconnectService.IsValid(target)
                || !hub.Service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase)
                || hasActiveDisk(usb.Id) || !portMatches(hub.Id, usb.Id, usb.Port))
                throw new IOException("이 USB 포트를 개별적으로 재연결할 수 없습니다.");
            return target;
        }
        throw new IOException("지원되는 USB 저장장치가 아닙니다.");
    }
}
