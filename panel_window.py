# -*- coding: utf-8 -*-
"""
외장하드 1개에 대응하는 '미니 탐색기' 패널.

- 등록된 폴더/파일들은 상단 탭(즐겨찾기 시작점)으로 표시된다.
- 탭을 클릭하거나 아이콘을 더블클릭해 폴더 안으로 들어가면, 그 안에서
  실제 윈도우 탐색기처럼 자유롭게 탐색(하위 폴더 진입/상위 이동/뒤로/앞으로)할 수 있다.
- 보기 모드: 아이콘(그리드) / 상세(이름·수정한 날짜·크기·유형 표
- 정렬: 이름/날짜/크기/유형, 오름차순/내림차순
- 드라이브가 뽑히면 이 패널 자체가 통째로 사라진다 (main.py 에서 destroy 호출).
"""

import os
import shutil
import datetime
import ctypes
import tkinter as tk
from tkinter import ttk, filedialog, simpledialog, messagebox

import drive_utils

try:
    from tkinterdnd2 import DND_FILES
    DND_AVAILABLE = True
except ImportError:
    DND_FILES = None
    DND_AVAILABLE = False

FILE_ATTRIBUTE_HIDDEN = 0x2
FILE_ATTRIBUTE_SYSTEM = 0x4


def is_hidden_or_system(full_path):
    """윈도우 탐색기가 기본적으로 숨기는 '숨김'/'시스템' 속성 파일인지 확인.
    예: System Volume Information, $RECYCLE.BIN, desktop.ini 등
    """
    try:
        attrs = ctypes.windll.kernel32.GetFileAttributesW(str(full_path))
        if attrs == -1:  # INVALID_FILE_ATTRIBUTES
            return False
        return bool(attrs & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM))
    except OSError:
        return False

BG_COLOR = "#1e1e1e"
PANEL_BG = "#252526"
TITLE_BG = "#2d2d30"
TAB_BG = "#333336"
TAB_ACTIVE_BG = "#3a8ee6"
TOOLBAR_BG = "#2d2d30"
FG_COLOR = "#f0f0f0"
DIM_FG = "#9a9a9a"
SELECT_BG = "#3a8ee6"

ICON_SIZES = [48, 64, 80, 96, 128]
DEFAULT_ICON_SIZE_INDEX = 1  # 64

EXT_ICON_MAP = {
    ".pdf": "📕", ".doc": "📘", ".docx": "📘", ".hwp": "📗",
    ".xls": "📗", ".xlsx": "📗", ".ppt": "📙", ".pptx": "📙",
    ".zip": "🗜️", ".rar": "🗜️", ".7z": "🗜️",
    ".mp3": "🎵", ".wav": "🎵", ".flac": "🎵",
    ".mp4": "🎬", ".avi": "🎬", ".mov": "🎬", ".mkv": "🎬",
    ".jpg": "🖼️", ".jpeg": "🖼️", ".png": "🖼️", ".gif": "🖼️", ".bmp": "🖼️", ".webp": "🖼️",
    ".exe": "⚙️", ".msi": "⚙️",
    ".py": "🐍", ".js": "📜", ".html": "🌐", ".css": "🎨",
    ".txt": "📄", ".md": "📄",
}


def icon_for(name, is_dir):
    if is_dir:
        return "📁"
    ext = os.path.splitext(name)[1].lower()
    return EXT_ICON_MAP.get(ext, "📄")


def human_size(num_bytes):
    if num_bytes is None:
        return ""
    size = float(num_bytes)
    for unit in ["B", "KB", "MB", "GB", "TB"]:
        if size < 1024 or unit == "TB":
            return f"{size:.0f}{unit}" if unit == "B" else f"{size:.1f}{unit}"
        size /= 1024
    return f"{size:.1f}TB"


def human_date(timestamp):
    try:
        return datetime.datetime.fromtimestamp(timestamp).strftime("%Y-%m-%d %H:%M")
    except (OSError, OverflowError, ValueError):
        return ""


ROOT_TAB_BG = "#565659"


