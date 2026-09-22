# -*- coding: utf-8 -*-
"""
config['zone'] 설정(모니터/격자/칸/분할방향/여백)에 따라, 연결된(등록된)
외장하드 개수(n)만큼 그 칸을 세로 또는 가로로 나눠서 각 패널이 차지할
(x, y, width, height)를 계산한다.

칸의 맨 위 TOP_BAR_HEIGHT 만큼은 모든 외장하드 패널에 공통으로 적용되는
상단바(프로그램 이름 + 보기옵션 버튼들)가 차지하고, 그 아래 남은 영역을
외장하드 패널들이 나눠 쓴다.
"""

import monitor_utils
import config_manager

TOP_BAR_HEIGHT = 28


def compute_cell_rect(zone, monitor):
    """격자에서 선택된 칸의 화면 좌표(여백 적용 후)를 반환."""
    cols = max(1, zone.get("grid_cols", 1))
    rows = max(1, zone.get("grid_rows", 1))
    col = min(zone.get("cell_col", 0), cols - 1)
    row = min(zone.get("cell_row", 0), rows - 1)

    cell_w = monitor["width"] / cols
    cell_h = monitor["height"] / rows

    left = monitor["left"] + col * cell_w
    top = monitor["top"] + row * cell_h
    right = left + cell_w
    bottom = top + cell_h

    if zone.get("margin_uniform", True):
        m = zone.get("margin_all", 0)
        left += m
        top += m
        right -= m
        bottom -= m
    else:
        left += zone.get("margin_left", 0)
        top += zone.get("margin_top", 0)
        right -= zone.get("margin_right", 0)
        bottom -= zone.get("margin_bottom", 0)

    left, top, right, bottom = int(left), int(top), int(right), int(bottom)
    if right <= left:
        right = left + 10
    if bottom <= top:
        bottom = top + 10

    return left, top, right, bottom


def _resolve_cell(config):
    zone = config_manager.ensure_zone(config)
    monitor = monitor_utils.get_monitor_by_index(zone.get("monitor_index", 0))
    return zone, compute_cell_rect(zone, monitor)


def compute_top_bar_rect(config):
    """모든 외장하드 패널 공통 상단바가 위치할 (x, y, w, h)."""
    _zone, (left, top, right, bottom) = _resolve_cell(config)
    width = right - left
    height = min(TOP_BAR_HEIGHT, max(1, bottom - top))
    return left, top, width, height


def compute_panel_rects(config, ordered_serials):
    """ordered_serials: 화면에 표시할 순서대로(위->아래 또는 왼쪽->오른쪽) 나열된
    볼륨 시리얼 리스트. 반환: {serial: (x, y, w, h)}
    (공통 상단바 높이만큼은 제외한 나머지 영역을 나눈다)
    """
    zone, (left, top, right, bottom) = _resolve_cell(config)
    top = min(top + TOP_BAR_HEIGHT, bottom - 10)

    n = len(ordered_serials)
    result = {}
    if n == 0:
        return result

    direction = zone.get("split_direction", "vertical")

    if direction == "horizontal":
        total_width = right - left
        each = total_width // n
        height = bottom - top
        for i, serial in enumerate(ordered_serials):
            x = left + i * each
            w = each if i < n - 1 else (right - x)
            result[serial] = (x, top, w, height)
    else:  # vertical (기본값)
        total_height = bottom - top
        each = total_height // n
        width = right - left
        for i, serial in enumerate(ordered_serials):
            y = top + i * each
            h = each if i < n - 1 else (bottom - y)
            result[serial] = (left, y, width, h)

    return result
