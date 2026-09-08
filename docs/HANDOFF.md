# MYSync 개발 핸드오프

최종 갱신: 2026-09-08. 로컬 작업 코드 기준이며 최신 변경의 커밋·푸시는 Git 상태로 확인하세요.

## 2026-09-08 진행 업데이트

- 밀린 전체 자동 검증을 실행했다. HTTP 507의 잘못된 일시 장애 분류와 미지원 폴더 하위 항목 중복 보고를 발견해 수정했다.
- 수정 후 `build.ps1 -Check` 전체 통과, 경고·오류 0개. ManagementChecks·ResilienceChecks·UnsupportedChecks도 실제 실행했다.
- 실패 사유·분류·시각을 Jobs에 저장하고 미해결 목록에 표시한다. 작업 ID와 연결된 구조화 파일 로그, WebDAV 응답 상태·강한 헤더 ETag 유무를 추가했다. [진단 로그](diagnostics.md) 참고.
- 아래 실서버 테스트 결과는 당시 기록이다. 로그 부재는 해결했지만 ETag 획득·임시 파일 정리는 여전히 미해결이다. 다음 작업은 해당 호환성 수정과 실서버 재검증이다.

## 목표와 현재 단계

Windows에서 로컬·원격 폴더를 선택해 전체 내용을 양방향으로 동기화하는 앱입니다. WPF/.NET 10, SQLite, DLL Provider 구조를 사용합니다. WebDAV를 먼저 구현했고 Google Drive는 미구현입니다.

**현재 단계: 실제 WebDAV 서버(Synology) 1차 테스트에서 전송이 실패했고 원인을 확정했습니다. 다음 작업은 그 수정입니다.** 아래 ‘실서버 1차 테스트 결과’를 먼저 읽으세요. 기능 구현은 충돌·복구 확인 화면, 전송 진행률, 계정 관리, 오류 분류·재시도, 검사 캐시, 미지원 항목 분류까지 마쳤습니다.

실제 작업 경로는 `D:\# - Workspace\MYSync`입니다. 이름이 비슷한 `my-sync` 폴더와 혼동하지 마세요. 저장소는 https://github.com/taro6222/MYSync.git 이며 기본 작업 브랜치는 `main`입니다. `development-progress.md`는 누적 이력이므로 과거의 미구현 표시는 현재 상태와 다를 수 있습니다.

## 실행·검증·배포

저장소 루트에서 실행합니다. 개발 환경에는 `.tools/dotnet/dotnet.exe`로 SDK 10.0.400이 설치되어 있습니다. 다른 환경에는 .NET 10 SDK가 필요합니다.

```powershell
.\build.ps1 -Check
.\build.ps1 -Run
.\publish.ps1
```

`-Check`는 솔루션 빌드 후 콘솔 기반 통합 검증을 실행합니다. xUnit 프로젝트가 아닙니다. `publish.ps1`은 Windows x64 런타임 포함 압축 단일 EXE를 `build/win-x64/MYSync.Desktop.exe`에 생성합니다. `plugins`와 `TEST-GUIDE.md`를 함께 배포합니다. 빌드 결과와 SDK는 Git 추적 대상이 아닙니다.

배포는 임시 출력에 생성한 뒤 교체합니다. 이전 출력은 `.tools/publish-backups`로 이동합니다. 기존 배포 EXE가 실행 중이면 교체하지 않으므로 트레이 메뉴에서 종료한 후 다시 실행하세요. 최근 배포 EXE는 76,659,262 바이트이며 아래 기능이 모두 들어 있습니다.

## 실서버 1차 테스트 결과 (미해결)

대상: `D:\TEST` ⇄ Synology NAS `https://nas.koken.co.kr:5006/Evacycle/`. 로컬 파일 9개 업로드와 원격 `#recycle` 내려받기를 시도했습니다. **`#recycle` 폴더 생성 1건만 성공하고 나머지 10건이 모두 실패했습니다.**

실패 연쇄는 이렇습니다.

1. 첫 업로드에서 임시 파일 PUT은 성공했습니다.
2. 검증하려고 다시 내려받았는데 **GET 응답에 강한 ETag가 없었습니다.** `StrongTag`가 거부합니다 — `서버의 강한 ETag가 없어 조건부 변경을 수행할 수 없습니다`.
3. ETag를 모르면 조건부 DELETE를 보낼 수 없으므로, 정책대로 임시 파일을 지우지 않았습니다.
4. 남은 `.mysync-upload-fc0b50896d1a449ca5a7868e7e3810b7`를 이후 모든 원격 검사가 발견해 검사를 중단시켰습니다 — `완료되지 않은 업로드 파일을 확인하세요`.
5. 실패 분류는 `[상태 불일치]`(Precondition)이라 재시도하지 않았습니다. 이 부분은 의도대로 동작한 것입니다.