class ExternalDrivePanel(tk.Toplevel):
    def __init__(self, master, serial, title_display, root_label, items, state, global_view, resolve_full_path,
                 on_add_item, on_remove_item, on_rename_item,
                 on_state_change):
        """
        title_display: 제목표시줄에 보여줄 문자열 (예: "D: MyDrive") - 자동 계산됨, 사용자가 못 바꿈
        root_label: 드라이브 문자만 (예: "D:") - 현재는 별도 표시에 안 쓰이지만 향후 확장을 위해 보관
        items: [{'path_relative':.., 'display_name':..}, ...]  -> 상단 즐겨찾기 탭
        state: {'active_tab_index','sort_key','sort_reverse'} - 드라이브별
        global_view: {'view_mode','icon_size','show_hidden'} - 모든 패널 공통(top_bar.py가 관리)
        resolve_full_path(path_relative) -> 현재 드라이브 문자 기준 절대경로 (드라이브 없으면 None)
        on_state_change(serial, patch_dict): 탐색 상태(경로/탭/정렬)가 바뀔 때마다 호출 (저장용)
        """
        super().__init__(master)
        self.serial = serial
        self.title_display = title_display
        self.root_label = root_label
        self.items = items
        self.resolve_full_path = resolve_full_path
        self.on_add_item = on_add_item
        self.on_remove_item = on_remove_item
        self.on_rename_item = on_rename_item
        self.on_state_change = on_state_change

        # ---- 상태 (드라이브별) ----
        self.active_tab_index = min(state.get("active_tab_index", 0), max(0, len(items) - 1))
        self.sort_key = state.get("sort_key", "name")
        self.sort_reverse = state.get("sort_reverse", False)

        # ---- 보기옵션 (모든 패널 공통, top_bar.py가 값 전달) ----
        self.view_mode = global_view.get("view_mode", "icons")
        icon_size = global_view.get("icon_size", ICON_SIZES[DEFAULT_ICON_SIZE_INDEX])
        self.icon_size_index = ICON_SIZES.index(icon_size) if icon_size in ICON_SIZES else DEFAULT_ICON_SIZE_INDEX
        self.show_hidden = global_view.get("show_hidden", False)

        self.current_relative_path = ""  # 외장하드를 다시 연결할 때마다 항상 루트(홈)에서 시작
        self.history_back = []
        self.history_forward = []
        self.selected_path = None
        self.current_entries = []  # 지금 폴더에 보이는 항목들 캐시
        self._cell_widgets = {}    # 아이콘 뷰에서 이름 -> (cell, icon_lbl, name_lbl) 위젯 캐시
        self._copy_cancelled = False

        self.overrideredirect(True)
        try:
            self.attributes("-alpha", 0.96)
        except tk.TclError:
            pass
        self.configure(bg=BG_COLOR)

        self._build_tab_bar()
        self._build_toolbar()
        self._build_body()

        self._navigate(self.current_relative_path, add_history=False, save=False)

    # ================= UI 구성 =================

    def _build_tab_bar(self):
        self.tab_bar = tk.Frame(self, bg=TAB_BG, height=26)
        self.tab_bar.pack(side="top", fill="x")
        self.tab_bar.pack_propagate(False)
        self._render_tabs()
        self._register_background_drop_target(self.tab_bar)

    def _render_tabs(self):
        for w in self.tab_bar.winfo_children():
            w.destroy()

        # 드라이브 자체를 나타내는 고정 탭 (제목표시줄 역할 겸용, 삭제/이름변경 불가)
        root_btn = tk.Label(
            self.tab_bar, text=f"💾 {self.title_display}", bg=ROOT_TAB_BG, fg="white",
            font=("Segoe UI", 8, "bold"), padx=6, pady=3,
        )
        root_btn.pack(side="left", padx=(2, 2), pady=2)
        root_btn.bind("<Button-1>", lambda e: self._navigate(""))

        eject_btn = tk.Label(
            self.tab_bar, text="⏏", bg=TAB_BG, fg=DIM_FG,
            font=("Segoe UI", 10), padx=6, pady=3,
        )
        eject_btn.pack(side="left", padx=(0, 4), pady=2)
        eject_btn.bind("<Button-1>", lambda e: self._eject_drive())

        for idx, item in enumerate(self.items):
            active = idx == self.active_tab_index
            btn = tk.Label(
                self.tab_bar,
                text=f"📌 {item['display_name']}",
                bg=TAB_ACTIVE_BG if active else TAB_BG,
                fg="white" if active else DIM_FG,
                font=("Segoe UI", 8, "bold" if active else "normal"),
                padx=6, pady=3,
            )
            btn.pack(side="left", padx=(2, 0), pady=2)
            btn.bind("<Button-1>", lambda e, i=idx: self._activate_tab(i))
            btn.bind("<Button-3>", lambda e, i=idx: self._show_tab_menu(e, i))

        add_btn = tk.Label(self.tab_bar, text="＋", bg=TAB_BG, fg=DIM_FG,
                            font=("Segoe UI", 9, "bold"), padx=6, pady=3)
        add_btn.pack(side="left", padx=(2, 0), pady=2)
        add_btn.bind("<Button-1>", lambda e: self._add_current_folder_as_favorite())

    def _build_toolbar(self):
        bar = tk.Frame(self, bg=TOOLBAR_BG)
        bar.pack(side="top", fill="x")

        nav_frame = tk.Frame(bar, bg=TOOLBAR_BG)
        nav_frame.pack(side="top", fill="x", padx=4, pady=(4, 2))

        def nav_btn(parent, text, cmd):
            b = tk.Label(parent, text=text, bg=TOOLBAR_BG, fg=FG_COLOR,
                         font=("Segoe UI", 10), padx=5)
            b.bind("<Button-1>", lambda e: cmd())
            return b

        self.back_btn = nav_btn(nav_frame, "⬅", self._go_back)
        self.back_btn.pack(side="left")
        self.fwd_btn = nav_btn(nav_frame, "➡", self._go_forward)
        self.fwd_btn.pack(side="left")
        self.up_btn = nav_btn(nav_frame, "⬆", self._go_up)
        self.up_btn.pack(side="left")
        self.home_btn = nav_btn(nav_frame, "🏠", self._go_home)
        self.home_btn.pack(side="left", padx=(0, 4))

        # 주소표시줄 (경로 breadcrumb)
        self.breadcrumb_frame = tk.Frame(bar, bg=TOOLBAR_BG)
        self.breadcrumb_frame.pack(side="top", fill="x", padx=4, pady=(0, 2))

        # 선택된 항목 경로 표시줄
        self.status_label = tk.Label(
            bar, text="", bg=TOOLBAR_BG, fg=DIM_FG, font=("Segoe UI", 8),
            anchor="w", padx=4,
        )
        self.status_label.pack(side="top", fill="x", pady=(0, 2))

    def _build_body(self):
        self.body_container = tk.Frame(self, bg=BG_COLOR)
        self.body_container.pack(side="top", fill="both", expand=True)
        self._build_icon_view()
        self._build_detail_view()

    def _build_icon_view(self):
        self.icon_canvas = tk.Canvas(self.body_container, bg=BG_COLOR, highlightthickness=0)
        self.icon_scrollbar = tk.Scrollbar(self.body_container, orient="vertical", command=self.icon_canvas.yview)
        self.icon_grid = tk.Frame(self.icon_canvas, bg=BG_COLOR)

        self.icon_grid.bind(
            "<Configure>",
            lambda e: self.icon_canvas.configure(scrollregion=self.icon_canvas.bbox("all")),
        )
        self.icon_canvas_window = self.icon_canvas.create_window((0, 0), window=self.icon_grid, anchor="nw")
        self.icon_canvas.configure(yscrollcommand=self.icon_scrollbar.set)
        self.icon_canvas.bind("<Configure>", self._on_icon_canvas_resize)
        self.icon_canvas.bind("<Button-3>", self._show_empty_menu)
        self.icon_canvas.bind_all("<MouseWheel>", self._on_mousewheel, add="+")
        self._register_background_drop_target(self.icon_canvas)
        self._register_background_drop_target(self.icon_grid)

    def _build_detail_view(self):
        columns = ("name", "date", "size", "type")
        self.tree = ttk.Treeview(self.body_container, columns=columns, show="headings", selectmode="browse")

        style = ttk.Style(self)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure("Treeview", background=PANEL_BG, fieldbackground=PANEL_BG,
                         foreground=FG_COLOR, rowheight=22, borderwidth=0)
        style.configure("Treeview.Heading", background=TOOLBAR_BG, foreground=FG_COLOR,
                         relief="flat")
        style.map("Treeview", background=[("selected", SELECT_BG)])

        headings = {"name": "이름", "date": "수정한 날짜", "size": "크기", "type": "유형"}
        widths = {"name": 160, "date": 120, "size": 70, "type": 70}
        for col in columns:
            self.tree.heading(col, text=headings[col], command=lambda c=col: self._sort_by(c))
            self.tree.column(col, width=widths[col], anchor="w")

        self.tree_scrollbar = tk.Scrollbar(self.body_container, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=self.tree_scrollbar.set)

        self.tree.bind("<<TreeviewSelect>>", self._on_tree_select)
        self.tree.bind("<Double-Button-1>", self._on_tree_double_click)
        self.tree.bind("<Button-3>", self._on_tree_right_click)
        self._register_background_drop_target(self.tree)

    def _show_active_view(self):
        self.icon_canvas.pack_forget()
        self.icon_scrollbar.pack_forget()
        self.tree.pack_forget()
        self.tree_scrollbar.pack_forget()

        if self.view_mode == "icons":
            self.icon_canvas.pack(side="left", fill="both", expand=True)
            self.icon_scrollbar.pack(side="right", fill="y")
        else:
            self.tree.pack(side="left", fill="both", expand=True)
            self.tree_scrollbar.pack(side="right", fill="y")

    # ================= 탐색(네비게이션) =================

    def _navigate(self, relative_path, add_history=True, save=True):
        abs_path = self.resolve_full_path(relative_path)
        if abs_path is None or not os.path.isdir(abs_path):
            self.status_label.config(text="⚠ 이 드라이브가 연결되어 있지 않거나 폴더를 찾을 수 없습니다.")
            self.current_entries = []
            self._render_current_view()
            return

        if add_history and relative_path != self.current_relative_path:
            self.history_back.append(self.current_relative_path)
            self.history_forward.clear()

        self.current_relative_path = relative_path
        self.selected_path = None
        self._load_entries(abs_path)
        self._render_breadcrumb(abs_path)
        self._render_current_view()
        self.status_label.config(text=f"{len(self.current_entries)}개 항목")

        if save:
            self._save_state()

    def _load_entries(self, abs_path):
        entries = []
        try:
            with os.scandir(abs_path) as it:
                for entry in it:
                    try:
                        # 윈도우 탐색기 기본 설정과 동일하게 숨김/시스템 항목은 표시 안 함
                        # (단, '숨김 항목 표시'가 켜져 있으면 다 보여줌)
                        if not self.show_hidden and is_hidden_or_system(entry.path):
                            continue
                        is_dir = entry.is_dir()
                        stat = entry.stat()
                        entries.append({
                            "name": entry.name,
                            "is_dir": is_dir,
                            "size": None if is_dir else stat.st_size,
                            "mtime": stat.st_mtime,
                            "ext": "" if is_dir else os.path.splitext(entry.name)[1].lower(),
                        })
                    except OSError:
                        continue
        except (PermissionError, OSError):
            self.status_label.config(text="⚠ 이 폴더에 접근할 권한이 없습니다.")

        entries.sort(key=self._sort_key_func(), reverse=self.sort_reverse)
        # 폴더 우선 표시 (이름/날짜/크기 어떤 기준이든 관계없이 폴더가 먼저)
        entries.sort(key=lambda e: (not e["is_dir"]))
        self.current_entries = entries

    def _sort_key_func(self):
        if self.sort_key == "date":
            return lambda e: e["mtime"]
        if self.sort_key == "size":
            return lambda e: e["size"] or 0
        if self.sort_key == "type":
            return lambda e: e["ext"]
        return lambda e: e["name"].lower()

    def _sort_by(self, key):
        if self.sort_key == key:
            self.sort_reverse = not self.sort_reverse
        else:
            self.sort_key = key
            self.sort_reverse = False
        abs_path = self.resolve_full_path(self.current_relative_path)
        if abs_path:
            self._load_entries(abs_path)
            self._render_current_view()
        self._save_state()

    def _go_back(self):
        if not self.history_back:
            return
        prev = self.history_back.pop()
        self.history_forward.append(self.current_relative_path)
        self._navigate(prev, add_history=False)

    def _go_forward(self):
        if not self.history_forward:
            return
        nxt = self.history_forward.pop()
        self.history_back.append(self.current_relative_path)
        self._navigate(nxt, add_history=False)

    def _go_up(self):
        if not self.current_relative_path:
            return  # 이미 드라이브 루트
        parent = os.path.dirname(self.current_relative_path)
        self._navigate(parent)

    def _go_home(self):
        if not self.items:
            self._navigate("")
            return
        self._navigate(self.items[self.active_tab_index]["path_relative"])

    def _activate_tab(self, index):
        self.active_tab_index = index
        self._render_tabs()
        self._navigate(self.items[index]["path_relative"])

    def _eject_drive(self):
        abs_root = self.resolve_full_path("")
        if not abs_root:
            messagebox.showwarning("꺼내기 실패", "드라이브를 찾을 수 없습니다.")
            return
        if not messagebox.askyesno(
            "외장하드 꺼내기",
            f"'{self.title_display}' 외장하드를 안전하게 꺼낼까요?\n(사용 중인 파일이 있으면 실패할 수 있어요)",
        ):
            return
        drive_root = abs_root if abs_root.endswith("\\") else abs_root + "\\"
        success, message = drive_utils.eject_drive(drive_root)
        if success:
            messagebox.showinfo("꺼내기 완료", "안전하게 제거되었습니다. 이제 케이블을 뽑으셔도 됩니다.")
        else:
            messagebox.showerror("꺼내기 실패", message)

    # ================= 렌더링 =================

    def _render_breadcrumb(self, abs_path):
        for w in self.breadcrumb_frame.winfo_children():
            w.destroy()

        drive, rest = os.path.splitdrive(abs_path)
        parts = [p for p in rest.split(os.sep) if p]
        cumulative = drive + os.sep

        def make_segment(text, path_value, is_last):
            fg = FG_COLOR if is_last else DIM_FG
            lbl = tk.Label(self.breadcrumb_frame, text=text, bg=TOOLBAR_BG, fg=fg,
                           font=("Segoe UI", 8, "bold" if is_last else "normal"))
            lbl.pack(side="left")
            if not is_last:
                lbl.bind("<Button-1>", lambda e, p=path_value: self._navigate_to_absolute(p))
                sep = tk.Label(self.breadcrumb_frame, text=" › ", bg=TOOLBAR_BG, fg=DIM_FG,
                               font=("Segoe UI", 8))
                sep.pack(side="left")

        make_segment(drive, cumulative, len(parts) == 0)
        for i, part in enumerate(parts):
            cumulative = os.path.join(cumulative, part)
            make_segment(part, cumulative, i == len(parts) - 1)

    def _navigate_to_absolute(self, abs_path):
        drive_root = self.resolve_full_path("")
        if drive_root is None:
            return
        relative = os.path.relpath(abs_path, drive_root)
        relative = "" if relative == "." else relative
        self._navigate(relative)

    def _render_current_view(self):
        self._show_active_view()
        if self.view_mode == "icons":
            self._render_icon_grid()
        else:
            self._render_tree()

        # 뒤/앞/위 버튼 활성 표시
        self.back_btn.config(fg=FG_COLOR if self.history_back else DIM_FG)
        self.fwd_btn.config(fg=FG_COLOR if self.history_forward else DIM_FG)
        self.up_btn.config(fg=FG_COLOR if self.current_relative_path else DIM_FG)

    def _render_icon_grid(self):
        for w in self.icon_grid.winfo_children():
            w.destroy()
        self._cell_widgets = {}

        cell_w = ICON_SIZES[self.icon_size_index] + 20
        width = max(self.icon_canvas.winfo_width(), cell_w)
        cols = max(1, width // cell_w)

        if not self.current_entries:
            tk.Label(self.icon_grid, text="빈 폴더입니다.", bg=BG_COLOR, fg=DIM_FG,
                     font=("Segoe UI", 9)).grid(row=0, column=0, padx=20, pady=20)
            return

        for idx, entry in enumerate(self.current_entries):
            row, col = divmod(idx, cols)
            self._create_icon_cell(entry, row, col, cell_w)

    def _create_icon_cell(self, entry, row, col, cell_w):
        icon_char = icon_for(entry["name"], entry["is_dir"])
        is_selected = self.selected_path == entry["name"]
        fg = FG_COLOR
        bg = SELECT_BG if is_selected else BG_COLOR
        font_size = max(16, int(ICON_SIZES[self.icon_size_index] * 0.4))

        cell = tk.Frame(self.icon_grid, bg=bg, width=cell_w, height=cell_w + 16)
        cell.grid(row=row, column=col, padx=2, pady=2)
        cell.grid_propagate(False)
        cell.normal_bg = bg  # 드롭 하이라이트가 끝났을 때 되돌아갈 색 (선택 상태 반영)

        icon_lbl = tk.Label(cell, text=icon_char, font=("Segoe UI Emoji", font_size), bg=bg, fg=fg)
        icon_lbl.pack(pady=(4, 0))
        name_lbl = tk.Label(cell, text=entry["name"], font=("Segoe UI", 8), bg=bg, fg=fg,
                             wraplength=cell_w - 6, justify="center")
        name_lbl.pack()

        for widget in (cell, icon_lbl, name_lbl):
            widget.bind("<Button-1>", lambda e, en=entry: self._select_entry(en))
            widget.bind("<Double-Button-1>", lambda e, en=entry: self._open_entry(en))
            widget.bind("<Button-3>", lambda e, en=entry: self._show_item_menu(e, en))

        if entry["is_dir"]:
            self._register_folder_drop_target(cell, entry)

        self._cell_widgets[entry["name"]] = (cell, icon_lbl, name_lbl)

    def _render_tree(self):
        self.tree.delete(*self.tree.get_children())
        for entry in self.current_entries:
            type_str = "파일 폴더" if entry["is_dir"] else (entry["ext"].lstrip(".").upper() + " 파일" if entry["ext"] else "파일")
            self.tree.insert(
                "", "end", iid=entry["name"],
                values=(entry["name"], human_date(entry["mtime"]), human_size(entry["size"]), type_str),
            )

    def _on_icon_canvas_resize(self, event):
        self.icon_canvas.itemconfig(self.icon_canvas_window, width=event.width)
        if self.view_mode == "icons":
            self._render_icon_grid()

    def _on_mousewheel(self, event):
        widget_under = self.winfo_containing(event.x_root, event.y_root)
        if widget_under and str(widget_under).startswith(str(self)):
            self.icon_canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

    # ================= 선택/열기 =================

    def _select_entry(self, entry):
        old_selected = self.selected_path
        self.selected_path = entry["name"]
        abs_path = self.resolve_full_path(self.current_relative_path)
        full = os.path.join(abs_path, entry["name"]) if abs_path else entry["name"]
        self.status_label.config(text=f"선택됨: {full}")

        # 전체를 다시 그리지 않고, 이전/새 선택 셀의 색만 바꾼다.
        # (전체를 다시 그리면 더블클릭의 두 번째 클릭이 새로 생긴 위젯에 떨어져서
        #  윈도우가 더블클릭으로 인식을 못 하는 문제가 있었음)
        if old_selected and old_selected != self.selected_path:
            self._set_cell_selected(old_selected, False)
        self._set_cell_selected(self.selected_path, True)

    def _set_cell_selected(self, name, selected):
        widgets = self._cell_widgets.get(name)
        if not widgets:
            return
        cell, icon_lbl, name_lbl = widgets
        bg = SELECT_BG if selected else BG_COLOR
        try:
            cell.config(bg=bg)
            icon_lbl.config(bg=bg)
            name_lbl.config(bg=bg)
            cell.normal_bg = bg
        except tk.TclError:
            pass

    def _on_tree_select(self, event):
        sel = self.tree.selection()
        if not sel:
            return
        name = sel[0]
        abs_path = self.resolve_full_path(self.current_relative_path)
        full = os.path.join(abs_path, name) if abs_path else name
        self.status_label.config(text=f"선택됨: {full}")

    def _open_entry(self, entry):
        if entry["is_dir"]:
            new_rel = os.path.join(self.current_relative_path, entry["name"]) if self.current_relative_path else entry["name"]
            self._navigate(new_rel)
        else:
            self._open_path_direct(entry["name"])

    def _on_tree_double_click(self, event):
        sel = self.tree.selection()
        if not sel:
            return
        name = sel[0]
        entry = next((e for e in self.current_entries if e["name"] == name), None)
        if entry:
            self._open_entry(entry)

    def _on_tree_right_click(self, event):
        iid = self.tree.identify_row(event.y)
        if iid:
            self.tree.selection_set(iid)
            entry = next((e for e in self.current_entries if e["name"] == iid), None)
            if entry:
                self._show_item_menu(event, entry)

    def _open_path_direct(self, name):
        abs_path = self.resolve_full_path(self.current_relative_path)
        if not abs_path:
            return
        full = os.path.join(abs_path, name)
        if not os.path.exists(full):
            messagebox.showwarning("열기 실패", "경로를 찾을 수 없습니다.")
            return
        try:
            os.startfile(full)  # Windows 전용
        except OSError as e:
            messagebox.showerror("열기 실패", str(e))

    # ================= 보기모드 / 아이콘 크기 (공통 상단바에서 제어) =================

    def apply_global_view(self, view_mode, icon_size, show_hidden):
        """공통 상단바(top_bar.py)의 보기옵션이 바뀌었을 때 main.py가 호출.
        모든 패널이 동시에 같은 값으로 맞춰진다."""
        changed = False

        if self.view_mode != view_mode:
            self.view_mode = view_mode
            changed = True

        if icon_size in ICON_SIZES:
            new_index = ICON_SIZES.index(icon_size)
            if new_index != self.icon_size_index:
                self.icon_size_index = new_index
                changed = True

        if self.show_hidden != show_hidden:
            self.show_hidden = show_hidden
            abs_path = self.resolve_full_path(self.current_relative_path)
            if abs_path:
                self._load_entries(abs_path)
            changed = True

        if changed:
            self._render_current_view()

    # ================= 메뉴 =================

    def _show_item_menu(self, event, entry):
        abs_path = self.resolve_full_path(self.current_relative_path)
        full = os.path.join(abs_path, entry["name"]) if abs_path else None
        menu = tk.Menu(self, tearoff=0)
        menu.add_command(label="열기", command=lambda: self._open_entry(entry))
        if full:
            menu.add_command(label="⭐ 즐겨찾기에 추가", command=lambda: self._add_favorite(full))
        menu.tk_popup(event.x_root, event.y_root)

    def _show_empty_menu(self, event):
        menu = tk.Menu(self, tearoff=0)
        menu.add_command(label="⭐ 현재 폴더를 즐겨찾기에 추가", command=self._add_current_folder_as_favorite)
        menu.add_command(label="정렬 기준: 이름", command=lambda: self._sort_by("name"))
        menu.add_command(label="정렬 기준: 날짜", command=lambda: self._sort_by("date"))
        menu.add_command(label="정렬 기준: 크기", command=lambda: self._sort_by("size"))
        menu.add_command(label="정렬 기준: 유형", command=lambda: self._sort_by("type"))
        menu.tk_popup(event.x_root, event.y_root)

    def _show_tab_menu(self, event, index):
        menu = tk.Menu(self, tearoff=0)
        menu.add_command(label="이 폴더로 이동", command=lambda: self._activate_tab(index))
        menu.add_command(label="이름 바꾸기", command=lambda: self._rename_tab_dialog(index))
        menu.add_separator()
        menu.add_command(label="즐겨찾기에서 제거", command=lambda: self._remove_tab(index))
        menu.tk_popup(event.x_root, event.y_root)

    # ================= 즐겨찾기(탭) 편집 =================

    def _add_current_folder_as_favorite(self):
        abs_path = self.resolve_full_path(self.current_relative_path)
        if abs_path:
            self._add_favorite(abs_path)

    def _add_favorite(self, full_path):
        drive_root = self.resolve_full_path("")
        if drive_root is None or not full_path.upper().startswith(drive_root.upper()):
            messagebox.showwarning("다른 드라이브", "이 패널과 같은 외장하드 안의 폴더만 즐겨찾기로 추가할 수 있어요.")
            return
        relative = os.path.relpath(full_path, drive_root)
        display_name = os.path.basename(full_path.rstrip("\\/")) or relative
        self.on_add_item(self.serial, relative, display_name)

    def refresh_items(self, items):
        """main.py가 즐겨찾기 목록이 바뀌었다고 알려줄 때 호출."""
        self.items = items
        self.active_tab_index = min(self.active_tab_index, max(0, len(items) - 1))
        self._render_tabs()

    def _rename_tab_dialog(self, index):
        item = self.items[index]
        new_name = simpledialog.askstring("이름 바꾸기", "즐겨찾기 이름:", initialvalue=item["display_name"], parent=self)
        if new_name:
            self.on_rename_item(self.serial, item["path_relative"], new_name)

    def _remove_tab(self, index):
        item = self.items[index]
        if messagebox.askyesno("즐겨찾기 제거", f"'{item['display_name']}' 을(를) 즐겨찾기에서 제거할까요?\n(실제 파일은 삭제되지 않습니다)"):
            self.on_remove_item(self.serial, item["path_relative"])

    # ================= 드래그 앤 드롭 =================

    def _register_background_drop_target(self, widget):
        if not DND_AVAILABLE:
            return
        try:
            widget.drop_target_register(DND_FILES)
            widget.dnd_bind("<<Drop>>", self._on_drop_background)
        except tk.TclError:
            pass

    def _register_folder_drop_target(self, cell, entry):
        if not DND_AVAILABLE:
            return
        try:
            cell.drop_target_register(DND_FILES)
            cell.dnd_bind("<<DropEnter>>", lambda e, c=cell: self._on_folder_drop_enter(e, c))
            cell.dnd_bind("<<DropLeave>>", lambda e, c=cell: self._on_folder_drop_leave(e, c))
            cell.dnd_bind("<<Drop>>", lambda e, en=entry: self._on_drop_on_folder(e, en))
        except tk.TclError:
            pass

    @staticmethod
    def _parse_drop_data(data):
        """tkinterdnd2 event.data 문자열을 경로 리스트로 변환.
        공백 포함 경로는 {중괄호}로 감싸져서 온다."""
        paths = []
        buf = ""
        in_brace = False
        for ch in data:
            if ch == "{":
                in_brace = True
                continue
            if ch == "}":
                in_brace = False
                paths.append(buf)
                buf = ""
                continue
            if ch == " " and not in_brace:
                if buf:
                    paths.append(buf)
                    buf = ""
                continue
            buf += ch
        if buf:
            paths.append(buf)
        return [p for p in paths if p]

    def _classify_paths(self, paths):
        """(같은 드라이브 경로 목록, 다른 드라이브 경로 목록) 튜플 반환."""
        drive_root = self.resolve_full_path("")
        same, other = [], []
        for p in paths:
            if drive_root and p.upper().startswith(drive_root.upper()):
                same.append(p)
            else:
                other.append(p)
        return same, other

    def _on_drop_background(self, event):
        paths = self._parse_drop_data(event.data)
        if not paths:
            return
        same, other = self._classify_paths(paths)
        for p in same:
            self._add_favorite(p)
        if other and not same:
            messagebox.showinfo(
                "다른 외장하드",
                "이 외장하드로 복사하려면, 패널 안의 특정 폴더 위에 정확히 놓아주세요.",
            )
        elif other:
            messagebox.showinfo(
                "일부 항목 건너뜀",
                "이 외장하드 안의 항목은 즐겨찾기로 등록했어요.\n"
                "다른 드라이브 항목을 복사하려면 특정 폴더 위에 놓아주세요.",
            )

    def _on_folder_drop_enter(self, event, cell):
        try:
            cell.config(bg=SELECT_BG)
            for child in cell.winfo_children():
                child.config(bg=SELECT_BG)
        except tk.TclError:
            pass

    def _on_folder_drop_leave(self, event, cell):
        if cell is None:
            return
        try:
            normal_bg = getattr(cell, "normal_bg", BG_COLOR)
            cell.config(bg=normal_bg)
            for child in cell.winfo_children():
                child.config(bg=normal_bg)
        except tk.TclError:
            pass

    def _on_drop_on_folder(self, event, entry):
        self._on_folder_drop_leave(event, event.widget if hasattr(event, "widget") else None)
        paths = self._parse_drop_data(event.data)
        if not paths:
            return
        same, other = self._classify_paths(paths)
        for p in same:
            self._add_favorite(p)

        if other:
            abs_current = self.resolve_full_path(self.current_relative_path)
            if not abs_current:
                return
            target_folder = os.path.join(abs_current, entry["name"])
            self._confirm_and_copy(other, target_folder, entry["name"])

    def _confirm_and_copy(self, source_paths, target_folder_abs, target_folder_display_name):
        names = ", ".join(os.path.basename(p.rstrip("\\/")) for p in source_paths)
        if not messagebox.askyesno(
            "복사 확인",
            f"'{target_folder_display_name}' 폴더로 복사하시겠습니까?\n\n{names}",
        ):
            return

        self._copy_cancelled = False
        for src in source_paths:
            if self._copy_cancelled:
                break
            dest = os.path.join(target_folder_abs, os.path.basename(src.rstrip("\\/")))
            self._copy_with_conflict_check(src, dest)

        # 복사한 폴더를 지금 보고 있었다면 새로고침
        abs_now = self.resolve_full_path(self.current_relative_path)
        if abs_now and os.path.normcase(abs_now) == os.path.normcase(os.path.dirname(target_folder_abs)):
            pass  # 대상은 하위 폴더라 지금 화면엔 영향 없음 (그대로 둠)

    def _copy_with_conflict_check(self, src, dest):
        if os.path.exists(dest):
            choice = self._ask_overwrite_dialog(os.path.basename(dest))
            if choice == "skip":
                return
            if choice == "cancel":
                self._copy_cancelled = True
                return
            # overwrite: 기존 것 제거 후 복사
            try:
                if os.path.isdir(dest):
                    shutil.rmtree(dest)
                else:
                    os.remove(dest)
            except OSError as e:
                messagebox.showerror("복사 실패", f"기존 파일을 제거하지 못했습니다.\n{e}")
                return

        try:
            if os.path.isdir(src):
                shutil.copytree(src, dest)
            else:
                shutil.copy2(src, dest)
        except OSError as e:
            messagebox.showerror("복사 실패", str(e))

    def _ask_overwrite_dialog(self, filename):
        result = {"choice": "cancel"}
        dlg = tk.Toplevel(self)
        dlg.title("파일이 이미 있습니다")
        dlg.resizable(False, False)
        tk.Label(dlg, text=f"'{filename}' 이(가) 이미 있습니다.\n어떻게 할까요?", padx=20, pady=14).pack()
        btn_frame = tk.Frame(dlg)
        btn_frame.pack(pady=10)

        def choose(c):
            result["choice"] = c
            dlg.destroy()

        tk.Button(btn_frame, text="덮어쓰기", command=lambda: choose("overwrite")).pack(side="left", padx=5)
        tk.Button(btn_frame, text="건너뛰기", command=lambda: choose("skip")).pack(side="left", padx=5)
        tk.Button(btn_frame, text="취소", command=lambda: choose("cancel")).pack(side="left", padx=5)

        dlg.transient(self)
        dlg.grab_set()
        dlg.wait_window()
        return result["choice"]

    # ================= 상태 저장 / 지오메트리 =================

    def _save_state(self):
        self.on_state_change(self.serial, {
            "active_tab_index": self.active_tab_index,
            "sort_key": self.sort_key,
            "sort_reverse": self.sort_reverse,
        })

    def set_geometry(self, x, y, w, h):
        self.geometry(f"{w}x{h}+{x}+{y}")
