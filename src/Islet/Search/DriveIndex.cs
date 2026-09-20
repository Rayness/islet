using System.Diagnostics;
using System.Runtime.InteropServices;
using Islet.Settings;

namespace Islet.Search;

/// <summary>
/// Свой индекс имён файлов для дисков и папок, которых нет в Windows Search
/// (по умолчанию он смотрит только пользовательские папки на системном диске).
///
/// ПАМЯТЬ. На обычной машине это миллионы записей, поэтому никаких строк на
/// запись: все имена лежат подряд в одном массиве символов, у записи — смещение,
/// длина и номер родительской папки. Полный путь собирается только для
/// найденного. Так 1,9 млн записей занимают около 90 МБ, а не 360.
///
/// Кешируется на диск, чтобы после перезапуска искать сразу, а не ждать
/// обхода. Изменения ловит FileSystemWatcher: новые пути дописываются в
/// «хвост», удалённые — в список исключённых. Полный обход — при запуске
/// (если кеш старше суток), по кнопке в настройках и при переполнении watcher'а.
/// </summary>
internal sealed class DriveIndex : IDisposable
{
    private const int CacheVersion = 2;
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(24);
    private static readonly string CachePath = Path.Combine(Paths.Cache, "drive-index.bin");

    private sealed class Snapshot
    {
        public string[] Roots = [];
        public char[] Chars = [];
        public int[] NameStart = [];
        public byte[] NameLength = [];   // имя в NTFS не длиннее 255 символов
        /// <summary>Номер записи-родителя; отрицательный — корень: -(номер корня + 1).</summary>
        public int[] Parent = [];
        public bool[] IsDir = [];
        public DateTime BuiltAt;
        public string RootsKey = "";

        public int Count => NameStart.Length;

        public ReadOnlySpan<char> Name(int i) => Chars.AsSpan(NameStart[i], NameLength[i]);

        public string FullPath(int i)
        {
            var parts = new Stack<int>();
            var at = i;
            while (at >= 0)
            {
                parts.Push(at);
                at = Parent[at];
            }
            var path = Roots[-at - 1];
            foreach (var part in parts)
                path = Path.Join(path, Name(part));
            return path;
        }
    }

    private readonly Lock _gate = new();
    private Snapshot _snapshot = new();
    private readonly List<string> _added = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _scanCts;

    public bool IsScanning { get; private set; }
    public int Count => _snapshot.Count + _added.Count;
    public DateTime BuiltAt => _snapshot.BuiltAt;
    public string? LastError { get; private set; }

    /// <summary>Состояние поменялось (начат/закончен обход). Может прийти с фонового потока.</summary>
    public event Action? StatusChanged;

    public void Start()
    {
        var settings = SettingsStore.Current;
        StopWatchers();
        if (!settings.DriveIndexEnabled)
        {
            _scanCts?.Cancel();
            lock (_gate) { _snapshot = new(); _added.Clear(); _removed.Clear(); }
            ReleaseMemory();
            StatusChanged?.Invoke();
            return;
        }

        var roots = settings.EffectiveRoots();
        var key = RootsKey(roots, settings.IndexExcludes);

        _ = Task.Run(() =>
        {
            if (_snapshot.RootsKey != key)
            {
                var cached = TryLoadCache();
                if (cached is not null && cached.RootsKey == key)
                {
                    lock (_gate) _snapshot = cached;
                    ReleaseMemory();
                    StatusChanged?.Invoke();
                    Log.Write($"drive index: {cached.Count} entries from cache ({cached.BuiltAt.ToLocalTime():g})");
                }
            }

            StartWatchers(roots);
            if (_snapshot.RootsKey != key || DateTime.UtcNow - _snapshot.BuiltAt > MaxCacheAge)
                Rescan();
        });
    }

    public void Rescan()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var settings = SettingsStore.Current;
        var roots = settings.EffectiveRoots();
        var excludes = new HashSet<string>(settings.IndexExcludes, StringComparer.OrdinalIgnoreCase);
        var key = RootsKey(roots, settings.IndexExcludes);

