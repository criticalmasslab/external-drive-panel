# -*- coding: utf-8 -*-
"""
설정을 %APPDATA%\\ExternalDeskPanel\\config.json 에 저장/로드.

config 구조:
{
    "notify_new_drive": true,
    "start_with_windows": false,
    "drive_order": [12345678, 87654321],   # 볼륨 시리얼 번호 순서 = 패널 순서
    "zone": {
        "monitor_index": 0,
        "grid_cols": 4, "grid_rows": 1,
        "cell_col": 3, "cell_row": 0,
        "split_direction": "vertical",
        "margin_uniform": true, "margin_all": 0,
        "margin_top": 0, "margin_bottom": 0, "margin_left": 0, "margin_right": 0
    },
    "drives": {
        "12345678": {
            "last_known_label": "MyDrive",
            "items": [
                {"path_relative": "Projects\\\\ClientA", "display_name": "ClientA"}
            ],
            "state": { ... }
        }
    }
}

이름(custom_name)은 더 이상 사용자가 직접 정하지 않는다. 패널/트레이 어디서든
표시 이름은 그때그때 드라이브 문자 + 볼륨 이름으로 자동 계산한다 (main.py 참고).
"""

import os
import json

APP_DIR_NAME = "ExternalDeskPanel"
CONFIG_FILE_NAME = "config.json"

DEFAULT_ZONE = {
    "monitor_index": 0,
    "grid_cols": 4,
    "grid_rows": 1,
    "cell_col": 3,          # 기본값: 가로 4칸 중 맨 오른쪽 = 기존 "오른쪽 1/4"과 동일
    "cell_row": 0,
    "split_direction": "vertical",  # 그 칸 안에서 여러 외장하드를 세로/가로 중 어떻게 나눌지
    "margin_uniform": True,
    "margin_all": 0,
    "margin_top": 0,
    "margin_bottom": 0,
    "margin_left": 0,
    "margin_right": 0,
}

DEFAULT_CONFIG = {
    "notify_new_drive": True,
    "start_with_windows": False,
    "drive_order": [],
    "zone": dict(DEFAULT_ZONE),
    "global_view": dict({
        "view_mode": "icons",   # "icons" | "details" - 모든 패널 공통
        "icon_size": 64,        # 모든 패널 공통
        "show_hidden": False,   # 모든 패널 공통
    }),
    "drives": {},
}

DEFAULT_GLOBAL_VIEW = {
    "view_mode": "icons",
    "icon_size": 64,
    "show_hidden": False,
}

DEFAULT_DRIVE_STATE = {
    "active_tab_index": 0,      # 현재 활성 즐겨찾기 탭
    "sort_key": "name",         # "name" | "date" | "size" | "type" - 드라이브별로 따로 기억
    "sort_reverse": False,
    "hidden": False,            # 패널을 잠시 숨겨둔 상태인지 (트레이 메뉴에서 토글)
}


def ensure_drive_state(config, serial):
    """예전 버전 설정 파일 호환: state 키가 없으면 기본값으로 채워준다."""
    key = str(serial)
    drive = config["drives"].get(key)
    if drive is None:
        return None
    if "state" not in drive:
        drive["state"] = dict(DEFAULT_DRIVE_STATE)
    else:
        for k, v in DEFAULT_DRIVE_STATE.items():
            drive["state"].setdefault(k, v)
    return drive["state"]


def ensure_global_view(config):
    """보기 옵션(아이콘 크기/보기모드/숨김표시)은 이제 모든 패널이 공통으로 사용."""
    if "global_view" not in config or not isinstance(config["global_view"], dict):
        config["global_view"] = dict(DEFAULT_GLOBAL_VIEW)
    else:
        for k, v in DEFAULT_GLOBAL_VIEW.items():
            config["global_view"].setdefault(k, v)
    return config["global_view"]


def ensure_zone(config):
    """예전 버전 호환: zone 키가 없거나 일부만 있으면 기본값으로 채워준다."""
    if "zone" not in config or not isinstance(config["zone"], dict):
        config["zone"] = dict(DEFAULT_ZONE)
    else:
        for k, v in DEFAULT_ZONE.items():
            config["zone"].setdefault(k, v)
    return config["zone"]


def get_config_dir():
    appdata = os.environ.get("APPDATA")
    if not appdata:
        # APPDATA가 없는 특이한 환경 대비 (윈도우가 아니거나 테스트 환경)
        appdata = os.path.expanduser("~")
    path = os.path.join(appdata, APP_DIR_NAME)
    os.makedirs(path, exist_ok=True)
    return path


def get_config_path():
    return os.path.join(get_config_dir(), CONFIG_FILE_NAME)


def load_config():
    path = get_config_path()
    if not os.path.exists(path):
        save_config(DEFAULT_CONFIG)
        return json.loads(json.dumps(DEFAULT_CONFIG))  # deep copy

    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except (json.JSONDecodeError, OSError):
        data = json.loads(json.dumps(DEFAULT_CONFIG))

    # 누락된 키 보완 (버전 업데이트 대비)
    for key, value in DEFAULT_CONFIG.items():
        if key not in data:
            data[key] = json.loads(json.dumps(value))

    ensure_zone(data)
    ensure_global_view(data)
    for serial in list(data["drives"].keys()):
        ensure_drive_state(data, int(serial))

    return data


def save_config(config):
    path = get_config_path()
    with open(path, "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)


def register_drive(config, serial, label, items):
    """새 외장하드를 config에 등록. items = [{'path_relative':.., 'display_name':..}, ...]"""
    key = str(serial)
    config["drives"][key] = {
        "last_known_label": label or "",
        "items": items,
        "state": dict(DEFAULT_DRIVE_STATE),
    }
    if serial not in config["drive_order"]:
        config["drive_order"].append(serial)
    save_config(config)


def unregister_drive(config, serial):
    key = str(serial)
    if key in config["drives"]:
        del config["drives"][key]
    if serial in config["drive_order"]:
        config["drive_order"].remove(serial)
    save_config(config)


def move_drive_order(config, serial, direction):
    """direction: -1 (위로/앞으로), +1 (아래로/뒤로)"""
    order = config["drive_order"]
    if serial not in order:
        return
    idx = order.index(serial)
    new_idx = idx + direction
    if 0 <= new_idx < len(order):
        order[idx], order[new_idx] = order[new_idx], order[idx]
        save_config(config)
