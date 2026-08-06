# Tray Folder

## 프로젝트 목적

Tray Folder는 직접 개발한 Windows 프로그램의 트레이 진입점을 하나로 모으기 위한 전용 WPF 앱입니다.
현재 프로토타입은 AutoPower를 표시하며, AutoPower 자체 트레이 아이콘이나 설치 파일은 변경하지 않습니다.

## 현재 기능

- `NotifyIcon` 기반 대표 트레이 아이콘과 앱 폴더 팝업
- AutoPower 실행 상태 표시, 실행 및 기존 창 복원 시도
- AutoPower 실행 파일 자동 탐색과 사용자 경로 설정
- 단일 실행과 두 번째 실행 요청 전달
- DPI, 다중 모니터, 작업 표시줄 위치를 고려한 팝업 배치
- LocalAppData 기반 설정 복구 및 로그 보존
- 향후 연동을 위한 최소 Tray Integration 계약

## 빌드와 테스트

.NET SDK 10.0.301 이상이 필요합니다.

```powershell
dotnet build .\Eslee.TrayFolder.slnx --configuration Debug
dotnet build .\Eslee.TrayFolder.slnx --configuration Release
dotnet test .\Eslee.TrayFolder.slnx --configuration Release
```

## 다음 단계

- AutoPower와 Named Pipe 통신 구현
- Standalone/Hosted 트레이 모드 전환 및 연결 해제 시 복구
- 다른 eslee 프로그램 등록 지원
- Inno Setup 기반 설치 패키지와 코드 서명
