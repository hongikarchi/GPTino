# GPTino Claude 백엔드 도입 계획 (Phase 0~5)

**작성일**: 2026-07-24 · **상태**: 계획 (미실행) · **근거**: 4-에이전트 read-only 코드 조사
(provider 배관 / 추상화 심 / Claude 클라이언트+MCP+auth / 검증+리스크)로 접점을 file:line까지 확정.

---

## 배경 & 확정 결정

- **목표**: 기존 Codex 옆에 **Claude 백엔드** 추가.
- **구동 방식 확정**: 사용자 **Claude Code 구독 CLI**로 구동. **API key / Claude Agent SDK 경로는 폐기**
  (Agent SDK는 API key 전용이라 구독 인증 불가; 구독 인증 vehicle은 `claude` CLI뿐).
- **백엔드는 세션 생성 시 고정**: 대화 저장소가 호환 안 됨 — Codex는 서버측 thread/rollout,
  Claude는 로컬 JSONL. 세션 내 모델 전환은 **같은 백엔드 안에서만** 매끄러움(model = 턴별 파라미터).
  교차-백엔드 전환은 "새 세션"이 기본; 도중 전환(대화 이식)은 별도 기능·불완전 충실도로 이연.
- **검증 원칙**: 게이트는 **외부 판정**(dev 엔드포인트 캔버스-그린) + **measure-first**
  (재스폰 방식 먼저, 지속 프로세스는 측정 후 승격).

## 아키텍처 사실 (조사로 확정)

- **게이트/브로커는 이미 백엔드-무관**: 쓰기 게이트·스케줄러·리스·멱등성·반환이 전부
  `SessionId`/`JobId` GUID로만 키잉. `GPTino.Core`에 Codex/Claude 참조 0.
  `DynamicToolDispatcher.DispatchAsync`는 `(namespace, tool, args, threadId)` 4-튜플만 소비하고 출처를 안 물음.
- **커플링은 가장자리 4곳뿐**: (a) 인바운드 와이어 파서(`DynamicToolCall.FromJson`, `item/tool/call`),
  (b) `codex_thread_id` 컬럼, (c) 아웃바운드 턴 러너(`SessionOrchestrator` + `ICodexSessionClient`),
  (d) 타임아웃 상수(25s/15s, Codex 30s 데드라인 연동).
- **Cordyceps/Wireify는 외부 의존성 아님**: `GPTino.CordycepsAdapter`/`GPTino.WireifyAdapter`는
  내부 이름(각각 MIT/Apache-2.0 출처 표기 재구현). `.references/wireify`는 gitignore·미빌드 참고자료.

## 전달 벡터 대칭 (왜 구현이 갈리나)

| | Codex (app-server) | Claude (구독 CLI) |
|---|---|---|
| 전송 | 헤드리스 stdio JSON-RPC (터미널 아님) | `claude -p --output-format stream-json` (재스폰) |
| 지시문 | `baseInstructions` **문자열 파라미터** | **CLAUDE.md 파일** (+ `--append-system-prompt`) |
| 툴 | `dynamicTools` **배열 파라미터** (MCP 일부러 끔) | **MCP 서버** (인라인 없음) |
| 대화 | 서버측 thread/rollout, `thread/resume` | 로컬 JSONL, `--resume <session-id>` |
| 프로세스 | 1 프로세스 N 스레드 멀티플렉싱 | 세션당 프로세스 |

**공유되는 것**: 오케스트레이터 세션/게이트 뼈대, `DispatchAsync` 4-튜플,
gptino_v1 규칙 문서(house-rules.md/payload-guide.md/skills — 백엔드-중립 markdown, `ThreadInstructions`의
sub-agent 한 문장만 Codex-특정).

---

## Phase 0 — CLI 스파이크 (제품 코드 0, 불확실성 검증)

**왜 먼저**: 구독 `claude` CLI에 load-bearing 미확인 7가지가 있음. 안 풀고 클라이언트를 짜면 헛수고.

검증 항목:
1. 플래그 표면: `-p`, `--output-format stream-json`, `--verbose`, `--resume <id>`, `--session-id`(‑p 사전생성?),
   `--mcp-config`, `--allowedTools`/`--disallowedTools`, `--permission-mode`, `--append-system-prompt`, `--model`, `--effort`.
