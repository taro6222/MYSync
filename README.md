# MYSync

## 전송·상태 표시 업데이트 (2026-09-08)

파일별 최신순 전송 기록, 처리량·평균 처리 속도·소요 시간·시작/완료 시각, 하단 요약 인디케이터를 추가했다. 자동 감시는 오류 시 설정을 끄는 대신 일시 오류 재시도 또는 확인 대기로 유지한다. EXE·창·트레이에 MYSync 아이콘을 적용했다. 전체 자동 검증 통과. 자세한 정책과 테스트 절차는 [전송 기록 안내](docs/transfer-history.md)를 참고한다. 아래 과거 설명과 다르면 이 업데이트가 우선한다.


## 동기화 연결 관리 업데이트 (2026-09-08)

- 동기화 목록의 **연결 삭제**로 감시를 중지하고 연결만 삭제합니다. 실제 파일·계정·기존 복구 기록은 유지합니다.
- 동일하거나 상하위 관계인 로컬 폴더를 여러 연결에 등록할 수 있습니다. 겹치는 연결은 하나씩 실행하며 다른 자동 감시를 먼저 일시정지합니다.
- 추가 화면을 계정 연결, 좌우 폴더 선택, 고정된 연결 미리보기·저장 영역으로 개편했습니다.
- `.MYSync-recovery`는 양쪽 동기화에서 제외되는 예약 이름입니다. 기존 복구 보관함 위치는 유지됩니다.
- 삭제 후 다시 추가하면 새로운 연결 ID와 최초 비교 기준을 사용합니다. 기존 연결의 복구 화면은 더 이상 열 수 없으므로 필요한 복구 작업은 삭제 전에 완료하세요.
- 전체 자동 검증 통과(중복·상하위 폴더 저장, 연결별 삭제, 파일 유지, 복구 경로 제외 포함). 실제 Google/NAS 전송 테스트는 별도입니다.


Windows에서 선택한 로컬 폴더와 원격 폴더를 양방향으로 동기화하는 WPF 앱입니다. MEGAsync와 유사한 동기화 목록·설정·트레이 사용 흐름을 목표로 개발 중입니다.

현재 **WebDAV 연결과 실제 파일 전송, 자동 동기화, 트레이 실행**을 구현했습니다. Google Drive는 재귀 검사·5 MiB 이하 파일 전송을 연결했고, 실제 전송 검증은 남아 있습니다.

## 구현 상태

| 기능 | 현재 상태 |
| --- | --- |
| WebDAV | HTTPS/Basic 인증, 주소·포트 별도 입력, 원격 폴더 탐색 |
| 계정 저장 | Windows DPAPI로 암호화, 저장 계정 재연결 |
| 동기화 | 재귀 검사, 로컬·원격·기준 상태 비교, 업로드·다운로드·파일 삭제 |
| 수동 실행 | 변경 미리보기, 실행·취소, 미완료 작업 재검사 |
| 자동 실행 | 로컬 변경 감지, 기본 5분 재검사, 쌍별 시작·일시정지, 재시작 복원 |
| 충돌·복구 | 파일 충돌 사본 보존, 로컬 교체·삭제 원본 보관, 불확실한 상태 차단 |
| Windows 통합 | 닫기 시 트레이 유지, 트레이 종료, 로그인 자동 실행, 단일 인스턴스 |
| Provider 확장 | DLL 플러그인 로드, 선택적 연결·전송 계약 |
| Google Drive | OAuth·재귀 검사·5 MiB 이하 전송·파일 휴지통 (실전송 검증 전) |
| 충돌·복구 확인 | 복구 기록 판정·보관본 복원·확인 처리, 파일 충돌 결정 |
| 전송 상태 | 쌍별 작업 수·전송량·현재 항목 표시 (참고용) |
| 계정 관리 | 이름 변경, 인증 정보 수정, 참조 없을 때 삭제 |
| 오류 처리 | 상태별 분류, 일시 장애만 지수 지연 재시도 |
| 진단 | 작업별 마지막 실패 사유·분류·시각 저장, HTTP 응답 메타데이터 파일 로그 |
| 검사 비용 | 로컬 지문·원격 강한 ETag 캐시 (계획 단계 한정) |
| 미지원 항목 | 링크·사용 불가 이름 등은 해당 경로만 제외하고 보고 |
| 원격 폴더 삭제 | 미구현 |

자동 동기화는 파일 삭제도 반영합니다. 새 동기화 쌍은 일시정지 상태로 저장됩니다. 오류나 미해결 충돌이 발생하면 자동 실행을 중단하고 확인을 요청합니다.

## 개발 환경과 실행

Windows와 .NET 10 SDK가 필요합니다. 현재 개발 환경은 `.tools/dotnet`의 SDK를 사용합니다.

```powershell
.\build.ps1 -Check
.\build.ps1 -Run
```

빌드 스크립트는 Sample·WebDAV 플러그인을 앱 출력에 배치합니다. `-Check`는 독립 실행형 통합 검증을 실행하며, 테스트 파일은 `.tools/checks`에 격리합니다.

