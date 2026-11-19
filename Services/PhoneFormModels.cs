using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Набор связанных коллекций формы телефона.</summary>
public sealed class PhoneRelationsSnapshot
{
    /// <summary>Члены PBX-группы.</summary>
    public List<PhoneGroupMember> GroupMembers { get; init; } = [];

    /// <summary>Связи с оборудованием.</summary>
    public List<PhoneEquipmentLink> Equipment { get; init; } = [];

    /// <summary>Параллельные вызовы.</summary>
    public List<PhoneParallelCall> ParallelCalls { get; init; } = [];

    /// <summary>Создаёт глубокую копию коллекций.</summary>
    public PhoneRelationsSnapshot Clone() => new()
    {
        GroupMembers = GroupMembers.Select(item => item.Clone()).ToList(),
        Equipment = Equipment.Select(item => item.Clone()).ToList(),
        ParallelCalls = ParallelCalls.Select(item => item.Clone()).ToList(),
    };
}

/// <summary>Загруженные данные формы телефона одним пакетом запросов.</summary>
public sealed class PhoneFormData
{
    /// <summary>Основная запись.</summary>
    public Phone Phone { get; init; } = new();

    /// <summary>Связанные коллекции.</summary>
    public PhoneRelationsSnapshot Relations { get; init; } = new();

    /// <summary>Группы, в которые входит номер.</summary>
    public IReadOnlyList<PhoneGroupMembership> Memberships { get; init; } = [];

    /// <summary>Разрешено редактирование ролью 43.</summary>
    public bool CanEdit { get; init; }
}

/// <summary>Актуальное сохранённое состояние для планирования удаления и его синхронизации.</summary>
public sealed class PhoneDeleteSnapshot
{
    /// <summary>Сохранённая основная запись.</summary>
    public Phone Phone { get; init; } = new();

    /// <summary>Сохранённая станция записи.</summary>
    public PhoneStationLookup Station { get; init; } = new();

    /// <summary>Сохранённые связи.</summary>
    public PhoneRelationsSnapshot Relations { get; init; } = new();
}

/// <summary>Вычисленные возможности выбранных типа и станции.</summary>
public sealed class PhoneCapabilities
{
    /// <summary>Для комбинации существуют порты.</summary>
    public bool HasPorts { get; init; }

    /// <summary>Доступен автонабор.</summary>
    public bool AutoDialAvailable { get; init; }

    /// <summary>Свободные и собственный текущий порты.</summary>
    public IReadOnlyList<string> AvailablePorts { get; init; } = [];
}

/// <summary>Уровень сообщения валидации по фактическому legacy-контракту.</summary>
public enum PhoneValidationSeverity
{
    /// <summary>Сохранение запрещено.</summary>
    Blocking,

    /// <summary>Предупреждение не запрещает сохранение.</summary>
    WarningAllowed,

    /// <summary>Информационное сообщение.</summary>
    Info,
}

/// <summary>Сообщение валидации, привязанное к полю.</summary>
public sealed record PhoneValidationIssue(
    string Field,
    string Message,
    PhoneValidationSeverity Severity = PhoneValidationSeverity.Blocking);

/// <summary>Результат попытки сохранения.</summary>
public sealed class PhoneSaveResult
{
    /// <summary>Идентификатор сохранённой записи либо исходный ID.</summary>
    public int PhoneId { get; init; }

    /// <summary>SQL-транзакция зафиксирована.</summary>
    public bool Saved { get; init; }

    /// <summary>Сообщения, из-за которых сохранение не выполнялось.</summary>
    public IReadOnlyList<PhoneValidationIssue> Issues { get; init; } = [];
}

/// <summary>SQL batch и параметры сохранения.</summary>
public sealed record PhoneSaveCommand(string Sql, DynamicParameters Parameters);

/// <summary>Исходное и новое состояние для сохранения и синхронизации.</summary>
public sealed class PhoneSaveRequest
{
    /// <summary>Сохранённая основная запись.</summary>
    public Phone OriginalPhone { get; init; } = new();

    /// <summary>Текущая основная запись.</summary>
    public Phone Phone { get; init; } = new();

    /// <summary>Сохранённые связи.</summary>
    public PhoneRelationsSnapshot OriginalRelations { get; init; } = new();

    /// <summary>Текущие связи.</summary>
    public PhoneRelationsSnapshot Relations { get; init; } = new();

    /// <summary>Доступные порты для проверки перед записью.</summary>
    public IReadOnlyCollection<string> AvailablePorts { get; init; } = [];

    /// <summary>Доступные группы перехвата.</summary>
    public IReadOnlyCollection<string> AvailablePickupGroups { get; set; } = [];

    /// <summary>Текущая дата для детерминированной проверки срока.</summary>
    public DateTime Today { get; set; } = DateTime.UtcNow.Date;
}

/// <summary>Запланированная операция синхронизации АТС до либо после SQL-изменения.</summary>
public sealed record PhoneSyncOperation(PhoneSyncKind Kind, string Key, bool Deleting = false);

/// <summary>Результат отдельной операции синхронизации АТС.</summary>
/// <param name="Operation">Выполненная операция.</param>
/// <param name="Error">Текст ошибки либо NULL при успехе.</param>
public sealed record PhoneSyncResult(PhoneSyncOperation Operation, string? Error)
{
    /// <summary>Операция завершилась успешно.</summary>
    public bool Success => Error is null;
}

/// <summary>Тип синхронизации АТС.</summary>
public enum PhoneSyncKind
{
    /// <summary>Порт Meridian.</summary>
    Meridian,

    /// <summary>Номер Xorcom.</summary>
    Xorcom,

    /// <summary>Оборудование CCM.</summary>
    Ccm,
}

/// <summary>Снимок станции для планирования синхронизации.</summary>
public sealed record PhoneSyncStation(int Id, string XorcomName);
