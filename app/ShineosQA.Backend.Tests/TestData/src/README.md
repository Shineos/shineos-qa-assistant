# テストフィクスチャPDFの再生成

これらは Microsoft Edge ヘッドレス印刷で生成した実物相当のPDF（テキスト層あり・CID/TrueType埋め込み）。
上書き生成コマンド（Git Bash・Windows）:

    EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
    "$EDGE" --headless=new --disable-gpu --user-data-dir="<一時dir>" --no-pdf-header-footer \
      --print-to-pdf="<repo>/app/ShineosQA.Backend.Tests/TestData/<name>.pdf" \
      "file:///<repo>/app/ShineosQA.Backend.Tests/TestData/src/<name>.html"

- cid-japanese.pdf : 日本語文章（旅費規程風・図面ではない）
- drawing-sample.pdf : 図番 ST-1042A・品名 サポートブラケット・材質 SS400・改訂B の自作合成図面
- no-text.pdf : テキスト層なし（青背景のみ）
