#!/bin/bash
# 大量質問レイテンシ計測: 25問（知識20/NO-HIT3/キャッシュ反復2）
B=http://127.0.0.1:8300
CSV=/d/dev/shineos-local-ai/spikes/phase0/soak.csv
echo "no,cls,wall_ms,ttfb_ms,ok,q" > $CSV
curl -s -X POST $B/api/cache-clear > /dev/null

ask() { # $1=no $2=cls $3=expect $4=q
  printf '{"message": "%s", "web_search": false}' "$4" > /tmp/sq.json
  local t0=$(date +%s%N)
  curl -sN --max-time 300 -o /tmp/sq.sse -H 'Content-Type: application/json' --data-binary @/tmp/sq.json $B/api/chat
  local t1=$(date +%s%N)
  local wall=$(( (t1 - t0) / 1000000 ))
  local ttfb=$(grep -o '"ttfb_ms":[0-9]*' /tmp/sq.sse | grep -o '[0-9]*' | head -1)
  local cached=$(grep -c '"cached":true' /tmp/sq.sse)
  local guard=$(grep -o '"guard":"[a-z]*"' /tmp/sq.sse | head -1)
  cp /tmp/sq.sse /d/dev/shineos-local-ai/spikes/phase0/q.sse
  powershell -NoProfile -ExecutionPolicy Bypass -File /d/dev/shineos-local-ai/spikes/phase0/decode2.ps1 > /dev/null 2>&1
  local ans=$(head -c 400 /d/dev/shineos-local-ai/spikes/phase0/q.txt)
  local ok="NG"
  if [ "$2" = "nohit" ]; then
    echo "$ans" | grep -q "該当する記載" && ok="OK"
    [ -n "$guard" ] && ok="OK"
  elif [ -n "$3" ]; then
    echo "$ans" | grep -q "$3" && ok="OK"
  fi
  local cls="$2"
  [ "$cached" -ge 1 ] && cls="cache"
  [ -n "$guard" ] && [ "$2" != "nohit" ] && cls="guard"
  echo "$1,$cls,$wall,${ttfb:-$wall},$ok,$4" >> $CSV
  echo "[$1 $cls ${wall}ms ttfb=${ttfb:-guard} $ok] $4"
}

n=1
ask $n hit "15,000" "出張時の宿泊費の上限はいくらですか"; n=$((n+1))
ask $n hit "3,000" "海外出張の日当はいくらですか"; n=$((n+1))
ask $n hit "1,500" "国内出張の日当はいくらですか"; n=$((n+1))
ask $n hit "3日" "結婚した場合の慶弔休暇は何日ですか"; n=$((n+1))
ask $n hit "7日" "忌引きで父母が亡くなった場合は何日ですか"; n=$((n+1))
ask $n hit "90日" "パスワードはどのくらいの期間で変更する必要がありますか"; n=$((n+1))
ask $n hit "1234" "パスワードを忘れた場合はどうすればよいですか"; n=$((n+1))
ask $n hit "3日" "年次有給休暇の申請はいつまでにすればよいですか"; n=$((n+1))
ask $n hit "5営業日" "出張申請はいつまでにすればよいですか"; n=$((n+1))
ask $n hit "22時" "タクシーは利用できますか"; n=$((n+1))
ask $n hit "暗号化" "USBメモリの利用は認められていますか"; n=$((n+1))
ask $n hit "30日" "会議室の予約は何日前からできますか"; n=$((n+1))
ask $n hit "2週間" "備品のトナー発注はいつまでにすればよいですか"; n=$((n+1))
ask $n hit "1回" "名刺の注文は月に何回までですか"; n=$((n+1))
ask $n hit "1か月" "制服のサイズ交換はいつまでにすればよいですか"; n=$((n+1))
ask $n hit "7日前" "駐車場の利用申請はいつまでにすればよいですか"; n=$((n+1))
ask $n hit "200円" "駐車場の利用料金はいくらですか"; n=$((n+1))
ask $n hit "所属長" "在宅勤務の申請条件は何ですか"; n=$((n+1))
ask $n hit "10日" "経費精算書の提出期限はいつですか"; n=$((n+1))
ask $n hit "情報システム課" "社外へのファイル送付はどうすればよいですか"; n=$((n+1))
ask $n nohit "" "宇宙開発部門の予算配分について教えてください"; n=$((n+1))
ask $n nohit "" "社内カフェテリアのメニューを教えてください"; n=$((n+1))
ask $n nohit "" "創業記念日のイベント内容は何ですか"; n=$((n+1))
ask $n cache "15,000" "出張時の宿泊費の上限はいくらですか"; n=$((n+1))
ask $n cache "7日" "忌引きで父母が亡くなった場合は何日ですか"; n=$((n+1))
echo "=== CSV: $CSV ==="
