using PathRouter.Core;

// Временная песочница
var root = Path.Combine(Path.GetTempPath(), "PathRouterSmoke_" + Guid.NewGuid().ToString("N")[..8]);
var source = Path.Combine(root, "Docs");
var dest1 = Path.Combine(root, "Out1");
var dest2 = Path.Combine(root, "Out2");
Directory.CreateDirectory(source);
Directory.CreateDirectory(dest1);
Directory.CreateDirectory(dest2);

var db = Path.Combine(root, "test.db");
int failed = 0;
void Check(bool cond, string what)
{
    Console.WriteLine((cond ? "PASS  " : "FAIL  ") + what);
    if (!cond) failed++;
}

// Directory.Move под Windows иногда ловит «Access denied» из-за внешних индексаторов —
// делаем несколько попыток с паузой.
static void MoveWithRetry(string from, string to)
{
    for (int i = 0; ; i++)
    {
        try { Directory.Move(from, to); return; }
        catch (IOException) when (i < 5) { Thread.Sleep(200); }
    }
}

try
{
    using var repo = new RouteRepository(db);
    var svc = new RouteService(repo);

    // --- Сценарий 1–5: создать маршрут и положить ---
    File.WriteAllText(Path.Combine(source, "a.txt"), "v1");
    File.WriteAllText(Path.Combine(source, "b.txt"), "v1");
    var route = svc.CreateRoute(new[] { Path.Combine(source, "a.txt"), Path.Combine(source, "b.txt") },
        "Автомат коробов документация", dest1);

    Check(route.SourcePath == Path.GetFullPath(source), "источник определён");
    Check(File.Exists(Path.Combine(source, "a.txt")) && File.Exists(Path.Combine(source, "b.txt")), "исходные файлы остались на месте");
    Check(File.Exists(Path.Combine(dest1, "a.txt")) && File.Exists(Path.Combine(dest1, "b.txt")), "файлы лежат в назначении 1");
    Check(route.Destinations.Count == 1, "одна ветвь маршрута");

    // --- Сценарий 7–8: тот же маршрут -> второе назначение ---
    File.WriteAllText(Path.Combine(source, "a.txt"), "v2"); // новая документация в источнике
    var d2 = svc.AddDestination(route, dest2);
    Check(File.Exists(Path.Combine(dest2, "a.txt")), "файлы скопированы в назначение 2");
    Check(File.ReadAllText(Path.Combine(dest2, "a.txt")) == "v2", "свежее содержимое во 2-м назначении");

    // --- Сценарий 9: обновили документацию -> есть отличия ---
    File.WriteAllText(Path.Combine(source, "c.txt"), "new");
    File.Delete(Path.Combine(source, "a.txt"));
    repo.GetRoutes(true, true); // reload not needed; check in-memory
    svc.CheckRoute(route);
    Check(route.HasUpdates, "обнаружены изменения (+c.txt, -a.txt)");
    var d0 = route.Destinations[0].Diff!;
    Check(d0 is { Added: 1, Deleted: 1 }, "дифф первой ветви корректен (+c.txt, -a.txt; b.txt на месте)");

    // --- Сценарий 10: обновить первую ветвь ---
    svc.UpdateDestination(route, route.Destinations[0]);
    Check(!File.Exists(Path.Combine(dest1, "a.txt")), "удалённый файл убран из назначения 1");
    Check(File.Exists(Path.Combine(dest1, "c.txt")), "новый файл в назначении 1");
    svc.CheckRoute(route);
    Check(!route.Destinations[0].HasUpdates, "после обновления ветвь актуальна");
    Check(route.Destinations[1].HasUpdates, "вторая ветвь всё ещё требует обновления");

    // --- Механика «≠»: маршрут отвечает только за свои файлы ---
    svc.UpdateDestination(route, route.Destinations[1]);
    svc.CheckRoute(route);
    Check(!route.HasUpdates, "после обновления всех ветвей актуально");
    // чужой файл в папке получателя: соседний маршрут или пользователь положил своё
    File.WriteAllText(Path.Combine(dest2, "post_modified.txt"), "локальная правка");
    svc.CheckRoute(route);
    Check(!route.Destinations[1].HasUpdates,
          "чужой файл в получателе игнорируется (≠ не загорается)");
    // а вот если пропал НАШ файл — это ≠, нужно восстановление
    File.Delete(Path.Combine(dest2, "b.txt"));
    svc.CheckRoute(route);
    Check(route.Destinations[1].HasUpdates
          && route.Destinations[1].DestDiff?.Deleted == 1
          && route.Destinations[1].Diff?.Changed == false,
          "детект пропажи своего файла в получателе (≠), источник не тронут");
    svc.UpdateDestination(route, route.Destinations[1]);
    Check(File.Exists(Path.Combine(dest2, "b.txt")), "после синхронизации свой файл восстановлен из источника");
    Check(File.Exists(Path.Combine(dest2, "post_modified.txt")), "чужой файл получателя НЕ тронут");
    svc.CheckRoute(route);
    Check(!route.Destinations[1].HasUpdates, "после восстановления ветвь актуальна");

    // Перезапись своего файла в получателе — тоже ≠
    File.WriteAllText(Path.Combine(dest2, "b.txt"), "подмена!");
    svc.CheckRoute(route);
    Check(route.Destinations[1].DestDiff?.Modified == 1 && route.Destinations[1].HasUpdates,
          "перезапись своего файла в получателе детектится (~1)");
    svc.UpdateDestination(route, route.Destinations[1]);
    Check(File.ReadAllText(Path.Combine(dest2, "b.txt")) == "v1", "свой файл возвращён к состоянию источника");
    Check(File.Exists(Path.Combine(dest2, "post_modified.txt")), "чужой файл по-прежнему на месте");

    // --- Два маршрута в одну папку назначения: каждый отвечает только за своё ---
    var cSrc = Path.Combine(root, "SrcC");
    Directory.CreateDirectory(cSrc);
    File.WriteAllText(Path.Combine(cSrc, "mine_c.txt"), "C");
    var routeY = svc.CreateRoute(new[] { cSrc }, "Маршрут C в общую папку", dest2);
    // dest2 теперь общая: файлы маршрута (b,c) + маршрута Y (mine_c) + посторонний post_modified
    File.Delete(Path.Combine(cSrc, "mine_c.txt"));
    svc.CheckRoute(routeY);
    svc.UpdateDestination(routeY, routeY.Destinations[0]);
    Check(!File.Exists(Path.Combine(dest2, "mine_c.txt")), "маршрут Y удалил свой исчезнувший файл из общей папки");
    Check(File.Exists(Path.Combine(dest2, "b.txt")) && File.Exists(Path.Combine(dest2, "c.txt")),
        "файлы чужого маршрута в общей папке не тронуты");
    Check(File.Exists(Path.Combine(dest2, "post_modified.txt")), "посторонний файл в общей папке не тронут");
    Check(File.Exists(Path.Combine(cSrc, "mine_c.txt")) == false, "источник Y сам по себе: файл удалён, как задумано");
    repo.DeleteRoute(routeY.Id);

    // --- Запрет дублей назначений ---
    try { svc.AddDestination(route, dest2); Check(false, "дубль назначения отклонён"); }
    catch (InvalidOperationException) { Check(true, "дубль назначения отклонён"); }
    try { svc.AddDestination(route, source); Check(false, "назначение=источник отклонено"); }
    catch (InvalidOperationException) { Check(true, "назначение=источник отклонено"); }

    // --- rename / archive / hide / delete ---
    repo.RenameRoute(route.Id, "Новое имя");
    repo.SetArchived(route.Id, true);
    repo.SetHidden(route.Id, true);
    var all = repo.GetRoutes(true, true);
    Check(all.Count == 1 && all[0].Name == "Новое имя" && all[0].IsArchived && all[0].IsHidden, "rename/archive/hide");
    Check(repo.GetRoutes(false, true).Count == 0, "архивный скрыт без флага");
    repo.DeleteRoute(route.Id);
    Check(repo.GetRoutes(true, true).Count == 0, "удаление маршрута");

    // ---------- юнит-проверки новой логики ----------

    // DetermineSourcePath: один файл -> его папка
    var single = Path.Combine(source, "sub", "x.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(single)!);
    File.WriteAllText(single, "x");
    Check(RouteService.DetermineSourcePath(new[] { single }) == Path.GetFullPath(Path.Combine(source, "sub")),
        "DetermineSourcePath: один файл -> его папка");
    Check(RouteService.DetermineSourcePath(new[] { source }) == Path.GetFullPath(source),
        "DetermineSourcePath: одна папка -> она сама");

    // Общий префикс двух папок
    var dirA = Path.Combine(source, "a");
    var dirB = Path.Combine(source, "b");
    Directory.CreateDirectory(dirA); Directory.CreateDirectory(dirB);
    Check(RouteService.DetermineSourcePath(new[] { dirA, dirB }) == Path.GetFullPath(source),
        "DetermineSourcePath: общий корень двух папок");

    // Разные корни -> ArgumentException (а не молчаливый путь к текущему каталогу)
    var otherRoot = Path.GetPathRoot(Path.GetFullPath(source))!.StartsWith("C", StringComparison.OrdinalIgnoreCase)
        ? @"D:\" : @"C:\";
    try { RouteService.DetermineSourcePath(new[] { dirA, Path.Combine(otherRoot, "zzz") }); Check(false, "разные диски -> ArgumentException"); }
    catch (ArgumentException) { Check(true, "разные диски -> ArgumentException"); }

    // FileScanner.Compare: added/modified/deleted/sourceMissing
    var m1 = new List<FileEntry>
    {
        new() { RelPath = "a.txt", Size = 1, LastWriteTicks = 100, Hash = "h1" },
        new() { RelPath = "c.txt", Size = 9, LastWriteTicks = 999, Hash = "h9" },   // будет «изменён»
    };
    var m2 = new List<FileEntry>
    {
        new() { RelPath = "a.txt", Size = 1, LastWriteTicks = 100, Hash = "h1" },   // без изменений
        new() { RelPath = "b.txt", Size = 2, LastWriteTicks = 200, Hash = "h2" },   // добавлен
        new() { RelPath = "c.txt", Size = 3, LastWriteTicks = 300, Hash = "h3" },   // изменён (другой хэш)
    };
    var d1 = FileScanner.Compare(m2, m1);
    Check(d1 is { Added: 1, Modified: 1, Deleted: 0 } && !d1.SourceMissing && d1.Changed, "Compare: +1 добавлен, ~1 изменён");
    var dEmpty = FileScanner.Compare(new List<FileEntry>(), m1);
    Check(dEmpty is { Deleted: 2 } && dEmpty.Changed, "Compare: всё удалено");
    var dMissing = FileScanner.Compare(null, m1);
    Check(dMissing.SourceMissing && dMissing.Changed, "Compare: источник пропал");
    var dSame = FileScanner.Compare(m1, m1);
    Check(!dSame.Changed, "Compare: идентичные снапшоты — без изменений");

    // Откат при сбое копирования: назначение в недопустимое место (файл вместо папки-родителя)
    var blocker = Path.Combine(root, "blocker");
    File.WriteAllText(blocker, "я файл, а не папка");
    File.WriteAllText(Path.Combine(source, "new.txt"), "new");
    var before = repo.GetRoutes(true, true).Count;
    try
    {
        svc.CreateRoute(new[] { source }, "Сломанный маршрут", Path.Combine(blocker, "sub"));
        Check(false, "CreateRoute с битым назначением бросает исключение");
    }
    catch (Exception) { Check(true, "CreateRoute с битым назначением бросает исключение"); }
    Check(repo.GetRoutes(true, true).Count == before, "провал копирования не оставляет маршрут в БД (откат)");

    // SearchFiles: обратный просмотр путей
    var route2 = svc.CreateRoute(new[] { source }, "Индексный маршрут", dest1);
    var hits = repo.SearchFiles("new.txt");
    Check(hits.Any(h => h.RouteId == route2.Id && h.FileName == "new.txt" && h.DestPath == Path.GetFullPath(dest1)),
        "SearchFiles находит файл по имени во всех ветвях");
    Check(repo.SearchFiles("несуществующее_имя_файла").Count == 0, "SearchFiles: пусто на отсутствующее имя");
    Check(repo.SearchFiles("NEW.TXT").Count == repo.SearchFiles("new.txt").Count && repo.SearchFiles("new.txt").Count > 0,
        "SearchFiles регистронезависим");
    repo.DeleteRoute(route2.Id);

    // UpdateDestination при пропавшем источнике: назначение не трогаем, дифф = SourceMissing
    var route3 = svc.CreateRoute(new[] { source }, "Исчезающий источник", dest2);
    MoveWithRetry(source, source + "_gone");
    svc.CheckRoute(route3);
    Check(route3.HasUpdates && route3.Destinations[0].Diff?.SourceMissing == true, "пропавший источник детектится");
    svc.UpdateDestination(route3, route3.Destinations[0]); // не должно удалять файлы в dest2
    Check(File.Exists(Path.Combine(dest2, "new.txt")), "UpdateDestination с пропавшим источником не трогает назначение");
    MoveWithRetry(source + "_gone", source);
    repo.DeleteRoute(route3.Id);

    // --- Пути (группы): объединение, коллапс, разгруппировка ---
    var r1 = repo.InsertRoute("Маршрут 1", source);
    var r2 = repo.InsertRoute("Маршрут 2", source);
    var r3 = repo.InsertRoute("Маршрут 3", source);
    repo.SetGroup(new[] { r1.Id, r2.Id }, "Путь А");
    var grouped = repo.GetRoutes(true, true).ToDictionary(r => r.Id);
    Check(grouped[r1.Id].GroupName == "Путь А" && grouped[r2.Id].GroupName == "Путь А",
        "SetGroup: маршруты объединены в путь");
    Check(grouped[r3.Id].GroupName is null, "SetGroup: посторонний маршрут не в группе");
    Check(repo.GetGroups().Any(g => g.Name == "Путь А" && !g.IsCollapsed), "GetGroups: путь появился, не свёрнут");

    repo.SetRouteCollapsed(r1.Id, true);
    Check(repo.GetRoutes(true, true).First(r => r.Id == r1.Id).IsCollapsed,
        "SetRouteCollapsed: флаг свёрнутости сохранился");
    repo.SetGroupCollapsed("Путь А", true);
    Check(repo.GetGroups().First(g => g.Name == "Путь А").IsCollapsed, "SetGroupCollapsed: путь свёрнут");
    repo.SetGroupCollapsed("Путь А", false);
    Check(!repo.GetGroups().First(g => g.Name == "Путь А").IsCollapsed, "SetGroupCollapsed: путь развёрнут");

    repo.Ungroup(new[] { r1.Id, r2.Id });
    var after = repo.GetRoutes(true, true).ToDictionary(r => r.Id);
    Check(after[r1.Id].GroupName is null && after[r2.Id].GroupName is null, "Ungroup: маршруты вышли из пути");
    Check(repo.GetGroups().All(g => g.Name != "Путь А"), "Ungroup: пустой путь удалён из справочника");
    repo.DeleteRoute(r1.Id); repo.DeleteRoute(r2.Id); repo.DeleteRoute(r3.Id);

    // --- Экспорт/импорт: валидация файла базы ---
    Check(RouteRepository.IsValidDatabase(db), "IsValidDatabase: своя БД — валидна");
    var garbage = Path.Combine(root, "not_a_db.db");
    File.WriteAllText(garbage, "это не база данных");
    Check(!RouteRepository.IsValidDatabase(garbage), "IsValidDatabase: мусор отклонён");
    repo.Checkpoint();
    Check(File.Exists(db), "Checkpoint отрабатывает");

    // --- производительность: 500 маршрутов ---
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 500; i++)
    {
        var r = repo.InsertRoute($"Маршрут {i}", source);
        repo.AddDestination(r.Id, dest1, new List<FileEntry>());
    }
    sw.Stop();
    Check(repo.GetRoutes(true, true).Count == 500, "500 маршрутов создано");
    Console.WriteLine($"INFO   500 маршрутов вставлено за {sw.ElapsedMilliseconds} мс");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine(failed == 0 ? "\nALL TESTS PASSED" : $"\n{failed} TESTS FAILED");
return failed;
