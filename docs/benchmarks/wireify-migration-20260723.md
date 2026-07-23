# Wireify-수렴 마이그레이션 벤치마크 — 2026-07-23

과제(모든 라운드 동일 하니스, 프롬프트 파일 고정): 5×5 포인트 그리드 + H형강(H-200×200, t=12) 기둥 3000mm,
그리드 간격 X/Y·기둥 높이 슬라이더. 성공 판정은 외부(dev 엔드포인트): 스크립트 컴포넌트 존재 + 와이어 ≥1 +
출력 DataCount > 0 + 런타임 에러 0.

하니스: `run-round.ps1` — Rhino 자동 기동(runscript `_GPTinoOpenPanel` + bench.3dm) → SendKeys로
`-Grasshopper Document Open` bench.gh → endpoint.json 폴링 → API 세션 생성 → 과제 전송 → 5초 폴링
(완료 = 마지막 메시지 assistant & 25초 경과 & 큐 비움; 타임아웃 12분) → live-jobs.db/runtime.db 수집.

## R0 — 베이스라인 (커밋 761fa4d, 마이그레이션 전, Python 과제)

- 결과: **NOT-GREEN (과제 실패)** — 에이전트가 자체 중단
- 타임라인: task 06:09:41 → final 06:20:48 (**11m07s**)
- 커밋된 잡: 2 (컴포넌트+슬라이더 9개 생성 / 슬라이더 값 설정)
- 거부된 제출: **3회 동일 오류** `invalid or duplicate Python parameters` (setComponentIo 제출 거부 —
  submit-time 거부라 live_jobs에 행 없음; 에이전트 최종 보고 기준)
- 미완료: 소스 작성 후 IO 스키마 단계에서 좌초 → 배선·실행 전무
- 관찰: 과거 벤치마크 메모리의 고질 병목("python IO reshaping 지속 실패")이 재현됨.
  베이스라인 에이전트는 실패 후 반복 → 안전 규칙(3회)으로 포기.

## R1 — HEAD beb79fb (마이그레이션 A~D, Python 강제 과제) — 수정 전

- 결과: **NOT-GREEN** — 같은 벽(`invalid or duplicate Python parameters`)에 좌초
- 타임라인: task 06:23:28 → final 06:29:35 (**6m07s**, R0 대비 −5m00s)
- 커밋된 잡: 2 (R0과 동일 단계까지)
- **leash 효과 확인**: 베이스라인은 3회 반복 후 11분에 포기, HEAD는 2연속 실패 규칙으로 6분에 명확한
  보고와 함께 중단 — 낭비 시간 절반 이하.
- **신규 마찰 클래스 확정**: 모델은 HEAD 가이드대로 소켓을 `{name,access,typeHint}`로 보냈는데
  (placeholder id·nickName 생략 — 드래프트 아티팩트로 확인), 호스트 `ValidatePythonSchema`와
  어댑터 `ValidateSchema`가 여전히 placeholder UUID 고유성·nickName·typeHint를 요구.
  가이드-검증기 모순 = 계약 마찰. → **라운드 간 수정**: 어댑터 정규화(id 생성, nickName←name,
  typeHint←object) + 호스트 검증은 이름만(위반 항목을 에러에 명시) + 가이드 명확화 (커밋: schema fix)

## R1b — HEAD 0083380 (+스키마 마찰 수정, Python 강제 과제)

- 결과: **NOT-GREEN** — 스키마 벽 통과(거부 0), 다음 단계(소스 작성)에서 신규 마찰
- 타임라인: task 06:36:02 → final 06:39:46 (**3m44s**)
- 잡: create+sliders 커밋 2 + 소스 잡 1 **RecoveryRequired**
  (`Python source changed after the request snapshot`)
- **신규 마찰 클래스**: `expectedSourceSha256` — 신규 컴포넌트의 시드 템플릿 해시를 모델이 알 수 없는데
  페이로드 내 해시 가드가 강제(엔벨로프 fingerprint 체인과 중복 가드). 에이전트는 RecoveryRequired
  안전 규칙대로 즉시 중단(올바른 행동).
