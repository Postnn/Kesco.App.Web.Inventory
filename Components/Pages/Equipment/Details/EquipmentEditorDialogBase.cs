using System.Text.Json;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Редактор автоматически восстанавливает ввод после рендера.</summary>
public interface IEquipmentDeferredRawRestore { }

/// <summary>Общий жизненный цикл дочернего редактора с сохранением ввода до подтверждённой записи.</summary>
/// <typeparam name="TDraft">Тип пользовательского черновика.</typeparam>
public abstract partial class EquipmentEditorDialogBase<TDraft> : ComponentBase, IDisposable
{
    private EquipmentEditorContext? _attachedContext;
    private bool _draftStateRestored;
    private string? _lastPublishedDraftState;
    private EquipmentEditorContext? _rawRestoreContext;
    private EquipmentEditorReadiness _rawRestoreReadiness;
    private string? _openingDraftState;
    private Dictionary<string, JsonElement>? _openingFields;
    private IDialogReference? _cancelChoiceDialog;

    [Inject] private IDialogService CancellationDialogs { get; set; } = null!;

    private bool HasDeferredRawRestore => this is IEquipmentDeferredRawRestore && EditorContext?.RawRestore is not null;

    /// <summary>Запись и принятие результата заблокированы до подтверждённого восстановления полей.</summary>
    protected bool RawRestoreBlocked => HasDeferredRawRestore &&
        (!ReferenceEquals(_rawRestoreContext, EditorContext) || _rawRestoreReadiness != EquipmentEditorReadiness.Ready);

    /// <summary>Показывает состояние автоматического восстановления в текущей области редактора.</summary>
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

    /// <summary>Серверная проверка присутствия для сохранённой записи редактора.</summary>
    [Parameter]
    public ClayPresenceTarget? PresenceTarget { get; set; }

    /// <summary>Отображает присутствие в актуальном редакторе.</summary>
    protected RenderFragment EditorPresence => builder =>
    {
        if (!IsEditorCurrent() || PresenceTarget is not { } target)
            return;

        builder.OpenComponent<ClayPresencePanel>(0);
        builder.AddAttribute(1, nameof(ClayPresencePanel.Target), target);
        builder.CloseComponent();
    };

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

    /// <summary>Проверяет точную область ввода перед отменой или сбросом в открытом редакторе.</summary>
    protected async Task CancelScopedAsync()
    {
        if (_saving || CancelPending || !IsEditorCurrent())
            return;
        if (EditorContext is not { Cancellation: { } cancellation } context)
        {
            Cancel();
            return;
        }

        CancelPending = true;
        long? sensitiveLease = null;
        var resetAccepted = false;
        bool Current() => IsEditorCurrent() && ReferenceEquals(EditorContext, context);
        try
        {
            var sensitive = await ReadSensitiveInputStateAsync();
            if (SensitiveCancellationRoot.HasValue && sensitive is null or { Composing: true })
            {
                if (Current()) _error = "Завершите ввод секретного поля и повторите отмену.";
                return;
            }
            var visible = context.CaptureDraftState();
            var scope = await cancellation.ReadScope();
            if (!Current())
                return;
            if (visible is null || scope is null || _saveBlocked)
            {
                _error = "Не удалось проверить состояние редактора. Ввод оставлен в редакторе.";
                return;
            }

            var choice = ClayEditorCancelChoice.CloseWithoutSaving;
            if (scope.HasRaw || visible != _openingDraftState || sensitive is { Dirty: true })
            {
                var parameters = new DialogParameters<ClayEditorCancelDialog>();
                if (cancellation.IsNew)
                    parameters.Add(x => x.ResetText, "Отменить мои изменения и начать заново");
                var prompt = await CancellationDialogs.ShowExAsync<ClayEditorCancelDialog>("Отмена изменений", parameters,
                    new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, CloseButton = true, CloseOnEscapeKey = true });
                if (!Current())
                {
                    prompt.Close(DialogResult.Cancel());
                    return;
                }
                _cancelChoiceDialog = prompt;
                var decision = await prompt.Result;
                if (!Current() || decision is not { Canceled: false, Data: ClayEditorCancelChoice selected } ||
                    selected == ClayEditorCancelChoice.Continue)
                    return;
                choice = selected;
            }

            string? replacement = null;
            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                replacement = await cancellation.ReadFreshState();
                if (!Current())
                    return;
                if (replacement is null)
                {
                    _error = "Не удалось прочитать актуальные данные. Ввод сохранён.";
                    return;
                }
            }

