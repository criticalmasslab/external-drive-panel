@echo off
chcp 65001 >nul
echo ============================================
echo  External Desk Panel - exe 빌드 스크립트
echo ============================================
echo.

echo [1/4] pyinstaller 등 필요 패키지 설치 확인 중...
pip install --quiet -r requirements.txt
pip install --quiet pyinstaller
if errorlevel 1 (
    echo 패키지 설치에 실패했습니다. 인터넷 연결을 확인해주세요.
    pause
    exit /b 1
)

echo [2/4] 아이콘(icon.ico) 생성 중...
python make_icon.py

echo [3/4] exe 빌드 중... (시간이 좀 걸릴 수 있어요)
REM tkinterdnd2, pystray는 순수 파이썬 코드 외에 별도 리소스 파일도 있어서
REM --collect-all 로 통째로 챙겨줘야 exe에서 정상 동작합니다.
pyinstaller --noconfirm --onefile --windowed --name ExternalDeskPanel --icon=icon.ico ^
    --collect-all tkinterdnd2 ^
    --collect-all pystray ^
    --collect-all PIL ^
    main.py

echo [4/4] 완료!
echo.
echo dist\ExternalDeskPanel.exe 파일이 생성되었습니다.
echo 이 파일 하나만 있으면 파이썬 없이도 실행됩니다.
echo.
pause
