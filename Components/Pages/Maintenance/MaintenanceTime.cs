using System.Globalization;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Maintenance;

/// <summary>Преобразует время представления и условия фильтра между шкалами SQL Server и браузера.</summary>
internal static class MaintenanceTime
{
    private const string DistinctFormat = "dd.MM.yyyy HH:mm:ss.fff zzz";

    internal static DateTime? ToUtc(DateTime? sourceValue, TimeZoneInfo? sourceZone)
    {
        if (sourceValue is not { } value || sourceZone is null) return null;
        var wallClock = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return sourceZone.IsInvalidTime(wallClock) ? null : TimeZoneInfo.ConvertTimeToUtc(wallClock, sourceZone);
    }

    internal static async Task<string?> FormatDistinctAsync(DateTime sourceValue, TimeZoneInfo? sourceZone,
        IJSRuntime js, CancellationToken ct)
    {
        if (ToUtc(sourceValue, sourceZone) is not { } utc) return null;
        var offset = await js.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", ct, utc.ToString("O"));
        return new DateTimeOffset(utc).ToOffset(TimeSpan.FromMinutes(offset)).ToString(DistinctFormat, CultureInfo.InvariantCulture);
    }

    internal static bool IsTimeColumn(string column) =>
        column is MaintenanceColumns.Deadline or MaintenanceColumns.LastCompleted;

    internal static async Task<ClayFilterGroupNode?> ToSqlFilterAsync(ClayFilterGroupNode? group,
        TimeZoneInfo? sourceZone, IJSRuntime js, CancellationToken ct)
    {
        if (group is null) return null;
        var result = new ClayFilterGroupNode { Logic = group.Logic };
        foreach (var node in group.Nodes)
        {
            switch (node)
            {
                case ClayFilterGroupNode nested:
                    result.Nodes.Add((await ToSqlFilterAsync(nested, sourceZone, js, ct))!);
                    break;
                case ColumnFilter filter when IsTimeColumn(filter.Column) && filter.HasValue:
                    var first = await ToSqlConditionAsync(filter.Column, filter.Operator, filter.Value, sourceZone, js, ct);
                    result.Nodes.Add(filter.HasSecondClause
                        ? Group(filter.LogicalOperator, first,
                            await ToSqlConditionAsync(filter.Column, filter.SecondOperator, filter.SecondValue, sourceZone, js, ct))
                        : first);
                    break;
                case ValueFilter values when IsTimeColumn(values.Column) && values.Values.Count > 0:
                    var converted = (ValueFilter)values.Clone();
                    converted.Values = values.Values
                        .Select(value => (object?)ToSourceValue(value, RequireSourceZone(sourceZone)))
                        .ToList();
                    result.Nodes.Add(converted);
                    break;
                default:
                    result.Nodes.Add(node);
                    break;
            }
        }
        return result;
    }

    private static async Task<IClayFilterNode> ToSqlConditionAsync(string column, ColumnFilterOperator op,
        object? value, TimeZoneInfo? sourceZone, IJSRuntime js, CancellationToken ct)
    {
        if (op is ColumnFilterOperator.IsNull or ColumnFilterOperator.IsNotNull)
            return Condition(column, op, null);

        var zone = RequireSourceZone(sourceZone);
        var day = ParseDate(value).Date;
        var start = await BrowserMidnightInSourceAsync(day, zone, js, ct);
        var end = await BrowserMidnightInSourceAsync(day.AddDays(1), zone, js, ct);
        return op switch
        {
            ColumnFilterOperator.Equals => Group(LogicalOperator.And,
                Condition(column, ColumnFilterOperator.GreaterThanOrEqual, start),
                Condition(column, ColumnFilterOperator.LessThan, end)),
            ColumnFilterOperator.NotEquals => Group(LogicalOperator.Or,
                Condition(column, ColumnFilterOperator.LessThan, start),
                Condition(column, ColumnFilterOperator.GreaterThanOrEqual, end)),
            ColumnFilterOperator.GreaterThan => Condition(column, ColumnFilterOperator.GreaterThanOrEqual, end),
            ColumnFilterOperator.GreaterThanOrEqual => Condition(column, ColumnFilterOperator.GreaterThanOrEqual, start),
            ColumnFilterOperator.LessThan => Condition(column, ColumnFilterOperator.LessThan, start),
            ColumnFilterOperator.LessThanOrEqual => Condition(column, ColumnFilterOperator.LessThan, end),
            _ => throw new InvalidOperationException("Оператор фильтра даты не поддерживается."),
        };
    }

    private static async Task<DateTime> BrowserMidnightInSourceAsync(DateTime day, TimeZoneInfo sourceZone,
        IJSRuntime js, CancellationToken ct)
    {
        var milliseconds = await js.InvokeAsync<long>("clayGridTimeZone.localDateTimeToUtcMilliseconds", ct,
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "00:00:00");
        return SourceWallClock(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime, sourceZone);
    }

    private static DateTime ToSourceValue(object? value, TimeZoneInfo sourceZone) =>
        value is string text && DateTimeOffset.TryParseExact(text, DistinctFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp)
            ? SourceWallClock(timestamp.UtcDateTime, sourceZone)
            // Старые черновики содержат исходное datetime, полученное напрямую из SQL.
            : DateTime.SpecifyKind(ParseDate(value), DateTimeKind.Unspecified);

    private static DateTime SourceWallClock(DateTime utc, TimeZoneInfo sourceZone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, sourceZone), DateTimeKind.Unspecified);

    private static TimeZoneInfo RequireSourceZone(TimeZoneInfo? sourceZone) =>
        sourceZone ?? throw new InvalidOperationException("Часовой пояс источника регламентных работ не настроен.");

    private static DateTime ParseDate(object? value) =>
        value is DateTime date ? date :
        value is string text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date)
            ? date
            : throw new InvalidOperationException("Некорректное значение фильтра даты регламентной работы.");

    private static ColumnFilter Condition(string column, ColumnFilterOperator op, object? value) =>
        new() { Column = column, Operator = op, Value = value };

    private static ClayFilterGroupNode Group(LogicalOperator logic, IClayFilterNode first, IClayFilterNode second) =>
        new() { Logic = logic, Nodes = [first, second] };
}
