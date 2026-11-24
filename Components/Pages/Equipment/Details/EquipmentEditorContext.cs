namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Неизменяемый контекст экземпляра редактора оборудования.</summary>
public sealed class EquipmentEditorContext
{
    private readonly Func<bool> _isCurrent;
    private bool _valid = true;

    /// <summary>Создаёт контекст, привязанный к карточке и поколению редактора.</summary>
    public EquipmentEditorContext(int equipmentId, long generation, Func<bool> isCurrent)
    {
        EquipmentId = equipmentId;
        Generation = generation;
        _isCurrent = isCurrent;
    }

    /// <summary>Идентификатор оборудования на момент открытия редактора.</summary>
    public int EquipmentId { get; }

    /// <summary>Поколение экземпляра редактора.</summary>
    public long Generation { get; }

    /// <summary>Запрещает дальнейшие команды закрытого редактора.</summary>
    public void Invalidate() => _valid = false;

    /// <summary>Typed state, сохранённый для восстановления редактора.</summary>
    public string? DraftState { get; set; }

    internal Microsoft.AspNetCore.Components.ComponentBase? Component { get; set; }

    /// <summary>Захватывает выбранные значения и baseline текущего редактора.</summary>
    public string? CaptureDraftState() => Component is null ? null : EquipmentEditorStateStore.Capture(Component);

    /// <summary>Восстанавливает typed state без команд записи или внешних действий.</summary>
    public void RestoreDraftState(string json)
    {
        if (Component is not null)
            EquipmentEditorStateStore.Restore(Component, json);
    }

    /// <summary>Проверяет контекст до отправки бизнес-команды.</summary>
    public void EnsureCurrent()
    {
        if (!_valid || !_isCurrent())
            throw new InvalidOperationException("Контекст оборудования изменился. Откройте форму повторно.");
    }
}