- → **라운드 간 수정**: 어댑터가 `expectedSourceSha256:"gptino:auto"` 수용(체인이 동시수정 가드 유지),
  불일치 에러에 현재 SHA를 실어 레시피화, 가이드 갱신.

## R1c — HEAD (스키마+소스SHA 마찰 수정 포함, Python 강제 과제) ✅

- 결과: **GREEN (과제 완주)** — 스크립트 1, 와이어 9, 출력 데이터 76 (포인트 25 + 닫힌 Brep 기둥 25)
- 타임라인: task 06:43:51 → **canvas-green 06:48:12 (4m21s)** → final 06:48:15
- 잡: **5개 전부 Committed, 거부 0, RecoveryRequired 0**
  1. create 컴포넌트+슬라이더 / 2. 슬라이더 값 / 3. **소스+스키마+실행 (한 ChangeSet — 새 4단계 체인 그대로)**
  / 4. 배선 9개 / 5. 실행+검증
- 에이전트가 새 계약을 그대로 탐: snapshot_read 왕복 없음(committed.sockets 사용), wait=true 체이닝,
  predicate 생략(서버 기본).

**R0 vs R1c (같은 과제·같은 프롬프트·같은 모델)**: 실패(11m07s, 거부 3, 좌초) → **성공(4m21s, 거부 0)**.

## R2 — HEAD (언어 기본값 = C#) — GUID 수정 전

- 결과: **NOT-GREEN** — C# GUID 낙제 발견 (이 라운드의 목적 중 하나였던 검증 항목)
- 타임라인: task 06:58:45 → final 07:02:58 (**4m13s**, leash로 조기 중단)
- 에이전트 행동이 인상적: 가이드의 `ae3b6678` proxy로 create 커밋 → 스크립트 op 거부
  ("not a supported Rhino 8 script component") → **스스로 component_catalog 검색 → 진짜 C# Script
  (`b6ba1144-02d6-4a2d-b53c-ec62e290eeb7`) 발견·생성 커밋** → 어댑터가 그 GUID를 몰라 다시 거부 →
  2연속 실패 leash로 중단 + 정확한 브로커 메시지 보고.
- 판정: `ae3b6678`(정적 추출)은 IScriptComponent가 없는 유사 컴포넌트. 진짜 GUID는 에이전트가
  라이브로 검증해 준 `b6ba1144-...`. → **라운드 간 수정**: 어댑터 상수·가이드 4곳 교체.

## R2b — HEAD 5737f1e (진짜 C# GUID, 언어 지시 없음 → 정책 기본 C#) ✅

- 결과: **GREEN** — csharp 스크립트 1, 와이어 9, 출력 데이터 76 (포인트 25 + Closed Brep 기둥 25)
- 타임라인: task 07:06:48 → **canvas-green 07:10:53 (4m05s)** — Python(R1c 4m21s)보다 빠름
- 잡: **5개 전부 Committed, 거부 0** — R1c와 동일한 4단계 체인, 언어만 C#
- **C# 파이프라인 전체 라이브 검증 완료**: b6ba1144 GUID create → `// #! csharp` 디렉티브 →
  IScriptComponent SetSource/ReBuild reflection → 스키마 정규화 → 실행 → 기하 검증.

## R3 — HEAD (의도적 버그 → 자가수리, 게이트 B) — 판별 확대 전

- 결과: **NOT-GREEN** — 버그 실행은 정확히 보고됐으나 자가수리 루프가 열리지 않음
- 에러 리포트 품질은 완벽: `The name 'missingOffset' does not exist in the current context [14:40]`
  (위치까지 포함된 Roslyn 진단이 diagnostics 파이프로 전달됨)
- 원인: 컴파일 에러가 **setComponentIo 응답**에 실려 나옴(스키마 쓰기가 솔브를 트리거) —
  `IsScriptContentOperation`이 SetComponentIo를 제외(감사 대기 항목)해서 eager throw →
  **RecoveryRequired** 막다른 길 → 에이전트가 규칙대로 중단.
