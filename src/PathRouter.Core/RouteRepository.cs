using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;

namespace PathRouter.Core;

/// <summary>
/// Хранилище маршрутов и манифестов в SQLite. БД лежит в %LocalAppData%\PathRouter.
/// Все публичные методы потокобезопасны (синхронизация на уровне метода) —
/// соединение SQLite не потокобезопасно само по себе.
/// </summary>
public sealed class RouteRepository : IDisposable
{
    private readonly SqliteConnection _connection;

    public static string DefaultDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PathRouter", "routes.db");

    public RouteRepository(string? dbPath = null)
    {
        var path = dbPath ?? DefaultDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS routes(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                source_path TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                is_archived INTEGER NOT NULL DEFAULT 0,
                is_hidden INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS destinations(
                id TEXT PRIMARY KEY,
                route_id TEXT NOT NULL REFERENCES routes(id) ON DELETE CASCADE,
                dest_path TEXT NOT NULL,
                order_index INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                last_sync_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_dest_route ON destinations(route_id);
            CREATE TABLE IF NOT EXISTS manifest(
                destination_id TEXT NOT NULL REFERENCES destinations(id) ON DELETE CASCADE,
                rel_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                last_write_ticks INTEGER NOT NULL,
                hash TEXT NOT NULL,
                PRIMARY KEY(destination_id, rel_path)
            );
            CREATE INDEX IF NOT EXISTS idx_manifest_dest ON manifest(destination_id);
            CREATE INDEX IF NOT EXISTS idx_manifest_rel ON manifest(rel_path);
            CREATE TABLE IF NOT EXISTS route_groups(
                name TEXT PRIMARY KEY,
                is_collapsed INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS layout(
                block_key TEXT PRIMARY KEY,
                y REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS force_layout(
                node_id TEXT PRIMARY KEY,
                x REAL NOT NULL,
                y REAL NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Миграции: добавляем колонки к существующим БД (ALTER TABLE ... IF NOT EXISTS нет в SQLite)
        AddColumnIfMissing("routes", "group_name", "ALTER TABLE routes ADD COLUMN group_name TEXT");
        AddColumnIfMissing("routes", "is_collapsed", "ALTER TABLE routes ADD COLUMN is_collapsed INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("destinations", "is_conserved", "ALTER TABLE destinations ADD COLUMN is_conserved INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("destinations", "note", "ALTER TABLE destinations ADD COLUMN note TEXT");
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void AddColumnIfMissing(string table, string column, string alterSql)
    {
        if (HasColumn(table, column)) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = alterSql;
        cmd.ExecuteNonQuery();
    }

    // ---------- Маршруты ----------

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Route> GetRoutes(bool includeArchived, bool includeHidden)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, source_path, created_utc, is_archived, is_hidden, group_name, is_collapsed
            FROM routes
            WHERE (@incArch = 1 OR is_archived = 0)
              AND (@incHidden = 1 OR is_hidden = 0)
            ORDER BY group_name, created_utc;
            """;
        cmd.Parameters.AddWithValue("@incArch", includeArchived ? 1 : 0);
        cmd.Parameters.AddWithValue("@incHidden", includeHidden ? 1 : 0);

        var routes = new List<Route>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            routes.Add(new Route
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                SourcePath = reader.GetString(2),
                CreatedUtc = DateTime.Parse(reader.GetString(3)),
                IsArchived = reader.GetInt64(4) != 0,
                IsHidden = reader.GetInt64(5) != 0,
                GroupName = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsCollapsed = reader.GetInt64(7) != 0
            });
        }

        foreach (var route in routes)
            route.Destinations.AddRange(GetDestinations(route.Id));
        return routes;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public Route InsertRoute(string name, string sourcePath)
    {
        var route = new Route
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            SourcePath = Path.GetFullPath(sourcePath),
            CreatedUtc = DateTime.UtcNow
        };

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO routes(id, name, source_path, created_utc, is_archived, is_hidden)
            VALUES($id, $name, $src, $created, 0, 0);
            """;
        cmd.Parameters.AddWithValue("$id", route.Id);
        cmd.Parameters.AddWithValue("$name", route.Name);
        cmd.Parameters.AddWithValue("$src", route.SourcePath);
        cmd.Parameters.AddWithValue("$created", route.CreatedUtc.ToString("O"));
        cmd.ExecuteNonQuery();
        return route;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RenameRoute(string routeId, string newName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE routes SET name=$name WHERE id=$id;";
        cmd.Parameters.AddWithValue("$name", newName);
        cmd.Parameters.AddWithValue("$id", routeId);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetArchived(string routeId, bool archived)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE routes SET is_archived=$v WHERE id=$id;";
        cmd.Parameters.AddWithValue("$v", archived ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", routeId);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetHidden(string routeId, bool hidden)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE routes SET is_hidden=$v WHERE id=$id;";
        cmd.Parameters.AddWithValue("$v", hidden ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", routeId);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void DeleteRoute(string routeId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM routes WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", routeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Удаляет одну ветвь (манифест уходит каскадом).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void DeleteDestination(string destinationId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM destinations WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", destinationId);
        cmd.ExecuteNonQuery();
    }

    // ---------- Пути (группы маршрутов) ----------

    /// <summary>Все пути и их состояние свёрнутости.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<RouteGroupInfo> GetGroups()
    {
        // Подтягиваем и группы, у которых есть маршруты, но нет строки состояния
        using var sync = _connection.CreateCommand();
        sync.CommandText = """
            INSERT OR IGNORE INTO route_groups(name, is_collapsed)
            SELECT DISTINCT group_name, 0 FROM routes WHERE group_name IS NOT NULL;
            """;
        sync.ExecuteNonQuery();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name, is_collapsed FROM route_groups ORDER BY name;";
        var list = new List<RouteGroupInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new RouteGroupInfo { Name = reader.GetString(0), IsCollapsed = reader.GetInt64(1) != 0 });
        return list;
    }

    /// <summary>Объединяет маршруты в путь (группу) с заданным названием.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetGroup(IEnumerable<string> routeIds, string groupName)
    {
        using var tx = _connection.BeginTransaction();
        using (var g = _connection.CreateCommand())
        {
            g.Transaction = tx;
            g.CommandText = "INSERT OR IGNORE INTO route_groups(name, is_collapsed) VALUES($n, 0);";
            g.Parameters.AddWithValue("$n", groupName);
            g.ExecuteNonQuery();
        }
        foreach (var id in routeIds)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE routes SET group_name=$g WHERE id=$id;";
            cmd.Parameters.AddWithValue("$g", groupName);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        CleanupEmptyGroups();
    }

    /// <summary>Переименовать путь: переносит маршруты, состояние свёрнутости и раскладку.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RenameGroup(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;
        using var tx = _connection.BeginTransaction();
        void Exec(string sql)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$new", newName);
            cmd.Parameters.AddWithValue("$old", oldName);
            cmd.ExecuteNonQuery();
        }
        Exec("UPDATE routes SET group_name=$new WHERE group_name=$old;");
        Exec("UPDATE route_groups SET name=$new WHERE name=$old;");
        Exec("UPDATE layout SET block_key='G:' || $new WHERE block_key='G:' || $old;");
        tx.Commit();
    }

    /// <summary>Убирает маршруты из путей (group_name = NULL).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Ungroup(IEnumerable<string> routeIds)
    {
        foreach (var id in routeIds)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE routes SET group_name=NULL WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        CleanupEmptyGroups();
    }

    private void CleanupEmptyGroups()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM route_groups
            WHERE NOT EXISTS (SELECT 1 FROM routes WHERE routes.group_name = route_groups.name);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сворачивание/разворачивание одного маршрута на графе.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetRouteCollapsed(string routeId, bool collapsed)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE routes SET is_collapsed=$v WHERE id=$id;";
        cmd.Parameters.AddWithValue("$v", collapsed ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", routeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сворачивание/разворачивание целого пути.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetGroupCollapsed(string groupName, bool collapsed)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO route_groups(name, is_collapsed) VALUES($n, $v)
            ON CONFLICT(name) DO UPDATE SET is_collapsed=$v;
            """;
        cmd.Parameters.AddWithValue("$n", groupName);
        cmd.Parameters.AddWithValue("$v", collapsed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Консервация/вскрытие ветви: проверка целостности и синхронизация вкл/выкл.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetConserved(string destinationId, bool conserved)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE destinations SET is_conserved=$v WHERE id=$id;";
        cmd.Parameters.AddWithValue("$v", conserved ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", destinationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Заметка к ветви: произвольный текст пользователя. Пустая строка/null — удалить.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetDestinationNote(string destinationId, string? note)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE destinations SET note=$n WHERE id=$id;";
        cmd.Parameters.AddWithValue("$n", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
        cmd.Parameters.AddWithValue("$id", destinationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сброс WAL-журнала в основной файл — перед копированием БД (экспорт).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Checkpoint()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    // ---------- Раскладка графа ----------

    /// <summary>Сохранённые Y-позиции блоков графа (путь/источник), ключ — block_key.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Dictionary<string, double> GetLayout()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT block_key, y FROM layout;";
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dict[reader.GetString(0)] = reader.GetDouble(1);
        return dict;
    }

    /// <summary>Запомнить вертикальную позицию блока графа (ручная раскладка пользователя).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SaveLayoutPosition(string blockKey, double y)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO layout(block_key, y) VALUES($k, $y)
            ON CONFLICT(block_key) DO UPDATE SET y=$y;
            """;
        cmd.Parameters.AddWithValue("$k", blockKey);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.ExecuteNonQuery();
    }

    // ---------- Силовой граф (вид «как в Obsidian») ----------

    /// <summary>Сохранённые позиции точек силового графа: node_id -> (x, y).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Dictionary<string, (double X, double Y)> GetForceLayout()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT node_id, x, y FROM force_layout;";
        var dict = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dict[reader.GetString(0)] = (reader.GetDouble(1), reader.GetDouble(2));
        return dict;
    }

    /// <summary>Запомнить позицию точки силового графа (после перетаскивания пользователем).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SaveForceLayout(string nodeId, double x, double y)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO force_layout(node_id, x, y) VALUES($k, $x, $y)
            ON CONFLICT(node_id) DO UPDATE SET x=$x, y=$y;
            """;
        cmd.Parameters.AddWithValue("$k", nodeId);
        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Проверяет, что файл — база Каравана (нужные таблицы на месте).</summary>
    public static bool IsValidDatabase(string path)    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('routes','destinations','manifest');";
            return Convert.ToInt32(cmd.ExecuteScalar()) == 3;
        }
        catch
        {
            return false;
        }
    }

    // ---------- Назначения ----------

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<RouteDestination> GetDestinations(string routeId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, route_id, dest_path, order_index, created_utc, last_sync_utc, is_conserved, note
            FROM destinations WHERE route_id=$rid ORDER BY order_index, created_utc;
            """;
        cmd.Parameters.AddWithValue("$rid", routeId);

        var list = new List<RouteDestination>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RouteDestination
            {
                Id = reader.GetString(0),
                RouteId = reader.GetString(1),
                DestPath = reader.GetString(2),
                OrderIndex = (int)reader.GetInt64(3),
                CreatedUtc = DateTime.Parse(reader.GetString(4)),
                LastSyncUtc = DateTime.Parse(reader.GetString(5)),
                IsConserved = reader.GetInt64(6) != 0,
                Note = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        foreach (var dest in list)
            dest.Manifest = GetManifest(dest.Id);
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public RouteDestination AddDestination(string routeId, string destPath, List<FileEntry> manifest)
    {
        var dest = new RouteDestination
        {
            Id = Guid.NewGuid().ToString("N"),
            RouteId = routeId,
            DestPath = Path.GetFullPath(destPath),
            OrderIndex = GetNextOrderIndex(routeId),
            CreatedUtc = DateTime.UtcNow,
            LastSyncUtc = DateTime.UtcNow,
            Manifest = manifest
        };

        using var tx = _connection.BeginTransaction();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO destinations(id, route_id, dest_path, order_index, created_utc, last_sync_utc)
                VALUES($id, $rid, $path, $ord, $created, $sync);
                """;
            cmd.Parameters.AddWithValue("$id", dest.Id);
            cmd.Parameters.AddWithValue("$rid", routeId);
            cmd.Parameters.AddWithValue("$path", dest.DestPath);
            cmd.Parameters.AddWithValue("$ord", dest.OrderIndex);
            cmd.Parameters.AddWithValue("$created", dest.CreatedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$sync", dest.LastSyncUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        SaveManifestInternal(tx, dest.Id, manifest);
        tx.Commit();
        return dest;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void UpdateManifest(string destinationId, List<FileEntry> manifest)
    {
        using var tx = _connection.BeginTransaction();
        SaveManifestInternal(tx, destinationId, manifest);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE destinations SET last_sync_utc=$sync WHERE id=$id;";
            cmd.Parameters.AddWithValue("$sync", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", destinationId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private int GetNextOrderIndex(string routeId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(order_index), -1) + 1 FROM destinations WHERE route_id=$rid;";
        cmd.Parameters.AddWithValue("$rid", routeId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---------- Манифесты ----------

    private void SaveManifestInternal(SqliteTransaction tx, string destinationId, List<FileEntry> manifest)
    {
        using var del = _connection.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM manifest WHERE destination_id=$id;";
        del.Parameters.AddWithValue("$id", destinationId);
        del.ExecuteNonQuery();

        using var ins = _connection.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO manifest(destination_id, rel_path, size, last_write_ticks, hash)
            VALUES($id, $path, $size, $mtime, $hash);
            """;
        var pId = ins.Parameters.Add("$id", SqliteType.Text);
        var pPath = ins.Parameters.Add("$path", SqliteType.Text);
        var pSize = ins.Parameters.Add("$size", SqliteType.Integer);
        var pMtime = ins.Parameters.Add("$mtime", SqliteType.Integer);
        var pHash = ins.Parameters.Add("$hash", SqliteType.Text);

        foreach (var f in manifest)
        {
            pId.Value = destinationId;
            pPath.Value = f.RelPath;
            pSize.Value = f.Size;
            pMtime.Value = f.LastWriteTicks;
            pHash.Value = f.Hash;
            ins.ExecuteNonQuery();
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<FileEntry> GetManifest(string destinationId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT rel_path, size, last_write_ticks, hash
            FROM manifest WHERE destination_id=$id ORDER BY rel_path;
            """;
        cmd.Parameters.AddWithValue("$id", destinationId);

        var list = new List<FileEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FileEntry
            {
                RelPath = reader.GetString(0),
                Size = reader.GetInt64(1),
                LastWriteTicks = reader.GetInt64(2),
                Hash = reader.GetString(3)
            });
        }
        return list;
    }

    // ---------- Поиск файлов ----------

    /// <summary>
    /// Обратный просмотр путей: находит все известные копии файлов, чьё относительное
    /// имя содержит подстроку pattern (регистронезависимо), по всем маршрутам и ветвям.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<FileHit> SearchFiles(string pattern, int limit = 200)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.name, r.source_path, d.dest_path, m.rel_path, m.size, d.last_sync_utc
            FROM manifest m
            JOIN destinations d ON d.id = m.destination_id
            JOIN routes r ON r.id = d.route_id
            WHERE lower(m.rel_path) LIKE '%' || lower($pat) || '%'
            ORDER BY m.rel_path, r.name
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$pat", pattern);
        cmd.Parameters.AddWithValue("$lim", limit);

        var list = new List<FileHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FileHit
            {
                RouteId = reader.GetString(0),
                RouteName = reader.GetString(1),
                SourcePath = reader.GetString(2),
                DestPath = reader.GetString(3),
                RelPath = reader.GetString(4),
                Size = reader.GetInt64(5),
                LastSyncUtc = DateTime.Parse(reader.GetString(6))
            });
        }
        return list;
    }

    public void Dispose() => _connection.Dispose();
}
