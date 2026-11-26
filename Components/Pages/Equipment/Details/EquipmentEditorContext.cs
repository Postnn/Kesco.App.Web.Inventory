namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Состояние готовности редактора к восстановлению и сохранению.</summary>
public enum EquipmentEditorReadiness
{
    /// <summary>Обязательные чтения ещё выполняются.</summary>
    Pending,
    /// <summary>Обязательные чтения и восстановление завершены успешно.</summary>
    Ready,
    /// <summary>Обязательное чтение или восстановление завершилось ошибкой.</summary>
    Failed,
}

/// <summary>Ревизия typed state, опубликованная конкретным редактором.</summary>
/// <param name="EquipmentId">Идентификатор оборудования.</param>
/// <param name="Generation">Поколение редактора.</param>
/// <param name="Revision">Монотонная ревизия внутри поколения.</param>
/// <param name="State">Сериализованное typed state без секретов.</param>
public sealed record EquipmentEditorDraftChange(int EquipmentId, long Generation, long Revision, string State);

/// <summary>Неизменяемый контекст экземпляра редактора оборудования.</summary>
public sealed class EquipmentEditorContext
{
    private readonly Func<bool> _isCurrent;
    private readonly object _draftSync = new();
    private volatile bool _valid = true;
    private string? _pendingDraftState;
    private long _draftRevision;
    private Task _draftPublish = Task.CompletedTask;

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

    /// <summary>Сохраняет каждую последнюю ревизию; вызовы не выполняются параллельно.</summary>
    public Func<EquipmentEditorDraftChange, Task>? DraftChanged { get; set; }

    /// <summary>Применяет raw только в DOM-области регистрации этого редактора.</summary>
    public Func<Task>? RawRestore { get; set; }

    /// <summary>Повторяет только ожидающие разрешённые поля после свежей проверки прав редактором.</summary>
    public Func<Task>? PendingRawRestore { get; set; }

    /// <summary>Уведомляет родителя о pending/ready/failed состоянии редактора.</summary>
    public Func<EquipmentEditorReadiness, Task>? ReadinessChanged { get; set; }

    /// <summary>Typed state, сохранённый для восстановления редактора.</summary>
    public string? DraftState { get; set; }

    /// <summary>Признак актуального открытого поколения.</summary>
    public bool IsCurrent => _valid && _isCurrent();

    internal Microsoft.AspNetCore.Components.ComponentBase? Component { get; set; }

    /// <summary>Запрещает дальнейшие команды и отложенные уведомления закрытого редактора.</summary>
    public void Invalidate() => _valid = false;

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
        if (!IsCurrent)
            throw new InvalidOperationException("Контекст оборудования изменился. Откройте форму повторно.");
    }

    internal Task PublishDraftStateAsync(string state)
    {
        lock (_draftSync)
        {
            if (!IsCurrent)
                return Task.CompletedTask;
            _pendingDraftState = state;
            _draftRevision++;
            if (_draftPublish.IsCompleted)
                _draftPublish = PublishDraftLoopAsync();
            return _draftPublish;
        }
    }

    internal async Task ApplyRawAsync()
    {
        if (!IsCurrent || RawRestore is null)
            return;
        await RawRestore();
    }

    internal async Task ApplyPendingRawAsync()
    {
        if (!IsCurrent)
            return;
        if (PendingRawRestore is not { } retry)
            throw new InvalidOperationException("Повтор восстановления недоступен.");
        await retry();
    }

    internal async Task ReportReadinessAsync(EquipmentEditorReadiness readiness)
    {
        if (IsCurrent && ReadinessChanged is not null)
            await ReadinessChanged(readiness);
    }

    private async Task PublishDraftLoopAsync()
    {
        while (true)
        {
            string state;
            long revision;
            Func<EquipmentEditorDraftChange, Task>? callback;
            lock (_draftSync)
            {
                if (_pendingDraftState is null)
                    return;
                state = _pendingDraftState;
                revision = _draftRevision;
                _pendingDraftState = null;
                callback = DraftChanged;
            }
            if (!IsCurrent)
                return;
            if (callback is not null)
                await callback(new EquipmentEditorDraftChange(EquipmentId, Generation, revision, state));
            lock (_draftSync)
            {
                if (_pendingDraftState is null)
                    return;
            }
        }
    }
}
