# -*- coding: utf-8 -*-
"""
외장하드 바탕화면 패널 - 메인 진입점

동작 개요:
1. 등록된 외장하드가 연결되면, 화면 영역 설정(zone)에 지정된 칸에
   해당 외장하드 전용 '미니 탐색기' 패널이 자동으로 뜬다.
2. 여러 개가 동시에 연결되면 그 칸을 설정된 방향(세로/가로)으로 n등분한다.
3. 등록 안 된 외장하드가 연결되면 (설정에 따라) 등록할지 물어본다.
4. 트레이 아이콘에서 수동 등록 / 설정 / 드라이브별 숨기기·등록해제 / 종료 가능.
5. 패널/드라이브 이름은 사용자가 직접 정하지 않고, 드라이브 문자 + 볼륨
   이름으로 항상 자동 계산한다.

Windows 전용 프로그램입니다. (ctypes.windll, os.startfile 사용)
드래그 앤 드롭 기능은 tkinterdnd2 패키지가 설치되어 있어야 동작합니다
(없어도 나머지 기능은 정상 동작).
"""

import os
import sys
import ctypes
import tkinter as tk
from tkinter import messagebox


def _enable_dpi_awareness():
    """고해상도(DPI 스케일링) 모니터에서 화면이 흐릿하게 보이는 문제 방지.
    윈도우에게 "이 프로그램이 DPI를 직접 처리하겠다"고 알려줘서, 윈도우가
    임의로 확대해서 흐리게 그리는 대신 실제 해상도로 선명하게 그리게 한다.
    창을 하나라도 만들기 전에 반드시 호출해야 한다.
    """
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
        return
    except (AttributeError, OSError):
        pass
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except (AttributeError, OSError):
        pass


_enable_dpi_awareness()

try:
    from tkinterdnd2 import TkinterDnD
    DND_ROOT_AVAILABLE = True
except ImportError:
    DND_ROOT_AVAILABLE = False

import config_manager
import drive_utils
import layout_manager
from panel_window import ExternalDrivePanel, ICON_SIZES
from register_dialog import RegisterDialog
from settings_dialog import SettingsDialog
from zone_settings_dialog import ZoneSettingsDialog
from top_bar import TopBar

POLL_INTERVAL_MS = 2000


