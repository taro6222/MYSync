# MYSync

Windows용 WebDAV·Google Drive 폴더 동기화 클라이언트. 현재 1단계 기반 구현이며 실제 전송과 클라우드 인증은 아직 제공하지 않습니다.

## 실행

.NET 10 SDK가 필요합니다. 이 작업 환경에는 `.tools/dotnet`에 설치했습니다.

```powershell
.\build.ps1 -Check
.\build.ps1 -Run
```

빌드 스크립트는 샘플 플러그인을 앱 출력의 `plugins/Sample`에 배치합니다. 앱에서 샘플 Provider, 로컬 폴더, 샘플 원격 폴더를 선택하면 동기화 쌍을 저장합니다. 설정은 `%LOCALAPPDATA%/MYSync/settings.db`에 저장되고 재시작 시 복원됩니다. 이 버전은 실제 파일을 변경하지 않습니다.

## 구조

- `src/MYSync.Desktop`: WPF 화면과 바인딩 모델
- `src/MYSync.Provider.Abstractions`: 초기 플러그인 계약
- `src/MYSync.PluginHost`: 플러그인 검증·로드
- `src/MYSync.Provider.Sample`: 로드 검증용 DLL
- `src/MYSync.Sync.Core`: 동기화 쌍 모델
- `src/MYSync.Sync.Infrastructure`: SQLite 설정 저장
- `tests/MYSync.Checks`: 독립 실행형 통합 검증

계약 v1은 기반 검증용으로 폴더 목록만 포함합니다. 전송·인증 계약과 전체 MVVM 명령 분리는 후속 단계에서 확장합니다. 현재 플러그인은 신뢰하는 코드만 설치해야 하며, 보안 샌드박스가 아닙니다.

## 2단계 현재 상태

재귀 로컬 검사, 3자 비교 계획, SQLite 기준 상태·작업 큐를 추가했습니다. `build.ps1 -Check`에서 변경·삭제·충돌과 중단 복구를 검증합니다. 아직 파일 작업 실행기와 UI 연결은 구현 전이며 실제 동기화는 실행하지 않습니다. 자세한 범위는 `docs/development-progress.md`를 참고하세요.
