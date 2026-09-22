# -*- coding: utf-8 -*-
"""
외장하드(드라이브) 감지 및 식별 관련 유틸리티.

윈도우에서 외장하드를 뺐다 꽂으면 드라이브 문자(D:, E: ...)가 바뀔 수 있기
때문에, 경로가 아니라 '볼륨 시리얼 번호'로 드라이브를 식별한다.
이 값은 그 저장장치(볼륨)가 포맷되지 않는 한 항상 동일하다.
"""

import os
import string
import ctypes
from ctypes import wintypes


def list_drive_roots():
    """현재 컴퓨터에 연결되어 실제로 접근 가능한 드라이브 루트 목록을 반환.
    예: ['C:\\\\', 'D:\\\\', 'E:\\\\']
    """
    roots = []
    for letter in string.ascii_uppercase:
        root = f"{letter}:\\"
        if os.path.exists(root):
            roots.append(root)
    return roots


def get_volume_info(drive_root):
    """drive_root (예: 'D:\\\\') 의 (volume_serial:int, volume_label:str) 반환.
    정보를 얻을 수 없으면 (None, None) 반환. (예: 드라이브가 준비 안 된 경우)
    """
    kernel32 = ctypes.windll.kernel32  # 윈도우 전용. 다른 OS에서는 여기서 예외 발생.

    volume_name_buf = ctypes.create_unicode_buffer(1024)
    fs_name_buf = ctypes.create_unicode_buffer(1024)
    serial_number = wintypes.DWORD(0)
    max_component_len = wintypes.DWORD(0)
    fs_flags = wintypes.DWORD(0)

    ok = kernel32.GetVolumeInformationW(
        ctypes.c_wchar_p(drive_root),
        volume_name_buf,
        ctypes.sizeof(volume_name_buf),
        ctypes.byref(serial_number),
        ctypes.byref(max_component_len),
        ctypes.byref(fs_flags),
        fs_name_buf,
        ctypes.sizeof(fs_name_buf),
    )

    if not ok:
        return None, None

    return serial_number.value, volume_name_buf.value


def get_drive_type(drive_root):
    """DRIVE_UNKNOWN=0, DRIVE_NO_ROOT_DIR=1, DRIVE_REMOVABLE=2,
    DRIVE_FIXED=3, DRIVE_REMOTE=4, DRIVE_CDROM=5, DRIVE_RAMDISK=6
    """
    kernel32 = ctypes.windll.kernel32
    return kernel32.GetDriveTypeW(ctypes.c_wchar_p(drive_root))


def find_root_by_serial(target_serial):
    """현재 연결된 드라이브들 중 target_serial과 볼륨 시리얼이 일치하는
    드라이브 루트를 찾아 반환. 없으면 None.
    """
    for root in list_drive_roots():
        try:
            serial, _label = get_volume_info(root)
        except OSError:
            continue
        if serial is not None and serial == target_serial:
            return root
    return None


# ---------------- 외장하드 안전하게 꺼내기 ----------------

_GENERIC_READ = 0x80000000
_GENERIC_WRITE = 0x40000000
_FILE_SHARE_READ = 0x1
_FILE_SHARE_WRITE = 0x2
_OPEN_EXISTING = 3

_FSCTL_LOCK_VOLUME = 0x00090018
_FSCTL_DISMOUNT_VOLUME = 0x00090020
_IOCTL_STORAGE_MEDIA_REMOVAL = 0x2D4804
_IOCTL_STORAGE_EJECT_MEDIA = 0x2D4808


def eject_drive(drive_root):
    """drive_root: 예: 'D:\\\\'. 안전하게 꺼내기를 시도한다.
    반환: (성공 여부: bool, 실패 사유: str)

    참고: 일부 USB 외장 HDD(특히 UASP 방식)는 이 방식으로 소프트웨어
    꺼내기가 지원되지 않을 수 있다. 그런 경우 실패 사유를 반환하니,
    호출하는 쪽에서 '작업표시줄의 하드웨어 안전 제거 아이콘을 이용하라'고
    안내하면 된다.
    """
    drive_letter = drive_root.rstrip("\\")  # "D:"
    volume_path = f"\\\\.\\{drive_letter}"

    kernel32 = ctypes.windll.kernel32
    kernel32.CreateFileW.restype = ctypes.c_void_p
    kernel32.CreateFileW.argtypes = [
        ctypes.c_wchar_p, wintypes.DWORD, wintypes.DWORD,
        ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
    ]
    kernel32.DeviceIoControl.restype = wintypes.BOOL
    kernel32.DeviceIoControl.argtypes = [
        ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD,
        ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p,
    ]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]

    invalid_handle = ctypes.c_void_p(-1).value

    handle = kernel32.CreateFileW(
        volume_path,
        _GENERIC_READ | _GENERIC_WRITE,
        _FILE_SHARE_READ | _FILE_SHARE_WRITE,
        None,
        _OPEN_EXISTING,
        0,
        None,
    )
    if handle is None or handle == invalid_handle:
        return False, "드라이브를 열 수 없습니다."

    try:
        bytes_returned = wintypes.DWORD(0)

        def ioctl(code, in_buf=None, in_size=0):
            return kernel32.DeviceIoControl(
                handle, code, in_buf, in_size, None, 0, ctypes.byref(bytes_returned), None
            )

        if not ioctl(_FSCTL_LOCK_VOLUME):
            return False, "드라이브가 사용 중이어서 잠글 수 없습니다.\n열려있는 파일/탐색기 창을 닫고 다시 시도해주세요."

        ioctl(_FSCTL_DISMOUNT_VOLUME)  # 실패해도 계속 진행

        prevent_removal = ctypes.c_ubyte(0)  # FALSE = 제거 허용
        ioctl(_IOCTL_STORAGE_MEDIA_REMOVAL, ctypes.byref(prevent_removal), 1)

        if not ioctl(_IOCTL_STORAGE_EJECT_MEDIA):
            return False, (
                "이 장치는 소프트웨어로 꺼내기가 지원되지 않는 것 같습니다.\n"
                "작업표시줄의 '하드웨어 안전하게 제거' 아이콘을 이용해주세요."
            )

        return True, ""
    finally:
        kernel32.CloseHandle(handle)


def snapshot_all_volumes():
    """현재 연결된 모든 드라이브의 {serial: {'root':..., 'label':..., 'type':...}}
    스냅샷을 반환. 감시 루프에서 매 주기 호출한다.
    """
    result = {}
    for root in list_drive_roots():
        try:
            serial, label = get_volume_info(root)
            dtype = get_drive_type(root)
        except OSError:
            continue
        if serial is None:
            continue
        result[serial] = {"root": root, "label": label, "type": dtype}
    return result
