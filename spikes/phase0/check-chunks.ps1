Add-Type -Path 'D:\dev\shineos-local-ai\app\ShineosQA.Backend\bin\Release\net10.0\Microsoft.Data.Sqlite.dll'
$conn = [Microsoft.Data.Sqlite.SqliteConnection]::new('Data Source=D:\dev\shineos-local-ai\output\backend-dev-data\knowledge.db')
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = 'SELECT c.chunk_id, c.file_id, f.name, length(c.text) AS len, substr(c.text,1,80) AS head, length(c.emb) AS emblen FROM chunks c JOIN files f ON f.file_id=c.file_id ORDER BY c.chunk_id'
$r = $cmd.ExecuteReader()
while ($r.Read()) {
  Write-Output ("chunk={0} file={1} [{2}] textlen={3} emblen={4} head={5}" -f $r['chunk_id'], $r['file_id'], $r['name'], $r['len'], $r['emblen'], $r['head'])
}
$r.Close(); $conn.Close()
