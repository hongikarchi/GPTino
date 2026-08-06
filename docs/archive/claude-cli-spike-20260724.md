# Phase 0 — Claude Code 구독 CLI 스파이크 결과 (2026-07-24)

`docs/claude-backend-plan.md`의 Phase 0. 구독 `claude` CLI(설치 v2.1.218, 네이티브
`C:\Users\user\.local\bin\claude.exe`)에 대한 load-bearing 미확인 7가지를 실 머신에서 검증.
`--help`/파일시스템은 무과금, stream-json 트랜스크립트 1턴만 최소 과금($0.024).

## 7개 질문 결과

| # | 질문 | 결과 |
|---|---|---|
| 1 | 플래그 표면 | ✅ 확정 (+유리한 발견, 아래) |
| 2 | stream-json 이벤트 스키마 | ✅ 실 트랜스크립트로 확정 |
| 3 | 자격증명 저장 + 로그인 | ✅ 파일(`~/.claude/.credentials.json`) + `claude auth {login,logout,status}` |
| 4 | Windows 실행 해석 | ✅ 네이티브 `~/.local/bin/claude.exe`, `Process.Start` 직접 가능 |
| 5 | 헤드리스 신뢰/권한 | ✅ `--print`가 신뢰 다이얼로그 스킵 → Wireify trust 해킹 불필요 |
| 6 | MCP 전송 호환 | ✅ 실 loopback-HTTP 왕복 실증 (헤드리스 -p가 우리 MCP 툴 호출·반환) |
| 7 | `ModelContextProtocol.Core` NuGet | ⬜ 미확인(무-CLI 데스크체크, Phase 3에서 확인) |

## Q1 — 플래그 (확정 + 계획 단순화 발견)

- `-p/--print`, `--output-format {text,json,stream-json}`, `--input-format {text,stream-json}`(지속-입력 다중턴 가능), `--verbose`, `--include-partial-messages`.
- `--session-id <uuid>`: **우리 UUID를 직접 핀** → StartThread에서 GUID 발급, init 캡처 불필요.
- `--resume [id]`, `--fork-session`, `--no-session-persistence`.
- **`--effort {low,medium,high,xhigh,max}` 존재** → Codex effort를 그대로 매핑(조사 가정 "Claude엔 effort 없음"은 오답).
- **`--strict-mcp-config`** + `--mcp-config <files|JSON strings>` → 우리 MCP만, 사용자 MCP 무시. Codex의 MCP-끄기 대응이 한 플래그.
- `--tools ""`(빌트인 전부 끄기) / `--allowedTools` / `--disallowedTools`.
- `--append-system-prompt`, `--system-prompt`, (+`--append-system-prompt-file`/`--system-prompt-file` — `--bare` 설명에 명시) → 큰 지시문 파일, 32KB 인자한계 회피.
- `--model <alias|full>` (opus/sonnet/fable/full name), `--max-budget-usd`(과금 상한).
- **금지: `--bare`** — OAuth/키체인 안 읽고 `ANTHROPIC_API_KEY` 강제 → 구독 인증 깨짐. 또한 `--betas`는 "API key users only".

## Q2 — stream-json 스키마 (실 트랜스크립트)

이벤트 `type` 스위치:
- `system` / `subtype:"init"` — `session_id`(여기 최초 등장), `tools`, `mcp_servers`, `model`, `permissionMode`, `apiKeySource`("none"=구독 확인), `memory_paths`.
- `rate_limit_event` — `rate_limit_info`(status, resetsAt, rateLimitType, utilization, overageStatus). → 쿼터 표시등 소스로 유용.
- `assistant` — `message.content[]` 블록: `text`(어시스턴트 텍스트) / `fallback`(모델 전환) / (툴 활성 시 `tool_use`).
- `user` — 툴 결과 에코(툴 활성 시).
- `system` / `subtype:"model_refusal_fallback"` — 모델 거부→폴백(`original_model`/`fallback_model`/`api_refusal_category`). **필수 처리**.
- `result` / `subtype:"success"|"error_*"` — **종단**. `is_error`, `result`(최종 텍스트), `stop_reason`, `usage`, `total_cost_usd`, `modelUsage`, `permission_denials`, `terminal_reason`, `ttft_ms`.

검증: `--session-id` UUID가 output `session_id`와 일치(핀 성공). `--tools ""`→`tools:[]`, `--strict-mcp-config`→`mcp_servers:[]`. `apiKeySource:"none"` = 구독 헤드리스 동작 확인. 트리비얼 1턴 ttft ~3.3s / 총 ~3.3s(재스폰 콜드 오버헤드 데이터포인트).

## Q6 — 실 loopback-HTTP MCP 왕복 (실증)

scratchpad에 FastMCP streamable-HTTP 서버(툴 `ping`→추측불가 토큰 `SPIKE-OK-7F3A9C`)를 loopback(127.0.0.1:8770/mcp)로 띄우고
`claude -p --model opus --mcp-config <mcp.json> --strict-mcp-config --allowedTools mcp__spike__ping --permission-mode bypassPermissions`로 호출.