2. stream-json 이벤트 스키마: `system/init`(`session_id`), `assistant`, 종단 `result`(`is_error`/`usage`).
3. 자격증명 저장 위치(파일 vs OS 키체인) → `ClaudeAuthProbe` 방식 결정.
4. Windows 실행 해석: `claude.cmd`(npm 심) 직접 `Process.Start` 가능? 아니면 네이티브 바이너리(`node_modules/@anthropic-ai/claude-code/**`)?
5. 헤드리스 신뢰/권한 부트스트랩: `-p`가 `permissions.allow`+`hasTrustDialogAccepted` 존중? 아니면 `--permission-mode`?
6. MCP 전송 호환: 헤드리스 `-p`의 MCP 클라이언트가 SDK `StreamableHttpServerTransport`(loopback HTTP)와 붙나.
7. 패키지 핀: `ModelContextProtocol.Core`(Wireify는 2.0.0-preview.1)가 AgentHost TFM에 맞나.

**게이트**: 7개 다 답 or 폴백 결정 → `docs/benchmarks/claude-cli-spike-*.md`.
**충돌**: 없음 — GPTino 소스/빌드/Rhino 무접촉, 외부 CLI 조사만.

## Phase 1 — Provider 배관 (Codex만으로도 지금 배포 가능)

**목표**: 모델 provider 표기 + 세션 backend 컬럼 + 드롭다운 필터를 전부 additive로. Codex-only에서 provider="codex"로 흐름.

작업:
- `ModelView`에 `Provider="codex"` (Api/ApiModels.cs:80-86), 채우는 곳 유일 `CodexAppServerClient.ListModelsAsync`(:116-123).
  패널 `ModelInfo.provider?`(ui/panel/src/types.ts:38-45) + `demoModels`(mock.ts:14-39).
- 드롭다운을 `session.backend`로 필터(ChatPane.tsx:430-448): `!m.provider || !session.backend || m.provider===session.backend`
  → codex-only에서 항등. optgroup은 Claude 이후.
- `sessions` 테이블 `backend TEXT NOT NULL DEFAULT 'codex'` (HasColumnAsync 마이그레이션 관용, SessionStore.cs:69-76).
  `SessionRecord.Backend`, `CreateSessionRequest.Backend="codex"`(ApiModels.cs:34-39), 생성 경로(Program.cs:266-276) param.
- `RuntimeStateProjector.cs:75` 하드코딩 `"codex"` → `session.Backend`.
  (참고: `session.backend`는 이미 SessionCanvas.tsx:89 툴팁이 읽음.)

**게이트**: 기존 DevLoop `orchestrator`/`mcp`/`full` 변경 없이 그린 + 신규 유닛(마이그레이션 idempotency,
provider camelCase 직렬화, 프로젝터 저장값 emit) + `?demo=1` 패널 확인. **충돌**: 코드 편집 O — dev 터미널과 겹치면 충돌.

## Phase 2 — 백엔드 추상화 심 (Codex-only 리팩터, 테스트 그린 유지)

**목표**: 오케스트레이터가 어느 백엔드든 구동 가능하게. Claude 미포함, 동작 불변 리팩터.

작업:
- **타입 중립화**: `CodexTurnReadResult/CodexTurnError/CodexAgentMessage/CodexProtocolException`
  (CodexAppServerClient.cs:1582-1630) → `AgentTurnReadResult` 등. `JsonElement CodexErrorInfo`는 write-only → 드롭 무료.
- **인터페이스 리네임** `ICodexSessionClient`→`IAgentSessionClient` (레이어링 말고 리네임).
- **컬럼 리네임** `codex_thread_id`→`external_conversation_id` (guarded RENAME COLUMN, UNIQUE 유지).
  접근자 `SetThreadIdAsync`/`FindSessionByThreadAsync`/`SessionRecord.CodexThreadId` 리네임.
- **per-backend resolver DI**: `IAgentBackend{Id,Client,Catalog}` + `IAgentBackendResolver{Resolve/TryResolve/All}`
  (keyed DI보다 나음). `ModelSelector`를 backendId별 캐시로. `SessionOrchestrator`가 턴마다 `_backends.Resolve(latest.Backend).Client`.
- **(권장, 최대 조각)** 알림 디코딩을 클라이언트로: 이벤트 `Func<string,JsonElement,Task>` → 타입드 `Func<AgentEvent,Task>`.
  `HandleNotificationAsync`(item/completed, turn/completed, phase)를 `CodexAppServerClient` 안으로.

**게이트**: 기존 테스트 전체가 **리네임-only 편집으로 그린** = 동작 불변 증명. 신규: resolver/마이그레이션 유닛.
**충돌**: 코드 편집 O.

## Phase 3 — Claude 백엔드 온라인 (핵심)

**목표**: `backend=claude` 고정 세션이 실제 캔버스를 그린. = Codex 프로세스 구동 + Wireify MCP 호스팅 + per-session 시크릿 접착.

