# Google Drive 로그인 설정

## 오류 알림·개별 정책·OAuth 배포 업데이트 (2026-09-08)

트레이 오류 알림 클릭 시 해당 오류 페이지로 이동한다. 연결별 파일·폴더 제외와 합산 전송 속도(KiB/s, 0=무제한)를 설정할 수 있다. Google 설정은 DLL 내부 리소스로 전환해 배포 폴더에 oauth-client.json을 복사하지 않는다. 내장 리소스는 추출 가능하므로 비밀 보장으로 간주하지 않는다. [설정 및 테스트](sync-options-and-alerts.md), [OAuth 배포와 보안 한계](oauth-distribution.md)를 참고한다. 아래 구버전 설명과 다르면 이 업데이트를 우선한다.


전송 단계 업데이트: 이제 동기화 쌍 저장과 5 MiB 이하 파일 전송을 지원한다. 공통 OAuth 설정은 준비됐고 사용자가 로그인·탐색 성공을 확인했다. 전송에는 `https://www.googleapis.com/auth/drive` 범위를 추가하고 기존 계정에서 Google 다시 로그인해야 한다. 아래 읽기 전용·탐색 전용 문구는 초기 안내이며 [전송 안내](google-drive-transfer.md)를 우선한다.

일반 사용자는 Google Cloud 설정 없이 **Google로 로그인** 버튼으로 연결한다. 공통 OAuth 설정은 앱 배포자가 한 번 준비한다. 현재 Google Drive는 로그인·폴더 탐색만 지원하며 파일 전송은 후속 단계다.

## 사용자의 연결 순서

1. 동기화 추가에서 Google Drive를 선택하고 **Google로 로그인**을 누른다.
2. 기본 브라우저에서 계정을 선택하고 Drive 읽기 권한을 승인한다.
3. MYSync로 돌아와 내 드라이브 폴더를 탐색한다.
4. 다음 실행에서는 저장 계정 연결을 사용한다. 인증을 다시 받아야 하면 **Google 다시 로그인**을 누른다.

클라이언트 ID·보안 비밀번호 입력창은 표시하지 않는다. 로그인 대기는 최대 3분이다. 공통 설정이 없는 개발 빌드는 “Google 로그인이 아직 준비되지 않았습니다”라고 안내한다.

## 배포자가 한 번 준비할 설정

1. [Google Cloud Console](https://console.cloud.google.com/)에서 MYSync에 사용할 프로젝트를 선택하고 Google Drive API를 활성화한다.
2. Google Auth Platform에서 동의 화면을 구성하고, 개발 중에는 본인 계정을 테스트 사용자로 추가한다.
3. 현재 단계의 범위는 `https://www.googleapis.com/auth/drive.readonly`다. 선택 폴더 하나로 한정된 권한이 아니라 Drive 읽기 권한이며, 전송 구현 시 필요한 쓰기 권한과 재동의를 추가한다.
4. OAuth 클라이언트 유형을 **데스크톱 앱**으로 만들고 Google에서 JSON 설정을 다운로드한다.
5. 다운로드한 JSON을 저장소의 `.tools/google/oauth-client.json`에 둔다. 최상위 `installed` 아래에 `client_id`, `client_secret`이 있어야 한다. 웹 앱 또는 서비스 계정 JSON은 지원하지 않는다.
6. `build.ps1` 또는 `publish.ps1`을 실행한다. 설정은 출력의 `plugins/GoogleDrive/oauth-client.json`에 복사된다.

`.tools`와 `build`는 Git에서 제외되어 있다. 실제 설정 파일을 소스에 커밋하거나 채팅에 붙여 넣지 않는다. 네이티브 앱에 배포되는 OAuth 클라이언트 정보는 추출 가능한 앱 식별 정보이며 서버의 비밀키처럼 숨길 수 있는 값은 아니다. 사용자의 갱신 토큰과는 구분한다.

저장 계정은 원래 OAuth 클라이언트와 토큰을 함께 암호화해 유지한다. 공통 설정을 바꿔도 기존 토큰에 새 클라이언트를 억지로 연결하지 않는다. 새 공통 클라이언트로 전환하려면 다시 로그인한다. 디버그 출력의 기존 설정은 원본 파일을 제거해도 자동 제거하지 않으므로 설정 제거 테스트에는 새 출력 폴더를 사용한다.

현재 배포용 OAuth 클라이언트는 아직 준비되지 않았다. 일반 사용자의 절차와 달리, 이 프로젝트의 배포자는 위 준비를 먼저 해야 실제 로그인이 가능하다.

## 구현·검증

Google.Apis.Auth 1.76.0의 PKCE 설치 앱 인증과 127.0.0.1 루프백 수신기를 사용한다. SDK의 평문 FileDataStore는 사용하지 않는다. 생성된 토큰은 호스트의 DPAPI CurrentUser 계정 저장소에 보관한다. 로그인·수정·재연결 성공 시 저장하고, 토큰·헤더·본문은 로그에 남기지 않는다.

공통 선택 계약 `IBrowserLoginProvider`로 버튼 문구와 입력창 생략 여부를 결정한다. WebDAV는 기존 설정 입력을 유지한다. `IPersistableConnectionProvider`로 OAuth 결과를 암호화 저장소에 전달한다.

모의 인증·HTTP로 폴더 페이지, 중복 이름의 별도 ID, 불완전 검색·잘못된 부모·공유 드라이브 거부, 암호화 저장, SDK 로드 및 공통 설정 누락·형식 검증을 수행한다. 실제 Google 로그인·갱신·화면 조작은 미검증이다. Google 동기화 쌍 저장은 전송 구현 전까지 차단한다.

공개 배포와 테스트 사용자·제한 범위 검증은 [Google OAuth 자격 증명 안내](https://developers.google.com/workspace/guides/create-credentials#desktop-app), [Drive 범위 안내](https://developers.google.com/workspace/drive/api/guides/api-specific-auth), [데스크톱 OAuth 안내](https://developers.google.com/identity/protocols/oauth2/native-app)를 따른다.