stream-json 증거:
- init: `"tools":["mcp__spike__ping"]`, `"mcp_servers":[{"name":"spike","status":"connected"}]` — 우리 loopback 서버 **연결됨**.
- assistant: `tool_use{name:"mcp__spike__ping", input:{msg:"hello"}}` + `tool_use_meta{display_name,server_display_name}`.
- user: `tool_result{content:"{\"result\":\"SPIKE-OK-7F3A9C msg=hello\"}"}` (+ `structuredContent`) — 서버가 실행·반환.
- result: `"result":"SPIKE-OK-7F3A9C msg=hello"`, `is_error:false`, `permission_denials:[]`, `num_turns:2`, 비용 ~$0.044.

추측불가 토큰이 최종 출력에 왔으므로 **헤드리스 -p ↔ loopback HTTP MCP 왕복 확정**. GPTino Kestrel MCP → `DispatchAsync`가 쓸 정확한 경로.

추가 발견:
- **단일 `claude -p` 호출이 전체 agentic 루프(툴콜→결과→최종답)를 한 프로세스에서 처리**(`num_turns:2`) → StartTurn = 1 스폰이 툴 사용 포함 턴 전체를 담당.
- 툴 결과가 `{"result":"..."}` + `structuredContent`로 래핑됨(FastMCP). GPTino C# MCP는 result.Text를 텍스트 블록으로 반환하도록 제어.
- **`--mcp-config` 인라인 JSON은 PowerShell 따옴표 뭉갬으로 실패** → 파일 경로가 견고. C#은 `ProcessStartInfo.ArgumentList`라 인라인도 되지만 per-session mcp.json이 더 안전.
- opus 핀으로 Fable 오탐 회피(폴백 없음). bypassPermissions로 승인 무-block 확인(프로덕션은 allowlist `mcp__gptino__*` 사용).

## 계획을 바꾸는 발견

1. **기본 모델 = `claude-fable-5` + 과도한 cyber 안전장치 오탐.** "Reply with exactly: ok"조차 오탐→거부→Opus 4.8 자동 폴백. Rhino/GH 모델링은 오탐 위험 더 큼.
   → **Claude 백엔드 기본 모델을 opus/sonnet으로 핀**(정적 카탈로그), 파서가 **`model_refusal_fallback` 필수 처리**. model은 이미 턴별 파라미터.
2. **주간 쿼터 77%, overage 비활성**(`overageStatus:"rejected"`, `overageDisabledReason:"org_level_disabled"`). Claude-백엔드 세션이 dev 작업과 같은 주간 예산 사용 → 라이브 게이트/스트레스는 쿼터 소모 큼. **live-claude/스트레스 웨이브 시 쿼터 모니터링 + `--max-budget-usd` 상한 권장.**
3. **Claude Code 자체 auto-memory**(`memory_paths.auto`)가 `~/.claude/projects/.../memory/`에 씀 → GPTino MEMORY.md와 간섭 가능. `--bare`는 끄지만 OAuth도 끔 → settings로 auto-memory만 끄는 법 Phase 3에서 확인.

## 계획 반영 (초안 대비 변경/단순화)

- **Slice 3c 단순화**: trust 부트스트랩(Wireify `hasTrustDialogAccepted`) **삭제** — `--print`가 스킵. 실행 해석은 네이티브 exe라 vendor-dir 파기 **삭제**. effort 매핑 **추가**(‑‑effort).
- **Slice 2 격리**: `--strict-mcp-config` + `--tools ""` + `--allowedTools mcp__gptino__*` + `--mcp-config <inline JSON with X-GPTino-Secret>` → Codex MCP-끄기의 깔끔한 대응. `.mcp.json` 파일조차 불필요(인라인 문자열).
- **StartThread**: `--session-id <GUID>` 핀 방식 채택(캡처 방식 불요).
- **정적 카탈로그(Slice 3c)**: opus/sonnet/haiku 노출, **기본 opus/sonnet**(fable 기본 회피 or 경고).
- **ClaudeAuthProbe(Phase 4)**: `.credentials.json` 파일 휴리스틱(1차) + `claude auth status`(권위적 폴백). 로그인 런처 = `claude auth login`.

## 남은 항목 (비차단)

- Q7 `ModelContextProtocol.Core` NuGet 버전/TFM 확인 — 무-CLI 데스크체크, Phase 3 패키지 추가 시(Wireify가 2.0.0-preview.1 사용 중이라 존재 확실).

**게이트 판정**: Phase 0 통과(7/7 실증, Q7만 무-CLI 데스크체크 이연). Phase 1(provider 배관, Codex-only)로 진행 가능.

**Phase 0 총 비용**: 유과금 claude 호출 2건(트리비얼 텍스트 1턴 + MCP 왕복 1턴) ≈ $0.068. `--help`/파일시스템/uv 설치는 무과금.
