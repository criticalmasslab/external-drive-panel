# -*- coding: utf-8 -*-
"""
시스템 트레이 아이콘. pystray + Pillow 필요 (requirements.txt 참고).
tkinter는 메인 스레드에서만 안전하게 조작해야 하므로, 트레이 메뉴 클릭은
main.py 쪽에서 넘겨준 콜백을 통해 tkinter 메인 스레드에서 실행되게 한다
(콜백들은 이미 main.py에서 root.after(0, ...)로 감싸서 넘어온다).

메뉴 구조:
    설정
    외장하드 등록
    ------------------
    💾 D: MyDrive        ▶  패널 숨기기/다시 보이기, 등록 해제
    💾 E: Backup         ▶  ...
    ------------------
    종료

등록된 외장하드 목록은 수시로 바뀌므로(등록/해제), get_drive_entries()를
매번 새로 호출해서 메뉴를 다시 만든다.
"""

import threading

import pystray
from PIL import Image, ImageDraw


def _make_icon_image():
    # 간단한 외장하드 느낌의 아이콘을 코드로 직접 그림 (별도 이미지 파일 불필요)
    size = 64
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle((6, 14, 58, 50), radius=8, fill=(58, 142, 230, 255))
    draw.rectangle((14, 22, 50, 30), fill=(255, 255, 255, 255))
    draw.ellipse((42, 38, 50, 46), fill=(255, 255, 255, 255))
    return img


class TrayApp:
    def __init__(self, on_register, on_settings, on_quit, get_drive_entries,
                 on_toggle_hidden, on_unregister):
        """
        get_drive_entries() -> [{"serial":int, "display_name":str, "hidden":bool}, ...]
        on_toggle_hidden(serial)
        on_unregister(serial)
        """
        self.on_register = on_register
        self.on_settings = on_settings
        self.on_quit = on_quit
        self.get_drive_entries = get_drive_entries
        self.on_toggle_hidden = on_toggle_hidden
        self.on_unregister = on_unregister
        self._icon = None
        self._thread = None

    def _build_menu(self):
        items = [
            pystray.MenuItem("설정", lambda: self.on_settings()),
            pystray.MenuItem("외장하드 등록", lambda: self.on_register()),
            pystray.Menu.SEPARATOR,
        ]

        try:
            entries = self.get_drive_entries() or []
        except Exception:
            entries = []

        for entry in entries:
            serial = entry["serial"]
            hide_label = "패널 다시 보이기" if entry.get("hidden") else "패널 숨기기"
            submenu = pystray.Menu(
                pystray.MenuItem(hide_label, self._make_toggle_action(serial)),
                pystray.MenuItem("등록 해제", self._make_unregister_action(serial)),
            )
            items.append(pystray.MenuItem(entry.get("display_name", str(serial)), submenu))

        items.append(pystray.Menu.SEPARATOR)
        items.append(pystray.MenuItem("종료", lambda: self._quit()))
        return pystray.Menu(*items)

    def _make_toggle_action(self, serial):
        def action(icon, item):
            self.on_toggle_hidden(serial)
        return action

    def _make_unregister_action(self, serial):
        def action(icon, item):
            self.on_unregister(serial)
        return action

    def refresh_menu(self):
        """등록/해제 등으로 드라이브 목록이 바뀌었을 때 메뉴를 다시 만든다."""
        if not self._icon:
            return
        try:
            self._icon.menu = self._build_menu()
            self._icon.update_menu()
        except Exception:
            pass

    def _quit(self):
        self.on_quit()
        if self._icon:
            self._icon.stop()

    def start(self):
        self._icon = pystray.Icon(
            "ExternalDeskPanel",
            icon=_make_icon_image(),
            title="외장하드 바탕화면 패널",
            menu=self._build_menu(),
        )
        self._thread = threading.Thread(target=self._icon.run, daemon=True)
        self._thread.start()

    def notify(self, title, message):
        if self._icon:
            try:
                self._icon.notify(message, title)
            except Exception:
                pass  # 일부 환경에서는 balloon 알림이 지원 안 될 수 있음
