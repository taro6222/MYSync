# MYSync 개발 핸드오프

작성일: 2026-09-07. 이 문서와 함께 커밋된 코드 기준입니다.

## 목표와 현재 단계

Windows에서 로컬·원격 폴더를 선택해 전체 내용을 양방향으로 동기화하는 앱입니다. WPF/.NET 10, SQLite, DLL Provider 구조를 사용합니다. WebDAV를 먼저 구현했고 Google Drive는 미구현입니다. 현재 WebDAV 수동·자동 전송과 Windows 트레이 수명주기까지 구현한 수동 테스트 단계입니다.

실제 작업 경로는 `D:\# - Workspace\MYSync`입니다. 이름이 비슷한 `my-sync` 폴더와 혼동하지 마세요. 저장소는 https://github.com/taro6222/MYSync.git 이며 기본 작업 브랜치는 `main`입니다. `development-progress.md`는 누적 이력이므로 과거의 미구현 표시는 현재 상태와 다를 수 있습니다.

## 실행·검증·배포

저장소 루트에서 실행합니다. 개발 환경에는 `.tools/dotnet/dotnet.exe`로 SDK 10.0.400이 설치되어 있습니다. 다른 환경에는 .NET 10 SDK가 필요합니다.

```powershell
.\build.ps1 -Check
.\build.ps1 -Run
.\publish.ps1
```

`-Check`는 솔루션 빌드 후 콘솔 기반 통합 검증을 실행합니다. xUnit 프로젝트가 아닙니다. `publish.ps1`은 Windows x64 런타임 포함 압축 단일 EXE를 `build/win-x64/MYSync.Desktop.exe`에 생성합니다. `plugins`와 `TEST-GUIDE.md`를 함께 배포합니다. 빌드 결과와 SDK는 Git 추적 대상이 아닙니다.

배포는 임시 출력에 생성한 뒤 교체합니다. 이전 출력은 `.tools/publish-backups`로 이동합니다. 기존 배포 EXE가 실행 중이면 교체하지 않으므로 트레이 메뉴에서 종료한 후 다시 실행하세요. 최근 배포 EXE는 약 76.6 MB입니다.

## 코드 진입점

| 파일/영역 | 책임 |
| --- | --- |
| `src/MYSync.Desktop/MainWindow.xaml(.cs)` | 계정 연결, 폴더·동기화 쌍 관리, 자동 실행·트레이 제어 |
| `src/MYSync.Desktop/SyncRunWindow.cs` | 수동 검사, 계획 미리보기, 실행·취소, 미완료 작업 재검사 |
| `src/MYSync.Desktop/ResolveWindow.cs` | 미해결 작업·충돌 결정, 복구 기록 조회·복원·확인 처리 |
| `src/MYSync.Desktop/TransferRow.cs` | 전송 탭 한 줄의 표시 상태 |
| `src/MYSync.Desktop/TextPromptWindow.cs` | 한 줄 입력 대화상자 |
| `src/MYSync.Desktop/App.xaml.cs` | 단일 인스턴스 mutex와 기존 창 표시 이벤트 |
| `src/MYSync.Desktop/StartupRegistration.cs` | HKCU Run 등록·조회, `--background` 실행 |
| `src/MYSync.Provider.Abstractions/Contracts.cs` | 탐색·연결·전송 Provider 계약 |
| `src/MYSync.PluginHost/PluginCatalog.cs` | DLL 로드·검증, 독립 Provider 세션 생성 |
| `src/MYSync.Provider.WebDav/WebDavProvider.cs` | HTTPS/Basic 연결, 주소·포트, PROPFIND 탐색 |
| `src/MYSync.Provider.WebDav/WebDavEndpoint.cs` | 재귀 검사, 파일 해시, 조건부 업로드·삭제 |
| `src/MYSync.Sync.Core/SyncPlanner.cs` | 로컬·원격·기준 상태 3자 비교, 작업 순서·충돌 판정 |
| `src/MYSync.Sync.Infrastructure/SyncJournal.cs` | SQLite 기준 상태·영속 작업 큐 |
| `src/MYSync.Sync.Infrastructure/SyncExecutor.cs` | 재검사 후 실행, 불확실한 결과 재조정, 충돌 사본 |
| `src/MYSync.Sync.Infrastructure/LocalEndpoint.cs` | 루트 범위 검증, 원본 보관·복구 기록 |
| `src/MYSync.Sync.Infrastructure/SyncMonitor.cs` | FileSystemWatcher, 이벤트 병합, 주기 검사·취소 |
| `src/MYSync.Sync.Infrastructure/RecoveryStore.cs` | 복구 기록 재판정, 보관본 외부 복원, 확인 처리 |
| `src/MYSync.Sync.Infrastructure/AccountStore.cs` | 계정 DPAPI 저장·복호화 |

