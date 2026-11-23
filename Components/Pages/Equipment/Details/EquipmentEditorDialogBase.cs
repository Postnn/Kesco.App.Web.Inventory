using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Общий жизненный цикл дочернего редактора с сохранением ввода до подтверждённой записи.</summary>
/// <typeparam name="TDraft">Тип пользовательского черновика.</typeparam>
public abstract class EquipmentEditorDialogBase<TDraft> : ComponentBase
{
    /// <summary>Экземпляр MudDialog текущего редактора.</summary>
    [CascadingParameter]
    protected IMudDialogInstance Dialog { get; set; } = null!;

    /// <summary>Серверная команда сохранения.</summary>
    [Parameter]
    public Func<TDraft, Task<EquipmentEditorSaveResult>>? Save { get; set; }

    /// <summary>Признак выполняющейся команды.</summary>
    protected bool _saving;

    /// <summary>Блокировка повтора при неизвестном исходе записи.</summary>
    protected bool _saveBlocked;

    /// <summary>Ошибка валидации или серверной команды.</summary>
    protected string? _error;

    /// <summary>Явно отменяет редактирование без бизнес-команды.</summary>
    protected void Cancel() => Dialog.Cancel();

    /// <summary>Валидирует и сохраняет черновик, закрывая диалог только после подтверждённого commit.</summary>
    protected async Task ApplyAsync(TDraft draft, Func<string?> validate)
    {
        if (_saving || _saveBlocked)
            return;
        _error = validate();
        if (_error is not null)
            return;
        if (Save is null)
        {
            Dialog.Close(DialogResult.Ok(draft));
            return;
        }
        _saving = true;
        try
        {
            var result = await Save(draft);
            if (result.CanClose)
            {
                Dialog.Close(DialogResult.Ok(draft));
                return;
            }
            _saveBlocked = !result.CanRetry;
            _error = result.Message;
        }
        finally
        {
            _saving = false;
        }
    }
}
