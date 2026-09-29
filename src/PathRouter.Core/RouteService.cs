namespace PathRouter.Core;

/// <summary>Оркестрация: создание маршрутов, «положить», обновление, проверка изменений.</summary>
public sealed class RouteService
{
    private readonly RouteRepository _repo;

    public RouteService(RouteRepository repo) => _repo = repo;

    // ---------- кэш сканирования на проход ----------

    /// <summary>
    /// В пределах одного прохода (BeginPass/EndPass) каждая папка сканируется один раз:
    /// если из одного источника идут 10 маршрутов, источник сканируется 10 -> 1 раз.
    /// Кэш используется только в CheckRoute (только чтение) — синхронизация всегда свежие сканы.
    /// </summary>
    private Dictionary<string, List<FileEntry>?>? _scanCache;

    public void BeginPass() => _scanCache ??= new Dictionary<string, List<FileEntry>?>(StringComparer.OrdinalIgnoreCase);

    public void EndPass() => _scanCache = null;

    private List<FileEntry>? Scan(string path)
    {
        if (_scanCache is not null)
        {
            if (_scanCache.TryGetValue(path, out var cached)) return cached;
            var fresh = FileScanner.ScanDirectory(path);
            _scanCache[path] = fresh;
            return fresh;
        }
        return FileScanner.ScanDirectory(path);
    }

