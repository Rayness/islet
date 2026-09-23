using System.Data.OleDb;
using System.Text;

namespace Islet.Search;

/// <summary>
/// Файлы и папки через индекс Windows Search — тот же, что у поиска в Пуске и
/// Проводнике. Своего индекса не строим: он уже есть и всегда актуален.
/// </summary>
internal static class FileSearch
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public static Task<List<ResultItem>> SearchAsync(string query, int max) =>
        Task.Run(() =>
        {
            // Служба поиска изредка отвечает разовым E_FAIL — второй запрос проходит.
            try { return Query(query, max); }
            catch (Exception first)
            {
                Log.Write($"Windows Search failed, retrying: {first.Message}");
                try { return Query(query, max); }
                catch (Exception e)
                {
                    // Служба выключена или индекс недоступен — просто без файлов.
                    Log.Write($"Windows Search failed: {e.Message}");
                    return [];
                }
            }
        });

    private static List<ResultItem> Query(string query, int max)
    {
        var words = Tokenize(query);
        if (words.Count == 0) return [];

        // Каждое слово — префикс имени: «отч 2026» найдёт «Отчёт за 2026.xlsx».
        var condition = string.Join(" AND ", words.Select(w => $"\"{w}*\""));
        var sql =
            $"SELECT TOP {max} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemFolderPathDisplay, System.ItemType " +
            "FROM SystemIndex " +
            $"WHERE SCOPE='file:' AND CONTAINS(System.FileName, '{condition}') " +
            "AND System.FileExtension <> '.lnk' " +
            "ORDER BY System.Search.Rank DESC";

        var results = new List<ResultItem>();
        using var connection = new OleDbConnection(ConnectionString);
        connection.Open();
        using var command = new OleDbCommand(sql, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(0) is not string path) continue;
            var name = reader.GetValue(1) as string ?? Path.GetFileName(path);
            var folder = reader.GetValue(2) as string ?? "";
            var isFolder = string.Equals(reader.GetValue(3) as string, "Directory", StringComparison.OrdinalIgnoreCase);

            results.Add(new ResultItem
            {
                Title = name,
                Subtitle = folder,
                Kind = isFolder ? ResultKind.Folder : ResultKind.File,
                Target = path,
                IconSource = path,
                ProviderId = "files",
            });
        }
        return results;
    }

    /// <summary>
    /// Слова запроса без символов, которые что-то значат для языка запросов
    /// (кавычки, звёздочки, скобки). Заодно это и защита от инъекции.
    /// </summary>
    private static List<string> Tokenize(string query)
    {
        var words = new List<string>();
        foreach (var raw in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
                    sb.Append(c);
            }
            if (sb.Length > 0) words.Add(sb.ToString());
        }
        return words;
    }
}
