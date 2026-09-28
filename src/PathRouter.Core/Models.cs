namespace PathRouter.Core;

/// <summary>Один файл в снапшоте (манифесте) маршрута.</summary>
public sealed class FileEntry
{
    public string RelPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public long LastWriteTicks { get; set; }

    /// <summary>Быстрый хэш FNV-1a (путь+размер+mtime) — для сравнения снапшотов.</summary>
    public string Hash { get; set; } = string.Empty;
}

/// <summary>Результат сравнения текущего состояния источника с сохранённым снапшотом.</summary>
public sealed class DiffResult
{
    public int Added;
    public int Modified;
    public int Deleted;
    public bool SourceMissing;
    public bool Changed => SourceMissing || Added > 0 || Modified > 0 || Deleted > 0;

    public string Describe()
    {
        if (SourceMissing) return "папка источника не найдена";
        if (!Changed) return "актуально";
        var parts = new List<string>();
        if (Added > 0) parts.Add($"+{Added}");
        if (Modified > 0) parts.Add($"~{Modified}");
        if (Deleted > 0) parts.Add($"-{Deleted}");
        return string.Join(" ", parts);
    }
}

/// <summary>Маршрут (цифровой хвост): источник -> название -> [назначения].</summary>
public sealed class Route
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public bool IsArchived { get; set; }
    public bool IsHidden { get; set; }

    /// <summary>Имя пути (группы), в который объединён маршрут. Null — без группы.</summary>
    public string? GroupName { get; set; }

    /// <summary>Свёрнут на графе: виден только узел-название.</summary>
    public bool IsCollapsed { get; set; }

    public List<RouteDestination> Destinations { get; } = new();

    /// <summary>Есть ли отличия между источником и хотя бы одним назначением.</summary>
    public bool HasUpdates { get; set; }
}

/// <summary>Одна ветка маршрута: источник -> назначение (с манифестом последней синхронизации).</summary>
public sealed class RouteDestination
{
    public string Id { get; set; } = string.Empty;
    public string RouteId { get; set; } = string.Empty;
    public string DestPath { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastSyncUtc { get; set; }

    /// <summary>Снапшот источника на момент последней синхронизации.</summary>
    public List<FileEntry> Manifest { get; set; } = new();

    /// <summary>Отличия источника от снапшота (источник изменился).</summary>
    public DiffResult? Diff { get; set; }

    /// <summary>Отличия самой папки назначения от снапшота (получатель изменился самостоятельно).</summary>
    public DiffResult? DestDiff { get; set; }

    /// <summary>Требуется синхронизация: изменился источник или получатель (или исходник пропал).</summary>
    public bool HasUpdates => Diff?.Changed == true || DestDiff?.Changed == true;
}

/// <summary>Найденная копия файла: в каком маршруте и в какой папке назначения лежит.</summary>
public sealed class FileHit
{
    public string RouteId { get; set; } = string.Empty;
    public string RouteName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestPath { get; set; } = string.Empty;
    public string RelPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastSyncUtc { get; set; }

    /// <summary>Имя файла (последний сегмент относительного пути).</summary>
    public string FileName => RelPath.Replace('\\', '/').Split('/').Last();
}

/// <summary>Путь (группа объединённых маршрутов) и его состояние.</summary>
public sealed class RouteGroupInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsCollapsed { get; set; }
}
