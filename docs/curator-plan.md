# Rhino Curator 비서 + 데이터 플로우 뷰 — 개발 계획 (2026-07-30)

2026-07-29~30 사용자 논의로 확정된 방향. 근거: 코드베이스 검증(UI/백엔드/문서 리더 3),
설계 패널(탭 옹호/세션-role 옹호/데이터뷰 설계/기술 회의론 4-agent), 기존 Rhino MCP 소스
조사(mcneel/RhinoMCP, jingcheng-chen/rhinomcp, 4kk11/RhinoMCPServer, reer-ide/rhino_mcp).

## 확정 결정

- **curator 비서 = UI는 별도 탭, 실체는 세션.** 여기서 "탭"은 **기존 GPTino 패널(Rhino
  도킹 WebView) 안의 뷰 전환**이다 — Rhino 패널이 하나 더 생기는 것이 아니라, 지금
  패널 헤더 바로 아래에 [Model | 비서] 전환 바가 붙고 같은 화면 안에서 뷰가 바뀐다.
  전환해도 연결·세션·상태는 그대로다(하나의 RuntimeState 스냅샷을 두 뷰가 다르게
  투영할 뿐). 탭은 표현 계층(스킨)이고, 실제 동작은 다른 세션 카드와 똑같이 curator
  role 세션이 단일 작성자 브로커를 통과한다. 브로커 게이트·fingerprint CAS·acceptance
  predicate·undo record·managed history를 전부 그대로 상속한다.
- **기본 제공 세션**: Rhino 문서(파일 페어)당 curator 세션 1개. 삭제 불가, 역할 고정,
  GH doc 바인딩 없음(`target:"rhino"`), 우선순위는 드래그 목록 밖(기본 최하위 +
  작업별 "run next" 부스트). UI에는 항상 존재하는 것처럼 보이되 세션 레코드/codex
  스레드는 첫 메시지에 lazy 생성.
- **데이터 플로우 뷰**(GH가 Rhino에서 뭘 참조하고 뭘 bake하는지)는 탭 구축과 동시 진행.
  별도 탭이 아니라 SessionCanvas의 토글형 데이터 레이어.