3-슬라이스:
- **3a. `ClaudeCliSessionClient : IAgentSessionClient` (재스폰 먼저)**:
  턴마다 `claude -p --resume <sid> --output-format stream-json --model <m> --mcp-config <s> --disallowedTools Write Edit Bash …`.
  StartThread→GUID sid 발급·저장(or init 이벤트 캡처), StartTurn→스폰+stream-json 턴별 버퍼 드레인+turnId 합성,
  ReadTurn→버퍼(권위적 재읽기 없음, 기존 `notificationOnly` 폴백 경로로), Interrupt→`Kill(tree)`, Stop→kill.
  **effort는 Claude 대응 없음**. 프로세스 위생·cwd-금지·`GPTINO_*` 스크럽은 Codex 재사용.
- **3b. Kestrel 위 in-process MCP 서버**: 새 HttpListener 말고 기존 Kestrel에 `MapPost("/mcp")`
  (Program.cs:20 loopback + :121-129 가드 재사용). `DynamicToolSpecs.Create()`가 이미 MCP-형이라 그걸 단일 소스로
  `McpServerTool` 등록 → delegate가 `DynamicToolCall` 4-튜플 만들어 **기존 `DispatchAsync`** 호출.
  **crux = per-session 시크릿→sessionId** (`X-GPTino-Secret`, Wireify `X-Wireify-Secret` 미러).
  `ToProtocolResult()`(Codex-형) 쓰지 말고 result.Text를 MCP 텍스트로. `ModelContextProtocol.Core` 추가.
- **3c. 지시문 + 실행 해석**: `ClaudeHomeScaffolder`가 `ComposeBaseInstructions()`를 per-session CLAUDE.md로
  (Windows 32KB 인자한계 → 큰 블롭 파일, 작은 델타만 `--append-system-prompt`; managed-block 마커).
  `ClaudeInstallation.TryLocateExecutable` + 엄격 resolver(`ResolveCodexExecutable` 미러).
  **정적 Claude 카탈로그**(`model/list` 없음 → 하드코딩 ModelView[]). 인바운드 글루 = `/mcp` 라우트가 직접 dispatcher 호출.

**게이트 B1**: 신규 DevLoop `live-claude`(`live-codex` 미러) — 동일 그리드+기둥 과제를 `backend=claude` 세션으로
dev 엔드포인트 캔버스-그린(컴포넌트 + 와이어≥1 + DataCount>0 + 런타임에러 0).
**게이트 B2**: 의도적 버그→자가수리(Failed+diagnostics→gptino:auto 재제출→committed, RecoveryRequired 0).
**충돌**: 코드+빌드+라이브 — 반드시 라이브 GPTino/dev 터미널 유휴 상태에서.

## Phase 4 — 백엔드 선택 UX

**목표**: 패널에서 Claude 다르게 표기·선택. Phase 3 기능을 UI로.

작업:
- `ClaudeAuthProbe`(CodexAuthProbe 미러, 자격증명 위치는 Phase 0 확정) → `runtime.claudeAuth` → 헤더 Claude 칩
  (App.tsx:12 주석이 자리 예고). `ClaudeLoginLauncher`(`cmd /k claude /login`) + `/runtime/claude-login-terminal`.
- **연결 백엔드 신호 일반화** `runtime.backends[]`. `+session` 분기: **2개=선택창→고정, 1개=자동, 0개=설치/로그인 안내**
  (NewSessionPopover, App.tsx:57-117). 드롭다운 필터(Phase 1) 실효.
- **fixed-per-session 강제**: 기존 세션 backend 변경 하드 거부.

**게이트**: 유닛(fixed-per-session 거부, provider-필터, ClaudeAuthProbe) + 라이브 스모크
(한 런타임에 Claude 1 + Codex 1 → 각자 자기 provider 모델만·자기 auth 칩). **충돌**: 코드+라이브.

## Phase 5 — 혼합 스트레스 + 재스폰/지속 결정

**목표**: 교차-백엔드 격리 적대적 증명 + 프로세스 모델 확정.

작업:
- **이질 웨이브**: N Claude + M Codex 세션이 같은 문서쌍에 동시 쓰기 → 단일 브로커.
  헤드라인 = "Claude 슬라이더 이동 중 Codex 값 리셋" 둘 다 0 Blocked(도메인 fingerprint + live-jobs.db)
  + **대화 무-이월** + 백엔드별 크래시-주입 복구.
- **`claude-startup-bench` 스테이지**: 재스폰 턴당 오버헤드(스폰+JSONL재로드+MCP재연결) vs 모델 지연,
  대화 깊이 1/5/20/50 성장곡선, N-프로세스 자원, 크래시 견고성. **재스폰 시작 → startup 비대 or 재로드 tail 유의미할 때만**
  활성세션당 지속+유휴타임아웃 승격(승격 시 B1/B2/게이트C 재실행).

