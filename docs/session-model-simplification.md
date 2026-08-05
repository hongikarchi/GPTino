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
- ✅ **curator/role/mode 제거** — 서버·패널·테스트·문서 전수 완료. 실제로 걷어낸 것:
  1. **서버 게이트**: `IsPlanMode`/`IsReadOnlyRole` 분기와 `ProblemLog.RecordRoleDenial` 삭제.
     쓰기 경로에 남은 게이트는 **일시정지 하나**뿐 (`WriteToolsAreGatedByPauseAlone` 테스트가 고정)
  2. **지시문 주입**: `CuratorInstructions.cs`·`assets/instructions/curator.md` 삭제,
     `roleInstructions` 파라미터를 `ICodexSessionClient`/`CodexAppServerClient`/orchestrator에서 제거
  3. **엔드포인트**: `PUT /sessions/{id}/mode`, `POST /approval-grants`, `GET /audit`,
     부팅 시 상주 curator 프로비저닝, `POST /sessions`의 curator 거부 — 전부 삭제
  4. **투영·모델**: `SessionRecord.Role/Mode`, `CreateSessionRequest.Role`, `SetModeRequest`,
     `RuntimeStateProjector.ProjectMode/ProjectRole` 삭제
  5. **SessionStore**: 파킹·삭제가드·`SetModeAsync`·재정렬필터·`NormalizeRoleAndMode` 제거.
     `role` 컬럼은 NOT NULL·DEFAULT 없음 → **컬럼 유지 + 상수 `'modeler'` 공급** (DROP 안 함),
     `mode` 컬럼은 더 이상 읽지도 쓰지도 않음
  6. **마이그레이션** `AbsorbRolesAndModesAsync` — 순서가 중요(앞 둘이 셋째가 지우는 값을 읽음):
     쓰기 못 하던 세션(plan/planner/read-only)에 **시스템 메시지로 통보** → 파킹된 curator를
     일반 순서 끝으로 복귀 → role을 상수로 붕괴. 멱등
  7. **패널**: 탭 `model|data` 2개, curator 리전·`CuratorActions`·`AuditCard` 삭제,
     ChatPane의 Plan/Auto 세그먼트·Shift+Tab·role 분기 삭제, `NoGrasshopper`의 "Go to Curator" 제거
  8. **테스트·스크립트·문서**: `CuratorSessionTests` 삭제, SessionStore/Dispatcher 케이스 재작성,
     `smoke-agenthost.ps1`의 `role='planner'` 제거, `docs/modes.md` 폐기,
     `docs/curator-plan.md`은 **기록으로 보존**(감사 엔진·typed op·데이터뷰의 설계 근거라서)
  - 검증: 서버 369/369, 패널 34/34 + typecheck + build 통과
- ✅ **라이브 게이트** (`abde571`) — `scripts/gate-approval.ps1`. 일반 세션에서 감사 → goal 카드 →
  확인 → 승인 카드 → grant → 수정 → 검증까지 실Rhino로 통과. 3건 중 **2건만 승인**해서 거부한 건이
  살아남는지까지 확인. 전제조건: `-SceneKind hygiene` 픽스처(끝점 갭 0.005/0.003mm + 근접중복
  0.0005mm를 tolerance 0.001mm에 고정). 게이트는 시작 시 픽스처가 실제로 findings를 내는지 먼저
  확인하고 아니면 throw — **빈 결과에 매기는 점수는 통과가 아니라 미실행**이기 때문.
  - 게이트가 잡아낸 결함 2건: ① 사용자가 고른 **선택지가 에이전트에 전달되지 않아** 이미 답한 질문을
    다시 물음 → `ApprovalCard.Choices` 저장 + 턴 주입 ② `rhino_list`가 **삭제된 객체를 계속 보고**
    → 감사와 같은 열거자로 통일
  - `GET /dev/audit` 복원(dev 전용): 제품 표면에서 뺀 건 맞지만, **에이전트에게 묻지 않고 주장을
    검증할 수단**까지 없애면 라이브 게이트가 채점을 못 한다
- ✅ **artifacts 프루닝** (`fdc7b4c`) — 26.57GB/318,145파일 → **0.88GB/2,307파일**.
  `scripts/prune-artifacts.ps1`(기본 dry-run), dev-loop이 매 실행 전 `-KeepRuns 10`으로 자동 정리.
  부수 발견: dev-loop이 **일회성 증거 디렉터리 안에서** `bench.gh` 템플릿을 찾고 있어 첫 정리에
  런처가 깨짐 → `scripts/fixtures/empty-definition.gh`로 이전

## 무엇이 사라지고 무엇이 남았나

**사라진 안전장치는 없다.** plan 모드와 read-only role이 막던 것은 `change_submit`/`arrange_layout`
단 둘이었고, 그 자리는 이미 더 정밀한 장치가 지키고 있다: provenance 기본거부(사람이 그린 기하는
승인 grant 없이 못 건드림), fingerprint CAS, managed history/undo, acceptance predicate.
plan 모드는 **세션 전체**를 막았고 승인 카드는 **항목 하나**를 연다 — 후자가 좁고 정확하다.

**대신 사라진 것**: 세션을 만들 때 역할을 고르는 결정, 모드 토글을 잊어서 생기는 오작동,
그리고 "이 세션은 뭘 할 수 있는가"라는 질문 자체. 세션은 이제 하나뿐이고, 능력은 스킬에서,
자율성은 판단에서, 성공 기준은 goal 카드에서 온다.

## 참고 — 터미널 codex 대비 체감 성능 차이 (구 modes.md에서 이관)

1. **sandbox=read-only + MCP 전부 차단**: 에이전트가 쓸 수 있는 건 gptino_v1 typed 툴뿐.
   터미널 세션의 파일 쓰기·임의 명령 실행 자유도가 없다 (의도된 제약).
2. **브리지 타임아웃과 UI 스레드 정체**: 무거운 GH solve가 Rhino UI 스레드를 점유하면 브리지 op가
   45초에서 포기되고, 에이전트에게는 툴 호출 "실패"로 보인다 — 중간 포기의 주요 원인.
3. **게이트 거부가 에러로 보임**: fingerprint 불일치/stale 거부는 신뢰성 계층의 정상 동작이지만
   모델에게는 실패 신호라 재시도 대신 포기를 고를 수 있다. 거부 메시지에 해결 지시문을 넣는 것이
   이 부분을 겨냥한다.

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