**막힌 상태를 풀려면** NAS의 `Evacycle` 폴더에서 위 `.mysync-upload-` 파일을 사람이 직접 지워야 합니다. 그 전에는 이 쌍의 검사가 계속 막힙니다.

이 사건이 드러낸 두 가지 결함입니다.

- **로그가 없습니다.** 실패 사유가 어디에도 남지 않습니다. 원인 파악을 화면 캡처에 의존해야 했습니다. 작업 큐에는 상태만 있고 사유·분류가 없습니다.
- **ETag를 GET 응답 헤더에서만 읽습니다.** PROPFIND의 `getetag`는 3단계에서 이미 조회하고 있으므로 그 값을 쓰도록 바꾸면 해결될 가능성이 있습니다. 다만 Synology가 `getetag`를 제공하는지는 아직 확인하지 못했습니다. 순서를 바꿔 `getetag`를 먼저 읽고 그 값으로 `If-Match`를 건 GET을 보내면, 받은 바이트와 ETag가 같은 버전임을 보장할 수 있습니다.
- 부수 결함으로, ETag를 못 얻어도 **우리가 방금 만든 고유 이름의 임시 파일은 내용 해시를 확인한 뒤 정리할 수 있어야** 합니다. 하나 남았다고 쌍 전체가 막히는 지금 동작은 과합니다.

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
- 검사 실패(`Errors`)와 미지원 항목(`Unsupported`)은 다릅니다. 전자는 전체를 멈추고, 후자는 해당 경로와 그 하위만 계획에서 빼고 보고합니다. 미지원 항목을 `Errors`로 올리지 마세요. 링크 하나가 폴더 전체를 멈추게 됩니다.
- 검사 속도를 위한 로컬 지문 캐시와 원격 강한 ETag 캐시는 **계획 단계 전용**입니다. 쓰기·삭제 직전에는 항상 실물을 다시 해시해 예상 상태와 비교합니다. 이 재확인을 캐시로 대체하지 마세요.
- 작업 상태는 Pending → Running → Applied 또는 NeedsReconcile이며, 최종 수렴 때 Completed로 정리합니다. 재시작 시 Running을 NeedsReconcile로 복구합니다. 성공 응답 유실 시 재검사로 결과를 확인하고 무조건 재전송하지 않습니다.
- 로컬 파일 교체·삭제 전 원본을 동기화 루트 밖 같은 볼륨에 보관합니다. 미확인 복구 기록이 있으면 검사와 쓰기를 차단합니다. 복구 기록·백업을 임의 삭제해 차단을 해제하지 마세요.
- 파일 충돌은 결정적인 이름의 사본을 양쪽에 보존합니다. 폴더/타입 충돌은 자동 처리하지 않습니다. 파일 충돌 결정과 복구 기록 확인은 ‘충돌·복구’ 화면에서 처리하며, 사용자의 결정은 버리는 쪽을 기준 상태에 기록해 기존 3자 비교로 재계획합니다. 결정 전후 계획을 비교해 다른 경로의 작업이 바뀌면 중단합니다.
- 복구 기록은 저장된 Verified 값이 아니라 조회 시점의 해시로 다시 판정합니다. 확인 처리는 기록과 보관본을 `resolved` 폴더로 옮기고 확인 이력을 남긴 뒤에만 차단을 해제하며, 판정할 수 없는 기록은 사용자의 직접 확인이 필요합니다.
- WebDAV 업로드는 임시 파일 검증 후 MOVE합니다. 교체·삭제에는 강한 ETag와 조건부 요청이 필요합니다. 서버가 이 조건을 올바르게 집행해야 합니다.
- 원격 폴더 삭제는 하위 항목의 동시 변경을 원자적으로 보호할 수 없어 거부합니다. 원격 파일 삭제의 휴지통·버전 보존은 보장하지 않습니다.
- 확인되지 않은 원격 임시 파일은 임의 삭제하지 않습니다. 예약된 `.mysync-upload-` 항목 발견 시 검사를 차단합니다.

로컬 보존 처리가 전원 장애의 모든 경우나 악의적인 외부 프로세스의 경로 경쟁까지 보장하지는 않습니다. 세부 정책은 [로컬 복구](local-recovery.md), [WebDAV 전송](webdav-transfer.md), [충돌·복구 확인](conflict-recovery.md), [전송 상태와 계정 관리](transfers-and-accounts.md), [오류 분류·재시도와 검사 비용](errors-and-performance.md), [미지원 항목 처리](unsupported-items.md)를 확인하세요.

## Provider 확장 시 주의점

