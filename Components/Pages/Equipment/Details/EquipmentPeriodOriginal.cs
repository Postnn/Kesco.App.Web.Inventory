using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Неизменяемый original baseline периода расположения или ответственности.</summary>
public sealed record EquipmentPeriodOriginal
{
    /// <summary>Тип редактора, которому принадлежит baseline.</summary>
    public string EditorKind { get; init; } = string.Empty;

    /// <summary>Идентификатор оборудования.</summary>
    public int EquipmentId { get; init; }

    /// <summary>Исходный составной ключ периода в UTC.</summary>
    public DateTime FromUtc { get; init; }

    /// <summary>Исходная версия строки в UTC без потери дробных секунд.</summary>
    public DateTime ChangedAtUtc { get; init; }

    /// <summary>Ожидание существования исходной строки.</summary>
    public bool ExpectedExisting { get; init; }

    /// <summary>Исходный период был активным при открытии редактора.</summary>
    public bool ExpectedActive { get; init; }

    /// <summary>Исходное окончание периода в UTC.</summary>
    public DateTime? ToUtc { get; init; }

    /// <summary>Исходный LocationId или EmployeeId.</summary>
    public int SubjectId { get; init; }

    /// <summary>Читает original из typed state перед fresh lookup и повторном открытии редактора.</summary>
    public static EquipmentPeriodOriginal? Read(string? editorState)
    {
        if (string.IsNullOrWhiteSpace(editorState))
            return null;
        using var document = System.Text.Json.JsonDocument.Parse(editorState);
        return document.RootElement.TryGetProperty("Original", out var element)
            ? element.Deserialize<EquipmentPeriodOriginal>()
            : null;
    }

    /// <summary>Создаёт baseline расположения, отдельный от последующего fresh read.</summary>
    public static EquipmentPeriodOriginal FromLocation(EquipmentLocationPeriod value, bool active) => new()
    {
        EditorKind = nameof(EquipmentLocationDialog),
        EquipmentId = value.EquipmentId,
        FromUtc = value.FromUtc,
        ChangedAtUtc = value.ChangedAtUtc,
        ExpectedExisting = true,
        ExpectedActive = active,
        SubjectId = value.LocationId,
    };

    /// <summary>Создаёт baseline ответственности, отдельный от последующего fresh read.</summary>
    public static EquipmentPeriodOriginal FromResponsible(EquipmentResponsiblePeriod value, bool active) => new()
    {
        EditorKind = nameof(EquipmentResponsibleDialog),
        EquipmentId = value.EquipmentId,
        FromUtc = value.FromUtc,
        ChangedAtUtc = value.ChangedAtUtc,
        ExpectedExisting = true,
        ExpectedActive = active,
        ToUtc = value.ToUtc,
        SubjectId = value.EmployeeId,
    };

    internal string? ValidateFresh(EquipmentLocationPeriod? fresh, bool freshActive)
    {
        var prefix = ValidateIdentity(nameof(EquipmentLocationDialog));
        if (prefix is not null)
            return prefix;
        if (!ExpectedExisting)
            return "Исходный период расположения не подтверждён. Черновик сохранён; запись не отправлена.";
        if (fresh is null || fresh.FromUtc != FromUtc || fresh.EquipmentId != EquipmentId)
            return "Исходный период расположения удалён. Черновик сохранён; добавление новой записи не выполняется.";
        if (fresh.LocationId != SubjectId || ExpectedActive && !freshActive)
            return "Исходный период расположения закрыт или перемещён. Черновик сохранён; запись не отправлена.";
        if (fresh.ChangedAtUtc != ChangedAtUtc)
            return "Период расположения изменён другим пользователем. Черновик и исходная версия сохранены; запись не отправлена.";
        return null;
    }

    internal string? ValidateFresh(EquipmentResponsiblePeriod? fresh, bool freshActive)
    {
        var prefix = ValidateIdentity(nameof(EquipmentResponsibleDialog));
        if (prefix is not null)
            return prefix;
        if (!ExpectedExisting)
            return "Исходный период ответственности не подтверждён. Черновик сохранён; запись не отправлена.";
        if (fresh is null || fresh.FromUtc != FromUtc || fresh.EquipmentId != EquipmentId)
            return "Исходный период ответственности удалён. Черновик сохранён; добавление следующего периода не выполняется.";
        if (fresh.EmployeeId != SubjectId || ExpectedActive && !freshActive || ToUtc is null && fresh.ToUtc is not null)
            return "Исходный период ответственности закрыт или изменён. Черновик сохранён; запись не отправлена.";
        if (fresh.ChangedAtUtc != ChangedAtUtc)
            return "Период ответственности изменён другим пользователем. Черновик и исходная версия сохранены; запись не отправлена.";
        return null;
    }

    private string? ValidateIdentity(string editorKind)
    {
        if (EditorKind != editorKind || FromUtc == default)
            return "Исходный ключ периода не подтверждён. Черновик сохранён; запись не отправлена.";
        return null;
    }
}
