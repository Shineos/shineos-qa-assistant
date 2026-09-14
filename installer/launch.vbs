' 社内知恵袋 v2 ランチャ: バックエンドを非表示起動してブラウザでアプリを開く
' 既に起動済みならブラウザだけ開く（二重起動しない）
Dim sh, procs, already, i
Set sh = CreateObject("WScript.Shell")
already = False
On Error Resume Next
Set procs = GetObject("winmgmts:").ExecQuery("SELECT * FROM Win32_Process WHERE Name = 'ShineosQA.Backend.exe'")
already = (procs.Count > 0)
On Error GoTo 0
If Not already Then
  sh.CurrentDirectory = Replace(WScript.ScriptFullName, "\launch.vbs", "")
  sh.Run """ShineosQA.Backend.exe"" --config config.json", 0, False
  WScript.Sleep 2500
End If
sh.Run "http://127.0.0.1:8300/", 1, False
