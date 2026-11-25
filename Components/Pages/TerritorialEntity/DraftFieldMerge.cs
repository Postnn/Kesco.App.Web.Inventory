using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Доступное для редактирования поле карточки и ключ соответствующего браузерного ввода.</summary>
public sealed record DraftMergeField(string Path, string Label, string? DraftKey = null);

/// <summary>Значения поля, по которому требуется решение пользователя.</summary>
public sealed record DraftFieldConflict(DraftMergeField Field, string Original, string Local, string Fresh, bool HasBrowserInput);

internal sealed class DraftFieldMerge<TState> where TState : class
{
    private readonly JsonObject _merged;
    private readonly IReadOnlyList<Change> _changes;

    public IReadOnlyList<DraftFieldConflict> Conflicts { get; }

    public DraftFieldMerge(TState? original, TState local, TState fresh, IReadOnlyList<DraftMergeField> fields, IReadOnlyList<string> rawKeys)
    {
        var before = original is null ? null : ToObject(original);
        var localNode = ToObject(local);
        _merged = ToObject(fresh);
        var changes = new List<Change>();
        foreach (var field in fields)
        {
            var serverValue = Get(_merged, field.Path);
            var localValue = Get(localNode, field.Path);
            var previous = before is null ? null : Get(before, field.Path);
            var browserKeys = field.DraftKey is { } key
                ? rawKeys.Where(raw => raw.StartsWith($"data:{key}:", StringComparison.Ordinal)).ToArray()
                : [];
            if (JsonNode.DeepEquals(serverValue, localValue) &&
                (browserKeys.Length == 0 || before is not null && JsonNode.DeepEquals(previous, serverValue))) continue;
            if (before is not null && JsonNode.DeepEquals(serverValue, previous))
            {
                Put(_merged, field.Path, localValue);
                continue;
            }
            if (before is not null && browserKeys.Length == 0 && JsonNode.DeepEquals(localValue, previous)) continue;

            var conflict = new DraftFieldConflict(field, before is null ? "Исходная версия неизвестна" : Format(previous),
                Format(localValue), Format(serverValue), browserKeys.Length > 0);
            changes.Add(new Change(conflict, localValue, browserKeys));
        }
        _changes = changes;
        Conflicts = changes.Select(change => change.Conflict).ToArray();
    }

    public (TState State, string[] DiscardedRaw) Select(IReadOnlyDictionary<string, bool> takeFresh)
    {
        var result = (JsonObject)_merged.DeepClone();
        var discarded = new List<string>();
        foreach (var change in _changes)
        {
            var path = change.Conflict.Field.Path;
            if (!takeFresh.TryGetValue(path, out var fresh))
                throw new InvalidOperationException("Для каждого изменённого поля требуется выбрать версию.");
            if (fresh) discarded.AddRange(change.RawKeys);
            else Put(result, path, change.Local);
        }
        return (result.Deserialize<TState>() ?? throw new InvalidOperationException("Черновик не удалось прочитать."),
            discarded.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static JsonObject ToObject(TState state) => JsonSerializer.SerializeToNode(state)?.AsObject()
        ?? throw new InvalidOperationException("Снимок карточки не сериализован.");

    private static (JsonObject Parent, string Key) Property(JsonObject root, string path)
    {
        var parts = path.Split('.');
        foreach (var part in parts[..^1])
            root = root[part]?.AsObject() ?? throw new InvalidOperationException("Поле отсутствует в снимке.");
        return (root, parts[^1]);
    }

    private static JsonNode? Get(JsonObject root, string path)
    {
        var (parent, key) = Property(root, path);
        return parent[key];
    }

    private static void Put(JsonObject root, string path, JsonNode? value)
    {
        var (parent, key) = Property(root, path);
        parent[key] = value?.DeepClone();
    }

    private static string Format(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return text.Length == 0 ? "Пусто" : text;
        if (value is JsonArray items)
        {
            if (items.Count == 0) return "Пусто";
            return string.Join(", ", items.Select(item =>
            {
                if (item is not JsonObject row || row["Name"]?.GetValue<string>() is not { } name) return item?.ToJsonString();
                var id = row["Id"]?.ToJsonString();
                return row["Private"]?.GetValue<byte>() == 1 ? $"{name} [{id}] *" : $"{name} [{id}]";
            }));
        }
        return value?.ToJsonString() ?? "Пусто";
    }

    private sealed record Change(DraftFieldConflict Conflict, JsonNode? Local, string[] RawKeys);
}
