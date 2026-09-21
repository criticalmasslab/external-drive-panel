# -*- coding: utf-8 -*-
"""
화면 영역(격자) 설정 창.
- 모니터 선택
- 화면을 가로 x 세로 격자로 나누고, 그중 칸 하나를 선택
- 그 칸 안에서 여러 외장하드를 세로/가로 중 어떻게 나열할지
- 칸 가장자리에서 안쪽으로 띄울 여백(사방 동일 or 개별)

패널 툴바의 ⚙ 아이콘, 트레이 "설정" 창 양쪽에서 공통으로 이 창을 연다.
"""

import tkinter as tk
from tkinter import messagebox

import monitor_utils
import config_manager

PREVIEW_W = 320
PREVIEW_H = 200
CELL_BG = "#3a3a3a"
CELL_SELECTED_BG = "#3a8ee6"
CELL_BORDER = "#666666"


class ZoneSettingsDialog(tk.Toplevel):
    def __init__(self, master, config, on_changed):
        """on_changed(): 설정이 바뀌어 레이아웃을 다시 그려야 할 때 호출"""
        super().__init__(master)
        self.title("화면 영역 설정")
        self.resizable(False, False)
        self.config_data = config
        self.on_changed = on_changed
        self.zone = config_manager.ensure_zone(config)

        self.monitors = monitor_utils.get_monitors()
        self.monitor_var = tk.IntVar(value=self.zone.get("monitor_index", 0))
        self.cols_var = tk.IntVar(value=self.zone.get("grid_cols", 4))
        self.rows_var = tk.IntVar(value=self.zone.get("grid_rows", 1))
        self.selected_col = self.zone.get("cell_col", 3)
        self.selected_row = self.zone.get("cell_row", 0)
        self.direction_var = tk.StringVar(value=self.zone.get("split_direction", "vertical"))
        self.margin_uniform_var = tk.BooleanVar(value=self.zone.get("margin_uniform", True))
        self.margin_all_var = tk.IntVar(value=self.zone.get("margin_all", 0))
        self.margin_top_var = tk.IntVar(value=self.zone.get("margin_top", 0))
        self.margin_bottom_var = tk.IntVar(value=self.zone.get("margin_bottom", 0))
        self.margin_left_var = tk.IntVar(value=self.zone.get("margin_left", 0))
        self.margin_right_var = tk.IntVar(value=self.zone.get("margin_right", 0))

        self._build_ui()
        # 메인 창이 숨겨진 상태(root.withdraw())일 수 있으므로 transient()는 쓰지 않는다
        self.lift()
        self.attributes("-topmost", True)
        self.after(200, lambda: self.attributes("-topmost", False))
        self.focus_force()
        self.grab_set()

    def _build_ui(self):
        pad = {"padx": 12, "pady": 6}

        tk.Label(self, text="모니터 선택", font=("Segoe UI", 10, "bold"), anchor="w").pack(fill="x", **pad)
        for m in self.monitors:
            label = f"모니터 {m['index'] + 1}  ({m['width']}x{m['height']}){'  [주 모니터]' if m['is_primary'] else ''}"
            tk.Radiobutton(self, text=label, variable=self.monitor_var, value=m["index"],
                           command=self._redraw_preview).pack(anchor="w", padx=24)

        tk.Frame(self, height=1, bg="#cccccc").pack(fill="x", pady=8, padx=12)

        tk.Label(self, text="화면을 몇 칸으로 나눌까요?", font=("Segoe UI", 10, "bold"), anchor="w").pack(fill="x", **pad)
        grid_frame = tk.Frame(self)
        grid_frame.pack(anchor="w", padx=24)
        tk.Label(grid_frame, text="가로 칸 수:").pack(side="left")
        tk.Spinbox(grid_frame, from_=1, to=8, width=4, textvariable=self.cols_var,
                   command=self._on_grid_size_change).pack(side="left", padx=(4, 16))
        tk.Label(grid_frame, text="세로 칸 수:").pack(side="left")
        tk.Spinbox(grid_frame, from_=1, to=8, width=4, textvariable=self.rows_var,
                   command=self._on_grid_size_change).pack(side="left", padx=(4, 0))

        tk.Label(self, text="아래에서 패널이 위치할 칸을 클릭하세요:", anchor="w").pack(fill="x", padx=12, pady=(8, 2))
        self.preview_canvas = tk.Canvas(self, width=PREVIEW_W, height=PREVIEW_H, bg="#1e1e1e", highlightthickness=1,
                                         highlightbackground="#888888")
        self.preview_canvas.pack(padx=12, pady=(0, 8))
        self.preview_canvas.bind("<Button-1>", self._on_preview_click)
        self._redraw_preview()

        tk.Frame(self, height=1, bg="#cccccc").pack(fill="x", pady=8, padx=12)

        tk.Label(self, text="외장하드가 여러 개면 그 칸 안에서:", font=("Segoe UI", 10, "bold"), anchor="w").pack(fill="x", **pad)
        dir_frame = tk.Frame(self)
        dir_frame.pack(anchor="w", padx=24)
        tk.Radiobutton(dir_frame, text="세로로 나열", variable=self.direction_var, value="vertical").pack(side="left")
        tk.Radiobutton(dir_frame, text="가로로 나열", variable=self.direction_var, value="horizontal").pack(side="left", padx=(12, 0))

        tk.Frame(self, height=1, bg="#cccccc").pack(fill="x", pady=8, padx=12)

        tk.Label(self, text="여백 (칸 가장자리에서 안쪽으로, px)", font=("Segoe UI", 10, "bold"), anchor="w").pack(fill="x", **pad)
        tk.Checkbutton(self, text="사방 동일하게 적용", variable=self.margin_uniform_var,
                       command=self._toggle_margin_mode).pack(anchor="w", padx=24)

        self.margin_uniform_frame = tk.Frame(self)
        tk.Label(self.margin_uniform_frame, text="여백:").pack(side="left")
        tk.Spinbox(self.margin_uniform_frame, from_=0, to=300, width=5, textvariable=self.margin_all_var).pack(side="left", padx=4)

        self.margin_each_frame = tk.Frame(self)
        for label_text, var in (("위", self.margin_top_var), ("아래", self.margin_bottom_var),
                                  ("왼쪽", self.margin_left_var), ("오른쪽", self.margin_right_var)):
            row = tk.Frame(self.margin_each_frame)
            row.pack(side="left", padx=4)
            tk.Label(row, text=label_text).pack()
            tk.Spinbox(row, from_=0, to=300, width=5, textvariable=var).pack()

        self._toggle_margin_mode()

        btn_frame = tk.Frame(self)
        btn_frame.pack(fill="x", padx=12, pady=14)
        tk.Button(btn_frame, text="취소", command=self.destroy).pack(side="right", padx=(6, 0))
        tk.Button(btn_frame, text="저장", command=self._save, bg="#3a8ee6", fg="white").pack(side="right")

    def _toggle_margin_mode(self):
        if self.margin_uniform_var.get():
            self.margin_each_frame.pack_forget()
            self.margin_uniform_frame.pack(anchor="w", padx=24, pady=(4, 8))
        else:
            self.margin_uniform_frame.pack_forget()
            self.margin_each_frame.pack(anchor="w", padx=24, pady=(4, 8))

    def _on_grid_size_change(self):
        cols = max(1, self.cols_var.get())
        rows = max(1, self.rows_var.get())
        self.selected_col = min(self.selected_col, cols - 1)
        self.selected_row = min(self.selected_row, rows - 1)
        self._redraw_preview()

    def _redraw_preview(self):
        self.preview_canvas.delete("all")
        cols = max(1, self.cols_var.get())
        rows = max(1, self.rows_var.get())
        cell_w = PREVIEW_W / cols
        cell_h = PREVIEW_H / rows

        for r in range(rows):
            for c in range(cols):
                x0, y0 = c * cell_w, r * cell_h
                x1, y1 = x0 + cell_w, y0 + cell_h
                is_selected = (c == self.selected_col and r == self.selected_row)
                fill = CELL_SELECTED_BG if is_selected else CELL_BG
                self.preview_canvas.create_rectangle(x0, y0, x1, y1, fill=fill, outline=CELL_BORDER)

    def _on_preview_click(self, event):
        cols = max(1, self.cols_var.get())
        rows = max(1, self.rows_var.get())
        cell_w = PREVIEW_W / cols
        cell_h = PREVIEW_H / rows
        col = min(int(event.x // cell_w), cols - 1)
        row = min(int(event.y // cell_h), rows - 1)
        self.selected_col, self.selected_row = col, row
        self._redraw_preview()

    def _save(self):
        self.zone.update({
            "monitor_index": self.monitor_var.get(),
            "grid_cols": max(1, self.cols_var.get()),
            "grid_rows": max(1, self.rows_var.get()),
            "cell_col": self.selected_col,
            "cell_row": self.selected_row,
            "split_direction": self.direction_var.get(),
            "margin_uniform": self.margin_uniform_var.get(),
            "margin_all": max(0, self.margin_all_var.get()),
            "margin_top": max(0, self.margin_top_var.get()),
            "margin_bottom": max(0, self.margin_bottom_var.get()),
            "margin_left": max(0, self.margin_left_var.get()),
            "margin_right": max(0, self.margin_right_var.get()),
        })
        config_manager.save_config(self.config_data)
        self.destroy()
        self.on_changed()
