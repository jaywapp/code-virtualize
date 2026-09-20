# UI 시안 검증 기록

- 검증일: 2026-09-20.
- 범위: sample1/2/3의 합성 데이터 기반 정적 프로토타입.
- 실행: loopback HTTP 서버 + 로컬 Chromium(headless, GPU 비활성).
- 내장 브라우저 연결은 프로세스 초기화 실패로 사용하지 못해 별도 로컬 Chromium에서 확인했다.
- desktop 1440×1000, mobile 390×844, dark theme를 각 시안에서 확인하고 [screenshots/](screenshots/)에 9개 화면을 저장했다.

## 실제 조작

| 항목 | 결과 |
|---|---|
| 이름/경로 검색과 Calculate 선택 | 3종 통과 |
| XML 주석 표시·없는 검색어·검색 초기화 | 3종 통과 |
| workspace 관계 범위·Cart 그래프 노드 선택 | sample3 통과 |
| Session 기준 Calculate 변경 없음 | 3종 통과 |
| VCS 기준 Calculate의 추가 line 표시 | 3종 통과 |
| stale/empty/error에서 원문 body 숨김 | 3종 통과 |
| 위 상태 복구·loading 종료 | 3종 통과 |
| partial에서 실패한 분석 범위 표시 | 3종 통과 |
| 테마 전환 | 3종 확인 |
| 모바일 심볼 메뉴 | 3종 통과 |
| JavaScript pageerror | 3종 모두 0건 |
| 페이지 전체 가로 넘침 | 3종 모두 없음 |

기계 판정 원본: [checks.json](screenshots/checks.json). 수치·hash·revision·source는 합성 예제다.

## 독립 마감 검토

별도 reviewer가 스크린샷 9개와 코드·방향 계약을 검토했다. 준비용 시안을 막는 중대 결함은 없었다.

| 발견 | 수정 | 최종 판정 |
|---|---|---|
| 모바일 select의 선택값 잘림 | toolbar label을 전체 너비로 배치, select 100% | resolved |
| 모바일에 맞지 않는 “오른쪽 검사” 안내 | 위치에 의존하지 않는 안내로 변경 | resolved |

최종 verdict: **resolved — 2건 모두 해결**. 수정 후 동일 viewport·기능 검증을 한 번 더 수행했다. 디자인 detector는 완성 후 한 번 실행했고 결과는 `[]`였다. 반복 실행하지 않았다. JavaScript 구문 검사와 git whitespace 검사도 통과했다.

## 검증 한계

실제 엔진·Git/Perforce·MCP·제품 Web API·인증·실제 source hashing과 연결하지 않았다. 모든 accessibility 기준이나 성능 benchmark를 인증한 결과도 아니다. 별도 reviewer는 브라우저를 다시 실행하지 않고 저장된 화면·검증 결과·코드만 검토했다. 최종 UI 방향과 frontend stack 선택은 아직 남아 있다.
