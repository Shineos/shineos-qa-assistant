#:package Microsoft.Data.Sqlite@10.0.0
using Microsoft.Data.Sqlite;

var dbPath = "D:/dev/shineos-local-ai/output/backend-dev-data/knowledge.db";
await using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

long chats;
await using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM chats";
    chats = (long)cmd.ExecuteScalar()!;
}
await using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "DELETE FROM answer_cache";
    cmd.ExecuteNonQuery();
}
long cacheLeft;
await using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM answer_cache";
    cacheLeft = (long)cmd.ExecuteScalar()!;
}
Console.WriteLine($"chats={chats} answer_cache rows remaining={cacheLeft}");
