@echo off
rem v2 commit script (run outside ZCode hooks) - ASCII only for CP932 console
cd /d D:\dev\shineos-local-ai
git add -A
git commit -m "v2: direct llama.cpp architecture + model-bundled installer

- RAG: hybrid search (BM25+vector) -> rerank -> guard -> neighbor-chunk join -> SSE
- Speed: high-confidence rerank skip, prompt reorder (prefix cache), LLM preload
  + inference warmup, cache-before-LLM, IQ4_XS quantization
- Tiers: quick(1.7B) / standard(4B) / quality(30B, OOM tier fallback)
- UI: composer model selector (row-click switch, not-downloaded badge, DL spinner),
  first-run wizard (1.7B default), thinking indicator, source preview
- Quality: 18-scenario suite, 4B-IQ4_XS 18/18, no IQ4_XS regression proven
- Installer v2: self-contained exe + llama.cpp engine (44MB) and 1.7B-bundled
  (2.08GB) variants, silent install verified
- Fixes: JobObject struct (Affinity=ULONG_PTR) orphans, tier-switch killing
  embed/rank permanently, preload-vs-switch race, Qwen3 hybrid thinking disable,
  model-tagged answer cache
- .mimosa/security-policy.json: threat model + llama-server.exe allowed binary"
echo.
echo === result ===
git log --oneline -1
