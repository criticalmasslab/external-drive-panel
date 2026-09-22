# -*- coding: utf-8 -*-
"""설정 창: 화면 영역 설정 진입 / 패널 순서 변경 / 등록된 외장하드 관리 / 시작프로그램 등록"""

import tkinter as tk

import config_manager
import startup_utils
from zone_settings_dialog import ZoneSettingsDialog


class SettingsDialog(tk.Toplevel):
    def __init__(self, master, config, get_display_name, on_changed, on_unregister):
        """
        get_display_name(serial) -> 목록에 보여줄 이름 (드라이브 문자 + 볼륨 이름, 자동 계산)
        on_changed(): 순서/화면영역 등이 바뀌어 레이아웃을 다시 그려야 할 때
        on_unregister(serial): 등록 해제를 실제로 수행 (확인창 포함, main.py에서 처리)
        """
        super().__init__(master)
        self.title("설정")
        self.geometry("420x420")
        self.resizable(False, False)
        self.config_data = config
        self.get_display_name = get_display_name
        self.on_changed = on_changed
        self.on_unregister = on_unregister

        self._build_ui()
        # 메인 창이 숨겨져 있으므로 transient()는 쓰지 않는다
        self.lift()
        self.attributes("-topmost", True)
        self.after(200, lambda: self.attributes("-topmost", False))
        self.focus_force()
        self.grab_set()

    def _build_ui(self):
        pad = {"padx": 12, "pady": 6}

        # ---- 화면 영역 설정 ----
        tk.Label(self, text="패널 화면 위치", font=("Segoe UI", 10, "bold"), anchor="w").pack(fill="x", **pad)
        tk.Button(self, text="⚙ 화면 영역 설정 열기", command=self._open_zone_settings).pack(anchor="w", padx=24)

        tk.Frame(self, height=1, bg="#cccccc").pack(fill="x", pady=10, padx=12)

        # ---- 옵션 ----
        self.notify_var = tk.BooleanVar(value=self.config_data.get("notify_new_drive", True))
        tk.Checkbutton(
            self, text="등록 안 된 새 외장하드가 연결되면 등록 여부 물어보기",
            variable=self.notify_var, command=self._save_notify,
        ).pack(anchor="w", **pad)

        self.startup_var = tk.BooleanVar(value=startup_utils.is_registered())
        tk.Checkbutton(
            self, text="윈도우 시작 시 자동 실행",
            variable=self.startup_var, command=self._toggle_startup,
        ).pack(anchor="w", **pad)

        tk.Frame(self, height=1, bg="#cccccc").pack(fill="x", pady=10, padx=12)

        # ---- 등록된 외장하드 순서/삭제 ----
        tk.Label(self, text="등록된 외장하드 (패널 순서)", font=("Segoe UI", 10, "bold"), anchor="w").pack(fill="x", **pad)

        list_frame = tk.Frame(self)
        list_frame.pack(fill="both", expand=True, padx=12)
        self.listbox = tk.Listbox(list_frame)
        self.listbox.pack(side="left", fill="both", expand=True)
        sb = tk.Scrollbar(list_frame, command=self.listbox.yview)
        sb.pack(side="right", fill="y")
        self.listbox.config(yscrollcommand=sb.set)
        self._refresh_drive_list()

        btn_frame = tk.Frame(self)
        btn_frame.pack(fill="x", padx=12, pady=8)
        tk.Button(btn_frame, text="▲ 위로", command=lambda: self._move(-1)).pack(side="left")
        tk.Button(btn_frame, text="▼ 아래로", command=lambda: self._move(1)).pack(side="left", padx=6)
        tk.Button(btn_frame, text="등록 해제", command=self._unregister_selected, fg="#c0392b").pack(side="right")

        tk.Button(self, text="닫기", command=self.destroy).pack(pady=10)

    def _open_zone_settings(self):
        ZoneSettingsDialog(self, self.config_data, self.on_changed)

    def _refresh_drive_list(self):
        self.listbox.delete(0, "end")
        for serial in self.config_data["drive_order"]:
            name = self.get_display_name(serial)
            self.listbox.insert("end", name)

    def _save_notify(self):
        self.config_data["notify_new_drive"] = self.notify_var.get()
        config_manager.save_config(self.config_data)

    def _toggle_startup(self):
        if self.startup_var.get():
            startup_utils.register_startup()
        else:
            startup_utils.unregister_startup()

    def _move(self, direction):
        sel = self.listbox.curselection()
        if not sel:
            return
        idx = sel[0]
        serial = self.config_data["drive_order"][idx]
        config_manager.move_drive_order(self.config_data, serial, direction)
        self._refresh_drive_list()
        new_idx = max(0, min(idx + direction, self.listbox.size() - 1))
        self.listbox.selection_set(new_idx)
        self.on_changed()

    def _unregister_selected(self):
        sel = self.listbox.curselection()
        if not sel:
            return
        idx = sel[0]
        serial = self.config_data["drive_order"][idx]
        self.on_unregister(serial)
        self._refresh_drive_list()
