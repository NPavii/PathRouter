using System.Text;

namespace PathRouter.Core;

/// <summary>Сканирование папок и сравнение снапшотов.</summary>
public static class FileScanner
{
    /// <summary>Рекурсивно сканирует папку. Возвращает null, если папки нет.</summary>
    public static List<FileEntry>? ScanDirectory(string path)
    {
        if (!Directory.Exists(path)) return null;

        var root = Path.GetFullPath(path);
        var result = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    var rel = Path.GetRelativePath(root, file);
                    result.Add(new FileEntry
                    {
                        RelPath = rel,
                        Size = fi.Length,
                        LastWriteTicks = fi.LastWriteTimeUtc.Ticks,
                        Hash = ComputeHash(rel, fi.Length, fi.LastWriteTimeUtc.Ticks)
                    });
                }
                catch (Exception) { /* файл недоступен — пропускаем */ }
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir)) pending.Push(sub);
            }
            catch (Exception) { /* нет доступа — пропускаем */ }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
        return result;
    }

    public static string ComputeHash(string relPath, long size, long lastWriteTicks)
    {
        // FNV-1a 64-bit по строке "путь|размер|mtime"
        ulong hash = 14695981039346656037UL;
        void Feed(byte b) { hash ^= b; hash *= 1099511628211UL; }
        foreach (var ch in relPath) { Feed((byte)(ch & 0xFF)); Feed((byte)(ch >> 8)); }
        Feed((byte)'|');
        foreach (var ch in size.ToString()) Feed((byte)ch);
        Feed((byte)'|');
        foreach (var ch in lastWriteTicks.ToString()) Feed((byte)ch);

        return hash.ToString("x16");
    }

    /// <summary>Сравнивает текущее состояние с манифестом последней синхронизации.</summary>
    public static DiffResult Compare(List<FileEntry>? current, List<FileEntry>? manifest)
    {
        var diff = new DiffResult();
        if (current is null)
        {
            diff.SourceMissing = true;
            return diff;
        }
        manifest ??= new List<FileEntry>();

        var curByPath = current.ToDictionary(f => f.RelPath, f => f);
        var manByPath = manifest.ToDictionary(f => f.RelPath, f => f);

        foreach (var (path, cur) in curByPath)
        {
            if (!manByPath.TryGetValue(path, out var man)) diff.Added++;
            else if (cur.Hash != man.Hash) diff.Modified++;
        }
        foreach (var path in manByPath.Keys)
            if (!curByPath.ContainsKey(path)) diff.Deleted++;

        return diff;
    }

    /// <summary>
    /// Двусторонняя проверка «своих» файлов в папке назначения: маршрут отвечает только
    /// за то, что сам положил (манифест). Чужие файлы в папке игнорируются — они не появляются
    /// в диффе и не удаляются при синхронизации.
    /// Deleted — наши файлы, которых в получателе больше нет; Modified — наши файлы перезаписали.
    /// </summary>
    public static DiffResult CompareOwned(List<FileEntry>? current, List<FileEntry>? manifest)
    {
        var diff = new DiffResult();
        manifest ??= new List<FileEntry>();
        if (current is null)
        {
            // папка назначения пропала: всё, что мы туда клали, считаем утраченным
            diff.Deleted = manifest.Count;
            return diff;
        }

        var curByPath = current.ToDictionary(f => f.RelPath, f => f);
        foreach (var man in manifest)
        {
            if (!curByPath.TryGetValue(man.RelPath, out var cur)) diff.Deleted++;
            else if (cur.Hash != man.Hash) diff.Modified++;
        }
        return diff;
    }
}
