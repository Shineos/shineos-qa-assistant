#!/bin/bash
# バックエンドE2E: チャットSSE・キャッシュ・ガード・履歴
B=http://127.0.0.1:8300

sse() { # $1=json body
  printf '%s' "$1" > /tmp/e2e-q.json
  local t0=$(date +%s.%N)
  curl -sN --max-time 300 -o /tmp/e2e-out.sse -H 'Content-Type: application/json' --data-binary @/tmp/e2e-q.json "$B/api/chat"
  local t1=$(date +%s.%N)
  echo "=== wall: $(echo "$t1 $t0" | awk '{printf "%.1f", $1-$2}')s ==="
  # イベント別に要約
  grep -o '^event: [a-z]*' /tmp/e2e-out.sse | sort | uniq -c
  grep '^data: ' /tmp/e2e-out.sse | tail -1 | head -c 400; echo
  # 回答テキスト組み立て
  grep '^data: ' /tmp/e2e-out.sse | sed 's/^data: //' | while read -r l; do
    echo "$l" | grep -q '"content"' && echo "$l" | grep -o '"content": *"[^"]*"' | sed 's/"content": *//; s/^"//; s/"$//'
  done | tr -d '\n'; echo
}

echo "########## Q1: 宿泊費（初回・LLM起動込み） ##########"
sse '{"message": "出張時の宿泊費の上限はいくらですか"}'
sleep 1
echo "########## Q2: 同一質問（回答キャッシュ） ##########"
sse '{"message": "出張時の宿泊費の上限はいくらですか"}'
sleep 1
echo "########## Q3: NO-HIT（ガード） ##########"
sse '{"message": "宇宙開発部門の予算配分について教えてください"}'
sleep 1
echo "########## Q4: 同一文書の別質問（LCP/prefixキャッシュ） ##########"
sse '{"message": "国内出張の日当はいくらですか"}'
echo "########## 履歴 ##########"
curl -s "$B/api/chats"; echo
curl -s "$B/api/chats/1" | head -c 600; echo
echo "########## 設定 ##########"
curl -s -X POST "$B/api/settings" -H 'Content-Type: application/json' -d '{"web_search": true}'; echo
curl -s "$B/api/settings"
