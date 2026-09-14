#!/bin/bash
# テスト文書をUTF-8ファイル名で再アップロードし、docx/pdf内容のQ&Aで検証
B=http://127.0.0.1:8300
D=/d/dev/shineos-local-ai/spikes/phase0/testdocs
# curlにfilename=で明示（git-bashはUTF-8）
curl -s -X POST $B/api/knowledge -F "files=@$D/テスト社内規程.docx;filename=テスト社内規程.docx;type=application/vnd.openxmlformats-officedocument.wordprocessingml.document"
echo
curl -s -X POST $B/api/knowledge -F "files=@$D/test-policy.pdf;filename=test-policy.pdf;type=application/pdf"
echo
curl -s $B/api/knowledge | tail -c 700
echo
echo "=== Q1: docx内容の質問 ==="
printf '%s' '{"message": "リモートワーク手当は月額いくらですか", "web_search": false}' > /tmp/docq.json
curl -sN --max-time 300 -o /tmp/docq.sse -H 'Content-Type: application/json' --data-binary @/tmp/docq.json $B/api/chat
grep '^data: ' /tmp/docq.sse | grep '"content"' | sed 's/.*"content"://' | tr -d '"' | tr -d '\n' | head -c 250
echo
echo "=== Q2: pdf内容の質問（日英クロスリンガル） ==="
printf '%s' '{"message": "テレワーク手当の支給対象は誰ですか", "web_search": false}' > /tmp/pdfq.json
curl -sN --max-time 300 -o /tmp/pdfq.sse -H 'Content-Type: application/json' --data-binary @/tmp/pdfq.json $B/api/chat
grep '^data: ' /tmp/pdfq.sse | grep '"content"' | sed 's/.*"content"://' | tr -d '"' | tr -d '\n' | head -c 250
echo
