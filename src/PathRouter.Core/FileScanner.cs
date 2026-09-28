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
}
