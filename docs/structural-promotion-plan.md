# 구조해석 파이프라인 내장화 계획 (2026-08-06)

**한 문장**: `scripts/`에서 검증만 끝난 실무 파이프라인(축선 추출 → 조인트 수선 → PyNite 해석 →
시각화 → 대안)을 에이전트가 호출 가능한 **서버 툴 2개 + 상주 솔버 스크립트 + 기존 카드/칩 UX**로
옮긴다. 원칙은 rhino_audit과 동일 — **탐지·계산은 결정론적 서버 코드, 해석·역질문·판단은 LLM**.

**상위 문서**: `structural-analysis-dev-plan.md` (Karamba/PyNite 검증 이력, 실무 수용 기준 5항),
`structural-analysis-plan.md` (전략). 이 문서는 "호스트 툴 승격" 잔여 항목의 실행 계획.

---

## 출발점 (검증 완료된 자산)

| 자산 | 위치 | 상태 |
|---|---|---|
| 축선 추출 (블록 xform 역산 + PCA 가새 + 중복 제거) | `scripts/extract-steel-axes.py` (175줄) | 실무 3dm 1,199부재, 봉쇄감사 557/557 |
| 그래프 수선 + PyNite 해석 + 판정 | `scripts/pynite-real-model.py` (354줄) | 0.85초 해석, 평형 정확, Karamba 교차 0.9% |
| KS 단면 카탈로그 | `assets/data/structural/sections-ks.json` | 웹검증 21종, It/Iw 해석 컬럼 |
| 스킬 3종 + 판정 스크립트 | `assets/skills/structural-*.md`, `structural_check.py` | 라이브 게이트 통과 |
| PyNite-in-GH 경로 | gh-pynite-cookbook.md | 0.004% 이론해 일치 |
| 카드/칩 UX | goal 카드·승인 카드·포커스 칩 | 라이브 게이트 통과 (`abde571`) |

**갭**: 에이전트는 위 스크립트를 실행할 수 없다 (typed 툴 + GH 스크립트 컴포넌트만; 샌드박스
read-only). 파이프라인은 이 개발 세션의 시스템 파이썬으로만 돌았다.

## 아키텍처 결정

1. **추출 = C# 브리지 read op** (`structural_extract`). 라이브 문서(미저장 변경 포함)를 읽어야
   하므로 RhinoCommon 경유가 유일한 정답. rhino_audit과 같은 형태의 read-only 연산 — ChangeSet
   불필요, fingerprint 동봉. 175줄 파이썬의 수학(행렬 적용, 멱반복 PCA, 각도·거리 중복판정)은
   전부 RhinoCommon에서 더 직접적으로 표현된다 (`InstanceReference.InstanceXform` 등).
2. **해석 = AgentHost가 스폰하는 프로세스 밖 파이썬** (`structural_solve`). 순수 계산이라 문서
   접근이 필요 없고, UI 스레드·45초 브리지 타임아웃 문제를 원천 회피한다. PyNiteFEA는 Rhino의
   `py39-rh8` 환경에 이미 설치됨 — 그 python.exe를 탐색·실행하고, 없으면 설치 안내와 함께
   정직하게 실패. 솔버 스크립트는 **자산으로 배포**(LLM이 매번 생성하지 않음 — 수백 줄 검증
   로직의 재현성이 안전 주장의 근거).
3. **그래프 수선은 해석 쪽 전처리**로 남는다 (solve 입력 단계). 수선은 해석 모델만 바꾸고
   사용자의 Rhino 기하는 건드리지 않으므로 **승인 카드가 아니라 역질문** 대상이다:
   "자유단 N개 발견 — 의도된 캔틸레버인가요, 스냅 수선할까요?" (포커스 칩으로 해당 부위 표시).
4. **시각화 v1 = 문제 부위만**. 추출이 원본 솔리드의 objectId를 기록하므로, 판정 실패 부재는
   **포커스 칩으로 실물을 직접** 가리킬 수 있다 (신규 op 0개). 여기에 판정색 축선을
   `GPTino::Structural` 레이어에 bake (GPTino provenance → 자유 삭제·undo 가능). 전체 변형
   형상 표시는 검증된 GH 컴포넌트 경로(gh-pynite-cookbook)로 커버 — 1,199부재 bulk-bake 전용
   op는 실사용에서 필요가 증명되면 후속.
5. **대안 제시 = goal 카드 옵션 재사용**. 옵션에 objectIds를 실으면 ◎ 버튼으로 뷰포트 확인까지
   이미 배선돼 있다(라이브 게이트 통과 자산). `[[alt:]]` 마커·AltChip은 v1에서 포커스 칩과 동일
   동작(클릭 → /focus)으로 배선하고, 별도 선택-회신 채널은 만들지 않는다 — 선택은 카드가 담당.
6. **Karamba는 제품 경로가 아니라 오라클**. 교차검증은 벤치마크 하네스에 남고, 라이선스 보유
   사용자용 1급 경로는 기존 쿡북 스킬로 이미 제공됨.

## 데이터 흐름 (툴 계약)

