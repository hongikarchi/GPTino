# 적대적 스트레스 캠페인 — 2026-07-23

Rhino 1회 기동으로 다수 시나리오를 연속 실행(재기동 대기 최소화). 각 웨이브에서 발견한 마찰을
코드 수정→재설치→다음 웨이브로 검증. 성공 판정 외부화: dev 엔드포인트 스냅샷 + live-jobs.db 잡 상태.

## 집계 (4웨이브, 23시나리오, 잡 148개)

| 웨이브 | 시나리오 | 잡: 커밋/Blocked/Failed | 크래시·RecoveryRequired·타임아웃 |
|---|---|---|---|
| Wave 1 | 8 | 46 / 3 / 5* | 0 |
| Wave 2 | 6 | 33 / 5 / 0 | 0 |
| Wave 3 | 6 | 41 / 0 / 0 | 0 |
| Wave 4 | 3 | 12 / 2 / 0 | 0 |
| **합계** | **23** | **132 / 10 / 5** | **0** |

`*` Wave 1의 Failed 5건은 s6의 **의도적** 에러 체인(문법·미정의변수·0나누기 → 각각 진단 반환 → 정상 완성). 설계대로.

**전 웨이브 통틀어 크래시 0, RecoveryRequired 0, 타임아웃 0, 브리지·AgentHost 상시 생존.**

## 웨이브별 검증 성과

### Wave 1 — 기초 내구성
- 20층 타워(원형 기둥 160개), 10,000 포인트 그리드, 삭제+재배선, 5메시지 큐, 중간 취소 — 전부 크래시 없이 처리.
- **동시 2세션 분리 영역**: 교차 오염 0. **동시 2세션 같은 슬라이더 충돌**: writer lease로 직렬화, 둘 다 완료.
- 발견 마찰: 슬라이더 생성→값 설정 시 형제 도메인 fingerprint 미투영 → Blocked 3.
- **수정(커밋)**: `BuildCommittedJobView`가 생성 컴포넌트의 layout/value 형제 fingerprint도 투영.

### Wave 2 — 도메인 분리 검증
- **move-while-value (헤드라인)**: 한 세션이 슬라이더 10개 이동(값 보존) + 다른 세션이 10개 값 재설정(위치 보존) **동시, 둘 다 0 Blocked**. 도메인별 fingerprint 분리의 결정적 증거.
- numpy 없는 환경 → 순수 파이썬 폴백, `usedNumpy=False` 보고.
- 발견 마찰: **기존 컴포넌트 편집**(이 세션이 안 만든) 시 gptino:auto no-baseline 거부가 fingerprint를 안 실어줌 → 불필요한 snapshot_read 왕복.
- **수정(커밋)**: no-baseline 거부에 live fingerprint 인라인(다른 두 declined 분기와 동일). 안전성 변화 0.

### Wave 3 — 공격적 확장
- **5세션 병렬** 완주(22 컴포넌트). 모순 요청("삼각형이자 사각형이자 원")을 **모프 슬라이더**로 해석. 대형 파사드(96 메시 패널, 슬라이더 5) 단일 C# 저작.
- 빈 캔버스에서 "편집할 기존 컴포넌트 없음"을 **환각 없이 정확히 거부** — 없는 걸 지어내지 않음.
- **41잡 전부 커밋, 마찰 0.**

### Wave 4 — 크로스세션 기존 편집 (Wave 2 수정 검증)
- 세션 A가 `CrossEditTarget` 생성 → **세션 B**가 소스+값+이동 편집(B 입장 pre-existing) → 커밋.
- **3세션이 동시에 같은 컴포넌트를 소스/이동/값 각기 다른 도메인으로 편집 → 전부 커밋.** 도메인 분리 + no-baseline 복구 결합 시험 통과.
- Blocked 2건은 gptino:auto 첫 시도의 정당한 거부 → **인라인 fingerprint로 1회 재제출 즉시 복구**(수정 전 2왕복 → 1왕복).

## 남은 알려진 동작 (마찰 아님)

- gptino:auto는 이 세션이 안 쓴 리소스를 자기해결할 수 없음(사람-우선 안전 경계). 첫 편집 시 Blocked 1회 후
  인라인 fingerprint로 즉시 복구. 이것을 완전 제거하려면 서버 auto-seed가 필요한데, 동시 사람 편집을
  덮어쓸 위험이 있어 의도적으로 채택 안 함(현재의 block-with-fingerprint가 안전/편의 균형점).

### Wave 5 — Rhino 씬·베이크 어댑터 (미검증 계열)
- **베이크 파이프라인**: bake_manager 스킬로 구 메시 25개를 레이어에 베이크(end-to-end). **재베이크 idempotent**:
  replace 25개 중복 0 → 3×3으로 9개 정확 갱신. **브릿지 Rhino ops**: 구+박스 생성, 45° 회전 bbox 정확.
- 7턴 누적 세션 유지, DataTree 4×6 구조 유지.
- 발견: 컴포넌트 진화 중 **소켓 제거 스키마 → append-only 위반**이 소스 부분적용 후 RecoveryRequired(데드엔드).
- **수정(커밋)**: `PreflightPythonSchemas`가 쓰기 전에 소켓 수 비교 → 부분적용 없는 깨끗한 Failed. C# 쿡북에
  typed-geometry 입력 캐스팅 관용구 추가.

### Wave 6 — 크래시 복구 (폴트 주입)
- 세션 A 베이스라인 구축 → **AgentHost 프로세스 강제 kill** → **자동 재기동 ~12초** → 재연결.
- **베이스라인 캔버스 생존**(4컴포넌트, durable job store에 7잡 Committed 영속) + Layer 1 신원 재앵커 +
  **새 세션이 복구 후 계속 작업**(Panel 추가). rev 리셋은 정상(새 AgentHost fresh 스냅샷 시퀀스).
- 6b(실행 중 kill 정조준): 잡 실행 창이 서브초라 외부 폴링으로 미포착 — 대신 **14,400 박스 메시 대형 잡 완주** 확인.
  interrupted-job→RecoveryRequired 브랜치는 유닛 테스트(`RestartMarksInterruptedJobRecoveryRequiredWithoutReplayingIt`)
  + SQLite WAL synchronous=FULL(kill-safe 설계)로 커버.

## 집계 (6웨이브)

23+5+2시나리오, 잡 240+개. **크래시 복구 불능 0**(Wave 6에서 크래시 후 완전 복구 확인). 산출 코드 수정 4건.

## 이 캠페인이 산출한 코드 수정

1. 형제 도메인 fingerprint 투영 (Wave 1) — 생성→값 설정 낭비 Blocked 제거
2. no-baseline 거부에 fingerprint 인라인 (Wave 2) — 기존 편집 2왕복→1왕복
3. 소켓 제거 스키마 쓰기 전 preflight (Wave 5) — 부분적용 RecoveryRequired → 깨끗한 Failed
4. C# 쿡북 typed-geometry 입력 캐스팅 (Wave 5)

## 아직 안 한 (수동 필요) 테스트
- **GH 선택 라이브**(55aee1d 구현, 미확인): Rhino에서 컴포넌트 클릭 후 "선택한 거 뭐야" — 30초 수동 확인.
- **수동 드리프트**: 에이전트 작업 중 사람이 GH에서 컴포넌트 편집 → 진짜 human-wins 확인 — 수동 편집 필요.

관련: [[gptino-wireify-migration]] [[gptino-benchmark-loop]]
