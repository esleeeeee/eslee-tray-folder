# Tray Folder

현재 릴리스 준비 버전: **v0.1.3** — [변경·검증·제한](.github/release-notes/v0.1.3.md).

## 프로젝트 목적

Tray Folder는 직접 개발한 Windows 프로그램의 트레이 진입점을 하나로 모으기 위한 전용 WPF 앱입니다.
Auto Power, Folder Locker, Download Router, OneKey, QuickSend 다섯 앱을 기본 카탈로그로 제공합니다.

## 현재 기능

- `NotifyIcon` 기반 대표 트레이 아이콘과 앱 폴더 팝업
- 등록 앱 실행 상태 표시, 실행 및 기존 창 복원 시도
- 등록 앱 실행 파일 자동 탐색과 사용자 경로·트레이 모드 일괄 설정
- 단일 실행과 두 번째 실행 요청 전달
- DPI, 다중 모니터, 작업 표시줄 위치를 고려한 팝업 배치
- LocalAppData 기반 설정 복구 및 로그 보존
- Named Pipe 등록, 앱별 명령 응답, 원격 메뉴 및 Hosted/Standalone 모드 전환
- 설정 저장 성공 후에만 실행 경로와 모드를 적용하고, 손상된 설정은 원본 백업 후 복구

## 빌드와 테스트

.NET SDK 10.0.301 이상이 필요합니다.

```powershell
dotnet build .\Eslee.TrayFolder.slnx --configuration Debug
dotnet build .\Eslee.TrayFolder.slnx --configuration Release
dotnet test .\Eslee.TrayFolder.slnx --configuration Release
```

## 트레이 연동과 제한

Auto Power와 Folder Locker의 기본 모드는 Hosted이며, 나머지 세 앱은 Standalone입니다.
Hosted는 해당 앱의 Tray Integration 지원과 연결이 필요합니다. 호스트 연결이 끊어지면
지원 앱은 자체 트레이로 복귀하고, 다시 연결하면 저장된 모드를 전달받습니다.
연결된 앱은 원격 메뉴와 명령을 제공할 수 있으며, 연결되지 않은 앱은 실행 경로를 통해
실행하거나 기존 창 복원을 시도합니다. 창 복원 여부는 대상 앱의 창 상태에 따라 다릅니다.

PR CI는 두 테스트 프로젝트를 실행하고 테스트 결과, 검증 커밋 SHA와 빌드 파일 SHA-256을
보관합니다. 테스트는 임시 설정과 로컬 테스트 파이프를 사용합니다. 실제 앱 실행·창 복원,
설치 패키지 및 코드 서명은 별도의 Windows 배포 검증 대상입니다.
