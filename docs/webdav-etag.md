# WebDAV ETag 호환성 보완 — 2026-09-08

진단 기능 커밋 `3f50c3c` 이후, GET 응답 헤더에 ETag가 없고 PROPFIND의 getetag에만 강한 ETag가 있는 서버를 지원한다.

## 구현

- 교체·삭제 대상과 업로드 임시 파일은 PROPFIND `Depth:0`으로 파일 자체의 메타데이터를 먼저 조회한다. 다른 항목, 누락, 중복, 폴더로 변경된 결과는 거부한다.
- 유효한 강한 getetag가 있으면 `If-Match` GET을 보낸다. GET 성공 시 받은 내용의 해시와 조회한 ETag를 연결한다. GET에 ETag가 없어도 사용할 수 있다.
- GET 응답에도 ETag가 있다면 조회 값과 일치해야 한다. 다르거나 412이면 변경 작업을 수행하지 않는다.
- getetag가 없거나 약하면 GET 헤더의 강한 ETag를 예비 경로로 사용한다. PROPFIND 자체가 실패하면 GET으로 우회하지 않는다. 양쪽 모두 강한 ETag가 없으면 변경을 거부한다.
- 검사 단계에서도 조회한 강한 ETag가 있으면 조건부 GET을 사용한다. 캐시는 조건부 GET으로 확인한 속성 ETag 또는 실제 GET 헤더 ETag에 연결한다.
- 성공적으로 검증한 임시 파일은 기존처럼 ETag 조건부 DELETE로 정리한다. 이제 GET 헤더가 없어도 속성 ETag로 정리할 수 있다. 정리 응답 상태를 로그에 남긴다.

## 남은 제한

해시 확인만 하고 조건 없는 DELETE를 보내지 않는다. 해시 검사와 삭제 사이에 내용이 변경될 수 있기 때문이다. ETag가 없거나 부분 전송 등으로 확인되지 않은 임시 파일은 보존한다. 이전 실행이 남긴 원격 임시 파일의 소유·내용을 복원하는 관리 기능은 아직 없다. 기존 NAS 파일을 자동으로 삭제하지 않았다.

파일 로그의 `webdav.resource-etag`는 속성의 강한 ETag 유무, `webdav.response`는 응답 헤더의 강한 ETag 유무다. 값 자체는 기록하지 않는다.

## 검증

`build.ps1 -Check` 전체 통과, 경고·오류 0개. 모의 서버에서 PROPFIND에만 ETag가 있는 업로드·교체·삭제, 대상 동시 수정과 임시 파일 정리, PROPFIND/GET 사이의 변경, GET의 불일치 ETag, Depth:0의 다른 항목, 약한·누락 ETag 거부를 검증했다. 기존 GET 헤더 기반 전송도 통과했다.

실제 Synology의 getetag 제공 여부와 조건부 GET/MOVE/DELETE 집행은 아직 미검증이다. 테스트 전용 폴더에서 재확인해야 한다. 기존 `.mysync-upload-`가 남은 쌍은 계속 검사 차단될 수 있다.

근거: [RFC 4918 §15.6 getetag](https://www.rfc-editor.org/rfc/rfc4918.html#section-15.6), [RFC 9110 §13.1.1 If-Match](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.1).
