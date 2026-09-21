# -*- coding: utf-8 -*-
"""
현재 사용자의 '시작프로그램' 폴더에 바로가기를 만들어
윈도우 로그인 시 이 프로그램이 자동으로 실행되게 한다.
관리자 권한이 필요 없다 (사용자 전용 시작프로그램 폴더 사용).

파이썬 스크립트(main.py)로 실행 중이든, PyInstaller로 만든 .exe로
실행 중이든 자동으로 알맞은 방식을 골라 등록한다.
"""

import os
import sys

SHORTCUT_NAME = "ExternalDeskPanel.lnk"
VBS_NAME = "ExternalDeskPanel.vbs"


def is_frozen():
    """PyInstaller 등으로 패키징된 .exe로 실행 중이면 True."""
    return getattr(sys, "frozen", False)


def get_launch_target():
    """지금 이 방식대로 다시 실행하려면 무엇을(target), 어떤 인자로(args)
    실행해야 하는지 반환. (target: str, args: list[str], workdir: str)
    """
    if is_frozen():
        # PyInstaller onefile/onedir 로 만든 exe: 그 자체가 실행 대상
        target = sys.executable
        args = []
        workdir = os.path.dirname(sys.executable)
    else:
        # 파이썬 스크립트로 실행 중: pythonw + main.py 경로
        py_dir = os.path.dirname(sys.executable)
        pythonw_candidate = os.path.join(py_dir, "pythonw.exe")
        target = pythonw_candidate if os.path.exists(pythonw_candidate) else sys.executable

        # main.py 위치: 이 파일(startup_utils.py)과 같은 폴더에 있다고 가정
        this_dir = os.path.dirname(os.path.abspath(__file__))
        main_py = os.path.join(this_dir, "main.py")
        args = [main_py]
        workdir = this_dir

    return target, args, workdir


def get_startup_folder():
    appdata = os.environ.get("APPDATA")
    return os.path.join(appdata, "Microsoft", "Windows", "Start Menu", "Programs", "Startup")


def get_shortcut_path():
    return os.path.join(get_startup_folder(), SHORTCUT_NAME)


def get_vbs_path():
    return os.path.join(get_startup_folder(), VBS_NAME)


def is_registered():
    return os.path.exists(get_shortcut_path()) or os.path.exists(get_vbs_path())


def register_startup():
    """시작프로그램에 등록. pywin32가 있으면 진짜 .lnk를, 없으면 .vbs 방식으로 대체."""
    target, args, workdir = get_launch_target()
    os.makedirs(get_startup_folder(), exist_ok=True)

    try:
        import win32com.client  # pywin32가 설치되어 있으면 진짜 바로가기(.lnk) 생성

        shell = win32com.client.Dispatch("WScript.Shell")
        shortcut = shell.CreateShortCut(get_shortcut_path())
        shortcut.Targetpath = target
        shortcut.Arguments = " ".join(f'"{a}"' for a in args)
        shortcut.WorkingDirectory = workdir
        shortcut.WindowStyle = 7  # 최소화 상태로 시작 (콘솔 창 안 튀게)
        shortcut.save()
        return True
    except ImportError:
        # pywin32가 없으면 .vbs 스크립트로 대체 (창 안 뜨고 조용히 실행됨)
        args_str = " ".join(f'""{a}""' for a in args)
        vbs_content = (
            'Set WshShell = CreateObject("WScript.Shell")\n'
            f'WshShell.Run """{target}"" {args_str}", 0, False\n'
        )
        with open(get_vbs_path(), "w", encoding="utf-8") as f:
            f.write(vbs_content)
        return True


def unregister_startup():
    for path in (get_shortcut_path(), get_vbs_path()):
        if os.path.exists(path):
            os.remove(path)
