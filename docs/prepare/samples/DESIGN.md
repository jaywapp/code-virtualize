---
name: Code Virtualize UI 비교 시안 (최종 선택 전)
description: 합성 C# fixture를 세 가지 정보 구조로 검사하는 정적 비교 프로토타입의 공통 시각 계약
colors:
  canvas: "#f5f6f4"
  surface: "#ffffff"
  panel: "#eef1ef"
  ink: "#202d2b"
  muted: "#526660"
  line: "#cbd5d0"
  accent: "#12685c"
  accent-soft: "#e0efea"
  warning: "#805115"
  warning-soft: "#fbefd9"
  error: "#a32e35"
  error-soft: "#fff0ef"
  code-surface: "#f7f9f7"
  diff-delete: "#8d3439"
  diff-add: "#16694c"
typography:
  headline:
    fontFamily: "Segoe UI, Arial, sans-serif"
    fontSize: "22px"
    fontWeight: 700
    lineHeight: 1.35
    letterSpacing: "-0.02em"
  title:
    fontFamily: "Segoe UI, Arial, sans-serif"
    fontSize: "16px"
    fontWeight: 700
    lineHeight: 1.5
  body:
    fontFamily: "Segoe UI, Arial, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Segoe UI, Arial, sans-serif"
    fontSize: "12px"
    fontWeight: 600
    lineHeight: 1.5
  code:
    fontFamily: "Consolas, Cascadia Code, monospace"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.8
rounded:
  badge: "4px"
  control: "5px"
  panel: "6px"
  large-panel: "8px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "20px"
  2xl: "24px"
  3xl: "28px"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.surface}"
    typography: "{typography.body}"
    rounded: "{rounded.control}"
    padding: "6px 12px"
    height: "36px"
  button-secondary:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.control}"
    padding: "6px 12px"
    height: "36px"
  input-search:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.control}"
    padding: "8px 10px"
    height: "36px"
  badge-status:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.ink}"
    typography: "{typography.label}"
    rounded: "{rounded.badge}"
    padding: "1px 6px"
  symbol-selected:
    backgroundColor: "{colors.accent-soft}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.control}"
    padding: "10px"
  code-panel:
    backgroundColor: "{colors.code-surface}"
    textColor: "{colors.ink}"
    typography: "{typography.code}"
    rounded: "{rounded.panel}"
    padding: "16px 0"
---

# Design System: Code Virtualize UI 비교 시안 (최종 선택 전)

> **문서 상태:** 이 문서는 `samples/`에 구현된 정적 합성 비교 시안 3종을 기록한다. 어느 안도 최종 제품 UI로 선택되지 않았고, frontend stack도 확정하지 않는다. 상위 `docs/prepare/design.md`의 제품 기획과 구분해서 읽는다.

## Overview

**Creative North Star: "검증 가능한 로컬 검사대" (비교 시안 공통어, 최종 제품 방향 아님)**

세 시안은 Windows에서 C# workspace를 다루는 개발자와 리뷰어가 심볼, 원문, 관계, 변경 근거를 차분하게 확인하는 도구를 가정한다. 밝은 중립 표면과 절제된 teal을 공통으로 사용하고, native control과 명확한 경계로 조작 가능성과 상태를 드러낸다. 장식보다 출처, 범위, baseline, 실패 이유를 우선한다.

이 시각 언어는 CLI와 함께 제공될 로컬 Web UI의 비교 재료다. Git의 명시 revision과 세션 시작 snapshot을 모두 표현하며, Perforce는 제품의 필수 후속 범위로 남는다. 화면의 source, hash, 관계, diff는 모두 합성 fixture이고 실제 엔진 결과가 아니다.

**Key Characteristics:**

- 최종 선택 전의 세 방향이 하나의 토큰, fixture, 상태 용어를 공유한다.
- 원문 최신성과 참조 coverage를 서로 다른 상태 줄로 항상 분리한다.
- 시스템 sans는 조작과 설명에, Consolas 계열 mono는 경로, signature, source, diff에 쓴다.
- 라이트와 다크 테마 모두 중립 표면의 경계와 teal 강조를 유지한다.
- loading, empty, stale, partial, error를 정상적인 제품 상태로 다룬다.

