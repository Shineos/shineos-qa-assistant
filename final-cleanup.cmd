@echo off
cd /d D:\dev\shineos-local-ai
git add -A
git commit -m "chore: remove temporary commit/push helper scripts and stray file"
git push origin feature/architecture-redesign
git log --oneline -2
