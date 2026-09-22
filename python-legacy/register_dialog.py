# -*- coding: utf-8 -*-
"""
새 외장하드를 등록하는 다이얼로그.
- 트레이 메뉴에서 '외장하드 등록'을 눌렀을 때: 현재 연결된 드라이브 중
  아직 등록 안 된 것들을 골라서 등록
- 새 외장하드가 감지되어 자동 팝업이 떴을 때: 해당 드라이브가 미리 선택된 채로 시작
"""

import os
import tkinter as tk
from tkinter import filedialog, messagebox

import drive_utils


class RegisterDialog(tk.Toplevel):
    def __init__(self, master, config, on_registered, preset_serial=None, preset_root=None, preset_label=None):
        """
        config: config_manager.load_config() 로 얻은 dict
        on_registered(serial, items, label): 등록 완료 시 콜백
        preset_serial/root/label: 특정 드라이브를 미리 지정하고 싶을 때 (자동 팝업 경로)
        """
        super().__init__(master)
        self.title("외장하드 등록")
        self.geometry("460x420")
        self.resizable(False, False)
        self.config_data = config
        self.on_registered = on_registered

        self.selected_serial = preset_serial
        self.selected_root = preset_root
        self.selected_label = preset_label
        self.picked_items = []  # [{'path_relative', 'display_name', 'full_path'}]

        self._build_ui(skip_drive_pick=preset_serial is not None)

        # 메인 창이 숨겨져 있으므로 transient()는 쓰지 않는다
        # (transient 대상이 숨김 상태면 이 창도 같이 안 보이는 문제가 생김)
        self.lift()
        self.attributes("-topmost", True)
        self.after(200, lambda: self.attributes("-topmost", False))
        self.focus_force()
        self.grab_set()

    def _build_ui(self, skip_drive_pick):
        pad = {"padx": 10, "pady": 6}

        if not skip_drive_pick:
            tk.Label(self, text="등록할 외장하드 선택:", anchor="w").pack(fill="x", **pad)
            self.drive_var = tk.StringVar()
            candidates = self._get_unregistered_drives()
            if not candidates:
                tk.Label(self, text="등록 가능한(=아직 등록 안 된) 외장하드가 연결되어 있지 않습니다.").pack(**pad)
                tk.Button(self, text="닫기", command=self.destroy).pack(pady=10)
                return

            self.drive_map = {f"{c['root']}  ({c['label'] or '이름없음'})": c for c in candidates}
            self.drive_var.set(next(iter(self.drive_map)))
            option_menu = tk.OptionMenu(self, self.drive_var, *self.drive_map.keys())
            option_menu.pack(fill="x", **pad)
        else:
            tk.Label(
                self,
                text=f"드라이브: {self.selected_root}  ({self.selected_label or '이름없음'})",
                anchor="w",
            ).pack(fill="x", **pad)

        tk.Label(self, text="즐겨찾기(시작점)로 등록할 폴더/파일 (선택사항):", anchor="w").pack(fill="x", **pad)

        list_frame = tk.Frame(self)
        list_frame.pack(fill="both", expand=True, padx=10)
        self.listbox = tk.Listbox(list_frame)
        self.listbox.pack(side="left", fill="both", expand=True)
        scrollbar = tk.Scrollbar(list_frame, command=self.listbox.yview)
        scrollbar.pack(side="right", fill="y")
        self.listbox.config(yscrollcommand=scrollbar.set)

        btn_frame = tk.Frame(self)
        btn_frame.pack(fill="x", padx=10, pady=6)
        tk.Button(btn_frame, text="+ 폴더 추가", command=self._add_folder).pack(side="left", padx=(0, 6))
        tk.Button(btn_frame, text="+ 파일 추가", command=self._add_file).pack(side="left")
        tk.Button(btn_frame, text="선택 삭제", command=self._remove_selected).pack(side="right")

        bottom_frame = tk.Frame(self)
        bottom_frame.pack(fill="x", padx=10, pady=12)
        tk.Button(bottom_frame, text="취소", command=self.destroy).pack(side="right", padx=(6, 0))
        tk.Button(bottom_frame, text="등록", command=self._confirm, bg="#3a8ee6", fg="white").pack(side="right")

    def _get_unregistered_drives(self):
        registered_serials = set(int(s) for s in self.config_data["drives"].keys())
        result = []
        for root in drive_utils.list_drive_roots():
            serial, label = drive_utils.get_volume_info(root)
            if serial is None or serial in registered_serials:
                continue
            # C 드라이브(보통 내장 시스템 드라이브)는 기본 후보에서 제외해 실수 등록 방지
            if root.upper() == "C:\\":
                continue
            result.append({"root": root, "label": label, "serial": serial})
        return result

    def _ensure_drive_selected(self):
        if self.selected_serial is not None:
            return True
        chosen_key = self.drive_var.get()
        chosen = self.drive_map.get(chosen_key)
        if not chosen:
            messagebox.showwarning("선택 필요", "드라이브를 선택해주세요.", parent=self)
            return False
        self.selected_serial = chosen["serial"]
        self.selected_root = chosen["root"]
        self.selected_label = chosen["label"]
        return True

    def _add_folder(self):
        if not self._ensure_drive_selected():
            return
        path = filedialog.askdirectory(title="추가할 폴더 선택", initialdir=self.selected_root, parent=self)
        if path:
            self._add_item(path)

    def _add_file(self):
        if not self._ensure_drive_selected():
            return
        path = filedialog.askopenfilename(title="추가할 파일 선택", initialdir=self.selected_root, parent=self)
        if path:
            self._add_item(path)

    def _add_item(self, full_path):
        if not full_path.upper().startswith(self.selected_root.upper()):
            messagebox.showwarning("다른 드라이브", "선택한 외장하드 안의 폴더/파일만 추가할 수 있어요.", parent=self)
            return
        relative = os.path.relpath(full_path, self.selected_root)
        display_name = os.path.basename(full_path.rstrip("\\/")) or relative
        self.picked_items.append({"path_relative": relative, "display_name": display_name})
        self.listbox.insert("end", display_name)

    def _remove_selected(self):
        sel = list(self.listbox.curselection())
        for idx in reversed(sel):
            self.listbox.delete(idx)
            del self.picked_items[idx]

    def _confirm(self):
        if not self._ensure_drive_selected():
            return
        if not self.picked_items:
            if not messagebox.askyesno("확인", "아무 폴더/파일도 추가하지 않았어요. 빈 패널로 등록할까요?", parent=self):
                return
        self.on_registered(self.selected_serial, self.picked_items, self.selected_label)
        self.destroy()