## Colors

라이트 기본 테마는 차가운 회록색 canvas와 흰 surface 위에 짙은 녹회색 ink를 놓고, teal을 선택과 실행에 제한한다. 다크 테마는 같은 의미를 `shared.css`의 `data-theme="dark"` 변수로 치환한다.

### Primary

- **검증 Teal** (`{colors.accent}`): 링크, primary action, 선택 테두리, 최신 상태에 사용한다.
- **선택 Teal Wash** (`{colors.accent-soft}`): 선택된 심볼, 선택된 읽기 단계, 추가 diff 행의 배경에 사용한다.

### Secondary

- **주의 Ochre** (`{colors.warning}`): partial, stale, 텍스트 추정처럼 확인이 필요한 상태에 사용한다.
- **주의 Paper** (`{colors.warning-soft}`): 부분 분석 안내와 경고 notice의 배경이다.

### Tertiary

- **실패 Red** (`{colors.error}`): 원문 읽기 오류와 삭제 diff를 구분한다.
- **실패 Blush** (`{colors.error-soft}`): error notice와 삭제 diff의 배경이다.
- **추가 Green** (`{colors.diff-add}`) / **삭제 Red** (`{colors.diff-delete}`): unified diff의 의미 색이다.

### Neutral

- **Workspace Canvas** (`{colors.canvas}`): 페이지 최하단 배경이다.
- **Inspection Surface** (`{colors.surface}`): toolbar, detail, inspector, control의 기본 표면이다.
- **Quiet Panel** (`{colors.panel}`): sidebar, signature, 설명 구획, hover 표면이다.
- **Primary Ink** (`{colors.ink}`): 제목과 본문 텍스트다.
- **Evidence Muted** (`{colors.muted}`): metadata, 범위 설명, 보조 문구다.
- **Structural Line** (`{colors.line}`): 열, panel, control, 목록 항목을 나누는 1px 경계다.
- **Code Surface** (`{colors.code-surface}`): source와 diff의 별도 읽기 표면이다.

**The Meaning Before Hue Rule.** teal은 선택·실행·최신, ochre는 불완전·추정, red는 실패·삭제 의미를 유지한다. 색만으로 상태를 전달하지 않고 항상 텍스트를 함께 둔다.

**The Freshness Is Not Coverage Rule.** 최신 teal과 부분 coverage ochre가 같은 화면에 동시에 나타날 수 있어야 한다.

## Typography

**Display Font:** Segoe UI (with Arial, sans-serif fallback)
**Body Font:** Segoe UI (with Arial, sans-serif fallback)
**Label/Mono Font:** Consolas (with Cascadia Code, monospace fallback)

**Character:** Windows 환경에 익숙한 system sans로 조작 밀도를 안정시키고, code와 evidence는 고정폭 글꼴로 원문성을 드러낸다. 표현적인 display face는 사용하지 않는다.

### Hierarchy

- **Headline** (700, 22px, 1.35): 각 시안과 선택 심볼의 주 제목. 모바일 page heading은 20px, sample3 detail heading은 19px다.
- **Title** (700, 16px, 1.5): sidebar, graph, inspector의 구획 제목.
- **Body** (400, 14px, 1.5, 최대 72ch): 설명과 안내. source 읽기 중심 시안의 code는 14px/1.9로 넓힌다.
- **Label** (600, 12px, 1.5): field label, 상태명, metadata. 대문자 장식 label은 쓰지 않는다.
- **Code** (400, 13px, 1.8): source, signature, path, 관계 위치. sample3의 desktop diff는 11px, 모바일은 12px다.

**The Source Stays Source Rule.** XML 주석과 source는 AI 요약체로 바꾸지 않고 mono 원문으로 표시한다.

## Layout

