# Google OAuth 배포 설정

2026-09-08 업데이트.

배포 입력은 기존 .tools/google/oauth-client.json이다. 이 파일은 Git에서 제외된 개발자 전용 입력이며 배포 폴더에 복사하지 않는다. MSBuild가 Google 플러그인 DLL의 MYSync.GoogleOAuth 리소스로 포함하고 새 로그인은 메모리에서 이를 읽는다. Debug 생성 폴더에 남아 있던 구버전 JSON도 build.ps1이 제거한다. publish.ps1은 새 폴더를 게시하므로 최신 build/win-x64에는 oauth-client.json이 없다.

기존 계정은 저장 당시의 OAuth 클라이언트 연결을 유지한다. 갱신 토큰을 포함한 사용자 계정 값은 계속 Windows 사용자별 DPAPI 암호화 저장소를 사용한다. 원본 입력이나 이전 배포 백업은 .tools 아래에 남아 있으며 배포 대상이 아니다.

## 보안 한계와 선택

DLL 내장은 일반 파일 노출을 줄이는 패키징 변경이며 암호화나 비밀 보장의 수단이 아니다. 리소스를 추출하면 내장된 값을 읽을 수 있다. Google은 설치형 앱이 client_secret을 비밀로 유지할 수 없음을 명시한다. [Google 데스크톱 OAuth 공식 문서](https://developers.google.com/identity/protocols/oauth2/native-app)

현재 구조에서는 브라우저 OAuth·PKCE와 사용자 토큰의 암호화 저장이 실제 보호 수단이다. 공통 클라이언트 시크릿을 사용자 기기에 전혀 전달하지 않아야 한다면 서버가 비밀을 보관하고 토큰 교환을 담당하는 별도의 인증 백엔드와 이에 맞는 OAuth 클라이언트/리디렉션 설계가 필요하다. 서버 운영·접근 제어·토큰 관리가 추가되므로 이 변경에서는 도입하지 않았다.

새 버전은 외부 JSON 변경을 읽지 않는다. 배포 OAuth 설정을 바꾸려면 개발 입력을 변경하고 다시 빌드한다. 입력이 없는 빌드는 로그인 설정 미준비 안내를 표시한다. 사용자 개인 토큰은 내장 리소스에 포함하지 않는다.