```
structural_extract { scope?: layerFilter|selection, options? }
  → 요약(부재수·마크별·경고·자유단 수) + 세션 아티팩트 structural/members.json
    members[]: { mark, layer, ax, bx, len_mm, sourceObjectIds[], fingerprints[],
                 kind: curve|instance|pca, sectionGuess?, confidence }

structural_solve { membersArtifact, answers?: { supports?, cantileverNodeIds?,
                   repairPolicy?, loads? } }
  → 요약(최대변위·판정 통과/실패 수·지점 반력 합) + structural/results.json
    (부재별 변위·판정, 실패 부재의 sourceObjectIds 역참조 포함)
```

아티팩트 경유인 이유: 1,199부재 전체를 툴 결과로 반환하면 컨텍스트 낭비. 요약만 대화에,
전체는 artifact_read로 필요 행만.

## Phase 계획

### Phase 1 — `structural_extract` (대, 세션 1~2회)

**산출물**: `RhinoSceneFoundationAdapter`에 추출 메서드(+`ICordycepsRhinoSceneAdapter` 계약,
브리지 핸들러, `LiveDocumentBackend` 패스스루), `DynamicToolSpecs`/`Dispatcher` 배선, 단위 테스트.

- 소스 3종: **커브**(축선이 이미 선이면 그대로), **InstanceReference**(프로토타입 xform 역산 —
  1000mm 단위블록 패턴 자동 감지), **loose 솔리드**(PCA, `approx` 플래그). mesh는 명시적 scope 밖
  (경고로 보고). 하드코딩이던 "철골" 레이어 필터는 scope 파라미터로 일반화.
- 단면 추정: 프로토타입 외형치수 ÷1.02 → sections-ks.json 매칭, 오차·신뢰도 동봉.
- 중복 제거(3° / 250mm)와 **품질 감사 내장**: 수용 기준 1항의 "선이 겹치거나 틀어지면 불합격" —
  직교 그리드 상식 체크(±3° 밖 사선 비율 보고)를 추출 결과에 포함.

**게이트**: ① 신규 `structural-solids` 씬(단위블록 H형강 프로토타입+인스턴스 + loose 가새 +
**고의 자유단 1개** — hygiene 씬과 같은 원칙: 결함 없는 픽스처는 미실행) → 부재수·자유단 수
정확 일치 ② 실무 3dm 수동 대조: 1,199부재·중복 610 제거와 동일 결과.

### Phase 2 — `structural_solve` + 파이썬 러너 (중~대, 세션 1~2회)

**산출물**: AgentHost의 범용 파이썬 러너(환경 탐색·타임아웃·JSON 입출력·stderr 캡처),
솔버 스크립트 자산(`assets/data/structural/solver.py` — pynite-real-model.py의 그래프 수선·해석·
판정부 + structural_check.py의 한계표), 툴 배선.

- 러너: `%USERPROFILE%\.rhinocode\py39-rh8\python.exe` 탐색 → `import Pynite` 사전 점검 →
  실패 시 설치 명령 안내를 툴 에러로. 타임아웃·출력 캡 명시.
- 하중 v1: 자중 자동 + 역질문으로 층활하중/마감(기본값 제시, ULS/SLS 조합은 스킬 규율 그대로).
- **축 관례 함정 고정**: `add_section` Iy=강축 — 쿡북에 문서화된 결함이므로 솔버 자산에 주석 +
  이론해 게이트로 재고정.

**게이트**: ① 기존 structural 씬 TestBeam 이론해(δ=PL³/48EI≈7.62mm, 1% 내) ② 교란 게이트
(지점 제거 → 판정 실패로 뒤집혀야 어서션이 살아있음) ③ 실무 3dm: 기존 리포트와 수치 동일.

### Phase 3 — 결과 시각화 + 역질문 흐름 (중, 세션 1회)

**산출물**: 판정색 축선 bake(기존 typed op 조합, `GPTino::Structural::{NG,Warn}` 레이어),
하우스룰에 구조 대화 규율 추가(추출 → 자유단·지점 역질문[포커스 칩 필수] → solve → 문제 부위
포커스+bake → 대안). `InstructionAssembler` 바이트 동기화.

**게이트**: 픽스처의 고의 자유단에 대해 ① 에이전트가 먼저 물었는지(전 턴에 solve 호출 없음)
② 답변 후 solve ③ 판정 실패 부재의 포커스 칩이 실물 솔리드를 가리키는지 ④ bake된 결과물이
GPTino provenance인지(사용자 승인 없이 삭제 가능해야).

### Phase 4 — 대안 제시 + alt 배선 (소~중, 세션 1회)

**산출물**: 대안 흐름 = goal 카드 옵션(objectIds 포함) + 대안별 미리보기 기하 bake.
패널 `AltChip` 클릭 → `/focus` 배선 (`onSelectAlt` 잔여 항목 해소).

**게이트**: 판정 실패 부재에 단면 확대/부재 추가 2안 제시 → 카드 옵션 ◎로 뷰포트 전환 확인 →
선택 → 선택안만 적용(승인 카드 경유 — 기존 게이트 자산 재사용).

