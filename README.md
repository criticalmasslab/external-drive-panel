# External Drive Panel

이 프로젝트는 현재 C# / WPF(.NET 8)로 새로 개발 중입니다.

이전 Python 버전은 [`python-legacy/`](python-legacy/) 폴더에 보관되어 있습니다.

## 항목 우클릭 메뉴 테스트 (Windows / .NET 8 SDK)

```powershell
dotnet run --project tests/ItemMenuLogicSmoke -c Release
dotnet run --project tests/ItemMenuSmoke -c Release
```

첫 테스트는 메뉴 조합·확장자·즐겨찾기 경로 비교·ZIP 생성/충돌/실패 정리를,
둘째는 실제 WPF 창에서 네 가지 화면의 공통 메뉴·선택·단축키·ZIP 알림/갱신을 검사합니다.
`dotnet run --project tests/ItemMenuSmoke -c Release -- --interactive`는 사용자 설정을
읽거나 저장하지 않는 임시 패널을 띄웁니다. 여기서 실제 속성 창, 연결 프로그램,
우클릭 메뉴와 단축키를 수동 확인할 수 있습니다. 창을 닫으면 테스트 파일도 정리됩니다.

앱 선택은 `ShellExecuteEx(openas)`를 먼저 사용하며, 해당 동사가 지원되지 않는
환경의 `ERROR_NO_ASSOCIATION`에 한해 `SHOpenWithDialog`로 대체합니다.
ZIP은 일반 파일/폴더를 지원하며, 링크/연결 지점은 재귀 순환 방지를 위해 안내 후 중단합니다.