- → **라운드 간 수정**: 판별을 python-state 계열 전체(source/execute/schema/typing)로 확대.
  Wireify op의 Error diagnostic은 전부 스크립트 내용(op 실패는 예외 경로로 별도 도착).
  회귀 테스트 추가.

## R3b — HEAD 0221c6c (판별 확대, 의도적 버그 → 자가수리) ✅ 게이트 B 통과

- 결과: **GREEN** — 9 GH_Point (0,0,0)~(4000,4000,0), 런타임 에러 0
- 타임라인: task 07:19:03 → **canvas-green 07:22:38 (3m35s)**
- 자가수리 루프 실증: 잡 3 버그 실행 → **Failed** + diagnostics에 두 op의 python_error
  (위치 [14:33] 포함) → 잡 4 소스 수정 재제출 **즉시 Committed** (레저 갱신으로 stale-block 없음)
  → 배선 → 실행 검증 → 라벨 폴리시. **RecoveryRequired 0.**

## 최종 비교표

| 라운드 | 빌드 | 과제 | 결과 | task→green | 잡 커밋/거부 | 비고 |
|---|---|---|---|---|---|---|
| R0 | 761fa4d (전) | Python | ❌ 실패 | — (11m07s에 포기) | 2 / 3 거부 | IO 스키마 벽, 좌초 |
| R1 | HEAD(A~D) | Python | ❌ 실패 | — (6m07s 중단) | 2 / 다수 거부 | 같은 벽, leash로 조기 중단 |
| R1b | +스키마 정규화 | Python | ❌ 실패 | — (3m44s 중단) | 2+1 RecReq | 소스 SHA 벽 |
| R1c | +소스SHA auto | Python | ✅ **GREEN** | **4m21s** | **5 / 0** | 25pt+25기둥 |
| R2 | 〃 | C# 기본 | ❌ 실패 | — (4m13s 중단) | 3 / 2 거부 | 가짜 C# GUID 적발 |
| R2b | +진짜 GUID | C# 기본 | ✅ **GREEN** | **4m05s** | **5 / 0** | C# 전 파이프라인 검증 |
| R3 | 〃 | 버그→수리 | ❌ 중단 | — | 3+1 RecReq | 스키마 op 에러 dead-end |
| R3b | +판별 확대 | 버그→수리 | ✅ **GREEN** | **3m35s** | **6 / 0 (+의도적 Failed 1)** | 자가수리 루프 실증 |

## 판정

1. **게이트 A+C (관측·프로토콜 다이어트)**: 통과. 그린 라운드들은 snapshot_read 왕복 없이
   committed.sockets/outputs로 체이닝했고 predicate·fingerprint를 손으로 만들지 않았다.
   같은 과제에서 베이스라인 실패(11m) → 4m21s 완주.
2. **게이트 B (반복 가능한 실패)**: 통과 (R3b). 에러는 여전히 커밋을 막되(잡 3 Failed, 히스토리
   클린) 진단+applied로 즉시 수리 루프가 돈다.
3. **게이트 D (C#)**: 통과 (R2b). C#이 Python보다 약간 빠르며(4m05s vs 4m21s), 언어 지시 없이
   정책이 작동. `.gh` 파일 재열기 시간 측정은 하니스 미구현으로 이연 — CPython 부팅/pip 소멸은
   구조적 근거(리서치)로 뒷받침, 실측은 후속.
4. **벤치마크 루프의 가치 재확인**: 라이브 라운드 4개가 각각 코드 수정 하나씩을 산출했다
   (스키마 정규화 → 소스SHA auto → 진짜 C# GUID → 판별 확대). 전부 "모델이 알 수 없는 것을
   요구하는 검증기" 클래스였고, 서버가 채우는 방향으로 소거됐다.

남은 항목: .gh 재열기 시간 실측(하니스에 SaveAs 자동화 필요), 멀티세션 동시 라운드,
composite op 판단(현 4단계 체인이 거부 0으로 돌므로 당분간 불필요 판정).