공통 topbar, 합성 데이터 고지, page heading, 검사 조건 toolbar, freshness/coverage statusbar, 작업 영역, footer 순서를 유지한다. toolbar는 flex-wrap을 사용하며 VCS/Session 기준, 관계 범위, 상태 시뮬레이션을 한 줄에서 시작한다.

세 방향의 데스크톱 구조는 의도적으로 다르다.

1. **심볼 탐색기:** 260px sidebar, 가변 source detail, 295px 관계 inspector의 3열이다. 1600px 이상에서는 290px / 가변 / 340px로 확장한다.
2. **원문 읽기:** 최대 1180px 안에서 240px symbol rail과 최대 820px reading column을 28px 간격으로 둔다. 관계 근거는 `details` 안에 접는다.
3. **관계와 변경:** 225px sidebar, 가변 graph, 400px diff inspector의 3열이다. graph canvas는 390px 높이와 360px 최소 너비를 가지며 넘치면 내부 스크롤한다. 1600px 이상에서는 245px / 가변 / 460px로 확장한다.

1100px 이하에서는 sample1과 sample3의 오른쪽 inspector가 두 번째 열 아래로 내려가고, 읽기 시안은 210px rail로 줄어든다. 700px 이하에서는 작업 영역을 단일 열로 바꾸고 sidebar를 기본 숨김 상태로 두며 “심볼 목록” native button으로 펼친다. toolbar의 각 label과 select는 한 행 전체 너비를 사용해 긴 한국어 option이 잘리지 않게 한다. graph는 내부 canvas 너비를 유지해 수평 스크롤로 공간 관계를 보존한다.

**The Three Structures Rule.** 세 시안은 색상 변형이 아니라 탐색 밀도, 정보 공개 순서, 주요 조작의 차이를 비교한다.

**The Evidence Does Not Move Rule.** 모바일에서도 freshness, coverage, baseline 의미를 숨기지 않는다. sidebar만 명시적인 control로 접는다.

## Elevation & Depth

기본은 shadow 없는 평면 시스템이다. canvas, surface, panel의 tonal layering과 1px line으로 계층을 만든다. 유일한 box shadow는 sample3의 선택된 graph node에 쓰는 `0 5px 15px #122b231a`이며, 선택 대상을 관계선 위에서 분리하는 구조적 역할만 한다.

### Shadow Vocabulary

- **Selected Graph Node** (`box-shadow: 0 5px 15px #122b231a`): graph에서 현재 선택된 node에만 적용한다.

**The Flat Evidence Rule.** card를 띄워 중요도를 연출하지 않는다. 경계, 배경 tone, 문서 순서로 정보 관계를 설명한다.

## Shapes

control은 완만한 5px, notice와 code panel 및 graph node는 6px, 큰 reading panel과 graph viewport는 8px radius를 쓴다. 상태 badge는 4px radius의 작은 직사각형이고 한 줄을 유지한다. 원형 장식과 과도한 pill은 없다. 관계선은 1.4px 실선으로 정적 해석을, `5 4` dash로 텍스트 추정을 나타낸다.

## Components

### Buttons

- **Shape:** compact한 native control (5px radius, 최소 높이 36px).
- **Primary:** accent 배경과 surface 글자, 6px 12px padding. 다크 테마에서는 짙은 `#14251d` 글자를 사용한다.
- **Hover / Focus / Active:** hover는 panel, active는 accent-soft, keyboard focus는 accent 3px outline과 3px offset이다.
- **Disabled:** opacity 0.5와 default cursor로 복구 진행 중 중복 실행을 막는다.

### Chips

- **Style:** 4px radius, 1px line, 1px 6px padding의 compact badge다.
- **State:** 정적 해석은 accent/accent-soft, 텍스트 추정은 warning/warning-soft를 사용한다. 접근성·kind badge는 neutral로 유지한다.

### Cards / Containers

- **Corner Style:** notice와 code는 6px, reading detail과 graph viewport는 8px다.
- **Background:** surface를 기본으로 하고 panel은 보조 구획, warning-soft/error-soft는 상태 구획에만 사용한다.
- **Shadow Strategy:** 기본 shadow 없음. 선택된 graph node만 구조적 shadow를 쓴다.
- **Border:** structural line 1px.
- **Internal Padding:** 16px에서 30px 범위이며 작업 밀도에 따라 달라진다.

