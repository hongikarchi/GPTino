# GPTino 구조해석 구현 계획 (세션 단위, file:line 확정)

**작성일**: 2026-07-30 · **상태**: 세션 0·1 완료 (커밋 6030157, c55842e — 라이브 스모크는 재패키징+Rhino 종료 대기) · **상위 문서**: `structural-analysis-plan.md` (전략·검증)
· 세션 0 비고: 트리거 포인터는 gh-karamba-cookbook.md만 지목(미존재 스킬 참조 방지 — structural-analysis.md·structural_check.py 포인터는 세션 3에서 확장)
· **근거**: 4-에이전트 read-only 코드 조사 (스킬 통로 / data_read 신설 지점 / 벤치마크 하네스 /
스크립트 실행·배선 경로)로 접점을 file:line까지 확정.

---

## 환경 확정 사실 (라이브 확인)

- Karamba3D **3.1.60519** YAK 설치 완료 (`%APPDATA%\McNeel\Rhinoceros\packages\8.0\Karamba3D\3.1.60519\`),
  런타임 폴더 `net48` / `net7.0-windows` / `net8.0-windows` 3종. **pin = 3.1.60519**,
  `#r` 폴백 경로는 `net7.0-windows\KarambaCommon.dll` (Rhino 8 기본 런타임 = .NET 7 —
  런타임 폴더 오지정은 어셈블리 이중 로드 버그 재현 경로).
- 설치 버전이 NuGet 최신과 동일 = 공식 예제 코퍼스(K3D_tests, 3.1.40531)보다 8릴리스 앞섬
  → 쿡북 드리프트 안전규칙(`out var`, 단수 팩토리)이 즉시 실전 조건.

## 조사로 확정된 아키텍처 사실

1. **스킬 통로는 완전 데이터 주도** — assets/skills/에 파일 추가만으로 배포·인덱스·서빙 완결
   (csproj Content glob `GPTino.AgentHost.csproj:28-31` → `SkillLibrary.cs:30` 무필터 열거 →
   `InstructionAssembler.cs:26-31` "## Built-in skills" 주입 → `DynamicToolSpecs.cs:188` skill_read).
   C# 변경 0. 단 **평면 배치 필수**(하위폴더 불가), 첫 줄 ≤140자(`SkillLibrary.cs:79` 절단),
   'Python'으로 시작 금지(`:77` 셔뱅 스킵 로직).
2. **house-rules 48-50행이 계획과 정면 충돌** — "Solver domains stay native … not to
   re-implementations inside one script". 수정 없이 진행하면 에이전트가 규칙 준수로 Karamba
   스크립트를 회피함. 수정 시 `InstructionAssembler.cs` DefaultText(96-98행 부근)와
   **바이트 동일** 동기화 필수 (`InstructionAssetParityTests.cs:15`가 빌드 게이트).
3. **배선 순간 자동 솔브** — `GrasshopperCanvasFoundationAdapter.cs:712` (SetWire가 NewSolution
   호출). 솔버 컴포넌트는 executePython 전에도 wire ChangeSet마다 부분 배선 상태로 실행됨
   → 스크립트 내부 미배선 가드는 선택이 아니라 필수(정확성+성능).
4. **solved 판정 공동화는 코드로 실증됨** — `GrasshopperPythonFoundationAdapter.cs:310`의
   solved는 "런타임 에러 부재"만 봄. null-guard 스킵 실행도 초록. 대응: **solve 성공 경로에서만
   `solved` 출력을 assign** → 기존 predicate `outputCountInRange 'solved:1:1'`로 코드 변경 없이
   어서션 가능 (`LiveDocumentBackend.cs:4483-4552` fail-closed 평가).
5. **GH_Model은 generic 소켓으로 통과 (코드 근거 확보)** — `ResolveSafeType`(:1019-1044)이
   비기하 힌트를 전부 object로 강등, `SetWireCoreAsync:700`의 AddSource는 타입 무검사.
   미확인은 네이티브 Karamba 뷰 컴포넌트의 수락 여부뿐(M2 라이브 항목).
6. **하네스에 수치 게이트 없음 + committed snapshot에 수치 없음** — `dev-wave.ps1` 게이트는
   커밋 델타(39-40, 56행)+실패 정규식(57-65행)뿐. snapshot.json엔 소켓 구조·슬라이더 값만
   (실측: `artifacts/dev-loop/…/state/snapshot.json`). 출력 수치는 dev 전용 엔드포인트로 추출
   (`Program.cs:519` /dev/snapshot, `:535` /dev/grasshopper/{id}/outputs). sampleValues는
   goo.ToString() **최대 5개·200자**(`GrasshopperCanvasFoundationAdapter.cs:899`) → 솔버 출력은
   단일 JSON 문자열 요약 규약 필요.
