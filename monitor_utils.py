# -*- coding: utf-8 -*-
"""
멀티모니터 환경에서 각 모니터의 위치/크기를 알아내기 위한 유틸리티.
순수 ctypes(user32.dll)만 사용하므로 추가 패키지 설치가 필요 없다.
"""

import ctypes
from ctypes import wintypes


class _MONITORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("rcMonitor", wintypes.RECT),
        ("rcWork", wintypes.RECT),
        ("dwFlags", wintypes.DWORD),
    ]


def get_monitors():
    """연결된 모든 모니터의 정보를 리스트로 반환.
    각 항목: {'index', 'left', 'top', 'right', 'bottom', 'width', 'height', 'is_primary'}
    좌표는 '가상 화면(virtual screen)' 기준이며, 주 모니터가 아닌 경우
    음수 좌표가 나올 수 있다 (정상).

    작업표시줄 등을 제외한 '작업 영역(work area)' 기준으로 반환한다. 그래서
    여백을 0으로 둬도 패널이 작업표시줄과 겹치지 않는다.
    """
    monitors = []

    MonitorEnumProc = ctypes.WINFUNCTYPE(
        ctypes.c_int,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.POINTER(wintypes.RECT),
        wintypes.LPARAM,
    )

    user32 = ctypes.windll.user32

    def _callback(hMonitor, hdcMonitor, lprcMonitor, dwData):
        rect = None
        info = _MONITORINFO()
        info.cbSize = ctypes.sizeof(_MONITORINFO)
        if user32.GetMonitorInfoW(hMonitor, ctypes.byref(info)):
            rect = info.rcWork  # 작업표시줄 등을 제외한 작업 영역
        else:
            rect = lprcMonitor.contents  # 실패 시 전체 모니터 영역으로 대체

        monitors.append(
            {
                "left": rect.left,
                "top": rect.top,
                "right": rect.right,
                "bottom": rect.bottom,
                "width": rect.right - rect.left,
                "height": rect.bottom - rect.top,
            }
        )
        return 1  # 계속 열거

    user32.EnumDisplayMonitors(0, 0, MonitorEnumProc(_callback), 0)

    # 주 모니터 판별: (0,0)을 포함하는 모니터
    for i, m in enumerate(monitors):
        m["index"] = i
        m["is_primary"] = m["left"] <= 0 <= m["right"] and m["top"] <= 0 <= m["bottom"]

    return monitors


def get_monitor_by_index(index):
    monitors = get_monitors()
    if 0 <= index < len(monitors):
        return monitors[index]
    # 유효하지 않으면 주 모니터(또는 첫 번째) 반환
    for m in monitors:
        if m.get("is_primary"):
            return m
    return monitors[0] if monitors else {"left": 0, "top": 0, "right": 1920, "bottom": 1080, "width": 1920, "height": 1080, "index": 0, "is_primary": True}


def right_quarter_rect(monitor):
    """주어진 모니터의 오른쪽 1/4 영역 좌표(left, top, right, bottom)를 반환."""
    quarter_width = monitor["width"] // 4
    left = monitor["right"] - quarter_width
    top = monitor["top"]
    right = monitor["right"]
    bottom = monitor["bottom"]
    return left, top, right, bottom
