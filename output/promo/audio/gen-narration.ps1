$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice("Microsoft Haruka Desktop")
$synth.Rate = -1
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(44100, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$outDir = "D:\dev\shineos-local-ai\output\promo\audio"

$segments = @{
  "seg0-opening" = "社内の規定やマニュアルを探すのに、時間がかかっていませんか。社内知恵袋は、社内文書から答えを見つける、完全オフラインの社内Q&Aアプリです。"
  "seg1-question" = "質問すると、登録された社内文書を検索して、根拠とともに回答します。出典をクリックすれば、該当箇所をその場で確認できます。"
  "seg2-knowledge" = "社内文書は、PDF、Word、マークダウン、テキストに対応。ドラッグアンドドロップで追加でき、追加した資料は、すぐに検索対象になります。"
  "seg3-ask-new" = "追加したばかりの資料についても、すぐに質問できます。"
  "seg4-model" = "回答AIは、高速のクイックから、高精度の高品質まで、三段階。画面からすぐに切り替えられます。"
  "seg5-settings" = "Web検索は既定でオフ。質問も社内文書も、外部には一切送信されません。"
  "seg6-ending" = "社内知恵袋。社内の知識を、パソコンの中だけで。詳しくは、Shineosのウェブサイトをご覧ください。"
}

foreach ($name in $segments.Keys) {
  $path = Join-Path $outDir "$name.wav"
  $synth.SetOutputToWaveFile($path, $fmt)
  $synth.Speak($segments[$name])
  $synth.SetOutputToNull()
  Write-Host "OK $name"
}
$synth.Dispose()
