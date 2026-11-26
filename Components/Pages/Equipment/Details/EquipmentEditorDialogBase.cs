using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Редактор восстанавливает raw после рендера и сам предлагает повтор ожидающего восстановления.</summary>
public interface IEquipmentDeferredRawRestore { }

/// <summary>Общий жизненный цикл дочернего редактора с сохранением ввода до подтверждённой записи.</summary>
/// <typeparam name="TDraft">Тип пользовательского черновика.</typeparam>
public abstract class EquipmentEditorDialogBase<TDraft> : ComponentBase, IDisposable
{
    private EquipmentEditorContext? _attachedContext;
    private bool _draftStateRestored;
    private string? _lastPublishedDraftState;
    private EquipmentEditorContext? _rawRestoreContext;
    private EquipmentEditorReadiness _rawRestoreReadiness;
    private static readonly ClayToolbarButtonOptions RetryRawRestoreOptions = new() { Text = "Повторить восстановление" };

    private bool HasDeferredRawRestore => this is IEquipmentDeferredRawRestore && EditorContext?.RawRestore is not null;

    /// <summary>Запись и принятие результата заблокированы до подтверждённого восстановления полей.</summary>
    protected bool RawRestoreBlocked => HasDeferredRawRestore &&
        (!ReferenceEquals(_rawRestoreContext, EditorContext) || _rawRestoreReadiness != EquipmentEditorReadiness.Ready);

    /// <summary>Показывает ход восстановления и повтор для текущей области редактора.</summary>
    protected RenderFragment RawRestoreNotice => builder =>
    {
        if (!RawRestoreBlocked)
            return;
        var failed = ReferenceEquals(_rawRestoreContext, EditorContext) && _rawRestoreReadiness == EquipmentEditorReadiness.Failed;
        builder.OpenComponent<MudAlert>(0);
        builder.AddAttribute(1, nameof(MudAlert.Severity), failed ? Severity.Warning : Severity.Info);
        builder.AddAttribute(2, nameof(MudAlert.ChildContent), (RenderFragment)(content =>
        {
            content.AddContent(0, failed ? "Не все поля восстановлены. Сохранение заблокировано." : "Восстановление полей…");
            if (!failed)
                return;
            content.OpenComponent<ClayToolbarButton>(1);
            content.AddAttribute(2, nameof(ClayToolbarButton.Options), RetryRawRestoreOptions);
            content.AddAttribute(3, nameof(ClayToolbarButton.OnClick), EventCallback.Factory.Create(this, RestoreRawAsync));
            content.CloseComponent();
        }));
        builder.CloseComponent();
    };

    /// <summary>Экземпляр MudDialog текущего редактора.</summary>
    [CascadingParameter]
    protected IMudDialogInstance Dialog { get; set; } = null!;

    /// <summary>Серверная команда сохранения.</summary>
    [Parameter]
    public Func<TDraft, Task<EquipmentEditorSaveResult>>? Save { get; set; }

    /// <summary>Контекст карточки и поколения, в котором открыт редактор.</summary>
    [Parameter]
    public EquipmentEditorContext? EditorContext { get; set; }

    /// <summary>Признак выполняющейся команды.</summary>
    protected bool _saving;

    /// <summary>Блокировка повтора при неизвестном исходе записи.</summary>
    protected bool _saveBlocked;

    /// <summary>Ошибка валидации или серверной команды.</summary>
    protected string? _error;

    /// <summary>Признак освобождения компонента для отложенных чтений.</summary>
    protected bool _disposed;

    /// <summary>Явно отменяет редактирование без бизнес-команды.</summary>
    protected void Cancel() => Dialog.Cancel();

    /// <inheritdoc />
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        if (EditorContext is null)
            return;
        if (!ReferenceEquals(_attachedContext, EditorContext))
        {
            _attachedContext = EditorContext;
            _draftStateRestored = false;
            _lastPublishedDraftState = null;
        }
        EditorContext.Component = this;
        if (!_draftStateRestored && EditorContext.DraftState is not null)
        {
            _draftStateRestored = true;
            EditorContext.RestoreDraftState(EditorContext.DraftState);
            OnDraftStateRestored();
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || EditorContext is null)
            return;
        var context = EditorContext;
        if (HasDeferredRawRestore && !ReferenceEquals(_rawRestoreContext, context))
            await RestoreRawAsync();
        if (_disposed || !ReferenceEquals(EditorContext, context) || !context.IsCurrent)
            return;
        var state = EditorContext.CaptureDraftState();
        if (state is null || state == _lastPublishedDraftState)
            return;
        _lastPublishedDraftState = state;
        try
        {
            await EditorContext.PublishDraftStateAsync(state);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or
            Microsoft.JSInterop.JSException or Microsoft.JSInterop.JSDisconnectedException)
        {
            if (!_disposed)
                _error = "Не удалось сохранить черновик редактора. Измените поле для повтора перед подтверждением записи.";
        }
    }

    private async Task RestoreRawAsync()
    {
        if (!HasDeferredRawRestore || EditorContext is not { } context || !IsEditorCurrent() ||
            (ReferenceEquals(_rawRestoreContext, context) && _rawRestoreReadiness != EquipmentEditorReadiness.Failed))
            return;

        _rawRestoreContext = context;
        _rawRestoreReadiness = EquipmentEditorReadiness.Pending;
        bool Current() => IsEditorCurrent() && ReferenceEquals(EditorContext, context);
        try
        {
            await context.ReportReadinessAsync(EquipmentEditorReadiness.Pending);
            if (!Current())
                return;
            await context.ApplyRawAsync();
            if (!Current())
                return;
            await context.ReportReadinessAsync(EquipmentEditorReadiness.Ready);
            if (Current())
                _rawRestoreReadiness = EquipmentEditorReadiness.Ready;
        }
        catch (Exception ex) when (IsRawRestoreFailure(ex))
        {
            if (!Current())
                return;
            _rawRestoreReadiness = EquipmentEditorReadiness.Failed;
            try { await context.ReportReadinessAsync(EquipmentEditorReadiness.Failed); }
            catch (Exception failure) when (IsRawRestoreFailure(failure)) { }
        }
        finally
        {
            if (Current())
                StateHasChanged();
        }
    }

    private static bool IsRawRestoreFailure(Exception error) =>
        error is InvalidOperationException or OperationCanceledException or JSException or JSDisconnectedException;

    /// <summary>Вызывается после typed restore до начала обязательных чтений.</summary>
    protected virtual void OnDraftStateRestored()
    {
    }

    /// <summary>Проверяет, что отложенное завершение относится к живому редактору.</summary>
    protected bool IsEditorCurrent() => !_disposed && (EditorContext?.IsCurrent ?? true);

    /// <inheritdoc />
    public void Dispose() => _disposed = true;

    /// <summary>Валидирует и сохраняет черновик, закрывая диалог только после подтверждённого commit.</summary>
    protected async Task ApplyAsync(TDraft draft, Func<string?> validate)
    {
        if (_saving || _saveBlocked || _disposed || RawRestoreBlocked)
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
            EditorContext?.EnsureCurrent();
            var result = await Save(draft);
            if (result.CanClose)
            {
                Dialog.Close(DialogResult.Ok(draft));
                return;
            }
            _saveBlocked = !result.CanRetry;
            _error = result.Message;
        }
        catch (InvalidOperationException ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _saving = false;
        }
    }
}
