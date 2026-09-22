# -*- coding: utf-8 -*-
"""
모든 외장하드 패널 위에 공통으로 뜨는 상단바.
왼쪽엔 프로그램 이름, 오른쪽엔 모든 패널에 동시에 적용되는 보기 옵션 버튼들
(⚙ 화면 영역 설정, 👁 숨김 항목 표시, － ＋ 아이콘 크기, ⊞ ☰ 보기모드).
"""

import tkinter as tk

from panel_window import BG_COLOR, TITLE_BG, FG_COLOR, DIM_FG, ICON_SIZES

APP_TITLE = "External Drive Panel"


class TopBar(tk.Toplevel):
    def __init__(self, master, global_view,
                 on_open_zone_settings, on_toggle_hidden, on_change_icon_size, on_set_view_mode):
        """
        global_view: {"view_mode","icon_size","show_hidden"} - 현재 값 표시용
        on_open_zone_settings(): ⚙ 클릭
        on_toggle_hidden(): 👁 클릭
        on_change_icon_size(direction): － ＋ 클릭 (direction: -1 | +1)
        on_set_view_mode(mode): ⊞(icons) / ☰(details) 클릭
        """
        super().__init__(master)
        self.on_open_zone_settings = on_open_zone_settings
        self.on_toggle_hidden = on_toggle_hidden
        self.on_change_icon_size = on_change_icon_size
        self.on_set_view_mode = on_set_view_mode

        self.overrideredirect(True)
        try:
            self.attributes("-alpha", 0.96)
        except tk.TclError:
            pass
        self.configure(bg=TITLE_BG)

        self._build_ui()
        self.update_view(global_view)

    def _build_ui(self):
        tk.Label(self, text=f"🖴 {APP_TITLE}", bg=TITLE_BG, fg=FG_COLOR,
                 font=("Segoe UI", 10, "bold")).pack(side="left", padx=8)

        right = tk.Frame(self, bg=TITLE_BG)
        right.pack(side="right", padx=4)

        def btn(text, cmd):
            b = tk.Label(right, text=text, bg=TITLE_BG, fg=FG_COLOR,
                         font=("Segoe UI", 10), padx=5)
            b.bind("<Button-1>", lambda e: cmd())
            b.pack(side="right")
            return b

        self.detail_view_btn = btn("☰", lambda: self.on_set_view_mode("details"))
        self.icon_view_btn = btn("⊞", lambda: self.on_set_view_mode("icons"))
        self.icon_plus_btn = btn("＋", lambda: self.on_change_icon_size(1))
        self.icon_minus_btn = btn("－", lambda: self.on_change_icon_size(-1))
        self.hidden_toggle_btn = btn("👁", self.on_toggle_hidden)
        self.settings_btn = btn("⚙", self.on_open_zone_settings)

    def update_view(self, global_view):
        """현재 보기옵션 값을 받아서 버튼 강조 상태를 갱신."""
        view_mode = global_view.get("view_mode", "icons")
        show_hidden = global_view.get("show_hidden", False)
        self.icon_view_btn.config(fg=FG_COLOR if view_mode == "icons" else DIM_FG)
        self.detail_view_btn.config(fg=FG_COLOR if view_mode == "details" else DIM_FG)
        self.hidden_toggle_btn.config(fg=FG_COLOR if show_hidden else DIM_FG)

    def set_geometry(self, x, y, w, h):
        self.geometry(f"{w}x{h}+{x}+{y}")