7. **data_read 최소 변경 세트 = 신규 1파일 + 4파일 수정** (아래 세션 4). Claude 백엔드는
   무영향 — `claude-backend-plan.md:108-112` Phase 3b가 DynamicToolSpecs.Create()를 MCP 등록의
   단일 소스로 쓰므로 스펙+디스패처 추가만으로 양쪽 노출.
8. **상시 인덱스 비용은 3줄** (md 2 + structural_check.py 1 — py도 스킬 목록에 노출됨).
   전략 문서의 "2줄"은 md만 센 수치였음 → 본 문서로 보정.

---

## 세션 0 (선결, 소규모): house-rules 솔버 예외 + 트리거 포인터

**산출물**: `assets/instructions/house-rules.md` + `InstructionAssembler.cs` DefaultText 동시 수정.

1. 48-50행 "Solver domains stay native"에 예외 문구: *vetted Toolkit cookbook 스크립트
   (gh-karamba-cookbook.md 준수)는 예외 — 단 판정·베이크 등 정형 배관은 여전히 사전 제작 스킬만.*
2. Tier-2 전례(커밋 6bd53d9)대로 트리거 포인터 추가: *구조 과제 시 gh-karamba-cookbook.md +
   structural-analysis.md를 skill_read로 fetch; structural_check.py는 수정 금지·verbatim 배선*
   (paneling 포인터 `house-rules.md:12` 옆, bake 규칙 `:8-11` 형식 준용).

**게이트**: `InstructionAssetParityTests` 통과 (한쪽만 고치면 빌드 실패 — 의도된 검증).

## 세션 1 (M1): gh-karamba-cookbook.md + Hello Karamba 라이브 스모크

**산출물**: `assets/skills/gh-karamba-cookbook.md` (신규, 코드 변경 0).

