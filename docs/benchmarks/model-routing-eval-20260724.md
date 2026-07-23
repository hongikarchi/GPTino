# 모델 라우팅 재평가 — 2026-07-24

로드맵 #4: 프로토콜 단순화 완료 후, 와이어링/네이밍 등 단순 턴의 컴팩트-모델 다운그레이드가
실제로 품질/속도를 해치는지 라이브 A/B로 재평가. (2026-07-23 결정: 단순=낮은모델 기조 유지,
단순화 후 벤치마크로 재평가.)

## 카탈로그 (결정적 사실)
7개 모델. **컴팩트 모델 실재**: gpt-5.4-mini(mini), gpt-5.3-codex-spark(spark), gpt-5.6-luna(luna).
기본=gpt-5.6-sol. 즉 FastSafe 다운그레이드는 effort만이 아니라 **진짜 다른(약한) 모델 선택**이 될 수 있음.

## A/B 1 — 멀티턴 (복잡 setup + 단순 후속 5턴, deep vs auto)

| | deep (강제 HighAssurance) | auto (키워드 라우팅) |
|---|---|---|
| 모델·effort (전 턴) | gpt-5.6-sol @ **xhigh** | gpt-5.6-sol @ **medium** |
| 잡 | 10 커밋 / 0 실패 | 11 커밋 / 0 실패 |
| setup | 151s | **110s** (−27%) |
| 실행+검증 턴 | 84.5s | **56.4s** (−33%) |
| 나머지 단순 턴 | 18~26s | 16~22s |

## A/B 2 — 격리 순수-단순 (contextFloor 없는 신규 세션 첫 메시지)
"ISO 오른쪽 500 이동", "ISO 슬라이더 다시 연결" (순수 이동/연결) → **여전히 gpt-5.6-sol @ medium (Standard)**.
6잡 전부 커밋. 컴팩트 모델 미선택.

## 핵심 발견
1. **컴팩트-모델 다운그레이드가 어떤 라이브 케이스에서도 발동하지 않았다.** 멀티턴이든 격리
   순수-단순이든 auto는 항상 플래그십 sol을 medium/xhigh로 유지. mini/spark/luna는 한 번도 선택 안 됨,
   low effort도 한 번도 안 씀. 즉 "와이어링 턴이 조용히 약한 모델로 떨어져 품질 저하"라는 원래 우려는
   **이 배포에서 경험적으로 거짓.** (분류기가 실제 메시지를 Standard/HighAssurance로 안착시켜, FastSafe/
   컴팩트 티어가 실질적으로 활성화되지 않음. 코드상 `이동`=SimpleWriteTerm인데 Standard로 간 정확한
   경로는 미완전 규명 — 그러나 결과는 일관·안전.)
2. **auto(sol@medium)가 deep(sol@xhigh)와 성공률 동일(양쪽 100%)이면서 20~35% 빠름.** 같은 모델,
   낮은 effort가 이 작업 유형(와이어/값/이동/실행)에서 신뢰성 손실 없이 더 빠름.

## 판정: 현행 라우팅 유지 (코드 변경 없음)
- 다운그레이드가 품질을 해치지 않음 — 발동 자체를 안 하기 때문.
- 실제로 발생하는 티어링(Standard/medium ↔ HighAssurance/xhigh, 동일 플래그십)은 순이득: 더 빠르고 성공률 동등.
- FastSafe/컴팩트 티어는 실질 vestigial. 컴팩트 모델의 비용/지연 이득을 원하면 SimpleWrite 분류를 **넓혀야**
  하는데, sol@medium이 이미 빠르고 안정적이라 상방 이득 적고 리스크 실재 → 권장 안 함.

관련: [[gptino-deferred-roadmap]] [[gptino-wireify-migration]]
