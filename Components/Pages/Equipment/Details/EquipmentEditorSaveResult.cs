namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Результат серверной команды дочернего редактора оборудования.</summary>
public enum EquipmentEditorSaveStatus
{
    /// <summary>Команда отклонена; исправленный ввод можно отправить повторно.</summary>
    Rejected,
    /// <summary>Ответ на команду потерян; перед повтором требуется сверка состояния.</summary>
    Unknown,
    /// <summary>Запись и обновление представления подтверждены.</summary>
    Committed,
    /// <summary>Запись подтверждена, но повторное чтение завершилось ошибкой.</summary>
    RefreshFailed,
}

/// <summary>Результат сохранения, определяющий закрытие диалога и допустимость повтора.</summary>
public sealed record EquipmentEditorSaveResult(EquipmentEditorSaveStatus Status, string? Message = null)
{
    /// <summary>Можно закрыть редактор без повторной записи.</summary>
    public bool CanClose => Status is EquipmentEditorSaveStatus.Committed or EquipmentEditorSaveStatus.RefreshFailed;

    /// <summary>Можно повторить команду после исправления ввода.</summary>
    public bool CanRetry => Status == EquipmentEditorSaveStatus.Rejected;

    /// <summary>Создаёт результат отклонённой команды.</summary>
    public static EquipmentEditorSaveResult Reject(string message) => new(EquipmentEditorSaveStatus.Rejected, message);

    /// <summary>Создаёт результат неизвестного исхода записи.</summary>
    public static EquipmentEditorSaveResult Unknown(string message) => new(EquipmentEditorSaveStatus.Unknown, message);

    /// <summary>Создаёт результат подтверждённой записи.</summary>
    public static EquipmentEditorSaveResult Commit(string? message = null) => new(EquipmentEditorSaveStatus.Committed, message);

    /// <summary>Создаёт результат записи с ошибкой последующего чтения.</summary>
    public static EquipmentEditorSaveResult RefreshFailed(string message) => new(EquipmentEditorSaveStatus.RefreshFailed, message);
}
