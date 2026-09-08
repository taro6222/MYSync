# Google Drive 연결 설정

2026-09-08: OAuth 로그인·암호화 계정 저장·내 드라이브 폴더 탐색을 구현했다. Google Drive 파일 전송과 동기화 쌍 저장은 아직 지원하지 않는다. NAS 연결 없이 설정할 수 있다.

## Google Cloud 준비

1. [Google Cloud Console](https://console.cloud.google.com/)에서 개발용 프로젝트를 만들거나 선택한다.
2. API 라이브러리에서 **Google Drive API**를 활성화한다.
3. **Google Auth Platform**에서 앱 이름·사용자 지원 이메일 등 동의 화면 정보를 입력한다. 개인 Google 계정 테스트라면 외부 사용자 유형을 선택하고 테스트 상태에서 본인 Google 이메일을 테스트 사용자에 추가한다.
4. 데이터 액세스에 `https://www.googleapis.com/auth/drive.readonly` 범위를 추가한다. 현재 코드는 이 읽기 전용 범위만 요청한다. 선택 폴더 하나로 제한되는 권한은 아니며 Drive 파일 읽기 권한이다.
5. 클라이언트에서 OAuth 클라이언트를 만들고 애플리케이션 유형을 **데스크톱 앱**으로 선택한다. 웹 애플리케이션이나 서비스 계정이 아니다.
6. 생성된 클라이언트 ID와 클라이언트 보안 비밀번호를 MYSync에 직접 입력한다. 채팅·스크린샷·Git에 붙여 넣지 않는다.

메뉴 위치는 계정과 Console 버전에 따라 다를 수 있다. 공식 [OAuth 클라이언트 생성 안내](https://developers.google.com/workspace/guides/create-credentials#desktop-app)를 참고한다.

## MYSync에서 연결

1. 최신 배포 폴더 전체를 사용한다. EXE 옆 `plugins/GoogleDrive`에 SDK DLL과 deps.json도 있어야 한다.
2. 동기화 추가 → **Google Drive (연결·탐색)** → 새 계정 연결.
3. 위 클라이언트 ID와 보안 비밀번호를 입력한다. 기본 브라우저에서 본인 Google 계정에 로그인하고 요청 범위를 확인해 승인한다.
4. 로그인 응답을 받으면 MYSync로 돌아간다. 대기 제한은 3분이다. 브라우저를 닫는 것만으로 즉시 취소되지는 않는다.
5. 현재 폴더가 목록 첫 번째에 표시된다. 하위 폴더를 선택해 열거나 루트로 돌아갈 수 있다. 이름이 같아도 별도 ID의 폴더로 유지한다.
6. 앱 재시작 후 저장 계정 연결로 재연결한다. 유효한 갱신 토큰이 있으면 브라우저를 열지 않는다. 권한 만료·철회 시 인증 정보 수정으로 다시 로그인한다.

테스트 상태에서는 Google 정책에 따라 갱신 토큰이 만료될 수 있다. `drive.readonly`는 제한된 범위이며 공개 배포 전 검증 요건을 별도로 검토해야 한다. 자세한 내용은 [Drive 권한 범위](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)와 [데스크톱 OAuth](https://developers.google.com/identity/protocols/oauth2/native-app)를 참고한다.

## 구현과 저장

- Google.Apis.Auth 1.76.0의 PKCE 설치 앱 흐름, 기본 브라우저, 127.0.0.1 루프백 수신기를 사용한다. Google 비밀번호는 MYSync에 입력하지 않는다.
- SDK 기본 FileDataStore를 사용하지 않는다. 로그인 중 토큰은 메모리에만 보관하고, 성공 후 선택적 공통 계약 IPersistableConnectionProvider로 호스트에 전달한다.
- 클라이언트 정보와 토큰 응답은 기존 `%LOCALAPPDATA%/MYSync/settings.db`에 DPAPI CurrentUser로 암호화한다. 로그에 토큰·요청 헤더·응답 본문을 기록하지 않는다.
- 액세스 토큰 갱신은 SDK가 담당한다. 초기 로그인·인증 정보 수정·저장 계정 재연결 성공 때 갱신된 토큰을 다시 암호화 저장한다. 장기 세션 중 변경된 토큰을 즉시 영속화하는 이벤트는 후속 범위다.
- Drive REST v3로 현재 폴더 확인·하위 폴더 페이지 조회를 수행한다. 불완전 검색, 반복 페이지, 중복 ID, 잘못된 부모, 공유 드라이브, 휴지통 항목은 거부한다.
- 이 단계는 ITransferProvider를 구현하지 않는다. Google 동기화 쌍 저장을 UI에서 막고 연결·탐색 단계임을 표시한다.
- 플러그인 의존성을 해석할 수 있도록 publish.ps1에서 deps.json을 보존한다. EXE의 단일 파일 구조는 유지하며 외부 플러그인 의존성은 폴더에 둔다.

## 검증과 다음 단계

모의 인증 세션·HTTP 응답으로 페이지 조회, 같은 이름의 서로 다른 ID, 범위 오류·불완전 결과·취소 거부, 연결 실패 시 기존 세션 유지, OAuth 결과 DPAPI 저장, 플러그인 SDK 로드를 검증한다. 실제 Google 로그인, 브라우저 루프백 응답, 토큰 갱신 서버 동작과 화면 조작은 별도로 확인해야 한다. 자동 검증은 Google에 연결하거나 브라우저를 열지 않는다.

다음은 재귀 파일 검사·미지원 항목 보고, 안전한 파일 ID 기반 전송 설계다. 전송 도입 시 쓰기 권한 재동의가 필요하다. Google 문서·바로가기·공유 드라이브·중복 이름의 동기화 정책과 응답 유실·동시 변경 보호를 먼저 검증한다.
