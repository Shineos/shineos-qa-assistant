#!/bin/bash
# レイテンシA/B: 新規質問3問の壁時間計測（キャッシュクリア後）
B=http://127.0.0.1:8300
curl -s -X POST $B/api/cache-clear > /dev/null
q() {
  printf '%s' "{\"message\": \"$1\", \"web_search\": false}" > /tmp/lat-q.json
  local t0=$(date +%s.%N)
  curl -sN --max-time 300 -o /tmp/lat-out.sse -H 'Content-Type: application/json' --data-binary @/tmp/lat-q.json "$B/api/chat"
  local t1=$(date +%s.%N)
  local ms=$(echo "$t1 $t0" | awk '{printf "%.0f", ($1-$2)*1000}')
  local answer=$(grep '^data: ' /tmp/lat-out.sse | grep -o '"content": *"[^"]*"' | sed 's/"content": *//' | tr -d '"' | tr -d '\n' | head -c 80)
  echo "[$ms ms] $answer"
}
q "交通費の精算方法を教えてください"
q "慶弔休暇で父母が亡くなった場合は何日ですか"
q "経費精算の締め日はいつですか"