- **역할 분담 원칙** (하우스룰 편입 예정): *계속 만질 파라메트릭 작업 → GH 세션 /
  한 번 실행하고 끝나는 배치·정리 → curator.* 축은 "모델링 vs 정리"가 아니라
  "살아있는 definition vs 일회성 배치"다 — 블록 scatter는 모델링이지만 one-shot이라
  curator 소관. (roadmap #1의 컴포넌트-vs-라이브러리 판단 기준과 대칭.)

## 왜 이 모양인가

- **쓰기는 분리될 수 없다.** curator의 purge가 GH 파라미터가 라이브 참조 중인 객체를
  지우는 것은 실제 세션 간 충돌이고, 이를 시각화하는 것이 캔버스(충돌 아크·큐 칩·commit
  와이어)다. 탭이 쓰기 경로까지 분리하면 큐/writer 상태를 두 번째 표면에 복제(=문서에서
  거부한 "no second dashboard")하거나 숨기게 된다.
- **탐지는 서버 결정론, 모델은 triage만.** near-miss/중복/junk의 *발견*은 서버 audit
  op이 결정론적으로 계산하고, 모델은 발견 목록의 분류·설명·수선 전략 선택만 한다.
  (roadmap #2 "모델 자기신고 금지", #6 "좌표는 서버가 계산"과 동일 원칙.)
- **빈 자리가 확인됐다.** MCP 조사 결과 McNeel 공식조차 레이어 생성/삭제/이동·블록·
  purge·정리·수선의 typed tool이 전무하고 전부 run_python/run_command에 위임한다
  (공식 rhino-organizer 에이전트 프롬프트: "벌크 재구성은 run_python을 써라").
  jingcheng-chen도 레이어 3종 외에는 동일하며, delete_layer가 `Delete()` 반환값을
  무시하고 무조건 성공을 보고한다(거짓성공). **typed + 검증된 문서 정리가 신뢰성 계층의
  자연 확장이자 경쟁 공백이다.**

## 사용자 체감 기능 (완성 시)

1. **문서 검진**: "이 문서 검진해줘" → 서버가 계산한 보고 카드 — 안 만나는 커브 끝점
   N쌍(간격 실측치), SelDup이 못 잡는 근사 중복 후보 M쌍, 미사용 블록 정의/빈 레이어/
   bad object 목록, 레이어 스키마 위반. 각 항목 [Rhino에서 하이라이트] 가능.
2. **승인 후 수선**: 카드에서 체크박스로 골라 Approve → gap join, 중복 삭제, purge,
   레이어 재배치가 fingerprint 고정 + predicate 검증으로 실행. 결과는 "검증된 수치"로
   보고(모델 주장이 아니라 서버 실측). Ctrl+Z로 되돌릴 수 있음.
3. **일회성 배치**: "선택한 커브 따라 이 블록 뿌려줘", "이 커브들 정리해줘" 같은
   파괴적 one-shot 작업을 GH definition 없이 처리.
4. **데이터 관리**: 캔버스에서 Rhino↔GH 참조/bake 흐름이 엣지로 보이고, **삭제된 Rhino
   객체를 가리키는 GH 참조(끊어진 참조)가 경고로 표시**된다 — 현재는 조용히 빈 출력을
   내는 침묵 실패.
5. **사람 지오메트리 보호**: GPTino가 만들지 않은 객체(provenance 유저스트링 없음)에
   대한 파괴적 op는 승인 grant 없이는 브로커가 거부 — "human wins"의 첫 집행 코드.
6. **원터치 버튼**: 자주 쓰는 정리 작업(검진·purge·스키마 적용…)이 비서 탭 상단에
   스트림덱처럼 버튼으로 놓이고, 자주 쓰는 요청을 직접 버튼으로 저장할 수 있다.

---

## 단계별 계획

### Phase 0 — 계약 정리 (선행 필수, 소)

**체감**: 없음(기반). **내용**:
- role↔mode 얽힘 해소: 현재 `PUT /sessions/{id}/mode`가 role을 덮어쓰고(plan→planner,
  auto→modeler, `Program.cs:357-363`), `RuntimeStateProjector`가 role에서 mode를 역산한다.
  mode/role을 직교 컬럼으로 분리, 기존 planner 행은 role=modeler+mode=plan으로 마이그레이션.
  role 어휘: `modeler | curator`(+기존 read-only 게이트 유지).
- role을 UI 계약에 노출: `GptinoSession`(types.ts)에 role 추가, projector에 투영,
  `client.ts:155-167`의 하드코딩 `role:"modeler"` 파라미터화.
- 이걸 안 하면 curator 세션에 plan 토글 시 curator가 조용히 지워진다.

### Phase 1 — 데이터 플로우 읽기 + 캔버스 데이터 레이어 (중)

**체감**: 참조/bake 가시성 + 끊어진 참조 경고. **내용**:
- `canvas.listReferencedRhinoIds` (Cordyceps read): 열린 GH doc의 파라미터가 들고 있는
  ReferenceID(Rhino GUID) 열거. 현재는 쓰기 경로(`referenceRhinoObjects`)만 있고 열람이 없다.
  **curator purge 가드의 선행 조건이기도 하다.**
- `data.flowSummary`: doc별 {referenceCount, missingReferenceCount, bakeCount, observedAt,
  revision} 집계를 RuntimeState에 실어 기존 SSE로 push — 동기화할 두 번째 데이터 경로를
  만들지 않는다. 캐시 키: (docKey의 canvasRevision, Rhino doc modified serial).
- `data.flowDetail` (브로커 read op, 패널 GET + gptino_v1 read 툴 겸용): 파라미터별 참조
  목록(존재 여부·레이어), bake family 목록.
- bake 귀속: typed bake 경로와 bake_manager.py에 `GPTino.SourceDocKey` 유저스트링 스탬프
  추가. 레거시 bake는 추측 귀속 금지 — "unattributed" 버킷으로 정직하게 표시.
- UI: ~~캔버스 툴바 "Data" 토글 + 하단 드로어~~ → **(2026-08-04 갱신, 사용자 지시)**
  데이터는 **model / curator / data 3탭 위계의 독립 탭**이다. Model 탭에는 doc 노드의
  압축 칩(`⇢12 ⇠38 · 2!`, `missingReferenceCount>0`이면 경고색)만 남는다 — 끊어진 참조는
  조용히 빈 데이터를 뱉으므로 수동적 신뢰성 신호로서 자리를 지키고, 클릭하면 Data 탭으로
  넘어간다. 파라미터별 참조 목록·bake family·unattributed 버킷은 Data 탭 본문이며
  "as of r{N}" 스탬프 필수. 토글·아크·드로어는 제거(탭 위에 떠서 curator 탭까지 덮었다).
  스캔 중인 doc은 0이 아니라 "scanning"으로(데이터 부재≠참조 부재).
- 위험도 낮음(전부 읽기 전용). 이 단계만으로도 독립적 가치가 있다.

### Phase 2 — curator 세션 + 탭 셸 + 문서 검진 (대)

**체감**: 비서 탭에서 대화 + 읽기 전용 검진 보고서. **내용**:
- `assets/instructions/curator.md`: 역할 정의 — Rhino 문서 위생·일회성 배치 전담,
  파라메트릭 모델링 요청은 GH 세션으로 리다이렉트. plan 모드가 계획한 지시문 주입
  패턴 그대로(리빌드 없이 실험 가능).
- 상주 세션: 확정 결정대로(삭제 불가·역할 고정·최하위 우선순위·lazy 생성).
  ~~role=curator에 high-assurance 라우팅 floor 적용~~ → **(2026-07-30 갱신)** 어댑티브
  라우팅이 제거되고(#48, c75c340·ba9fed1) 세션 reasoning effort를 직접 쓰는 체제로
  바뀌었다. curator는 **세션 생성 시 기본 effort/모델을 상향 고정**하는 것으로 같은
  목적을 달성한다 — 파괴적 작업 전담 세션이므로 세션 수준 고정이 키워드 스니핑보다
  애초에 정확하다.
- `rhino.audit` (읽기 전용, 공유 read 게이트 — rhino_list와 같은 클래스):
  `{kind, tolerance?, bandFactor?, layerScope?, typeScope?, cursor?, limit≤100}` →
  `{findings[{findingId, objectIds, fingerprints, measure, proposedFixes}], docTolerance,
  docUnits, cursor?, truncated}`.
  - `nearMissEndpoints`: 열린 커브 끝점 RTree 검색, (docTolerance, k×tol] 밴드. GUID 정렬로
    결정론 보장. T-junction(끝점-커브중간)은 별도 kind로 후속.
  - `nearDuplicates`: bbox 프리필터 → 커브는 `GetDistancesBetweenCurves`, brep/mesh는
    정점 샘플 편차. **위치 일치 중복만** — 회전/미러 불변 탐지는 비범위.
    GH 참조 여부를 후보마다 표시("referenced by GH — 삭제 시 hydration 끊김").
  - `purgeCandidates`: 미사용 블록 정의(`InstanceDefinition.InUse` 고정점 반복), 빈 레이어,
    미사용 dimstyle, bad object(`IsValidWithLog`).
  - `layerSchema`: 스키마 파일 대비 diff (Phase 5에서 수선 연결).
  - 모든 finding은 fingerprint를 동봉 — 이후 수선 ChangeSet이 "감사한 그 상태"에 CAS로
    고정된다. tolerance·units를 항상 결과에 명시(같은 값이 predicate에도 쓰여야 함).
  - UI 스레드 인질 방지: 커서 페이징 + 청크당 시간 예산(45초 브리지 포기 모드 회피).
- **기능 버튼 줄 (스트림덱, 사용자 확정)**: 비서 탭 상단에 프리셋 버튼 줄 —
  검진 · gap 수선 · 중복 정리 · Purge · 레이어 스키마 · 배치…. 버튼 클릭은 채팅 타이핑이
  아니라 해당 작업 카드를 파라미터 프리필 상태로 여는 것(→ 승인 카드와 같은 카드
  시스템). 채팅은 자유형 요청용으로 병존. 기능 발견성 문제("role 드롭다운으로는 아무도
  중복 탐지 기능을 못 배운다")의 해법.
  - **사용자 정의 버튼**: 자주 쓰는 요청을 "버튼으로 저장" — 프로젝트 컨텍스트 폴더
    (LocalAppData)에 저장, 프로젝트→개인 스코프는 roadmap #4 템플릿 스코프 모델과 정렬.
    사용 빈도 기반 자동 제안은 roadmap #5 사용 데이터 파이프라인과의 후속 접점.
  - 버튼의 실체는 curator 세션에 보내는 사전 구성된 턴(작업 kind + 파라미터)이다 —
    브로커 우회 실행 버튼이 아니다. 파괴적 작업은 버튼으로 시작해도 동일하게
    audit→카드→grant 경로를 지난다.
- **Named scope 세트 (사용자 요청의 변형, 2026-07-30 추가)**: Rhino 네이티브 Named
  Selection은 RhinoCommon 공개 API가 없다(Rhino 7에서 도입됐지만 2024-08 기준 미공개,
  McNeel "v9 예정" 답변 상태, RH-57938; 저장은 객체 UserData의 비공개 포맷). 비공개
  포맷 파싱은 포맷 변경에 취약해 직접 통합은 보류하고, **기존 유저스트링 인프라로 자체
  세트를 구현**한다: `GPTino.NamedSet:<name>` 스탬프 — 멤버십 부여는 기존
  UpdateRhinoAttributes(→rhino.upsert) 재사용이라 신규 mutation 불필요, rhino_list에
  유저스트링 필터만 확장. 용도: audit/수선 **범위 지정**("'facade' 세트만 검진"),
  GH 참조 입력("'facade' 세트를 GH로 참조해줘"), 데이터 뷰에서 삭제된 멤버 표시
  (끊어진 참조와 동일 패턴). 네이티브 API가 공개되면 그때 동기화를 후속으로.
- 탭 셸(UI 스킨 체크리스트):
  - **탭 위계 = model / curator / data** (2026-08-04 사용자 지시). 각 탭이 정확히 하나를
    소유한다: 모델링 세션 / 문서 관리 / 참조·bake 원장. 한 탭에만 의미 있는 툴바 버튼
    (Graph, + Session)은 그 탭에서만 렌더 — 다른 탭에서 보이면 보이지 않는 상태를 바꾼다.
  - 헤더·에러/pause 배너·conflict drawer는 탭 바 **위**에서 공유. 헤더에 writer 칩
    ("writer: curator → purge (r124)") 추가.
  - curator 탭에 브로커 큐 상태 표시("writer 대기 #2") — GH 세션이 lease를 잡고 있을 때
    탭이 먹통으로 보이면 안 된다.
  - Model 탭 캔버스: curator 활동/대기 시 고스트 노드(큐 시각화 진실성). `deriveGraph`에
    role 필터.
  - 탭 unread 배지(기존 unseen 메커니즘 재사용), toast 딥링크 탭 인식.
  - ChatPane draft를 `key={session.id}`로 격리(현재 세션 간 새는 기존 버그).
  - 선택 컨텍스트 tab-aware: 어느 탭에 있든 Rhino 선택이 해당 세션 scope로.
  - `?demo=1` mock에 curator 세션 + 검진 보고 픽스처(패널 검증 루프 유지).

### Phase 3 — 승인 카드 + provenance 정책 + 첫 수선 (대)

**체감**: gap 수선과 중복 정리가 실제로 실행됨. **내용**:
- **승인 카드 = Plan 모드 Approve 카드와 동일 컴포넌트로 1회 구축.** audit 보고를 카드로
  렌더(그룹 체크박스, tolerance 등 파라미터 편집, [Rhino에서 하이라이트], Apply/Revise).
  Plan 모드는 이 컴포넌트에 "계획 산출물"을 얹는 것으로 승인 플로를 얻는다(modes.md 격차 해소).
- **provenance default-deny (브로커 검증 계층)**: `GPTino.LogicalEntityId`도
  `gptino_bake_family`도 없는 객체(=사람이 만든 것)에 대한 delete/modify/transform/
  moveToLayer는 ChangeSet에 `approvalGrantId`가 없으면 거부. grant는 승인 카드 클릭 시
  발급되고 **보여준 findingId+fingerprint 집합에 바인딩**(approve-what-you-saw, TOCTOU 안전).
  자율 기본값: GPTino 생성 객체 + 명시적 선택 객체는 grant 없이 가능(autonomy-by-default 유지).
- `rhino.fixEndpointPair`: `{findingId, objA/endA, objB/endB, expectedFingerprintA/B,
  strategy: setEndPoint | extendToIntersection | averageMove}` — 전략은 전역 기본값 금지,
  finding별 모델/사용자 선택. 기본 predicate `endpointsCoincident`(구현: 복제본 2개
  `JoinCurves` 결과가 정확히 1개 — 부작용 없고 사용자가 원하는 "joinable"과 일치).
  주의: `SetStartPoint/SetEndPoint`는 커브 타입에 따라 미구현/NURBS화(호의 반지름 의도
  파괴) — 전략별 지원 타입을 어댑터에서 검사.
- **중복 삭제는 신규 op 불필요** — 기존 `deleteRhinoObject`(expectedFingerprint 필수)
  재사용. victim 선택은 자동화 금지(생성 시각 신뢰 불가, design-option 스택은 의도적
  near-copy) — 항상 카드에서 사람이 고른다.
- 배치 규칙: ChangeSet당 ≤20개 청크(preflight가 all-or-nothing이므로 stale 1건이 청크만
  죽이게), 청크별 committed/stale 보고 — 수선 중 사용자 개입 시 "이 청크 스킵, 재감사"로
  강등(전체 Blocked 금지).
- undo: v1은 기존 per-op undo record 유지(50건 수선=Ctrl+Z 50번)를 **문서화된 한계**로
  수용. 잡 단위 단일 undo는 BeginUndoRecord 중첩(반환 0 → 기존 op 하드에러) 계약을
  건드리는 별도 리팩터링으로 이연. (McNeel TurnUndoCheckpoint가 선례 — 참조 섹션.)

### Phase 4 — purge + bad object 격리 (중)

**체감**: "미사용 블록/빈 레이어/bad object 정리해줘". **내용**:
- `rhino.purgeTableEntries {entries[{table: block|dimStyle|linetype|material, id}]}`:
  실행 시점에 미사용 재검증 후 삭제. 기본 predicate `tableEntryAbsent{table,id}`
  (id 기반 절대 — count 기반은 동시 사용자 편집과 경합). operation-contract에 예약만
  돼 있던 rhinoLayer/rhinoMaterial/rhinoLinetype 리소스 kind가 처음 실체화된다.
- `rhino.moveObjectsToLayer {items[{objectId, expectedFingerprint}], targetLayerId}`:
  객체 속성 변경이라 레이어 fingerprint 확장 없이 가능(대상 레이어는 기존 ensureLayer로
  보장). predicate `objectOnLayer`. 배치 결과가 per-object afterFingerprint를 반환하는
  목록형 브리지 메시지 신설.
- **bad object는 삭제하지 않는다** — `GPTino::Quarantine` 레이어로 격리(수리 가능성
  실재, 속성 수준에서 전부 가역). moveObjectsToLayer가 그 수단.
- 주의: 레이어 이동은 객체 fingerprint(attributesJson 포함) 대량 재작성 이벤트 —
  다른 세션 ledger stale + roadmap #5 암묵 신호("에이전트 후 사람 수정") 오염.
  ledger 갱신에 curator 귀속 마커를 남겨 신호 파이프라인에서 구분한다.

### Phase 5 — 레이어 스키마 (중~대)

**체감**: "이 레이어 스키마대로 문서 정리해줘". **내용**:
- `rhino.listLayers`: 전체 테이블 {id, fullPath, parent, color, visible, locked, material,
  linetype, objectCount, fingerprint} + 테이블 fingerprint. 레이어 fingerprint를
  visible/locked/material/linetype까지 확장(현재 id/path/parent/color만 — 이것이
  UpdateRhinoLayer가 fail-closed였던 이유. 확장 후 presence/absence 증명 가능 →
  예약 해제 조건 충족).
- predicates `layerExists / layerAbsent` 추가.
- `rhino.updateLayer {layerId, expectedLayerFingerprint, color?, visible?, locked?}`:
  **v1은 rename/re-parent 제외** — 둘 다 하위 레이어 FullPath 전체 재작성이라 GH
  Geometry Pipeline 이름 필터·레이어 경로 문자열 스크립트를 "에러 없이 빈 출력"으로
  깨뜨린다. rename은 영향 분석(경로 참조 스캔)과 함께 v2.
- `rhino.deleteLayer {layerId, expectedLayerFingerprint}`: 빈 레이어 + 자식 없음 +
  current 아님일 때만.
- 스키마 적용 플로: `audit(kind=layerSchema)` → 카드 승인 → ensureLayer(부족한 레이어
  생성) + moveObjectsToLayer 배치. 스키마 정의는 프로젝트 컨텍스트 폴더(LocalAppData)의
  파일로 — rules.md와 같은 위치.
- **Layer State (사용자 요청, 2026-07-30 추가)**: `RhinoDoc.NamedLayerStates` 공식
  API(Save/Restore/Delete/FindName/Rename/Import + LayerStateSettings로 복원할 속성
  선택)를 typed op으로 노출 — `rhino.listLayerStates / saveLayerState / restoreLayerState`.
  두 용도: (a) **안전장치** — 레이어 재편·스키마 적용 전 자동 스냅샷
  ("GPTino: before-schema") 저장, 실패·불만 시 원클릭 복원 카드; (b) **기능** —
  "작업용 상태 / 발표용 상태" 같은 프리셋 버튼과 결합. 복원 검증은 listLayers 재열람으로
  저장분과 대조(결정론적). 복원 = 레이어 속성 대량 변경이므로 레이어 fingerprint 재작성
  이벤트로 취급(객체 fingerprint는 무관 — 객체 속성은 건드리지 않음).

### Phase 6 — 블록 인스턴스 + 일회성 배치 (중)

**체감**: "선택한 커브 따라 이 블록 뿌려줘". **내용**:
- `rhino.createInstance {objectId, logicalEntityId, definitionId, matrix, attributes?}`:
  `ObjectTable.AddInstanceObject` 사용(기존 CreateTransform의 행렬 검증 재사용).
  typed 경로의 능력 구멍 — `createRhinoPrimitive`에 인스턴스 개념이 없고, upsert의
  GeometryBase JSON 경로가 InstanceReference를 처리한다는 증거 없음.
- scatter 플로: curator 턴이 배치 좌표를 계산(스크립트 아닌 서버/모델 계산 + 승인 카드
  프리뷰 카운트) → createInstance 청크 배치 → objectExists per-instance.
- 여기까지 오면 "빠른 파괴적 모델링" 요구(커브 정리 포함)가 typed 경로로 전부 커버된다.

---

## Op 계약 요약

| 구분 | 이름 | Phase | 비고 |
|---|---|---|---|
| read | `canvas.listReferencedRhinoIds` | 1 | purge 가드 겸 데이터 뷰 소스 |
| read | `data.flowSummary` / `data.flowDetail` | 1 | SSE 편승 / 드로어+에이전트 겸용 |
| read | `rhino.audit` (4 kind) | 2 | 커서 페이징, tolerance 명시, finding에 fingerprint 동봉 |
| read | `rhino.listLayers` | 5 | 레이어 fingerprint 확장 포함 |
| predicate | `endpointsCoincident` | 3 | JoinCurves-on-duplicates == 1 |
| predicate | `objectOnLayer` | 4 | |
| predicate | `tableEntryAbsent` | 4 | id 기반, count 기반 금지 |
| predicate | `layerExists` / `layerAbsent` | 5 | |
| mutation | `rhino.fixEndpointPair` | 3 | 전략 enum, 전역 기본값 금지 |
| mutation | (기존 `deleteRhinoObject` 재사용) | 3 | 중복 삭제 — 신규 op 불필요 |
| mutation | `rhino.moveObjectsToLayer` | 4 | 격리·스키마 적용의 공용 수단 |
| mutation | `rhino.purgeTableEntries` | 4 | 실행 시점 미사용 재검증 |
| mutation | `rhino.updateLayer` / `rhino.deleteLayer` | 5 | v1 rename/re-parent 제외 |
| mutation | `rhino.createInstance` | 6 | AddInstanceObject |
| read | `rhino.listLayerStates` | 5 | NamedLayerStates 공식 API |
| mutation | `rhino.saveLayerState` / `rhino.restoreLayerState` | 5 | 복원 검증 = listLayers 대조 |
| 재사용 | named scope 세트 (`GPTino.NamedSet:<name>` 유저스트링) | 2 | 기존 upsert/list 재사용, 신규 op 없음 |
| 정책 | provenance default-deny + approvalGrant | 3 | 첫 파괴적 op 전 필수 |

규모 추정(기술 검증 기준): 브로커/어댑터 ~2.5–4k LOC(테스트 포함, wireify 마이그레이션
1개 웨이브급) + 패널 UI(탭 셸·카드·데이터 레이어) 별도. op 1개당 7개 레이어 관통
(BridgeMessages → adapter interface → handler → RhinoSceneFoundationAdapter →
LiveDocumentBackend 검증/predicate 테이블 → DynamicToolSpecs → operation-contract.md → 테스트).

## 병행 개발 충돌 관리 (2026-07-30 추가)

이 계획을 작성하는 동안에도 다른 세션이 세션 경로·패널 파일을 변경했다(effort 슬라이더
e4f2b34, 어댑티브 라우팅 제거 c75c340·ba9fed1) — 위 라우팅 참조 1건이 실제로 stale이
되어 갱신했다. 병행 세션 환경에서 지킬 것:

**충돌 표면 (핫파일 — 다른 트랙도 상시 건드리는 공유 파일)**:
- 백엔드 세션 경로: `Program.cs`, `Runtime/SessionOrchestrator.cs`,
  `Runtime/RuntimeStateProjector.cs`, `Data/SessionStore.cs`, `Api/ApiModels.cs`
  → **Phase 0의 대상 파일들과 정확히 일치.**
- op 배관 테이블: `Codex/DynamicToolSpecs.cs`, `Runtime/LiveDocumentBackend.cs`(검증/
  predicate 테이블), 브리지 핸들러 스위치 — op을 추가하는 모든 트랙이 여기서 만난다.
- 패널: `App.tsx`, `ChatPane.tsx`, `types.ts`, `client.ts`, `mock.ts`, `styles.css`
  → Phase 2 탭 셸의 대상이자 최근 커밋이 건드린 파일들.

**전용 영역 (위험 낮음)**: `GPTino.Rhino` 어댑터(audit 분석기 포함 신규 파일),
`assets/instructions/curator.md`, 신규 UI 컴포넌트 파일, `GPTino.Grasshopper`의
listReferencedRhinoIds 추가분.

**수칙**:
1. **한 트리 한 세션** — 여러 dev 세션이 같은 체크아웃에서 코드를 만지면 git 충돌
   이전에 미커밋 변경을 서로 덮어쓴다(git은 경고조차 못 한다). 동시 진행 시 git
   worktree로 분리.
2. 착수 직전 `git pull` + `git log` 최신화, **계획 문서의 파일:라인·클래스 참조 재검증**
   (이 문서의 라우팅 건이 선례).
3. phase보다 작은 단위(op 하나, 계약 변경 하나)로 자주 랜딩 — 오래 사는 diff 금지.
4. 배관 테이블 추가는 항상 목록 끝에 append(텍스트 충돌 최소화).
5. Phase 0은 세션 경로가 조용한 시점에 신속 랜딩. Phase 2 탭 셸은 "뷰 전환만" 최소
   diff로 먼저 랜딩한 뒤 내용을 채운다.
6. 계획·문서 변경도 바로 커밋 — 미커밋 문서가 공유 트리에 머무는 것 자체가 충돌 표면.
7. structural-analysis(Karamba) 트랙과는 도메인이 분리(GH vs Rhino 어댑터)되어 위험이
   낮지만, 그 트랙이 op을 추가하면 같은 배관 테이블에서 만난다 — 상호 append 규칙 준수.

## 라이브 게이트 기록 (Phase 4/5, 2026-08-04 완료)

전부 dev-loop 실Rhino에서 curator 세션을 통해 구동. 통과 = 브로커 잡 상태와 서버가
재관측한 문서 상태 양쪽으로 확인(모델 자기보고 불인정).

| 게이트 | 결과 |
| --- | --- |
| `ensureRhinoLayer` 중첩 경로 `GPTino::Quarantine` | 통과 — 부모 `GPTino` 자동 생성, 최상위 `Quarantine` 오생성 없음 |
| `moveObjectsToLayer` 무승인 | 통과 — `approval_required` → **Failed**(RecoveryRequired 아님) |
| `moveObjectsToLayer` + grant | 통과 — Default 6→5, Quarantine 1 |
| `deleteRhinoLayer` 점유 레이어 | 통과 — `precondition_refused` → Failed |
| `saveRhinoLayerState` save→숨김→restore | 통과 — visible 복구 확인 |
| `deleteRhinoLayer` 진짜 빈 leaf | 통과 — committed |
| `purgeTableEntries` 미사용 블록 정의 | 통과 — committed, 재감사 시 finding 소멸 |
| `deleteRhinoLayer` **블록 멤버만 있는 레이어** | **최초 실패 → 수정 후 통과** (아래) |

**게이트가 잡은 결함**: 레이어 인구조사가 블록 정의 멤버를 센다는 주장이 거짓이었다.
직전 수정은 `ObjectEnumeratorSettings.IdefObjects`를 2패스로 토글했는데, 그때까지 게이트가
돌린 모든 씬의 InstanceDefinitions가 **0개**여서 2패스가 실제로 무언가를 반환한 적이 없었다.
정의가 실재하는 씬을 만들자 멤버만 있는 레이어가 여전히 "빈 leaf"로 집계됐다 —
deleteLayer의 공허 증명이 사용자에게 보이지 않는 블록 기하를 파괴하도록 승인할 수 있는
거짓 증명. `EnumerateLayerOccupants`가 `document.InstanceDefinitions`를 순회하며
`GetObjects()`를 합집합하도록 교체(모드 필터로 무력화 불가, 중첩 정의는 테이블 순회로 자동
커버). 재게이트에서 `BlockLib` objectCount=1, purgeCandidates 미보고, 삭제 거부 확인.

**부수 마찰**: payload guide가 op별 adapter owner를 명시하지 않아 `deleteRhinoLayer`를
Cordyceps로 선언했다가 왕복 1회 손실. owner는 kind에서 완전히 파생되므로 선언은 틀릴 수만
있다 — 두 사본에 매핑 1줄 추가(장기적으로는 서버 파생으로 필드 제거가 옳다).

**미검증**: 3탭 패널의 실제 렌더. 번들 배포는 확인(호스트가 Data 탭 코드를 서빙, 구
dataLayer 토글 0건)했으나 Chrome 확장이 연결되지 않아 화면 검증은 못 했다.

## 브리지 교착 수정 + GH 선택화 (2026-08-04)

**증상**: Grasshopper 정의가 나중에 열리면 그 쌍(pair) 타겟이 AgentHost에 끝내 등록되지
않았다. 브리지는 계속 "connected"를 보고하는데 그 타겟으로 나가는 op은 전부 무한 대기.

**원인 (라이브 게이트가 확정)**: 등록 프레임이 어느 가드에서 사라진 게 아니라 **파이프가
막혀 있었다.** Rhino 수신 루프는 프레임마다 `RhinoUiThreadDispatcher.InvokeAsync`를
await 했고, Grasshopper가 문서를 여는 동안 그 UI 스레드는 수십 초간 점유된다. 그래서
수신 루프가 파이프를 비우지 않았고 → 파이프가 차서 AgentHost의 다음 쓰기가 블록 →
AgentHost 수신 루프도 정지 → **양쪽이 살아있는 채 서로를 기다리는 교착.** 트레이스에서
두 프로세스의 기록이 같은 초에 멈추고 어느 쪽도 faulted가 아닌 것이 그 지문이었다.
GH 문서 오픈이 UI 스레드가 가장 바쁜 순간이라 하필 그 전환에서만 재현됐다.

**수정 3종**:
1. **읽기와 처리 분리** — 수신 루프는 요청을 bounded 큐(256)에 넣고 즉시 파이프로 복귀.
   **워커 1개**가 큐를 순서대로 소비하므로 처리 순서는 종전과 동일하고, 달라지는 건
   파이프가 계속 비워진다는 것뿐. 등록 확인 응답만 큐를 거치지 않고 수신 루프에서 즉시
   처리한다 — 큐에 넣으면 "등록이 기다리는 답장이 바쁜 작업 뒤에 줄 서는" 같은 교착이
   축소 재현된다.
2. **확인 기반 등록** (`DocumentRegistrationLedger`) — 등록은 이제 `document.registered`
   (또는 Error)를 correlationId로 기다린다. 응답이 없으면 최대 3회 재시도, 거부는 재시도
   대상이 아니라 표면화, 소진 시 `_bridgeStatus`로 사용자에게 보인다. 확인은
   (타겟키 + generation) 쌍으로 판단하고 연결 종료 시 전량 폐기. AgentHost와 프로토콜
   버전은 무변경 — 이미 보내던 응답을 받기 시작했을 뿐이다.
3. **GH 선택화 재활성** — Rhino 전용 타겟을 무조건 등록하고 은퇴시키지 않는다. AgentHost는
   기본 타겟으로 **Grasshopper를 가진 것을 우선**하고, 모호성은 GH 타겟 수로만 센다.

**라이브 게이트 (final1)**: 쌍 등록 `targets=2`, 확인 `attempt=1`(재시도 없음),
`grasshopperFile=scene.gh`, curator 세션 전환 후 생존, 그리고 **이전에 60초 무한 대기하던
`/layers`가 71ms**. `/audit` 63ms, `/data-flow` 58ms, `/dev/snapshot` 355ms — 캔버스
어댑터까지 살아있음이 쌍 결합의 확증. GH 오픈 이후에도 트레이스가 끊기지 않는다.

**GH 선택화보다 큰 소득**: 종전 구조는 **Rhino UI가 바쁘면 브리지 전체가 멈출 수 있었다.**
사용자가 무거운 GH 정의를 열거나 재계산시키는 것만으로도 같은 교착이 가능했으므로, 이
수정은 GH 선택화를 쓰지 않는 사용자에게도 필요했다.

**계측기도 고쳤다**: `DevelopmentDiagnosticTrace`가 레코드 1건당 파일 1개를 250ms 공유
뮤텍스로 쓰고 있어서, **경합이 심한 순간(=추적할 가치가 있는 순간)에 조용히 레코드를
버렸다.** 이 진단 과정에서 "로그가 없다"를 증거로 두 번 잘못 읽었다. 프로세스별 JSONL
append + 프로세스 내 직렬화로 교체(테스트: 320건 동시 기록 유실 0). 조용히 실패하던
지점 11곳이 이제 전부 기록을 남긴다.

## 실사용 모델 검증 (2026-08-04, `260803 main ms.3dm` 사본)

합성 dev 씬이 아니라 **사용자의 실제 프로덕션 모델**(33MB, 레이어 70, 최상위 객체 2484)에서
전 기능을 돌렸다. 모든 판정은 잡 상태 + 서버 재관측 양쪽으로 확인.

| 항목 | 결과 |
| --- | --- |
| 레이어 테이블 읽기 | 통과 — 70 레이어/2484 객체, 62ms |
| `purgeCandidates` | 통과 — 94ms, 실제 결함 3건 발견(깨진 Brep 2 + 미사용 블록 '1 02') |
| `saveRhinoLayerState` (70 레이어) | 통과 — committed |
| `ensureRhinoLayer` 중첩 | 통과 — `GPTino::Quarantine` committed |
| `deleteRhinoLayer` 점유 레이어 | 통과 — Failed, "still holds objects" |
| `moveObjectsToLayer` 무승인 (실제 사용자 기하) | 통과 — Failed, approval_required |
| grant 발급 후 재시도 | 통과 — committed, 격리 2건 |
| `purgeTableEntries` 실제 미사용 블록 | 통과 — committed |
| data-flow read | 통과 — 200, 참조/bake 0 (GH 빈 문서) |

**블록 멤버 인구조사 교차검증**: purge 후 총 객체가 2484 → **2477**로 정확히 7 감소했고,
이는 블록 '1 02'의 멤버 객체 수와 일치한다. 인구조사가 블록 멤버를 실제로 세고 있다는
독립적인 확인(합성 픽스처가 아닌 실데이터에서).

### 발견: 감사 2종이 실사용 모델에서 무력

`nearMissEndpoints`는 scanned **0**, `nearDuplicates`는 scanned **1**이었다. 버그가 아니라
**스코프 불일치**다 — 두 분석기의 타입 필터가 `ObjectType.Curve | ObjectType.Point`인데,
이 모델의 최상위 구성은 Brep 304 / Extrusion 13 / **InstanceReference 181** / Curve 2
(표본 500 기준)이다. 즉 사용자의 실제 작업물에서 오늘 가치를 내는 감사는 `purgeCandidates`
하나뿐이다.

두 갈래의 후속이 필요하다:
1. **솔리드 대상 확장** — Brep/Extrusion 근사 중복(같은 표현·같은 위치의 복제), 열린 Brep
   가장자리 near-miss. bbox 프리필터는 그대로 쓰되 비교 술어가 커브용과 다르다.
2. **블록 내부** — 최상위 181개가 InstanceReference라는 건 기하 상당수가 정의 안에 있다는
   뜻이다. 감사는 의도적으로 최상위만 본다(v1 결정). 실모델에서는 이 결정이 커버리지의
   대부분을 잘라낸다 — 정의 단위 감사(정의당 1회, 인스턴스 수와 무관)를 재검토할 것.

## 검증 방법

- 각 phase는 기존 벤치 루프(dev-mode 실Rhino 자율 측정)로 라이브 게이트 통과 후 다음 단계.
- audit 결정론 테스트: 같은 문서 두 번 스캔 = 동일 finding 목록(GUID 정렬).
- 수선 검증: predicate 통과 + 사후 재감사에서 해당 finding 소멸 확인(이중 확인).
- `?demo=1` mock 픽스처를 phase마다 갱신 — 패널 UI 검증 루프 유지.

## 구현 시 함정 (체크리스트)

- [ ] tolerance/units: `doc.ModelAbsoluteTolerance`를 읽는 코드가 현재 전무. audit과
  predicate가 **같은 값**을 쓰고 결과에 항상 명시할 것 (mm 문서 휴리스틱이 m 문서에서 참사).
- [ ] 근사 중복 오탐: bake_manager append 모드 = design-option 스택은 의도적 near-copy.
  자동 병합 금지, victim 자동 선택 금지.
- [ ] naive 격자 양자화 해시 단독 사용 금지(격자 경계에서 1e-7 차이가 다른 셀).
- [ ] UI 스레드: 모든 브리지 op이 UI 스레드 marshal — 전량 스캔은 청크 시간 예산 필수.
  RhinoDoc은 off-thread 읽기 불가("백그라운드로 돌리면 됨" 아님).
- [ ] rhino_list 500개 캡·전체 기하 JSON 직렬화 비용 — audit은 프리필터(bbox, 타입 스코프)
  + finding만 fingerprint(런당 ~100 캡).
- [ ] BeginUndoRecord 중첩 시 0 반환 → 기존 op은 하드에러. 잡 단위 undo는 의도적 이연.
- [ ] 레이어 이동 = 객체 fingerprint 대량 재작성 → 학습 신호 오염 방지용 curator 귀속 마커.
- [ ] GH 스크립트 컴포넌트 우회 금지(ActiveDoc·CAS 없음·검증 없음·GH doc 필요·캔버스에
  컴포넌트 잔류). curator 지시문에 명시.

## 기존 MCP에서 차용할 구현 디테일 (참조)

- **jingcheng-chen/rhinomcp**: dispatch 초크포인트에서 undo bracketing(핸들러 코드 0줄로
  전 mutating 커맨드가 named undo 1개); `_delta` perception envelope(뮤테이터 전후 GUID
  집합 diff → created/deleted id 목록 — purge 검증 보강에 적합); dry_run capability
  negotiation(구버전 플러그인이 실작업을 몰래 실행하는 것 방지 — 승인 카드 프리뷰와 동형);
  신생 객체만 `IsValidWithLog` 헬스체크. 반면교사: delete_layer 반환값 무시(거짓성공),
  "실패 스크립트 롤백" docstring 허위, ActiveDoc 신뢰.
- **mcneel/RhinoMCP**: per-doc 리스너 + doc 주입(ActiveDoc 불신 — 우리와 같은 결론);
  TurnUndoCheckpoint(턴 전체 1 undo, BeginUndoRecord 0 반환 처리 — 잡 단위 undo 리팩터링
  시 선례); set_selection "필터 미해결 시 무선택" 가드; typed error code envelope;
  빈 프러스텀 캡처 거부 + 메타데이터 반환.
- **4kk11/RhinoMCPServer**: full-path 레이어 생성(`"Parent::Child::Grandchild"`) 시그니처,
  annotation/dimension 툴(후속 후보).
- **reer-ide/rhino_mcp**: 생성 객체 short_id 유저스트링 + 뷰포트 주석으로 시각 식별 —
  audit 카드 [Rhino에서 하이라이트]에 응용.

## v1 비범위 (명시적 제외)

- 회전/미러 불변 중복 탐지(연구 과제 — 약속하면 정직성 실패 예약).
- 레이어 rename/re-parent(하위 FullPath 재작성 파급 — 영향 분석과 함께 v2).
- 자동 victim 선택(중복 병합은 항상 사람 triage).
- 잡 단위 단일 undo(undo 계약 리팩터링과 함께 후속).
- Rhino 네이티브 Named Selection 패널과의 상호운용(RhinoCommon 공개 API 부재, RH-57938 —
  공개 시 자체 named set과 동기화 검토).
- GH 캔버스 janitor(roadmap #6)는 이 플랜의 카드/승인 인프라를 재사용하되 별도 트랙.