### Phase 5 — 종합 라이브 게이트 + 실무 검증 (세션 1회)

`gate-structural.ps1` (gate-approval.ps1 패턴): 픽스처에서 추출→역질문→해석→시각화→대안→적용
전 경로, 관측점 2개 이상(아티팩트 수치 + `/dev/audit`류 독립 프로브) 교차. 마지막으로 실무
3dm을 사용자 입회 하에 통과 — **수용 기준 5항이 이 파일 기준이므로 이게 최종 합격선**.

## 정직한 한계 (계획에 미포함)

- **경계조건**: 포디움 접합 상세는 도면 없이는 추정 — 지점 규칙은 역질문으로 확인받고 가정을
  goal 카드에 명시 (기존 결론 유지)
- **mesh 소스 추출**: scope 밖 경고로만 (수요 확인 후)
- **좌굴 판정**: Karamba 좌굴 활용률은 오라클 미검증 상태 그대로 — 판정표에서 제외 유지
- **1,199부재 전체 변형형상 bulk-bake**: GH 경로로 대체, 전용 op는 수요 증명 후

## 라이브 게이트 결과 (2026-08-06, structural-solids 픽스처)

**PASS — 추출·대화·해석·검산 전 경로.** 독립 프로브(`/dev/structural-extract`) 채점:
부재 10(길이 3000×5·4000×2·6000×2·대각 7211 전부 정확), 프로토타입 2종 KS×1.02 오차 0,
자유단 정확히 3, mesh 스킵 정직 보고. 대화 경로: 턴1은 추출만 하고 **해석 없이 멈춰**
자유단 3곳을 실측 GUID 포커스 칩으로 역질문(지점 가정·도면 필요까지 명시), 턴2는 답변을
`answers.cantileverPoints`로 정확히 전달(results.json에 confirmedCantilever=true), 무단 수선
0, 자중 33.25kN=반력 33.25kN(수동 검산 33.26 일치), 판정 10/10, 최대 변위 칩이 실제
캔틸레버 솔리드를 가리킴. 게이트가 잡은 결함 2건은 픽스처·dev 엔드포인트 측:
`rs.InsertBlock`의 T·S·R 합성이 회전된 보를 1000mm 스텁으로 만드는 문제(명시적 T·R·S로
수정), dev 프로브의 nullable 직렬화(생략으로 수정).

## 실무 3dm 게이트 결과 (2026-08-06, 260803 main ms.3dm — 수용 기준의 기준 파일)

**PASS — 검증된 파이썬 베이스라인과 수렴.** 추출 589부재·중복 610 제거·마크 30 정확 일치;
해석 위상 6지표 전부 일치(766요소·488절점·지점 35·스냅 388·T분할 177·광역보정 4), **최대변위
3345.44mm 소수점 일치**, 평형 0%, 프로세스 밖 solve 0.62초(스폰 오버헤드 무시 가능). 자중
잔차 0.42%는 `SB6 (Diagonal)` 4부재가 이제 올바른 경량 단면을 받아서 — 새 쪽이 베이스라인보다
정확. 대화 품질: 에이전트가 자유단 23곳을 **스스로 분류**(기둥 기초 19 vs 지상부 오류 4)해
그룹 칩으로 역질문, 경사축 102개 확인 요청, 최종 보고에서 **사용자 지정 19지점 vs 형상 감지
35지점 불일치를 자발 발견**(차이 16곳 = 포디움 지지 기둥과 정확 일치) + 처짐 검토라는 범위
한정 명시. 317/766 실패·3.3m 변위는 기지의 캐노피존 경계조건 한계 그대로 재현.

게이트가 잡은 결함 3건(전부 수정·테스트 고정):
1. **프로토타입이 블록 정의 내부** — 실무 파일은 delete-original이라 일반 열거자가 프로토타입
   솔리드를 못 봄(0/29) → InstanceDefinitions에서 측정하는 pass 1b 추가
2. **÷1.02 고정 가정** — 이 파일의 정의 기하는 정확 공칭 치수라 SC6·SC7이 2% 이웃 행으로
   오식별 → 스케일 가설 {1.0, 1.02} 모두 시도, 오차 최소 선택 (29/29 일치)
3. **변형 마크 미해결** — `SC5 (Bracing)` 38부재가 기본 단면으로 낙하(자중 +4.4%) →
   마크 접두사 폴백
+ 하네스: 실무 파일의 **Missing Fonts 모달**이 부팅을 막음 → `dismiss-font-dialog.ps1` 워치독

## 미확정 → 후속 항목

| 항목 | 상태 |
|---|---|
| 판정색 bake의 typed op 조합 (실패 부재 시각화) | 실패 부재가 나온 실무 게이트에서 칩 포인팅으로 대체 확인 — bake 경로는 후속 |
| goal 카드가 구조 흐름에서 언제 발화하는가 | 두 게이트 모두 역질문 프로즈로 충분했음 — 관찰 지속 |
| 캐노피존 경계조건 (317부재 실패의 원인) | 도면 대기 (기존 결론 유지) |
