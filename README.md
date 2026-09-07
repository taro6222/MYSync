# MYSync

Windows용 WebDAV·Google Drive 폴더 동기화 클라이언트 개발 프로젝트입니다.

현재 WPF 설정 화면, DLL 플러그인 로더, 변경 비교 엔진, 영속 작업 큐, 작업 실행기와 실제 로컬 파일 어댑터를 구현했습니다. WebDAV HTTPS/Basic 인증과 원격 폴더 탐색을 추가했습니다. 계정 영속 저장, WebDAV 전송, Google Drive 연결과 화면에서의 동기화 실행은 아직 구현 전입니다.

## 실행

.NET 10 SDK가 필요합니다. 이 작업 환경에는 `.tools/dotnet`에 설치했습니다.

```powershell
.\build.ps1 -Check
.\build.ps1 -Run
```

빌드 스크립트는 샘플 및 WebDAV 플러그인을 앱 출력의 `plugins`에 배치합니다. 앱에서 샘플 Provider, 로컬 폴더, 샘플 원격 폴더를 선택하면 동기화 쌍을 저장합니다. 설정은 `%LOCALAPPDATA%/MYSync/settings.db`에 저장되고 재시작 시 복원됩니다. 현재 UI는 설정만 저장하며 선택한 폴더의 파일을 변경하지 않습니다.

`-Check`는 `.tools/checks` 아래 격리된 폴더에서 기존 통합 검증과 실제 디스크 왕복 전송·교체·삭제 보관·충돌 보존 검증을 실행합니다.

## 구조

- `src/MYSync.Desktop`: WPF 화면과 바인딩 모델
- `src/MYSync.Provider.Abstractions`: 초기 플러그인 계약
- `src/MYSync.PluginHost`: 플러그인 검증·로드
- `src/MYSync.Provider.Sample`: 로드 검증용 DLL
- `src/MYSync.Provider.WebDav`: HTTPS/Basic 연결과 PROPFIND 원격 폴더 탐색
- `src/MYSync.Sync.Core`: 동기화 모델, 3자 비교, 전송 엔드포인트 계약
- `src/MYSync.Sync.Infrastructure`: SQLite 상태·큐, 재귀 검사, 실행기, 로컬 파일 어댑터
- `tests/MYSync.Checks`: 독립 실행형 통합 검증

플러그인 계약 v1은 기반 검증용으로 폴더 목록만 포함합니다. 선택적 IConfigurableProvider 계약으로 세션 연결을 지원합니다. 엔진의 전송 계약을 플러그인에 연결하고 계정 영속 저장을 추가하는 작업이 남아 있습니다. 현재 플러그인은 신뢰하는 코드만 설치해야 하며 보안 샌드박스가 아닙니다.

## 로컬 파일 보존

LocalEndpoint는 파일 교체·삭제 시 원본을 동기화 루트 밖의 같은 볼륨에 보관합니다. 완료를 확인하지 못한 복구 기록이 있으면 검사와 쓰기를 차단합니다. 복구 UI와 보존 기간 관리는 아직 구현 전입니다. 자세한 동작과 한계는 `docs/local-recovery.md`, 개발 이력은 `docs/development-progress.md`를 참고하세요.

## WebDAV 탐색

앱에서 WebDAV → 계정 연결 → HTTPS 폴더 주소·사용자 이름·비밀번호 입력 순서로 연결합니다. 연결 후 원격 목록에서 하위 폴더를 선택해 열거나 연결 루트로 돌아갈 수 있습니다. 목록 첫 항목은 현재 폴더입니다.

현재 연결 정보는 메모리에만 유지하며 재시작 후 다시 입력해야 합니다. 계정과 동기화 쌍을 안정적으로 연결하는 저장 구조가 구현되기 전까지 WebDAV 쌍 저장을 막아 두었습니다. 연결은 읽기 권한만 확인하며 쓰기 권한을 검증하지 않습니다. HTTP·자동 리디렉션·Digest/OAuth 인증은 현재 지원하지 않습니다.

## 테스트용 EXE 빌드

`./publish.ps1`로 Windows x64 런타임 포함 배포를 생성합니다. 실행 파일은 `build/win-x64/MYSync.Desktop.exe`입니다. 플러그인과 런타임 DLL이 필요하므로 폴더 전체를 유지하세요. 수동 테스트 범위는 `docs/manual-test.md`에 정리했습니다. 생성된 build 폴더는 Git 추적에서 제외합니다.