**게이트 C**: 교차-백엔드 이동-vs-값 0 Blocked, 무-이월, 백엔드별 복구, 웨이브 전체 크래시/RecoveryRequired/타임아웃 0.
결정은 `docs/benchmarks/claude-backend-*.md`. **충돌**: 코드+라이브.

---

## 순서 / 의존성

```
Phase 0 (스파이크) ─┐
                    ├─→ Phase 3 (Claude 온라인) ─→ Phase 4 (UX) ─→ Phase 5 (스트레스+결정)
Phase 1 (provider) ─┤        ▲
Phase 2 (추상화 심) ─┘────────┘
```

- Phase 1·2는 Claude 없이 독립 랜딩(1=additive/배포가능, 2=그린-테스트 리팩터).
  둘 다 SessionStore/ApiModels를 건드리므로 **1(컬럼 추가) → 2(컬럼 리네임)** 순서.
- Phase 3은 0+1+2 완료 전제. Phase 4는 3, Phase 5는 4 뒤.
- **충돌 없는 착수점 = Phase 0** (소스/빌드/Rhino 무접촉).

## 리스크 레지스터

| # | 리스크 | 완화 | 검증 |
|---|---|---|---|
| R1 | 구독 CLI 안정성(데몬 없음) | 버전 핀 + 서킷브레이커 미러 + 재스폰=크래시-자연 | live-claude + 크래시 웨이브 |
| R2 | stream-json 파싱 | fail-closed 파싱 + 프로세스 세대 펜싱 | ClaudeCliSessionClientTests(malformed 픽스처) |
| R3 | MCP per-session 스코핑 | per-session 시크릿→sessionId + allowlist | MCP 유닛 + 교차-백엔드 웨이브 |
| R4 | 대화 저장소 비호환(이월 금지) | backend 세션 고정 + 변경 거부 | fixed-per-session + 무-이월 유닛 |
| R5 | N-프로세스 자원(지속) | 재스폰 시작(0 유휴) + 유휴타임아웃 + N-캡 | claude-startup-bench |
| R6 | Windows 인자한계 32KB | 큰 지시문 CLAUDE.md, 작은 델타만 플래그 | 스캐폴드 유닛 |
| R7 | external-id 컬럼 충돌 | `(backend, id)` 복합 UNIQUE | 마이그레이션+충돌 유닛 |
| R8 | Claude CLI 버전 드리프트 | 버전 핀 + 관대한 파서 | live-claude 매 사이클 |
| R-cat | flat 카탈로그가 Claude에 Codex 모델 노출 | Provider 필드 + session.backend 필터 | 라우팅 파티션 유닛 |
| R9 | boundary 가드가 Codex 토큰 문자열-assert | Claude 경로 landing 시 일반화 | boundary 스테이지 |
| R10 | 헬스/PID 모델이 단일 지속 프로세스 가정 | 재스폰은 per-session/last-spawn 리포트 | live-claude 헬스 |

## 관련 파일 인덱스

- 계약: `src/GPTino.AgentHost/Codex/ICodexSessionClient.cs:5-40`; 오케스트레이터 턴 루프
  `Runtime/SessionOrchestrator.cs:250-309`,`:819`.
- 디스패처/4-튜플: `Codex/DynamicToolDispatcher.cs:112-155`,`:336-340`; `DynamicToolCall/Result`
  `Codex/CodexAppServerClient.cs:1598-1628`; 스펙 `Codex/DynamicToolSpecs.cs:43-120`.
- 신원 컬럼: `Data/SessionStore.cs:45,104-108,386`; `Api/ApiModels.cs:20`.
- 프로세스 위생/exe 해석: `Codex/CodexAppServerClient.cs:261-266,824-853,1128-1248,1184-1196`;
  `Codex/CodexInstallation.cs:56-125`.
- 격리 미러: `Codex/CodexAppServerClient.cs:22-35,774-813`.
- Auth/login/projection: `Codex/CodexAuthProbe.cs`, `Codex/CodexLoginLauncher.cs`,
  `Runtime/RuntimeStateProjector.cs:21,75,181,214-216`, `Program.cs:85-97,118,266-276,396-404`;
  옵션 `Hosting/AgentHostOptions.cs:22,46-51`.
- 패키징: `scripts/build-package.ps1`(forbidden-file 가드 L368-369 유지), `packaging/yak/manifest.yml`,
  `docs/installation.md`.
- Wireify 참조 패턴: `.references/wireify/src/WireifyCore/Mcp/{WireifyMcpHost.cs,WireifyToolRegistry.cs}`,
  `.../Connect/{HomeScaffolder.cs,ConfigMerger.cs,ITerminalLauncher.cs}`,
  `home-template/{CLAUDE.md.tmpl,mcp.json.tmpl,settings.json.tmpl}`.