### Inputs / Fields

- **Style:** native search/select, surface 배경, line stroke, 5px radius, 최소 높이 36px.
- **Focus:** accent 3px outline과 3px offset.
- **Error / Disabled:** error는 별도 notice로 원인과 재시도 action을 제공한다. 복구 중 scenario select와 복구 button을 disable한다.

### Navigation

topbar nav는 12px system sans 링크를 한 줄로 유지하고 현재 시안은 굵은 ink와 `aria-current="page"`로 표시한다. 700px 이하에서는 nav가 새 행을 차지하고 세 링크를 분산 배치한다. source/remark/diff tab은 underline과 text color로 선택을 표시하며 sample2에서는 같은 상태를 번호 있는 읽기 단계 button으로 표현한다.

### Symbol List

검색 결과는 4px 간격의 full-width button 목록이다. 선택 항목은 accent-soft 배경과 accent border를 사용하고 `aria-pressed`를 갱신한다. 이름, 접근성, kind, 파일명을 함께 보여 동명 심볼을 구분한다.

### Status and Recovery

statusbar는 원문 최신성, 참조 coverage, 분석 모드를 별도 문구로 표시한다. ready, stale, partial, empty, error와 550ms 합성 loading을 지원한다. stale/error/empty/loading 동안 source, diff, graph, 관계 근거의 이전 값을 현재 결과처럼 노출하지 않는다.

### Source and Diff

source는 줄 번호가 있는 scrollable `pre`, diff는 add/delete 배경과 `+`/`-` 기호를 함께 사용한다. VCS baseline은 명시 commit에서 dirty working tree까지, Session baseline은 시작 snapshot 이후만 비교한다. 선택한 기준은 heading, 설명, diff에 계속 유지된다.

### 세 비교안의 차이

- **시안 1, 심볼 탐색기:** symbol list, source/remark/diff, 관계 근거를 동시에 보여 잦은 전환을 우선한다.
- **시안 2, 원문 읽기:** 원문 → 주석 → 변경의 순서 button과 접힌 관계 근거로 한 심볼의 이해를 우선한다.
- **시안 3, 관계와 변경:** 선택 가능한 기하 graph, edge legend, 관계 근거, diff를 병치해 영향 후보 검사를 우선한다.

## Do's and Don'ts

### Do:

- **Do** 최종 선택 전 3종 비교 시안이라는 상태와 합성 데이터임을 제목, 고지, 설명에 유지한다.
- **Do** 원문 freshness와 참조 coverage를 분리하고, `0 references`를 `no impact`로 표현하지 않는다.
- **Do** VCS와 Session baseline을 모두 제공하고 각 기준에 포함되지 않는 변경을 설명한다.
- **Do** Windows + C# + Git 우선 맥락과 Perforce 필수 후속 범위를 제품 계약으로 유지한다.
- **Do** native semantic control, `aria-pressed`, `aria-current`, `aria-live`, keyboard focus를 유지한다.
- **Do** 모바일에서 select를 full width로 만들고 graph는 위치 관계를 보존한 채 내부 스크롤한다.
- **Do** dark theme과 `prefers-reduced-motion`에서 동일한 상태 의미를 유지한다.

### Don't:

- **Don't** 세 시안 중 하나를 최종 제품 UI 또는 확정 frontend stack으로 서술한다.
- **Don't** hash 일치를 완전한 관계 분석의 증거로 사용한다.
- **Don't** 텍스트 추정 edge를 정적 호출로, 합성 semantic fixture를 실제 분석 결과로 표현한다.
- **Don't** stale/error/loading 중 이전 source, diff, graph를 현재 결과처럼 보여준다.
- **Don't** decorative card, 사진, 일러스트, 표현적 motion으로 검사 근거보다 시각 효과를 앞세운다.
- **Don't** 상위 `docs/prepare/design.md`를 이 시안 문서로 대체하거나 덮어쓴다.