화면 제어는 주로 코드 비하인드에 있습니다. MVVM 분리는 완료되지 않았습니다.

## 실행 흐름과 저장 위치

1. WebDAV 연결 성공 후 연결 값 전체를 DPAPI CurrentUser로 암호화합니다. 저장 계정 선택 후 명시적으로 재연결할 수 있습니다.
2. 동기화 쌍에 계정 ID와 로컬·원격 루트를 저장합니다. 새 쌍은 일시정지 상태이며 겹치는 로컬 루트를 거부합니다.
3. 로컬·원격 전체 검사 결과를 기준 상태와 비교해 계획을 만들고 영속 큐에 저장합니다.
4. 각 작업 직전에 다시 검사합니다. 모든 작업 후 양쪽이 수렴한 경우에만 기준 상태와 작업 완료를 함께 커밋합니다.
5. 자동 실행은 쌍별 독립 Provider 세션을 사용합니다. 로컬 이벤트를 기본 2초간 병합하며 5분마다 전체 재검사합니다. 실패·미해결 상태는 자동 실행을 멈춥니다.
6. 창 닫기는 기본적으로 트레이에 숨깁니다. 명시적 종료는 실행 중 작업을 취소하고 기다립니다. 자동 실행 중이던 쌍은 다음 실행에서 재개합니다.