    /// <summary>
    /// Определяет исходную папку по перетащенным элементам.
    /// Один файл -> его папка; одна папка -> она сама; несколько -> общий корень.
    /// </summary>
    public static string DetermineSourcePath(IReadOnlyList<string> items)
    {
        if (items.Count == 0) throw new ArgumentException("Нет элементов");
        if (items.Count == 1)
        {
            var item = Path.GetFullPath(items[0]);
            return Directory.Exists(item) ? item : Path.GetDirectoryName(item)!;
        }

        var dirs = items.Select(i =>
        {
            var full = Path.GetFullPath(i);
            return Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        }).ToList();

        // Разные корни (диски) — общего префикса не существует
        var root0 = Path.GetPathRoot(dirs[0]);
        if (dirs.Any(d => !string.Equals(Path.GetPathRoot(d), root0, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                "Элементы находятся на разных дисках — общую исходную папку определить нельзя. " +
                "Перетащите элементы по отдельности или укажите папку вручную.");

        // Общий префикс путей
        var split = dirs.Select(d => d.Split(Path.DirectorySeparatorChar)).ToList();
        var common = new List<string>(split[0]);
        foreach (var parts in split.Skip(1))
        {
            var n = Math.Min(common.Count, parts.Length);
            var i = 0;
            while (i < n && string.Equals(common[i], parts[i], StringComparison.OrdinalIgnoreCase)) i++;
            common = common.Take(i).ToList();
            if (common.Count == 0) break;
        }
        return string.Join(Path.DirectorySeparatorChar.ToString(), common);
    }

    /// <summary>Сценарий 1–5: создать маршрут, скопировать файлы в назначение, сохранить всё.
    /// Исходные файлы остаются на месте — источник дальше отслеживается на изменения.</summary>
    public Route CreateRoute(IReadOnlyList<string> droppedItems, string name, string destPath)
    {
        var sourcePath = DetermineSourcePath(droppedItems);
        var scan = FileScanner.ScanDirectory(sourcePath)
                   ?? throw new DirectoryNotFoundException($"Исходная папка не найдена: {sourcePath}");

        var route = _repo.InsertRoute(name, sourcePath);
        try
        {
            var dest = _repo.AddDestination(route.Id, destPath, scan);
            CopyContents(sourcePath, dest.DestPath, scan, oldManifest: null); // файлы — вне транзакции БД
            route.Destinations.Add(dest);
            return route;
        }
        catch
        {
            // копирование не удалось — не оставляем «пустой» маршрут в графе
            _repo.DeleteRoute(route.Id);
            route.Destinations.Clear();
            throw;
        }
    }

    /// <summary>Сценарий 7–8: выбран существующий маршрут -> взять файлы из его источника и положить в новую папку.</summary>
    public RouteDestination AddDestination(Route route, string destPath)
    {
        var full = Path.GetFullPath(destPath).TrimEnd('\\');
        if (string.Equals(full, route.SourcePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Папка назначения совпадает с источником маршрута.");
        if (route.Destinations.Any(d =>
                d.DestPath.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Эта папка уже прикреплена к маршруту.");

        var scan = FileScanner.ScanDirectory(route.SourcePath)
                   ?? throw new DirectoryNotFoundException($"Исходная папка не найдена: {route.SourcePath}");
        var dest = _repo.AddDestination(route.Id, destPath, scan);
        try
        {
            CopyContents(route.SourcePath, dest.DestPath, scan, oldManifest: null);
        }
        catch
        {
            // ветвь без скопированных файлов — убираем, чтобы не висела «битой»
            _repo.DeleteDestination(dest.Id);
            throw;
        }
        route.Destinations.Add(dest);
        return dest;
    }

    /// <summary>Сценарий 10: обновить назначение — привести его к актуальному состоянию источника.
    /// Удаляются только «свои» файлы (были в старом манифесте, исчезли из источника);
    /// чужие файлы в папке назначения не трогаются.</summary>
    public DiffResult UpdateDestination(Route route, RouteDestination dest)
    {
        if (dest.IsConserved)
            throw new InvalidOperationException("Ветвь в консервации — синхронизация отключена. Сначала «Вскрыть».");

        // Конфликт «обе стороны правили»: версия получателя сохраняется как копия «*.конфликт-ДАТА»,
        // затем ветвь приводится к состоянию источника как обычно.
        if (dest.Conflicts.Count > 0 && Directory.Exists(dest.DestPath))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var rel in dest.Conflicts)
            {
                var current = Path.Combine(dest.DestPath, rel);
                if (!File.Exists(current)) continue; // получатель удалил свой вариант — сохранять нечего
                var backup = Path.Combine(dest.DestPath,
                    Path.GetDirectoryName(rel) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(rel) + ".конфликт-" + stamp + Path.GetExtension(rel));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(current, backup, overwrite: false);
                }
                catch { /* не смогли сохранить копию — не блокируем синхронизацию */ }
            }
        }

        var scan = FileScanner.ScanDirectory(route.SourcePath);
        if (scan is not null)
        {
            var oldManifest = dest.Manifest; // что этот маршрут положил ранее — только это и зеркалим
            CopyContents(route.SourcePath, dest.DestPath, scan, oldManifest);
            _repo.UpdateManifest(dest.Id, scan);
            dest.Manifest = scan;
            dest.LastSyncUtc = DateTime.UtcNow;
        }
        // после синхронизации обе стороны актуальны (если источник найден)
        dest.Diff = FileScanner.Compare(scan, dest.Manifest);
        dest.DestDiff = scan is null ? null : FileScanner.CompareOwned(FileScanner.ScanDirectory(dest.DestPath), dest.Manifest);
        dest.Conflicts = new List<string>();
        return dest.Diff;
    }

    /// <summary>Сценарий 9: проверить маршрут в обе стороны — источник vs снапшот и каждый получатель vs снапшот.
    /// Законсервированные ветви не проверяются: их целостность намеренно не отслеживается.</summary>
    public DiffResult CheckRoute(Route route)
    {
        var scan = Scan(route.SourcePath); // через кэш прохода: общий источник сканируется один раз
        var merged = new DiffResult();
        foreach (var dest in route.Destinations)
        {
            if (dest.IsConserved)
            {
                dest.Diff = null;
                dest.DestDiff = null;
                dest.Conflicts = new List<string>();
                continue;
            }
            dest.Diff = FileScanner.Compare(scan, dest.Manifest);
            var destScan = scan is null ? null : Scan(dest.DestPath);
            dest.DestDiff = scan is null ? null : FileScanner.CompareOwned(destScan, dest.Manifest);
            dest.Conflicts = FileScanner.FindConflicts(scan, destScan, dest.Manifest);
            merged.Added = Math.Max(merged.Added, dest.Diff.Added);
            merged.Modified = Math.Max(merged.Modified, dest.Diff.Modified);
            merged.Deleted = Math.Max(merged.Deleted, dest.Diff.Deleted);
            merged.SourceMissing |= dest.Diff.SourceMissing;
        }
        route.HasUpdates = route.Destinations.Count == 0
            ? scan is null
            : route.Destinations.Any(d => d.HasUpdates);
        if (route.Destinations.Count == 0 && scan is not null) route.HasUpdates = false;
        return merged;
    }

    // ---------- файловые операции ----------

    /// <summary>
    /// Копирует содержимое source в dest. oldManifest != null — привести dest к состоянию source,
    /// но удалять только файлы из oldManifest, которых больше нет в source (чужие файлы не трогаем).
    /// </summary>
    private static void CopyContents(string sourcePath, string destPath, List<FileEntry> scan, List<FileEntry>? oldManifest)
    {
        EnsureNotSame(sourcePath, destPath);
        Directory.CreateDirectory(destPath);
        var curHashes = scan.ToDictionary(f => f.RelPath, f => f.Hash);

        foreach (var entry in scan)
        {
            var src = Path.Combine(sourcePath, entry.RelPath);
            var dst = Path.Combine(destPath, entry.RelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }

        if (oldManifest is not null)
        {
            foreach (var f in oldManifest)
            {
                if (curHashes.ContainsKey(f.RelPath)) continue; // ещё актуален
                var dst = Path.Combine(destPath, f.RelPath);
                try { if (File.Exists(dst)) File.Delete(dst); } catch { /* занят — пропускаем */ }
            }
            RemoveEmptyDirs(destPath);
        }
    }

    private static void EnsureNotSame(string a, string b)
    {
        if (string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                          Path.GetFullPath(b).TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Источник и назначение совпадают.");
    }

    private static void RemoveEmptyDirs(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                              .OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { /* занято — оставляем */ }
        }
    }
}
