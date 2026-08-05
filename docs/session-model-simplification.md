# 세션 모델 단순화 — role/mode 제거와 goal 카드 (2026-08-05 사용자 확정)

**한 문장**: GPTino는 "말로 시키면 하는 하나의 것"이다. 세션에 붙은 역할·모드 개념을 전부
걷어내고, 능력은 스킬에서, 자율성은 판단에서, 성공 기준은 goal 카드의 검증 조건에서 온다.

## 확정 결정과 근거

| 항목 | 결정 | 근거 |
|---|---|---|
| curator role | **삭제** | role이 코드에서 가르는 것은 삭제불가·우선순위·지시문 주입·기본값 4가지뿐이고 **툴은 전혀 안 가름**(rhino_audit 등은 이미 전 세션 공용). 즉 기능이 아니라 프롬프트 포장지 |
| curator 탭·버튼 줄 | **삭제** (이주 아님) | 버튼은 "채팅으로 컨트롤한다"는 정체성을 깨뜨림 — 버튼으로 할 거면 라이노 툴바를 다시 만드는 것. 발견성은 **에이전트가 말로** 해결(관련 시점에 먼저 제안 / "뭘 할 수 있어?"에 답 / 인접 요청에 곁들이기) |
| plan/auto 토글 | **삭제** | 간단하면 자율, 애매하면 질문·계획은 모델이 판단할 일. 포커스 칩으로 질문 비용이 싸져서 사전 계획서의 효용이 줄었음. 대신 **"언제 먼저 물어야 하는가"(파괴적·비가역·범위 큰 작업)를 프롬프트에 명시** |
| read-only role | **삭제** | 자물쇠의 트리거가 UI면 크롬 금지 원칙 위반, 채팅이면 잠근 주체가 풀 수 있어 보장이 안 됨. 실제 보호는 브로커(사람 기하 기본거부 + 승인 grant + fingerprint CAS + undo)가 이미 수행. 진짜 격리가 필요하면 **파일 복사**가 답 |
| goal | **메타 프롬프팅 + 목표 카드로 강화** | GPTino의 차별점이 신뢰성 계층이고, 신뢰의 단위는 "무엇을 달성하면 성공인가". 개떡같은 요청 → 목표·검증기준·가정·범위밖 카드 → 사용자 확인 → 실행 → 그 기준으로 자기 채점 |
| Data 탭 | **유지** | role이 아니라 뷰 |

## 남는 것 (삭제 대상이 아님)

감사 엔진(RhinoSceneFoundationAdapter의 audit 계열), typed Rhino op(purge/layer/quarantine),
provenance default-deny + 승인 grant, fingerprint CAS, managed history/undo, 데이터 플로우 뷰.
**비싼 자산은 전부 role과 무관하게 이미 작동한다** — 이번 개편은 프롬프트/UI 계층만 건드린다.

## 진행 상태 (2026-08-05)

- ✅ **goal 카드** (`5e55768`) — goal_propose/goal_score 툴, goal_card 컬럼, 확정 카드가 매 턴
  주입, 증거 강제 자기채점, 패널 GoalCard 컴포넌트. 기존 GOAL 토글은 제거됨.
- ✅ **선행조건 A** (`3a82295`) — curator.md의 감사 규율(스캔0 정직성·tolerance 인계·격리·참조객체
  확인·GH스크립트 금지)을 house-rules로 병합. **curator.md는 이제 삭제 가능.**
- ✅ **선행조건 B** (`4393a5b`) — 승인 카드를 에이전트 주도로 전환(approval_request 툴,
  approval_card 컬럼, PUT /sessions/{id}/approval이 승인 항목만 grant 발급, 승인 블록 턴 주입,
  패널 ApprovalCard). **승인 UI가 curator 탭에서 독립했으므로 탭 삭제가 안전해짐.**
- ⬜ **다음: curator/role/mode 제거** — 아래 순서대로. 전수 접점은 조사 결과 기준:
  1. 서버 게이트: DynamicToolDispatcher의 IsPlanMode/IsReadOnlyRole 분기 + ProblemLog.RecordRoleDenial
  2. 지시문 주입: SessionOrchestrator의 curator 분기, CuratorInstructions.cs, assets/instructions/curator.md,
     InstructionAssetParityTests의 curator 검증
  3. 엔드포인트: PUT /sessions/{id}/mode, POST /sessions의 curator 거부, Program.cs 부팅 시 상주 curator 프로비저닝
  4. 투영: RuntimeStateProjector의 mode/role, ApiModels의 Mode/SetModeRequest/CreateSessionRequest.Role
  5. 스케줄러: LiveDocumentBackend의 curator 우선순위 제외
  6. SessionStore: 파킹/삭제가드/SetModeAsync/재정렬필터/NormalizeRoleAndMode 제거.
     **role 컬럼은 NOT NULL·DEFAULT 없음 → 컬럼 유지 + 상수 'modeler' 공급** (DROP 금지)
  7. 마이그레이션 3종: sort_order ≥1,000,000 복구 / curator 행을 일반 세션으로 흡수(이름·기록 보존) /
     기존 plan·read-only 세션에 "이제 쓰기 가능해졌다" 시스템 메시지 통보
  8. 패널: 탭 model|data 2개로, curator 리전·CuratorActions 삭제, ChatPane의 Plan/Auto·Shift+Tab·
     role 분기 삭제, types/useRuntime/client/mock/deriveGraph/NoGrasshopper/styles 정리
  9. 테스트·스크립트: CuratorSessionTests 삭제, SessionStoreTests의 planner/SetMode 케이스,
     DynamicToolDispatcherTests의 거부 케이스, smoke-agenthost.ps1의 role='planner', docs/modes.md 폐기
  10. 라이브 게이트: 감사→승인카드→grant→수정이 새 위치에서 끝까지 통과하는지 확인
- ⬜ **artifacts 프루닝** (26.4GB, dev-loop 런 1,234개) — dry-run 목록 승인 후 실행 + 자동 프루닝 추가

## 실행 순서

1. **goal 카드** — 신규 기능이라 기존 것을 안 깨뜨리고, "확인받고 진행" 흐름이 자리를 잡아야
   plan 모드를 안심하고 뺄 수 있음
2. **curator/role/mode 제거** — 감사가 지적한 20K자 모순이 문제 자체로 소멸
3. **artifacts 프루닝** (26.4GB, dev-loop 런 1,234개) — 독립 작업

## 이 개편이 해소하는 감사 지적

- HIGH "curator 세션이 자기 역할을 부정하는 모델링 프롬프트 20K자를 그대로 받는다" → 소멸
- HIGH "[[alt:]] 하우스룰이 배선되지 않은 기능을 지시" → 이미 지시문에서 제거(402ce57), 칩·파서는
  goal/알트 카드가 배선될 때 부활
- MEDIUM "프롬프트의 35%가 house-rules↔payload-guide 중복" → role 분기 작업 중 함께 정리 대상