| 위치 | 내용 |
| --- | --- |
| `%LOCALAPPDATA%/MYSync/settings.db` | SyncPairs, Accounts. 기존 DB에 nullable AccountId를 추가하는 마이그레이션 포함 |
| `%LOCALAPPDATA%/MYSync/journals/{pairId}.db` | Baselines, Jobs |
| `%LOCALAPPDATA%/MYSync/preferences.json` | CloseToTray 설정, 임시 파일 후 교체 저장 |
| 로컬 루트 형제 `.MYSync-recovery/{pairId}` | 원본 백업, 복구 기록, 임시 전송 파일 |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`의 `MYSync` | 사용자가 설정을 켤 때 등록하는 EXE 경로와 `--background` |

DPAPI 계정은 같은 Windows 사용자 환경에 종속됩니다. DB를 복사하는 것만으로 다른 사용자에게 계정을 이전할 수 없습니다. 자격 증명이나 실제 사용자 DB를 저장소에 넣지 마세요.

## 유지해야 할 데이터 보존 규칙

- 불완전한 검사 결과로 삭제·전송 계획을 실행하지 않습니다. 파일 해시·대소문자 충돌·경로 범위를 검사합니다.
- 작업 상태는 Pending → Running → Applied 또는 NeedsReconcile이며, 최종 수렴 때 Completed로 정리합니다. 재시작 시 Running을 NeedsReconcile로 복구합니다. 성공 응답 유실 시 재검사로 결과를 확인하고 무조건 재전송하지 않습니다.
- 로컬 파일 교체·삭제 전 원본을 동기화 루트 밖 같은 볼륨에 보관합니다. 미확인 복구 기록이 있으면 검사와 쓰기를 차단합니다. 복구 기록·백업을 임의 삭제해 차단을 해제하지 마세요.
- 파일 충돌은 결정적인 이름의 사본을 양쪽에 보존합니다. 폴더/타입 충돌은 자동 처리하지 않습니다. 파일 충돌 결정과 복구 기록 확인은 ‘충돌·복구’ 화면에서 처리하며, 사용자의 결정은 버리는 쪽을 기준 상태에 기록해 기존 3자 비교로 재계획합니다. 결정 전후 계획을 비교해 다른 경로의 작업이 바뀌면 중단합니다.
- 복구 기록은 저장된 Verified 값이 아니라 조회 시점의 해시로 다시 판정합니다. 확인 처리는 기록과 보관본을 `resolved` 폴더로 옮기고 확인 이력을 남긴 뒤에만 차단을 해제하며, 판정할 수 없는 기록은 사용자의 직접 확인이 필요합니다.
- WebDAV 업로드는 임시 파일 검증 후 MOVE합니다. 교체·삭제에는 강한 ETag와 조건부 요청이 필요합니다. 서버가 이 조건을 올바르게 집행해야 합니다.
- 원격 폴더 삭제는 하위 항목의 동시 변경을 원자적으로 보호할 수 없어 거부합니다. 원격 파일 삭제의 휴지통·버전 보존은 보장하지 않습니다.
- 확인되지 않은 원격 임시 파일은 임의 삭제하지 않습니다. 예약된 `.mysync-upload-` 항목 발견 시 검사를 차단합니다.

로컬 보존 처리가 전원 장애의 모든 경우나 악의적인 외부 프로세스의 경로 경쟁까지 보장하지는 않습니다. 세부 정책은 [로컬 복구](local-recovery.md), [WebDAV 전송](webdav-transfer.md), [충돌·복구 확인](conflict-recovery.md), [전송 상태와 계정 관리](transfers-and-accounts.md)를 확인하세요.

## Provider 확장 시 주의점

계약 버전은 1입니다. `IProvider`는 기본 탐색, 선택적 `IConfigurableProvider`는 연결 필드와 연결, `ITransferProvider`는 `ISyncEndpoint` 생성을 제공합니다. 호스트는 Provider.Abstractions와 Sync.Core를 공유해 타입 정체성을 유지합니다. 자동 실행용 `CreateSession`은 로드한 Provider 타입의 새 인스턴스를 생성하므로 인스턴스 간 계정 상태를 공유하지 않아야 합니다.

플러그인은 프로세스 내 신뢰 코드이며 샌드박스가 아닙니다. 폴더 배치 후 재시작해야 합니다. 현재 배포 스크립트는 플러그인의 deps/runtimeconfig JSON과 공유 DLL을 제거합니다. 외부 의존성이 많은 Provider를 추가할 때는 의존성 해석과 배포 매니페스트 보존 정책부터 검토하세요. Sample은 탐색 전용입니다.

## 검증 증거와 남은 검증

문서 작성 전 최신 코드에서 Debug 빌드와 전체 통합 검증, Release 단일 EXE 게시가 성공했습니다. 빌드는 경고·오류 0개였습니다.

자동 검증은 플러그인 로드/계약, 3자 비교·삭제 순서, SQLite 작업 복구, 실제 로컬 파일 왕복·원본 보존, DPAPI 암호화·구형 스키마 마이그레이션, WebDAV 조건부 전송·응답 유실·동시 변경, 이벤트 병합·주기 검사·취소, 설정 저장, 복구 기록 판정·복원·확인 처리와 충돌 결정 재계획, 계정 이름 변경·인증 정보 갱신·삭제, 실행기 진행률 보고를 포함합니다. WebDAV는 모의 HttpMessageHandler를 사용합니다.

**실제 WebDAV 서버, WPF 화면 상호작용, 트레이 메뉴, Windows 로그인 자동 실행, 장시간 운용은 아직 검증하지 않았습니다.** 자동 검사 통과를 실서비스 호환성 확인으로 해석하지 마세요. 실제 테스트 절차는 [manual-test.md](manual-test.md)를 사용하세요. 개발 과정에서 사용자 로그인 시작 등록을 직접 변경하지 않았습니다.

## 권장 다음 작업

1. 테스트 전용 WebDAV 폴더로 수동 가이드 수행: 최초 업·다운로드, 동시 수정, 파일 삭제, 네트워크 중단 후 재개, 서버별 ETag/MOVE 조건 준수 확인. 창 닫기·트레이 종료·두 번째 실행·로그인 시작도 확인합니다.
2. 안전한 오류 분류·재시도 정책과 대규모 폴더 성능을 개선합니다. 현재는 원격 파일 전체 다운로드 해시와 작업마다 재검사하므로 비용이 큽니다.
3. Google Drive OAuth와 안정적인 파일 ID 기반 탐색·전송 Provider를 추가합니다. 인증과 페이지 처리·중복 이름·변경 추적·휴지통 정책을 별도로 설계하고 공통 계약 검증을 재사용합니다.

다음 기능 변경 전에는 이 문서와 관련 설계 문서를 읽고, 변경 후 `build.ps1 -Check`를 실행하세요. 실제 배포 테스트를 요청할 때는 `publish.ps1`로 EXE를 갱신하고 자동 검증과 수동 검증 결과를 구분해 기록하세요.
