# rev2 정답 공통 판정 규약

세 corpus(`medium-litedb`, `medium-quartznet`, `large-aspnetcore`)의 `expected.json`은 이 규약을 똑같이 따른다. [protocol.md Revision 2](../../../benchmarks/protocol.md)가 정한 규칙이 우선이고, 이 문서는 protocol이 정하지 않은 판정 세부를 한 가지로 고정한다. 2026-09-24 medium-litedb 정답 작성에서 나온 판단을 기준으로 정리했고, 2026-09-25 세 corpus의 정답을 대조해 어긋난 부분을 보완했다. TASK-029 판정기와 교차 확인(C단계)도 이 규약을 기준으로 한다.

## hash

- 알고리즘: 정규화한 텍스트의 UTF-8 bytes에 SHA-256을 적용한다.
- 입력 텍스트: pinned blob(N4는 편집을 적용한 scratch 복사본)에서 `startLine`의 첫 문자부터 `endLine`의 마지막 문자까지의 원문 substring이다. CRLF와 단독 CR은 LF로 바꾼다. BOM은 넣지 않고, `endLine` 뒤에 줄 끝 문자를 붙이지 않는다.
- 이것은 R2-4의 bytes 계상 규칙(줄마다 내용 + LF 1 byte)과 다른 규칙이다. 정답 hash에는 계상 규칙을 쓰지 않는다.

## 선언 범위

- `startLine`은 member 자신의 signature·이름 토큰이 있는 줄이다. 그 앞의 attribute 줄과 doc comment 줄은 포함하지 않는다.
- `endLine`은 body를 닫는 `}`가 있는 줄이다. `;`로 끝나거나 expression-bodied인 한 줄 선언이면 `startLine`과 같다.

## NAV

- N3 정적 참조는 receiver의 정적 타입이 대상의 선언 타입(또는 대입 호환 타입)임을 둘러싼 method·constructor를 읽어 확인한 경우만 센다. 이름만 보고 추정하지 않는다.
- 진단·로그 메시지 문자열 안에 member 이름이 영어 텍스트로만 들어 있으면, 그 member를 찾거나 호출하는 데 쓰이지 않으므로 정적 참조도 dynamic 후보도 아니다. 대상별로 `n3_excludedNonReferences`에 따로 적는다.
- receiver의 정적 타입이 interface나 base 타입이면, 그 호출은 interface·base 선언의 참조이지 대상 구현의 참조가 아니다.
- 대상 자신을 가리키는 `nameof(...)`, `using static`을 거친 호출, method-group 참조, Moq 같은 식 트리 lambda 안의 호출은 컴파일러가 정적으로 결합하므로 정적 참조로 센다.
- XML doc comment의 `<see cref>`는 주석이므로 정적 참조가 아니다. `n3_excludedNonReferences`에 적는다.
- 이름만 같은 무관한 타입의 별개 선언과 그 참조는 `n3_excludedNonReferences`에 "다른 심볼"로 적는다.

## DIFF

- **rename**: 같은 containing type 안에서 simple name이 바뀌면 base `p`의 이전 이름 선언을 REMOVED로, target의 새 이름 선언을 ADDED로 기록한다. rename 한 건으로 합치지 않는다. 그래서 rename된 member도 D3의 "첫 번째 삭제 member" 후보가 된다. TASK-029 판정기는 조건의 rename 보고가 이 REMOVED + ADDED 짝을 모두 덮는지 확인해야 한다.
- **signatureChanged**: 이름이 같고 선언 줄 자체의 매개변수 목록, 매개변수 이름, 반환형, 필드·속성의 선언 타입, 접근 제한자, `readonly`·`static` 같은 modifier가 바뀐 경우다.
- **attribute 변경**: member나 type에 붙은 attribute가 추가·삭제·변경되면 그 심볼의 `signatureChanged`로 기록한다. attribute는 선언 구문의 일부다.
- **remark 변경**: XML doc comment(`///`, `<summary>`, `<remarks>` 등)가 바뀌면 그 심볼의 `remarkChanged`로 기록한다. protocol R2-5의 D1·D2 판정 대상이다.
- 평범한 `//` 주석만 바뀐 경우는, 주석이 선언 범위(위 "선언 범위" 절) 안에 있으면 `bodyChanged`, 밖에 있으면 기록하지 않는다.
- **전처리기**: `#if`, `#pragma` 같은 지시문만 바뀌고 안쪽 선언 텍스트가 byte 단위로 같으면, 그 심볼의 변경으로 기록하지 않는다.
- **file-level 변경**: 기존 파일에서 using, namespace, assembly attribute만 바뀐 경우는 `nonSymbolChange`에 기록한다. 새로 추가된 파일 안의 using은 기존 파일의 변경이 아니므로 기록하지 않는다.
- **local function**: 심볼 분류 대상이 아니다. local function 안의 변경은 가장 가까운 바깥 member의 변경으로 본다.
- **primary constructor 매개변수**: 별도 심볼로 기록하지 않는다. 바뀌면 그 type의 `signatureChanged`다.
- **D3 대상**: D1에서 `(path ordinal, line)` 순으로 첫 번째 삭제 member를 고른다. 없으면, 선언 범위(위 "선언 범위" 절) 안의 텍스트가 `p`와 `c'`에서 실제로 다른 첫 번째 member(body·signature 변경)를 고른다. attribute·remark·범위 밖 주석만 바뀐 member와 type 선언은 D3 대상이 아니다. D3 판정은 base 텍스트와 현재 텍스트가 달라야 의미가 있기 때문이다.
- **D3 없음**: 위 규칙으로 대상이 없는 쌍은 `d3_resolve`를 `null`로 두고 `d3NotApplicable: true`와 이유를 적는다. 그 쌍은 모든 조건에서 D3를 판정하지 않는다(2026-09-25 사용자 결정, ADR 005 보완).
- **위치 기록**: D1·D2 항목은 심볼마다 이름·signature 토큰이 있는 anchor 줄 하나를 기록한다. D3 지정 심볼만 전체 선언 범위와 hash를 기록한다.
- **필드**: private 구현용 필드도 선언 심볼이므로 별도 항목으로 기록한다.
- **path ordinal**: corpus 기준 상대 경로를 `/` 구분자로 쓰고, byte 값 기준 C ordinal 비교로 정렬한다.

## 기록 형식

- 판정이 애매했던 항목은 `expected.json`의 `judgmentCalls`에 이유와 함께 남긴다. 이 규약과 다르게 판정해야 하는 경우가 나오면, 임의로 정하지 말고 그 사례를 보고한다.
- 교차 확인 표본 크기는 `max(10, ceil(0.25 × 정답 항목 수))`다.
