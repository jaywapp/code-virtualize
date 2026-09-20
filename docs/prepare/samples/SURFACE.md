# 로컬 검사 화면 비교 시안

## 상태와 근거

- 상태: **선택 전 3종 비교 시안**, 사용자 최종 선택 미정.
- 표면 모드: **Operate**. 개발자가 원문·심볼 관계·diff를 검사하는 작업 화면이다. sample2의 읽기 밀도에는 Read 지침을 보조 적용한다.
- 제품 근거: `../PRODUCT.md`, `../user-confirm.md`의 UC-005(C: VCS/Session)·UC-006(A: L0/L1 + lazy resolve)·UC-008(A: syntax-only 기본/semantic trust)·UC-011(A+B: CLI와 로컬 Web UI).
- `prepare-repository`는 의미가 다른 UI 시안 3종을 명시한다. 최신 인터뷰와 이어서 수행하라는 요청을 근거로 대안 제작까지 진행하며 최종안 선택은 대신하지 않는다.

## 방향 계약

공통 시각 언어는 system sans UI, Consolas 코드, 억제된 teal 선택/행동 강조, 밝은 주간 기본 테마와 어두운 테마, 경계가 분명한 중립 표면이다. 마케팅 사진·장식 일러스트가 필요하지 않은 작업 도구다. semantic HTML과 native controls를 사용한다.

1. **심볼 탐색기:** 3열의 높은 정보 밀도. 목록 선택 즉시 원문/관계 근거 비교. 잦은 대상 전환을 우선한다.
2. **원문 읽기:** 단일 읽기 열과 순서 버튼. source/remark/diff 단계를 나누고 관계 근거를 접어서 이해를 우선한다.
3. **관계와 변경:** 기하 graph와 diff 병치. 노드 선택, 관계 범위, baseline을 바꾸며 영향 후보의 근거를 검사한다.

색만 바꾼 변형이 아니라 열 구조·정보 공개 순서·주요 조작이 다르다. 다만 비교를 위해 fixture와 상태·용어·controls는 공유한다.

## 스킬 적용 기록

- `impeccable`: Operate와 craft-floor의 가독성, native affordance, loading/empty/error, 키보드 focus, 대비, 구조적 모바일 접힘, reduced-motion을 적용한다.
- `design-taste-frontend`: 도구/데이터 UI는 원문상 직접 대상 밖이므로 brief 추론과 반템플릿 원칙만 적용한다. landing/photo/표현적 모션 기본값은 강제하지 않는다. variance 3 / motion 1 / density 8, 4, 6을 각 시안의 작업 목적에 맞춰 적용한다.
- 메인 에이전트의 concept seed 기록: key `5285aee8`, assigned count `5`. 실제 산출물 수는 사용자 명시 스킬의 3종 요구를 우선한다. seed는 디자인 선택이나 사용자 승인을 대신하지 않는다.
- 코드/주석은 영어, 사용자 인터페이스와 문서는 한국어로 작성한다. UI 기술 선택과 실제 제품 실행은 이 시안에 포함하지 않는다.

## Design Read

개발자가 로컬 분석 결과의 정확성을 확인하는 작업 UI로 해석한다. 절제된 색과 익숙한 제어 요소를 사용하고 탐색 밀도·순차 읽기·관계 비교의 차이를 평가한다.