- 첫 줄 = 평문 트리거 요약 ≤140자 (gh-paneling 관례; 207자 초과로 잘리는 전례 있음).
- 내용: Toolkit 진입·`out var`·단수 `UnitsConversionFactory`·mm→m·`FromGH`/`ToGH`·
  `GH_Model` 래핑·MessageLogger 판독 + **조사로 확정된 3규칙**:
  ① 솔버 가드 — 와이어 입력 null 시 Karamba 호출 전체 스킵, `solved` 출력은 성공 시에만 assign
  (gh-csharp-cookbook 18-19행 방어 관용구의 솔버 변형; 배선 자동 솔브 대응).
  ② `#r` 규칙 — bare-name 우선 시도, 폴백은 pin 경로(`…\3.1.60519\net7.0-windows\`),
  "최신 폴더 glob" 금지.
  ③ GH_Model 소켓은 **의도적 generic** — 하우스룰 "양끝 기하 힌트" 규범(`DynamicToolSpecs.cs:22-26`)의
  명시적 예외로 기술.

**라이브 스모크 (Rhino 재시작 후, dev-mode)**:
`#r` bare-name 해석 여부 실측 → Analyze/Utilization/OptiCroSec 3종 시그니처 →
`FactoryLoad.LoadCaseCombination` 실지원 범위 → 결과를 쿡북에 백필.

## 세션 2 (M2): 하이브리드 미니 씬 + V2/V2.5 통과

**산출물**: `scripts/dev-scene.py`에 구조 케이스 추가(`GPTINO_SCENE_KIND=structural` 분기 —
기둥 축선 4 + 보 스팬 그리드, 캡 예산 16/20 요소), `docs/benchmarks/structural-task.txt` 과제 프롬프트.

**합격 기준** (전략 문서 M2 + 조사 반영):
- V2 이론해(세장 부재 L/h≥20, 단순보 ≥10분할 — 분할도 캡 카운트) + V2.5 불변식(평형·부호·교란).
- `solved:1:1` predicate 그린 (공동화 차단).
- **GH_Model → 네이티브 뷰 컴포넌트 수락 여부 라이브 확정** (유일한 코드 미확정 지점).
  실패 시 대체: 스크립트가 colored mesh 출력 — `InspectOutputParameter`(:902-1011)가 Mesh 통계를
  자동 수집하므로 오히려 어서션 친화적.

## 세션 3 (M3): structural-analysis.md + structural_check.py + 하네스 수치 게이트

**산출물 A** — `assets/skills/structural-analysis.md` (신규): 6계층 파이프라인, ULS/SLS,
지지·하중 상식, 처짐 한계 적용 조건, data_read 사용법(행 추출→페이로드 주입, 경로 의존 금지).

**산출물 B** — `assets/skills/structural_check.py` (신규): bake_manager.py 템플릿 준수 —
1줄 `#! python 3`, 2줄 `#` 요약(인덱스 줄), 3줄~ 소켓 명세 헤더. γG/γQ/ψ 상수 테이블 내장,
단면 데이터는 필요 행 인라인 주입, 좌굴길이 정책(물리 부재 단위) 포함. 256KiB 캡 유의.

**산출물 C** — 하네스 수치 게이트, **1안(무 src 변경) 채택**:
- `docs/benchmarks/<task>.expect.json` 기대값 스펙 신설 — 표기는 기존 PredicateKind 관례
  `'outputName:min:max'` 재사용 (`Changes.cs:81`).
- `dev-wave.ps1`에 `-ExpectFile` 파라미터: 델타 섹션(55-65행) 뒤에서
  `GET /dev/snapshot`(닉네임→objectId) → `GET /dev/grasshopper/{id}/outputs` →
  sampleValues/dataCount 파싱·허용오차 비교 → 결과 객체(67행~)에 assertsPassed/assertsFailed 추가,
  **status=='idle'과 AND 결합** (타임아웃-후-우연-합격 배제; writer-active fail-fast
  `LiveDocumentBackend.cs:353` 대응은 기존 폴링이 충족).
- 2안(CommitHistoryAsync(:2232)에 state/outputs.json 커밋)은 4-컴포넌트 캡(:5105)·writeSet 제약이
  있어 보류 — 재현성 요구가 커지면 후속 승격.

**게이트**: 교란 테스트 포함 (지지 제거 웨이브 → 기대값 '실패해야 함'으로 어서션 살아있음 증명).

## 세션 4 (M4): 데이터 라이브러리 Phase 1 + data_read

**산출물** (조사 확정 최소 변경 세트):

| 파일 | 변경 |
|---|---|
| `src/GPTino.AgentHost/Hosting/DataLibrary.cs` (신규) | SkillLibrary(:13-84) 미러. 차이 3: 루트=`BaseDirectory/data`, 경로분리자 거부(:47-50) 제거하고 `ConstrainedPath.Resolve`(:5-23)만(하위폴더 `structural/` 허용, traversal은 여전히 차단), 인덱스 미주입(상시 3줄 유지). 캡 1MB(Phase 2/3 성장 대비) |
| `DynamicToolSpecs.cs` | :202-203 사이 `data_read` 스펙 — skill_read(:188-202) 미러, 설명문에 대표 파일명 직접 명시(`structural/sections.json`, `structural/materials.json`) |
| `DynamicToolDispatcher.cs` | 필드(:91 옆)·생성자 optional(:100)·switch 케이스(:140 옆)·RequireData(:167)·ActivitySummary(:231) |
| `Program.cs` | :77 옆 `AddSingleton<DataLibrary>()` — **누락 시 컴파일은 통과하고 첫 호출에 런타임 예외** (디스패처 테스트로 커버) |
| `GPTino.AgentHost.csproj` | :38 뒤 `assets\data\**` Content ItemGroup (instructions :33-38 패턴, `Link="data\…"`) |
| `assets/data/structural/sections.json`, `materials.json` (신규) | 전략 문서 스키마(Wpl,z·Av·iy/iz·Iw·r 포함), Phase 1 범위(IPE 전열+HEA/HEB, S235/S355), 출처·유효자릿수 필드 |
| `tests/…/DataLibraryTests.cs` (신규) + DynamicToolDispatcherTests 케이스 1건 | SkillLibraryTests 미러(하위경로 허용·traversal 거부·미존재 시 가용 목록) |

- `build-package.ps1:249` publish가 Content를 자동 동반, 금지 확장자 가드(:368-369)에 .json 없음 — 확인 완료.
- V1 3-way 대조 스크립트(scripts/ 또는 tests/)로 게이트.

## 미확정 → 라이브로 판정할 항목 (누적)

| 항목 | 판정 세션 |
|---|---|
| `#r` bare-name이 YAK 로드 어셈블리를 해석하는가 | 세션 1 |
| LoadCaseCombination의 3.1.60519 실지원 범위 | 세션 1 |
| GH_Model → 네이티브 Karamba 뷰 수락 | 세션 2 |
| 부분 배선 wire-solve의 45s 예산 내 비용 (가드 스킵 실측) | 세션 2 |
| Utilization 좌굴길이 기본값의 분할 기둥 거동 | 세션 3 |