class App:
    def __init__(self):
        if DND_ROOT_AVAILABLE:
            self.root = TkinterDnD.Tk()
        else:
            self.root = tk.Tk()
        self.root.withdraw()  # 메인 윈도우는 화면에 안 보이게 (트레이로만 조작)

        self.config = config_manager.load_config()
        self.panels = {}            # serial(int) -> ExternalDrivePanel
        self.prompted_serials = set()  # 이번 연결 세션에 이미 등록 여부를 물어본 미등록 드라이브

        self.tray = None
        self.top_bar = None

    # ---------- 이름 계산 (드라이브 문자 + 볼륨 이름, 자동) ----------

    def compute_display_name(self, serial):
        """트레이/패널 제목 등에 쓸 (title_display, root_label) 튜플 반환."""
        root_path = drive_utils.find_root_by_serial(serial)
        if root_path:
            letter = root_path.rstrip("\\")
            _, label = drive_utils.get_volume_info(root_path)
            title = f"{letter} {label}" if label else letter
            return title, letter

        info = self.config["drives"].get(str(serial), {})
        last_label = info.get("last_known_label", "")
        title = f"(연결 안됨) {last_label}".strip() if last_label else "(연결 안됨)"
        return title, "?:"

    # ---------- 드라이브 <-> 패널 동기화 ----------

    def resolve_full_path_factory(self, serial):
        def resolve(path_relative):
            root_path = drive_utils.find_root_by_serial(serial)
            if root_path is None:
                return None
            if not path_relative:
                return root_path.rstrip("\\")
            return os.path.join(root_path, path_relative)
        return resolve

    def create_panel(self, serial):
        info = self.config["drives"][str(serial)]
        state = config_manager.ensure_drive_state(self.config, serial)
        global_view = config_manager.ensure_global_view(self.config)
        title_display, root_label = self.compute_display_name(serial)
        panel = ExternalDrivePanel(
            self.root,
            serial,
            title_display,
            root_label,
            info["items"],
            state,
            global_view,
            self.resolve_full_path_factory(serial),
            self.on_add_item,
            self.on_remove_item,
            self.on_rename_item,
            self.on_state_change,
        )
        self.panels[serial] = panel
        if state.get("hidden", False):
            panel.withdraw()

    def destroy_panel(self, serial):
        panel = self.panels.pop(serial, None)
        if panel is not None:
            panel.destroy()

    def relayout(self):
        if self.top_bar:
            self.top_bar.set_geometry(*layout_manager.compute_top_bar_rect(self.config))

        ordered = [
            s for s in self.config["drive_order"]
            if s in self.panels and not self._is_hidden(s)
        ]
        rects = layout_manager.compute_panel_rects(self.config, ordered)
        for serial, (x, y, w, h) in rects.items():
            self.panels[serial].set_geometry(x, y, w, h)

    def _is_hidden(self, serial):
        state = self.config["drives"].get(str(serial), {}).get("state", {})
        return state.get("hidden", False)

    def sync_panels_with_snapshot(self, snapshot):
        """snapshot: drive_utils.snapshot_all_volumes() 결과. 변경 있었으면 True 반환."""
        changed = False
        for serial_str in list(self.config["drives"].keys()):
            serial = int(serial_str)
            connected = serial in snapshot
            shown = serial in self.panels
            if connected and not shown:
                self.create_panel(serial)
                changed = True
            elif not connected and shown:
                self.destroy_panel(serial)
                changed = True
        if changed:
            self.relayout()
        return changed

    # ---------- 아이템(즐겨찾기) 편집 콜백 ----------

    def on_add_item(self, serial, path_relative, display_name):
        items = self.config["drives"][str(serial)]["items"]
        # 이미 등록된 경로면 중복 추가하지 않음
        if any(it["path_relative"] == path_relative for it in items):
            return
        items.append({"path_relative": path_relative, "display_name": display_name})
        config_manager.save_config(self.config)
        if serial in self.panels:
            self.panels[serial].refresh_items(items)

    def on_remove_item(self, serial, path_relative):
        key = str(serial)
        items = self.config["drives"][key]["items"]
        self.config["drives"][key]["items"] = [it for it in items if it["path_relative"] != path_relative]
        config_manager.save_config(self.config)
        if serial in self.panels:
            self.panels[serial].refresh_items(self.config["drives"][key]["items"])

    def on_rename_item(self, serial, path_relative, new_name):
        items = self.config["drives"][str(serial)]["items"]
        for it in items:
            if it["path_relative"] == path_relative:
                it["display_name"] = new_name
        config_manager.save_config(self.config)
        if serial in self.panels:
            self.panels[serial].refresh_items(items)

    def on_state_change(self, serial, patch):
        state = config_manager.ensure_drive_state(self.config, serial)
        if state is None:
            return
        state.update(patch)
        config_manager.save_config(self.config)

    # ---------- 숨기기 / 등록 해제 (패널 + 트레이 공용) ----------

    def on_toggle_hidden(self, serial):
        state = config_manager.ensure_drive_state(self.config, serial)
        if state is None:
            return
        state["hidden"] = not state.get("hidden", False)
        config_manager.save_config(self.config)
        if serial in self.panels:
            if state["hidden"]:
                self.panels[serial].withdraw()
            else:
                self.panels[serial].deiconify()
            self.relayout()
        if self.tray:
            self.tray.refresh_menu()

    def on_unregister(self, serial):
        title_display, _ = self.compute_display_name(serial)
        if not messagebox.askyesno(
            "등록 해제",
            f"'{title_display}' 등록을 해제할까요?\n(파일은 삭제되지 않고, 패널만 더 이상 안 뜹니다)",
        ):
            return
        config_manager.unregister_drive(self.config, serial)
        self.destroy_panel(serial)
        self.relayout()
        if self.tray:
            self.tray.refresh_menu()

    def get_drive_entries(self):
        """트레이 메뉴 구성을 위한 등록된 드라이브 목록."""
        entries = []
        for serial in self.config["drive_order"]:
            title_display, _ = self.compute_display_name(serial)
            entries.append({
                "serial": serial,
                "display_name": title_display,
                "hidden": self._is_hidden(serial),
            })
        return entries

    # ---------- 등록 / 설정 다이얼로그 ----------

    def open_register_dialog(self, preset_serial=None, preset_root=None, preset_label=None):
        def handle_registered(serial, items, label):
            config_manager.register_drive(self.config, serial, label, items)
            snapshot = drive_utils.snapshot_all_volumes()
            self.sync_panels_with_snapshot(snapshot)
            if self.tray:
                self.tray.refresh_menu()

        RegisterDialog(self.root, self.config, handle_registered, preset_serial, preset_root, preset_label)

    def open_settings_dialog(self):
        SettingsDialog(self.root, self.config, self.compute_display_name_for_list, self.relayout, self.on_unregister)

    def compute_display_name_for_list(self, serial):
        title_display, _ = self.compute_display_name(serial)
        return title_display

    def open_zone_settings(self):
        ZoneSettingsDialog(self.root, self.config, self.relayout)

    # ---------- 공통 상단바 (모든 패널에 동시 적용되는 보기옵션) ----------

    def setup_top_bar(self):
        global_view = config_manager.ensure_global_view(self.config)
        self.top_bar = TopBar(
            self.root,
            global_view,
            on_open_zone_settings=self.open_zone_settings,
            on_toggle_hidden=self.on_topbar_toggle_hidden,
            on_change_icon_size=self.on_topbar_change_icon_size,
            on_set_view_mode=self.on_topbar_set_view_mode,
        )
        self.relayout()

    def _broadcast_global_view(self):
        global_view = config_manager.ensure_global_view(self.config)
        config_manager.save_config(self.config)
        for panel in self.panels.values():
            panel.apply_global_view(
                global_view["view_mode"], global_view["icon_size"], global_view["show_hidden"]
            )
        if self.top_bar:
            self.top_bar.update_view(global_view)

    def on_topbar_toggle_hidden(self):
        global_view = config_manager.ensure_global_view(self.config)
        global_view["show_hidden"] = not global_view.get("show_hidden", False)
        self._broadcast_global_view()

    def on_topbar_change_icon_size(self, direction):
        global_view = config_manager.ensure_global_view(self.config)
        current = global_view.get("icon_size", ICON_SIZES[1])
        idx = ICON_SIZES.index(current) if current in ICON_SIZES else 1
        new_idx = idx + direction
        if 0 <= new_idx < len(ICON_SIZES):
            global_view["icon_size"] = ICON_SIZES[new_idx]
            self._broadcast_global_view()

    def on_topbar_set_view_mode(self, mode):
        global_view = config_manager.ensure_global_view(self.config)
        if global_view.get("view_mode") == mode:
            return
        global_view["view_mode"] = mode
        self._broadcast_global_view()

    # ---------- 폴링 루프 ----------

    def poll_drives(self):
        snapshot = drive_utils.snapshot_all_volumes()
        changed = self.sync_panels_with_snapshot(snapshot)
        if changed and self.tray:
            # 연결/해제로 이름 표시("(연결 안됨)" 등)가 바뀔 수 있으니 트레이 메뉴 갱신
            self.tray.refresh_menu()

        if self.config.get("notify_new_drive", True):
            registered = set(int(s) for s in self.config["drives"].keys())
            for serial, info in snapshot.items():
                if serial in registered:
                    continue
                if info["root"].upper() == "C:\\":
                    continue  # 시스템 드라이브는 후보에서 제외
                if serial in self.prompted_serials:
                    continue
                self.prompted_serials.add(serial)
                self.show_new_drive_prompt(serial, info)

        # 연결 해제된 미등록 드라이브는 다음에 다시 꽂았을 때 또 물어볼 수 있게 초기화
        for serial in list(self.prompted_serials):
            if serial not in snapshot:
                self.prompted_serials.discard(serial)

        self.root.after(POLL_INTERVAL_MS, self.poll_drives)

    def show_new_drive_prompt(self, serial, info):
        label = info["label"] or info["root"]
        if messagebox.askyesno(
            "새 외장하드 감지",
            f"'{label}' 외장하드가 연결되었습니다.\n이 프로그램에 등록해서 패널로 관리할까요?",
        ):
            self.open_register_dialog(preset_serial=serial, preset_root=info["root"], preset_label=info["label"])

    # ---------- 트레이 ----------

    def setup_tray(self):
        try:
            from tray_app import TrayApp
        except ImportError:
            print("pystray/Pillow가 설치되어 있지 않아 트레이 아이콘 없이 실행합니다.")
            print("트레이 기능을 쓰려면: pip install pystray pillow")
            return

        self.tray = TrayApp(
            on_register=lambda: self.root.after(0, self.open_register_dialog),
            on_settings=lambda: self.root.after(0, self.open_settings_dialog),
            on_quit=lambda: self.root.after(0, self.quit),
            get_drive_entries=self.get_drive_entries,
            on_toggle_hidden=lambda serial: self.root.after(0, lambda: self.on_toggle_hidden(serial)),
            on_unregister=lambda serial: self.root.after(0, lambda: self.on_unregister(serial)),
        )
        self.tray.start()

    def quit(self):
        self.root.quit()
        sys.exit(0)

    # ---------- 실행 ----------

    def run(self):
        if os.name != "nt":
            messagebox.showerror("실행 불가", "이 프로그램은 Windows 전용입니다.")
            return

        self.setup_tray()
        self.setup_top_bar()
        # 시작하자마자 한 번 즉시 확인 (부팅 시 이미 꽂혀있던 외장하드 바로 표시)
        self.root.after(200, self.poll_drives)
        self.root.mainloop()


if __name__ == "__main__":
    App().run()