계약 버전은 1입니다. `IProvider`는 기본 탐색, 선택적 `IConfigurableProvider`는 연결 필드와 연결, `ITransferProvider`는 `ISyncEndpoint` 생성을 제공합니다. 호스트는 Provider.Abstractions와 Sync.Core를 공유해 타입 정체성을 유지합니다. 자동 실행용 `CreateSession`은 로드한 Provider 타입의 새 인스턴스를 생성하므로 인스턴스 간 계정 상태를 공유하지 않아야 합니다.

플러그인은 프로세스 내 신뢰 코드이며 샌드박스가 아닙니다. 폴더 배치 후 재시작해야 합니다. 현재 배포 스크립트는 플러그인의 deps/runtimeconfig JSON과 공유 DLL을 제거합니다. 외부 의존성이 많은 Provider를 추가할 때는 의존성 해석과 배포 매니페스트 보존 정책부터 검토하세요. Sample은 탐색 전용입니다.

## 검증 증거와 남은 검증

빌드는 모든 변경에서 경고·오류 0개였고 Release 단일 EXE 게시도 성공했습니다.

2026-09-08에 누락됐던 자동 검증까지 실행하고 발견된 두 결함을 수정한 뒤 전체 통과를 확인했습니다. 신규 DiagnosticChecks도 통과했습니다. 실서버 호환성과 실제 화면 조작 검증을 대신하지 않습니다.

자동 검증은 플러그인 로드/계약, 3자 비교·삭제 순서, SQLite 작업 복구, 실제 로컬 파일 왕복·원본 보존, DPAPI 암호화·구형 스키마 마이그레이션, WebDAV 조건부 전송·응답 유실·동시 변경, 이벤트 병합·주기 검사·취소, 설정 저장, 복구 기록 판정·복원·확인 처리와 충돌 결정 재계획, 계정 이름 변경·인증 정보 갱신·삭제, 실행기 진행률 보고, 상태별 오류 분류와 상한 있는 지수 지연 재시도, 감시의 일시 장애 재시도 예산, 로컬·원격 검사 캐시의 재사용과 무효화, 미지원 항목의 계획 제외·하위 보호·보고를 포함합니다. WebDAV는 모의 HttpMessageHandler를 사용합니다.

**실제 WebDAV 서버, WPF 화면 상호작용, 트레이 메뉴, Windows 로그인 자동 실행, 장시간 운용은 아직 검증하지 않았습니다.** 자동 검사 통과를 실서비스 호환성 확인으로 해석하지 마세요. 실제 테스트 절차는 [manual-test.md](manual-test.md)를 사용하세요. 개발 과정에서 사용자 로그인 시작 등록을 직접 변경하지 않았습니다.

## 권장 다음 작업

0. 완료: 밀린 자동 검증 실행 및 발견된 결함 수정. 이후 변경에도 `build.ps1 -Check`를 실행합니다.
1. 완료: 파일 로그·작업별 실패 사유·분류·시각 저장과 목록 표시. WebDAV 응답 상태와 헤더 ETag 유무를 기록합니다.
2. **ETag 획득 경로를 고칩니다.** PROPFIND `Depth:0`으로 `getetag`를 읽고 그 값으로 `If-Match`를 건 GET을 보내 바이트와 ETag의 일치를 보장합니다. GET 헤더 ETag는 예비로 둡니다. 서버가 어디에서도 강한 ETag를 주지 않으면, 조건부 변경을 거부하는 현재 정책을 유지하되 그 사실을 사용자에게 분명히 알리는 방식으로 정리합니다.
3. **임시 업로드 파일 정리를 개선합니다.** 우리가 만든 고유 이름의 임시 파일은 내용 해시를 확인한 뒤 정리할 수 있게 해, 하나가 남아 쌍 전체가 막히지 않도록 합니다.
4. 위 수정 후 실서버 수동 가이드를 다시 수행합니다: 최초 업·다운로드, 동시 수정, 파일 삭제, 네트워크 중단 후 재개. 창 닫기·트레이 종료·두 번째 실행·로그인 시작도 확인합니다.
5. 검사 결과의 영속 캐시, 전송 이어받기, 병렬 전송으로 대규모 폴더 성능을 더 개선합니다.
6. Google Drive OAuth와 안정적인 파일 ID 기반 탐색·전송 Provider를 추가합니다. 인증과 페이지 처리·중복 이름·변경 추적·휴지통 정책을 별도로 설계하고 공통 계약 검증을 재사용합니다. 구글 문서·바로가기·공유 드라이브·중복 이름은 [미지원 항목 처리](unsupported-items.md) 방침에 따라 미지원으로 보고합니다.

다음 기능 변경 전에는 이 문서와 관련 설계 문서를 읽고, 변경 후 `build.ps1 -Check`를 실행하세요. 실제 배포 테스트를 요청할 때는 `publish.ps1`로 EXE를 갱신하고 자동 검증과 수동 검증 결과를 구분해 기록하세요.