            if (!Current())
                return;
            if (sensitive is not null && (sensitiveLease = await FreezeSensitiveInputAsync(sensitive.Revision)) is null)
            {
                if (Current()) _error = "Ввод секретного поля изменился. Повторите отмену.";
                return;
            }
            if (visible != context.CaptureDraftState() ||
                !await cancellation.Replace(scope.Revision, visible, replacement, () => CheckSensitiveLeaseAsync(sensitiveLease)))
            {
                if (Current()) _error = "Ввод или данные редактора изменились. Повторите действие.";
                return;
            }
            if (!Current())
                return;
            if (replacement is null)
            {
                Cancel();
                return;
            }

            context.RestoreDraftState(replacement);
            OnDraftStateRestored();
            SetOpeningDraftState(context.CaptureDraftState());
            _lastPublishedDraftState = _openingDraftState;
            _rawRestoreContext = context;
            _rawRestoreReadiness = EquipmentEditorReadiness.Ready;
            ScopedInputVersion++;
            resetAccepted = true;
            _releaseSensitiveAfterRender = sensitiveLease;
            _error = null;
        }
        catch (Exception error) when (IsRawRestoreFailure(error) || error is System.Data.Common.DbException or
            UnauthorizedAccessException or TimeoutException)
        {
            if (Current()) _error = "Не удалось завершить отмену. Ввод сохранён.";
        }
        finally
        {
            cancellation.DiscardFreshState?.Invoke();
            if (!resetAccepted && sensitiveLease is { } lease)
                await ReleaseSensitiveInputAsync(lease);
            _cancelChoiceDialog = null;
            CancelPending = false;
        }
    }

    /// <summary>Отмечает незавершённый выбор или чтение при отмене.</summary>
    protected bool CancelPending { get; private set; }

    /// <summary>Заменяет DOM редактора после проверенного сброса ввода.</summary>
    protected int ScopedInputVersion { get; private set; }

    /// <inheritdoc />
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        if (EditorContext is null)
            return;
        EditorContext.Component = this;
        var attached = !ReferenceEquals(_attachedContext, EditorContext);
        if (attached)
        {
            _attachedContext = EditorContext;
            _draftStateRestored = false;
            _lastPublishedDraftState = null;
            SetOpeningDraftState(EditorContext.CaptureDraftState());
        }
        if (!_draftStateRestored && EditorContext.DraftState is not null)
        {
            _draftStateRestored = true;
            EditorContext.RestoreDraftState(EditorContext.DraftState);
            OnDraftStateRestored();
        }
        if (attached)
            StateHasChanged();
    }

    private void SetOpeningDraftState(string? state)
    {
        _openingDraftState = state;
        _openingFields = state is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(state);
    }

    /// <summary>Читает исходный снимок выбранной модели дочернего редактора.</summary>
    protected T? OpeningFieldValue<T>(string field) where T : class =>
        _openingFields?.TryGetValue(field, out var original) == true ? original.Deserialize<T>() : null;

    /// <summary>Сравнивает поле с исходным снимком текущего редактора.</summary>
    protected bool IsFieldChanged<T>(string field, T current) =>
        _openingFields?.TryGetValue(field, out var original) == true &&
        !EqualityComparer<T>.Default.Equals(current, original.Deserialize<T>());

    /// <summary>Возвращает исходный текст поля для отслеживания непринятого ввода.</summary>
    protected string? OpeningFieldText(string field) =>
        _openingFields?.TryGetValue(field, out var original) == true ? original.GetString() ?? string.Empty : null;

    /// <summary>Возвращает исходное представление даты для отслеживания непринятого ввода.</summary>
    protected string? OpeningFieldDate(string field, string? format) =>
        _openingFields?.TryGetValue(field, out var original) == true
            ? original.Deserialize<DateTime?>()?.ToString(format) ?? string.Empty
            : null;

    /// <summary>Возвращает исходное представление времени для отслеживания непринятого ввода.</summary>
    protected string? OpeningFieldTime(string field, string format) =>
        _openingFields?.TryGetValue(field, out var original) == true
            ? (original.Deserialize<TimeSpan?>() is { } time ? DateTime.Today.Add(time).ToString(format) : string.Empty)
            : null;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await UpdateSensitiveWatchAsync();
        if (_disposed || EditorContext is null)
            return;
        var context = EditorContext;
        if (HasDeferredRawRestore && !ReferenceEquals(_rawRestoreContext, context))
        {
            await RestoreRawAsync();
            if (ReferenceEquals(EditorContext, context) && _rawRestoreReadiness == EquipmentEditorReadiness.Failed)
                await RestoreRawAsync();
        }
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
    public void Dispose()
    {
        _disposed = true;
        _cancelChoiceDialog?.Close(DialogResult.Cancel());
        _ = ResetSensitiveWatchAsync();
    }

    /// <summary>Валидирует и сохраняет черновик, закрывая диалог только после подтверждённого commit.</summary>
    protected async Task ApplyAsync(TDraft draft, Func<string?> validate)
    {
        if (_saving || _saveBlocked || _disposed || RawRestoreBlocked || CancelPending)
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