앱의 **동기화 추가**에서 WebDAV 계정을 연결하고 로컬·원격 폴더를 선택합니다. 주소와 포트를 따로 입력하며 기본 포트는 443입니다. 저장 후 동기화 목록에서 **검사·실행** 또는 자동 시작을 선택합니다. Sample Provider는 탐색·플러그인 검증용이며 실제 전송은 지원하지 않습니다.

## 테스트용 EXE

```powershell
.\publish.ps1
```

배포 위치는 `build/win-x64`입니다.

```text
win-x64/
  MYSync.Desktop.exe
  TEST-GUIDE.md
  plugins/
    Sample/
    WebDav/
```

.NET 런타임은 압축된 단일 EXE에 포함됩니다. 외부 Provider를 로드하므로 `plugins` 폴더도 함께 배포해야 합니다. 일부 네이티브 구성 요소는 실행 시 추출됩니다. 생성된 `build`는 Git에서 제외합니다. 기존 배포는 새 빌드 교체 시 `.tools/publish-backups`에 보관하며, 배포 EXE가 실행 중이면 교체를 중단합니다.

## 구조

| 경로 | 역할 |
| --- | --- |
| `src/MYSync.Desktop` | WPF 화면, 계정·동기화 실행 흐름, 트레이·시작 등록 |
| `src/MYSync.Provider.Abstractions` | Provider 계약 |
| `src/MYSync.PluginHost` | 플러그인 검증·로드, 독립 세션 생성 |
| `src/MYSync.Provider.Sample` | 플러그인 탐색 검증용 구현 |
| `src/MYSync.Provider.WebDav` | WebDAV 탐색·검사·조건부 전송 |
| `src/MYSync.Sync.Core` | 동기화 모델, 3자 비교, 엔드포인트 계약 |
| `src/MYSync.Sync.Infrastructure` | SQLite 저장소·작업 큐, 로컬 파일 보존, 실행기·변경 감지 |
| `tests/MYSync.Checks` | 독립 실행형 통합 검증 |

계약 v1의 `IProvider`는 기본 정보와 폴더 탐색을 제공합니다. 선택적 `IConfigurableProvider`는 연결을, `ITransferProvider`는 전송 엔드포인트 생성을 담당합니다. Provider.Abstractions와 Sync.Core 어셈블리는 호스트와 공유합니다. 플러그인 폴더를 추가한 뒤 앱을 재시작하는 방식이며, 플러그인은 호스트 권한으로 실행되는 신뢰된 코드여야 합니다.

## 데이터와 현재 제약

- `%LOCALAPPDATA%/MYSync/settings.db`: 동기화 쌍과 DPAPI 암호화 계정.
- `%LOCALAPPDATA%/MYSync/journals/{pairId}.db`: 기준 상태와 영속 작업 큐.
- `%LOCALAPPDATA%/MYSync/preferences.json`: 트레이 닫기 설정.
- 로컬 루트의 형제 경로 `.MYSync-recovery/{pairId}`: 교체·삭제 전 원본과 복구 기록.

현재 WebDAV는 HTTP, 자동 리디렉션, Digest/OAuth 인증을 지원하지 않습니다. 파일 교체·삭제에는 서버의 강한 ETag와 조건부 요청 지원이 필요합니다. 원격 폴더 삭제는 안전한 조건부 삭제를 보장할 수 없어 차단합니다. 원격 휴지통·버전 보존은 보장하지 않습니다.

원격 파일을 내려받아 해시를 비교하므로 첫 검사는 비용이 큽니다. 이후 검사는 로컬 지문과 원격 강한 ETag 캐시로 다시 읽지 않으며, 캐시는 계획 단계에만 쓰이고 실제 쓰기·삭제 전에는 항상 실물을 다시 확인합니다. 전송 속도·남은 시간 표시, 이어받기, 병렬 전송은 후속 개발 대상입니다.

## 문서

- [Google Drive 전송·재동의 안내](docs/google-drive-transfer.md)

- [Google Drive 설정·로그인 안내](docs/google-drive-setup.md)

- [WebDAV ETag 호환성 보완](docs/webdav-etag.md)

- [실패 기록과 진단 로그](docs/diagnostics.md)

- [개발 핸드오프](docs/HANDOFF.md): 현재 코드, 검증 범위, 다음 작업
- [수동 테스트 가이드](docs/manual-test.md)
- [개발 계획](docs/development-plan.md) · [개발 이력](docs/development-progress.md)
- [계정 저장](docs/account-storage.md) · [자동 동기화](docs/automatic-sync.md)
- [WebDAV 전송](docs/webdav-transfer.md) · [로컬 복구](docs/local-recovery.md) · [충돌·복구 확인](docs/conflict-recovery.md)
- [전송 상태와 계정 관리](docs/transfers-and-accounts.md) · [오류 분류·재시도와 검사 비용](docs/errors-and-performance.md)
- [미지원 항목 처리](docs/unsupported-items.md)
- [트레이와 로그인 자동 실행](docs/tray-and-startup.md)
