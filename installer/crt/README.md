# VC++ ランタイム（CRT）アプリローカル同梱について

このフォルダの DLL は llama.cpp エンジン（`llama-server.exe` + 同梱DLL一式）が
動的リンクする Visual C++ 2015-2022 再頒布可能ランタイムであり、
インストーラの `{app}\engine` に配置される。

| ファイル | バージョン |
|---|---|
| msvcp140.dll | 14.50.35719.0 |
| vcruntime140.dll | 14.50.35719.0 |
| vcruntime140_1.dll | 14.50.35719.0 |

## なぜ同梱するか

llama.cpp 公式プリビルド（`llama-b10936-bin-win-cpu-x64.zip`）は CRT 動的リンクで、
VC++ 再頒布可能パッケージが導入済みの PC でしか起動しない。
クリーンな Windows 10/11 には CRT が同梱されていないため、エンジンが
「MSVCP140.dll was not found」で起動失敗し、アプリ全体が
`SHINE_E_ENGINE_DOWN`（AIエンジンの起動に失敗しました）になる。

Microsoft Store 審査（2026-09-28, ポリシー 10.1.2.10 / 10.2.4.1）でこの実害を
指摘されたため、CRT をアプリに統合して依存を解消した。
`{app}\engine` は `llama-server.exe` と同じフォルダであり、Windows ローダーの
DLL 検索順でシステム（System32）より優先的に解決される。

## 出所とライセンス

バージョン 14.50.35719.0（Microsoft Visual C++ Redistributable）は、
Microsoft Visual Studio/VC++ 再頒布可能パッケージのライセンス条項に基づき
アプリケーションと共に再頒布可能である。各 DLL は Microsoft の Authenticode
署名付きの original binary である。