        _ = Task.Run(() =>
        {
            IsScanning = true;
            LastError = null;
            StatusChanged?.Invoke();
            var sw = Stopwatch.StartNew();
            try
            {
                var snapshot = Scan(roots, excludes, cts.Token);
                snapshot.RootsKey = key;
                lock (_gate)
                {
                    _snapshot = snapshot;
                    _added.Clear();
                    _removed.Clear();
                }
                SaveCache(snapshot);
                ReleaseMemory();
                Log.Write($"drive index: {snapshot.Count} entries in {sw.Elapsed.TotalSeconds:F1}s, {snapshot.Chars.Length / 1024 / 1024} MB of names");
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Write($"drive index failed: {e}");
            }
            finally
            {
                if (_scanCts == cts)
                {
                    IsScanning = false;
                    StatusChanged?.Invoke();
                }
            }
        });
    }

    /// <summary>Обход и чтение кеша оставляют гигабайтный мусор — отдаём его системе сразу.</summary>
    private static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    public List<ResultItem> Search(string query, int max)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        Snapshot snap;
        string[] added;
        HashSet<string> removed;
        lock (_gate)
        {
            snap = _snapshot;
            added = [.. _added];
            removed = _removed.Count > 0 ? new(_removed, StringComparer.OrdinalIgnoreCase) : [];
        }

        var sw = Stopwatch.StartNew();
        // Сначала только номера и оценки: путь строим лишь для попавших в итог.
        // Проход по миллионам имён — по кускам на всех ядрах, у каждого свой список лучших.
        var chunks = Math.Max(1, Math.Min(Environment.ProcessorCount, snap.Count / 50_000));
        var chunkSize = (snap.Count + chunks - 1) / chunks;
        var locals = new List<(int Score, int Length, int Index, string? Path)>[chunks];
        Parallel.For(0, chunks, c =>
        {
            var local = new List<(int Score, int Length, int Index, string? Path)>();
            var end = Math.Min(snap.Count, (c + 1) * chunkSize);
            for (var i = c * chunkSize; i < end; i++)
            {
                var score = Score(snap.Name(i), words);
                if (score > 0)
                    Consider(local, (score, snap.NameLength[i], i, null), max);
            }
            locals[c] = local;
        });

        var best = new List<(int Score, int Length, int Index, string? Path)>();
        foreach (var local in locals)
            foreach (var item in local)
                Consider(best, item, max);
        foreach (var path in added)
        {
            var score = Score(Path.GetFileName(path), words);
            if (score > 0)
                Consider(best, (score, Path.GetFileName(path).Length, -1, path), max);
        }

        var results = best
            .OrderByDescending(b => b.Score)
            .ThenBy(b => b.Length)
            .Select(b => (Path: b.Path ?? snap.FullPath(b.Index), IsDir: b.Index >= 0 ? snap.IsDir[b.Index] : Directory.Exists(b.Path)))
            .Where(b => !removed.Contains(b.Path))
            .Take(max)
            .Select(b => new ResultItem
            {
                Title = Path.GetFileName(b.Path),
                Subtitle = Path.GetDirectoryName(b.Path) ?? "",
                Kind = b.IsDir ? ResultKind.Folder : ResultKind.File,
                Target = b.Path,
                IconSource = b.Path,
            })
            .ToList();

        if (sw.ElapsedMilliseconds > 60)
            Log.Write($"drive index search '{query}' took {sw.ElapsedMilliseconds} ms over {snap.Count} entries");
        return results;
    }

    /// <summary>2 — каждое слово начинает имя или слово в имени, 1 — просто входит, 0 — нет.</summary>
    private static int Score(ReadOnlySpan<char> name, string[] words)
    {
        var score = 2;
        foreach (var w in words)
        {
            var at = name.IndexOf(w, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return 0;
            if (at != 0 && char.IsLetterOrDigit(name[at - 1])) score = 1;
        }
        return score;
    }

    private static void Consider(List<(int Score, int Length, int Index, string? Path)> best, (int Score, int Length, int Index, string? Path) item, int max)
    {
        // Держим небольшой запас лучших (часть может оказаться удалённой), не сортируя весь индекс.
        if (best.Count < max * 3)
        {
            best.Add(item);
            return;
        }
        var worst = 0;
        for (var i = 1; i < best.Count; i++)
        {
            if (best[i].Score < best[worst].Score ||
                (best[i].Score == best[worst].Score && best[i].Length > best[worst].Length))
                worst = i;
        }
        var w = best[worst];
        if (item.Score > w.Score || (item.Score == w.Score && item.Length < w.Length))
            best[worst] = item;
    }

    // ------------------------------------------------------------------

    private static Snapshot Scan(List<string> roots, HashSet<string> excludes, CancellationToken ct)
    {
        var chars = new char[1 << 20];
        var charCount = 0;
        var nameStart = new List<int>();
        var nameLength = new List<byte>();
        var parent = new List<int>();
        var isDir = new List<bool>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var existingRoots = roots.Where(Directory.Exists).ToArray();
        var stack = new Stack<(string Path, int Entry)>();
        for (var r = 0; r < existingRoots.Length; r++)
            stack.Push((existingRoots[r], -(r + 1)));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, dirEntry) = stack.Pop();

            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    var name = entry.Name;
                    var directory = (entry.Attributes & FileAttributes.Directory) != 0;
                    if (name.Length > 255 || (directory && excludes.Contains(name)))
                        continue;

                    if (charCount + name.Length > chars.Length)
                        Array.Resize(ref chars, chars.Length * 2);
                    name.CopyTo(chars.AsSpan(charCount));

                    var index = nameStart.Count;
                    nameStart.Add(charCount);
                    nameLength.Add((byte)name.Length);
                    parent.Add(dirEntry);
                    isDir.Add(directory);
                    charCount += name.Length;

                    if (directory)
                        stack.Push((entry.FullName, index));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Папка исчезла или закрыта — пропускаем.
            }
        }

        return new Snapshot
        {
            Roots = existingRoots,
            Chars = chars[..charCount],
            NameStart = [.. nameStart],
            NameLength = [.. nameLength],
            Parent = [.. parent],
            IsDir = [.. isDir],
            BuiltAt = DateTime.UtcNow,
        };
    }

    private static string RootsKey(List<string> roots, List<string> excludes) =>
        string.Join("|", roots.Select(r => r.ToLowerInvariant()).Order()) + "#" +
        string.Join("|", excludes.Select(e => e.ToLowerInvariant()).Order());

    private static void SaveCache(Snapshot s)
    {
        try
        {
            Directory.CreateDirectory(Paths.Cache);
            var tmp = CachePath + ".tmp";
            using (var stream = File.Create(tmp))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(CacheVersion);
                w.Write(s.RootsKey);
                w.Write(s.BuiltAt.ToBinary());
                w.Write(s.Roots.Length);
                foreach (var root in s.Roots) w.Write(root);
                w.Write(s.Chars.Length);
                w.Write(s.Count);
                w.Flush();
                stream.Write(MemoryMarshal.AsBytes(s.Chars.AsSpan()));
                stream.Write(MemoryMarshal.AsBytes(s.NameStart.AsSpan()));
                stream.Write(s.NameLength);
                stream.Write(MemoryMarshal.AsBytes(s.Parent.AsSpan()));
                stream.Write(MemoryMarshal.AsBytes(s.IsDir.AsSpan()));
            }
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write($"drive index cache not saved: {e.Message}");
        }
    }

    private static Snapshot? TryLoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            using var stream = File.OpenRead(CachePath);
            using var r = new BinaryReader(stream);
            if (r.ReadInt32() != CacheVersion) return null;
            var s = new Snapshot
            {
                RootsKey = r.ReadString(),
                BuiltAt = DateTime.FromBinary(r.ReadInt64()),
            };
            s.Roots = new string[r.ReadInt32()];
            for (var i = 0; i < s.Roots.Length; i++) s.Roots[i] = r.ReadString();
            var charCount = r.ReadInt32();
            var count = r.ReadInt32();

            s.Chars = new char[charCount];
            s.NameStart = new int[count];
            s.NameLength = new byte[count];
            s.Parent = new int[count];
            s.IsDir = new bool[count];
            stream.ReadExactly(MemoryMarshal.AsBytes(s.Chars.AsSpan()));
            stream.ReadExactly(MemoryMarshal.AsBytes(s.NameStart.AsSpan()));
            stream.ReadExactly(s.NameLength);
            stream.ReadExactly(MemoryMarshal.AsBytes(s.Parent.AsSpan()));
            stream.ReadExactly(MemoryMarshal.AsBytes(s.IsDir.AsSpan()));
            return s;
        }
        catch (Exception e)
        {
            Log.Write($"drive index cache unreadable: {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------

    private void StartWatchers(List<string> roots)
    {
        var excludes = SettingsStore.Current.IndexExcludes;
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => OnAdded(e.FullPath, excludes);
                watcher.Deleted += (_, e) => OnRemoved(e.FullPath);
                watcher.Renamed += (_, e) => { OnRemoved(e.OldFullPath); OnAdded(e.FullPath, excludes); };
                watcher.Error += (_, e) =>
                {
                    Log.Write($"watcher overflow on {root}: {e.GetException().Message}; rescanning");
                    Rescan();
                };
                watcher.EnableRaisingEvents = true;
                lock (_gate) _watchers.Add(watcher);
            }
            catch (Exception e)
            {
                Log.Write($"watcher failed on {root}: {e.Message}");
            }
        }
    }

    private void OnAdded(string path, List<string> excludes)
    {
        foreach (var part in path.Split(Path.DirectorySeparatorChar))
        {
            if (excludes.Contains(part, StringComparer.OrdinalIgnoreCase))
                return;
        }
        lock (_gate)
        {
            _removed.Remove(path);
            // Хвост не должен расти без конца: при массовых изменениях проще переобойти.
            if (_added.Count < 50_000)
                _added.Add(path);
        }
    }

    private void OnRemoved(string path)
    {
        lock (_gate)
        {
            _added.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (_removed.Count < 50_000)
                _removed.Add(path);
        }
    }

    private void StopWatchers()
    {
        lock (_gate)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        StopWatchers();
    }
}
