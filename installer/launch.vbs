' 社内知恵袋 v2 ランチャ: バックエンドを非表示起動してから WebView2 ラッパーを開く
' （ラッパーを閉じるとラッパー側がバックエンドを停止する）
Dim sh, already
Set sh = CreateObject("WScript.Shell")
sh.CurrentDirectory = Replace(WScript.ScriptFullName, "\launch.vbs", "")

' ヘルスチェック（失敗したら未起動と判断して非表示起動する）
already = False
On Error Resume Next
Dim http
Set http = CreateObject("MSXML2.XMLHTTP")
http.Open "GET", "http://127.0.0.1:8300/health", False
http.Send ""
If http.Status = 200 Then already = True
On Error GoTo 0

If Not already Then
  sh.Run """ShineosQA.Backend.exe"" --config config.json", 0, False
  WScript.Sleep 2500
End If

sh.Run """ShineosQA.exe""", 1, False
