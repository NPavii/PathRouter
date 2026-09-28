using PathRouter.Core;

// Утилита демо-данных:
//   seed    — три демо-маршрута (как раньше)
//   reddemo — один маршрут с несуществующим источником (для проверки красной индикации)
//   clean   — удалить все маршруты с именем, начинающимся на «Демо»
var mode = args.Length > 0 ? args[0] : "seed";

using var repo = new RouteRepository();

if (mode == "clean")
{
    foreach (var r in repo.GetRoutes(true, true).Where(r => r.Name.StartsWith("Демо")))
        repo.DeleteRoute(r.Id);
    Console.WriteLine("Демо-маршруты удалены");
    return;
}

if (mode == "reddemo")
{
    var dest = Path.Combine(Path.GetTempPath(), "KaravanRedDemoDest");
    Directory.CreateDirectory(dest);
    var route = repo.InsertRoute("Демо: источник удалён", @"C:\Nonexistent\Karavan\MissingSource");
    var scan = new List<FileEntry>();
    repo.AddDestination(route.Id, dest, scan);
    var svc = new RouteService(repo);
    var fresh = repo.GetRoutes(true, true).First(r => r.Id == route.Id);
    svc.CheckRoute(fresh);
    Console.WriteLine($"reddemo: HasUpdates={fresh.HasUpdates}, SourceMissing={fresh.Destinations[0].Diff?.SourceMissing}");
    return;
}

// seed (по умолчанию)
var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var root = Path.Combine(local, "PathRouterDemo");
var source = Path.Combine(root, "Docs");
Directory.CreateDirectory(source);
File.WriteAllText(Path.Combine(source, "readme.txt"), "v1");
File.WriteAllText(Path.Combine(source, "spec.pdf"), "v1");

var svc2 = new RouteService(repo);
var names = new[] { "Автомат коробов — документация", "Линия розлива — чертежи", "Паллетайзер — ПО" };
foreach (var name in names)
{
    var dest = Path.Combine(root, "Out_" + names.ToList().IndexOf(name));
    Directory.CreateDirectory(dest);
    var route = svc2.CreateRoute(new[] { source }, name, dest);
    if (name == names[0])
    {
        var dest2 = Path.Combine(root, "Out_0b");
        Directory.CreateDirectory(dest2);
        svc2.AddDestination(route, dest2);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "v2-обновлено");
        File.WriteAllText(Path.Combine(source, "new_file.txt"), "новый");
    }
}
var routes = repo.GetRoutes(true, true);
foreach (var r in routes) svc2.CheckRoute(r);
Console.WriteLine($"Сидировано маршрутов: {routes.Count}, с обновлениями: {routes.Count(r => r.HasUpdates)}");
