using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Общие команды и lifecycle двух карточек справочника.</summary>
public abstract class TerritorialEntityCardBase<TState> : ComponentBase, IAsyncDisposable where TState : class
{
    private const string DraftScope = "main";
    private readonly string _draftOwner = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Настройки каркаса с уникальным CSS-корнем draft-регистрации.</summary>
    protected ClayEntityFormOptions FormOptions { get; } = new() { MaxWidth = "100%" };

    /// <summary>CSS-селектор корня этой карточки и её draft-области.</summary>
    protected string DraftRootSelector => $".{DraftRootClass}";

    /// <summary>Уникальный CSS-класс корня карточки.</summary>
    protected string DraftRootClass => FormOptions.Class!;

    /// <summary>Назначает уникальный CSS-корень для изоляции draft-ввода.</summary>
    protected TerritorialEntityCardBase() => FormOptions.Class = $"territory-draft-{_draftOwner}";
    private CancellationTokenSource? _routeLoad;
    private readonly Dictionary<string, Task> _pendingDraftWork = new(StringComparer.Ordinal);
    private ClayDraftRegistration? _registration;
    private string? _registrationKey;
    private TState? _draftOriginal;
    private DraftRowVersion? _freshVersion;
    private DraftRowVersion? _originalVersion;
    private ClayDraftOperation? _unresolvedOperation;
    private bool _storageRetryRequired;
    private bool _initialStorageFailed;
    private bool _baselineConflict;
    private bool _readOnlyDraft;
    private string? _notSentAttemptId;
    private long? _confirmedCleanupRevision;
    private Func<Task>? _afterConfirmedCleanup;
    private bool _restoreRaw;
    private bool _restoreIncomplete;
    private bool _applyingRaw;
    private bool _disposed;
    private bool _loadPending;
    private bool _navigationApproved;
    private string? _routeKey;

    [Inject] protected DbManager Db { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;
    [Inject] protected IJSRuntime JS { get; set; } = null!;
    [Inject] protected IDialogService Dialogs { get; set; } = null!;
    [Inject] protected AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] protected ClayDraftService DraftStore { get; set; } = null!;

    protected Employee? CurrentUser { get; private set; }
    protected TerritorialEntityAccess Access { get; private set; } = new();
    protected TState? Clean { get; private set; }
    protected TState? Draft { get; private set; }
    protected bool Dirty => Draft is not null && !StatesEqual(Draft, Clean);
    protected bool Busy { get; private set; }
    protected bool Loading { get; private set; } = true;
    protected bool StorageBlocked { get; private set; }
    protected bool ConfirmedCleanupPending => _confirmedCleanupRevision.HasValue;
    protected bool CanRefresh => !Busy && !Loading && (!StorageBlocked || _restoreIncomplete || ConfirmedCleanupPending ||
        _storageRetryRequired || _unresolvedOperation is not null || _baselineConflict && Editable && Clean is not null && MergeFields.Count > 0);
    protected bool DraftInputSuppressed => Loading || Busy || ConfirmedCleanupPending || _initialStorageFailed;
    protected bool CanCommit => !Loading && !Busy && CanRegisterDraft && !_restoreRaw && !_restoreIncomplete && !_applyingRaw && !StorageBlocked && _registration is not null;
    protected string? Message { get; set; }
    protected IReadOnlyList<string> Validation { get; set; } = [];
    protected CancellationToken Lifetime => _lifetime.Token;
    protected abstract string FormKey { get; }
    protected abstract string EntityKey { get; }
    protected abstract string RouteKey { get; }
    protected abstract bool Editable { get; }
    protected virtual bool CanRegisterDraft => Editable;
    protected virtual bool CanReadMissingDraft => false;

    /// <summary>Редактор предоставляет свежее чтение для безопасных команд отмены и обновления.</summary>
    protected virtual bool SupportsEditorCancellation => false;

    /// <summary>Поколение полей редактора, меняющееся при принятии нового снимка или отказе от ввода.</summary>
    protected int EditorGeneration { get; private set; }

    /// <summary>Данные и метаданные, полученные без изменения отображаемого состояния.</summary>
    protected sealed record FreshEditorState(TState Data, TerritorialEntityAccess Access, DraftRowVersion? Version,
        Action? ApplyMetadata = null);

    /// <summary>Читает карточку и её зависимости во временный контейнер.</summary>
    protected virtual Task<FreshEditorState?> ReadFreshEditorAsync(Employee actor, CancellationToken token) =>
        Task.FromResult<FreshEditorState?>(null);
    protected virtual IReadOnlyList<DraftMergeField> MergeFields => [];
    protected virtual DraftRowVersion? ReadDraftRowVersion() => null;

    protected sealed record DraftRowVersion(string Key, int ChangedBy, string? ChangedByName, DateTime ChangedAt);

    protected sealed record CompareRead(TState? Target, TState? Source = null, bool ChangedKey = false);

    protected virtual Task<CompareRead> ReadForComparisonAsync(TState proposed, Employee actor, CancellationToken token) =>
        Task.FromResult(new CompareRead(null));

    protected virtual bool SameComparisonValues(TState actual, TState proposed) => StatesEqual(actual, proposed);

    /// <summary>Сравнивает содержимое карточки после восстановления черновика.</summary>
    protected virtual bool StatesEqual(TState? left, TState? right) => EqualityComparer<TState>.Default.Equals(left, right);

    /// <summary>Загружает разрешения карточки после подтверждения сотрудника.</summary>
    protected virtual async Task LoadAccessAsync(Employee user, CancellationToken routeToken)
    {
        var access = await TerritorialEntityData.ReadAccessAsync(Db, user, routeToken);
        routeToken.ThrowIfCancellationRequested();
        Access = access;
    }

    /// <summary>Определяет ожидаемый доменный отказ без повтора.</summary>
    protected virtual bool IsDomainRejection(Exception exception) => exception is TerritorialEntityRuleException;

    protected abstract Task<TState?> LoadCardAsync(CancellationToken routeToken);

    protected static ClayToolbarButtonOptions OkButton { get; } = new() { Text = "ОК", Icon = Icons.Material.Filled.Check, Color = Color.Primary, Variant = Variant.Filled };
    protected static ClayToolbarButtonOptions ApplyButton { get; } = new() { Text = "Применить", Icon = Icons.Material.Filled.Save };
    protected static ClayToolbarButtonOptions ReloadButton { get; } = new() { Text = "Обновить", Icon = Icons.Material.Filled.Refresh };
    protected static ClayToolbarButtonOptions CancelButton { get; } = new() { Text = "Отмена", Icon = Icons.Material.Filled.Close };
    protected static ClayToolbarButtonOptions DeleteButton { get; } = new() { Text = "Удалить", Icon = Icons.Material.Filled.Delete };

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (string.Equals(_routeKey, RouteKey, StringComparison.Ordinal)) return;
        _routeKey = RouteKey;
        EditorGeneration++;
        _routeLoad?.Cancel();
        _routeLoad?.Dispose();
        _routeLoad = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        FormOptions.Class = $"territory-draft-{_draftOwner}-{Guid.NewGuid():N}";
        _loadPending = Loading = true;
        _restoreRaw = _restoreIncomplete = _applyingRaw = StorageBlocked = false;
        _unresolvedOperation = null;
        _storageRetryRequired = _initialStorageFailed = _baselineConflict = _readOnlyDraft = false;
        _notSentAttemptId = null;
        _confirmedCleanupRevision = null;
        _afterConfirmedCleanup = null;
        CurrentUser = null;
        Access = new();
        _draftOriginal = Clean = Draft = null;
        _originalVersion = _freshVersion = null;
        Message = null;
        Validation = [];
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loadPending && _routeLoad is { } load)
        {
            _loadPending = false;
            var token = load.Token;
            await ReleaseDraftAsync();
            if (!token.IsCancellationRequested) await LoadAsync(token);
            return;
        }
        if (!_restoreRaw || _registration is null) return;
        _restoreRaw = false;
        await RetryRawAsync();
    }

    private async Task RetryRawAsync()
    {
        if (_applyingRaw || _registration is not { } registration) return;
        var token = _routeLoad!.Token;
        _applyingRaw = _restoreIncomplete = true;
        try
        {
            var result = await registration.ApplyRawWithResultAsync(DraftScope, token);
            if (token.IsCancellationRequested) return;
            var readiness = await registration.GetRestoreReadinessAsync(token);
            if (token.IsCancellationRequested || !ReferenceEquals(registration, _registration)) return;
            _restoreIncomplete = result.Pending.Count != 0 || !readiness.Complete;
            if (!StorageBlocked) Message = _restoreIncomplete ? "Часть ввода ожидает восстановления. Нажмите Обновить для повторной попытки." : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested && !StorageBlocked)
                Message = "Не удалось восстановить ввод. Нажмите Обновить для повторной попытки.";
        }
        finally
        {
            if (!token.IsCancellationRequested && ReferenceEquals(registration, _registration))
            {
                _applyingRaw = false;
                StateHasChanged();
            }
        }
    }

    protected async Task UpdateDraftAsync(TState value)
    {
        if (DraftInputSuppressed || !Editable) return;
        Draft = value;
        if (_registration is not { } registration) return;
        try { await PersistAsync(); }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            if (!ReferenceEquals(registration, _registration) || Loading) return;
            _storageRetryRequired = StorageBlocked = true;
            Message = "Черновик не удалось сохранить в браузере. Введённые данные остаются на форме.";
        }
    }

    protected async Task RefreshAsync()
    {
        if (!CanRefresh || _routeLoad is not { } load) return;
        var token = load.Token;
        if (_confirmedCleanupRevision is { } confirmed)
        {
            await RetryConfirmedCleanupAsync(confirmed, token);
            return;
        }
        if (_storageRetryRequired)
        {
            await RetryStorageAsync(token);
            return;
        }
        if (_unresolvedOperation is { } unresolved)
        {
            if (_restoreIncomplete && Draft is not null) await RetryRawAsync();
            if (!token.IsCancellationRequested) await CompareUnresolvedAsync(unresolved, token);
            return;
        }
        if (_restoreIncomplete) { await RetryRawAsync(); return; }
        if (SupportsEditorCancellation && _registration is not null)
        {
            await RefreshEditorAsync(token);
            return;
        }
        if (_baselineConflict)
        {
            await ResolveFieldConflictsAsync(token);
            return;
        }
        if (StorageBlocked) return;
        if (Dirty && !await ConfirmAsync("Обновление", "Отменить несохранённые изменения и загрузить запись повторно?")) return;
        if (token.IsCancellationRequested || Loading) return;
        if (_registration is not null)
        {
            await _registration.DiscardAsync(Lifetime);
            if (token.IsCancellationRequested) return;
            await ReleaseDraftAsync();
        }
        if (!token.IsCancellationRequested) await LoadAsync(token);
    }

    protected async Task CancelAsync()
    {
        if (Busy) return;
        if (SupportsEditorCancellation && !ConfirmedCleanupPending && _unresolvedOperation is null)
        {
            await CancelEditorAsync();
            return;
        }
        var token = _routeLoad?.Token ?? Lifetime;
        if (ConfirmedCleanupPending &&
            !await ConfirmAsync("Закрытие формы", "База подтвердила операцию. Закрыть форму, не завершив обработку черновика?")) return;
        if ((Dirty || _restoreRaw || _restoreIncomplete || _applyingRaw) &&
            !await ConfirmAsync("Закрытие формы", "Закрыть форму без сохранения изменений?")) return;
        if (token.IsCancellationRequested) return;
        if (!Loading && _registration is { } registration && !StorageBlocked)
        {
            Busy = true;
            try
            {
                await registration.DiscardAsync(Lifetime);
                if (token.IsCancellationRequested) return;
                await ReleaseDraftAsync();
                if (token.IsCancellationRequested) return;
                await LoadAsync(token);
            }
            catch (Exception e) when (e is JSException or InvalidOperationException)
            {
                if (!token.IsCancellationRequested)
                {
                    StorageBlocked = true;
                    Message = "Не удалось завершить отмену. Откройте карточку повторно для сверки черновика.";
                }
                return;
            }
            finally { Busy = false; }
        }
        if (!token.IsCancellationRequested) await CloseAsync();
    }

    private async Task CancelEditorAsync()
    {
        if (Loading || _routeLoad is not { } route) return;
        var token = route.Token;
        var registration = _registration;
        var currentGeneration = EditorGeneration;
        if (registration is null)
        {
            if (!Dirty && !Loading) await CloseAsync();
            else Message = "Хранилище черновика недоступно. Ввод сохранён на форме.";
            return;
        }

        Busy = true;
        try
        {
            var scope = await registration.ReadEditorScopeAsync(DraftScope, token);
            if (!IsCurrentEditor(registration, currentGeneration, token)) return;
            if (scope.HasOperation)
            {
                Message = "Сначала завершите сверку предыдущей операции.";
                return;
            }
            if (!Dirty && !scope.HasRaw && !_restoreRaw && !_restoreIncomplete && !_applyingRaw)
            {
                await CloseAsync();
                return;
            }

            var dialog = await Dialogs.ShowExAsync<ClayEditorCancelDialog>("Отмена изменений",
                new DialogParameters(), new DialogOptionsEx
                {
                    DragMode = MudDialogDragMode.Simple, CloseOnEscapeKey = true, CloseButton = true,
                });
            var result = await dialog.Result;
            if (!IsCurrentEditor(registration, currentGeneration, token) ||
                result is not { Canceled: false, Data: ClayEditorCancelChoice choice } ||
                choice == ClayEditorCancelChoice.Continue) return;

            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                var fresh = await ReadEditorForTransitionAsync(token);
                if (!IsCurrentEditor(registration, currentGeneration, token)) return;
                if (fresh is null)
                {
                    Message = "Не удалось прочитать актуальную территорию. Ввод сохранён.";
                    return;
                }
                await AcceptFreshEditorAsync(registration, currentGeneration, scope.Revision, fresh, token);
                return;
            }

            if (!await registration.ReplaceEditorScopeAsync(DraftScope, scope.Revision, null, token))
            {
                if (!token.IsCancellationRequested) Message = "Ввод изменился. Подтвердите отмену ещё раз.";
                return;
            }
            if (!IsCurrentEditor(registration, currentGeneration, token)) return;
            EditorGeneration++;
            Draft = Clean = _draftOriginal = null;
            Validation = [];
            _restoreRaw = _restoreIncomplete = _applyingRaw = false;
            Message = "Ввод отменён. Если вкладка осталась открытой, закройте её или обновите страницу.";
            await CloseAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is JSException or SqlException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) Message = "Не удалось завершить отмену. Ввод сохранён на форме.";
        }
        finally { Busy = false; }
    }

    private async Task RefreshEditorAsync(CancellationToken token)
    {
        if (_registration is not { } registration) return;
        var generation = EditorGeneration;
        var showMerge = false;
        Busy = true;
        try
        {
            var scope = await registration.ReadEditorScopeAsync(DraftScope, token);
            if (!IsCurrentEditor(registration, generation, token) || scope.HasOperation) return;

            var fresh = await ReadEditorForTransitionAsync(token);
            if (!IsCurrentEditor(registration, generation, token)) return;
            if (fresh is null)
            {
                Message = "Не удалось прочитать актуальную территорию. Черновик сохранён.";
                return;
            }
            if (!Dirty && !scope.HasRaw && !_baselineConflict)
            {
                await AcceptFreshEditorAsync(registration, generation, scope.Revision, fresh, token);
                return;
            }

            var latest = await registration.ReadEditorScopeAsync(DraftScope, token);
            if (!IsCurrentEditor(registration, generation, token)) return;
            if (latest.Revision != scope.Revision || latest.HasOperation)
            {
                Message = "Ввод изменился во время обновления. Повторите чтение для сверки.";
                return;
            }
            Clean = fresh.Data;
            _freshVersion = fresh.Version;
            ApplyFreshMetadata(fresh);
            _baselineConflict = !StatesEqual(_draftOriginal, Clean) || _originalVersion != _freshVersion;
            _readOnlyDraft = !CanRegisterDraft;
            StorageBlocked = _readOnlyDraft || _baselineConflict;
            Message = _baselineConflict ? "Карточка изменилась. Проверьте различия с локальным вводом."
                : "Данные перечитаны. Ваш ввод сохранён.";
            showMerge = _baselineConflict && Editable && MergeFields.Count > 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is JSException or SqlException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) Message = "Не удалось обновить данные для сверки. Черновик сохранён.";
        }
        finally { Busy = false; }
        if (showMerge && IsCurrentEditor(registration, generation, token))
            await ResolveFieldConflictsAsync(token);
    }

    private bool IsCurrentEditor(ClayDraftRegistration registration, int generation, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && generation == EditorGeneration &&
        ReferenceEquals(_registration, registration);

    private async Task<FreshEditorState?> ReadEditorForTransitionAsync(CancellationToken token)
    {
        var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), token);
        if (actor is null || actor.Id != CurrentUser?.Id) return null;
        return await ReadFreshEditorAsync(actor, token);
    }

    private void ApplyFreshMetadata(FreshEditorState fresh)
    {
        Access = fresh.Access;
        fresh.ApplyMetadata?.Invoke();
    }

    private async Task AcceptFreshEditorAsync(ClayDraftRegistration registration, int generation, long revision,
        FreshEditorState fresh, CancellationToken token)
    {
        var snapshot = JsonSerializer.Serialize(new DraftSnapshot(fresh.Data, fresh.Data, fresh.Version));
        if (!await registration.ReplaceEditorScopeAsync(DraftScope, revision, snapshot, token))
        {
            if (!token.IsCancellationRequested) Message = "Ввод изменился. Повторите команду.";
            return;
        }
        if (!IsCurrentEditor(registration, generation, token)) return;
        EditorGeneration++;
        Draft = Clean = _draftOriginal = fresh.Data;
        _originalVersion = _freshVersion = fresh.Version;
        ApplyFreshMetadata(fresh);
        _restoreRaw = _restoreIncomplete = _applyingRaw = _baselineConflict = false;
        _readOnlyDraft = StorageBlocked = !CanRegisterDraft;
        Validation = [];
        Message = Editable ? null : "Данные обновлены. Карточка открыта для просмотра.";
    }

    protected async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_navigationApproved)
        {
            _navigationApproved = false;
            return;
        }
        if (Busy)
        {
            context.PreventNavigation();
            return;
        }
        if (!Dirty && !ConfirmedCleanupPending) return;
        context.PreventNavigation();
        var message = ConfirmedCleanupPending
            ? "База подтвердила операцию. Покинуть форму, не завершив обработку черновика?"
            : "Покинуть форму с несохранёнными изменениями?";
        if (!await ConfirmAsync("Закрытие формы", message)) return;
        _navigationApproved = true;
        Navigation.NavigateTo(context.TargetLocation);
    }

    protected Task CloseAsync() => JS.InvokeVoidAsync("clayModelEquipmentForm.close", Lifetime).AsTask();
    protected async Task RefreshOpenerAsync()
    {
        try { await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", Lifetime); }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            Message = "Операция подтверждена. Обновить родительское окно не удалось.";
        }
    }

    protected async Task<bool> ConfirmAsync(string title, string message)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { d => d.Title, title }, { d => d.Message, message }, { d => d.ConfirmText, "Да" }, { d => d.CancelText, "Нет" },
        };
        var dialog = await Dialogs.ShowAsync<ConfirmDialog>(title, parameters);
        return await dialog.Result is { Canceled: false };
    }

    protected async Task<bool> ExecuteWriteAsync(string operation, Func<string?, Task> action)
    {
        if (!CanCommit || _registration is null) return false;
        Busy = true;
        Message = null;
        ClayDraftOperation? attempt = null;
        var databaseConfirmed = false;
        string? beginningAttemptId = null;
        try
        {
            if (!(await _registration.GetRestoreReadinessAsync(Lifetime)).Complete)
            {
                _restoreIncomplete = true;
                Message = "Часть ввода ожидает восстановления. Нажмите Обновить.";
                return false;
            }
            await _registration.EnsureCommandAllowedAsync(Lifetime);
            await PersistAsync();
            var intentData = JsonSerializer.Serialize(new DraftSnapshot(_draftOriginal, Draft!, _originalVersion));
            beginningAttemptId = Guid.NewGuid().ToString("N");
            attempt = await _registration.BeginOperationAsync(
                new ClayDraftOperationIntent(operation, intentData, [DraftScope], beginningAttemptId), Lifetime);
            await action(await ReadPrincipalAsync());
            databaseConfirmed = true;
            await _registration.CommitOperationAsync(attempt.Revision, cancellationToken: Lifetime);
            return true;
        }
        catch (Exception exception)
        {
            if (_disposed) return false;
            if (databaseConfirmed)
            {
                _confirmedCleanupRevision = attempt!.Revision;
                StorageBlocked = true;
                Message = "База подтвердила операцию. Нажмите «Обновить», чтобы завершить обработку черновика.";
                return true;
            }
            var rejected = exception is UnauthorizedAccessException || IsDomainRejection(exception) ||
                exception is SqlException sql && !DbManager.IsConnectivityError(sql) && sql.Number != -2;
            if (attempt is null)
            {
                _notSentAttemptId = beginningAttemptId;
                _storageRetryRequired = StorageBlocked = true;
            }
            else
            {
                try
                {
                    if (rejected) await _registration.RejectOperationAsync(attempt.Revision, Lifetime);
                    else
                    {
                        StorageBlocked = true;
                        await _registration.UnknownOperationAsync(attempt.Revision, Lifetime);
                    }
                }
                catch (Exception storeError) when (storeError is JSException or InvalidOperationException or OperationCanceledException)
                {
                    StorageBlocked = true;
                }
            }
            if (StorageBlocked && attempt is not null) _unresolvedOperation = attempt;
            Message = _unresolvedOperation is not null ? "Исход операции требует сверки. Нажмите «Обновить»."
                : StorageBlocked ? "Команда не отправлена. Нажмите «Обновить» после восстановления хранилища."
                : exception is SqlException ? "База отклонила операцию. Проверьте данные или связанность записи." : exception.Message;
            return false;
        }
        finally { Busy = false; }
    }

    private async Task ResolveFieldConflictsAsync(CancellationToken token)
    {
        if (!Editable || _registration is not { } registration || Clean is not { } fresh || Draft is not { } local) return;
        Busy = true;
        try
        {
            var readiness = await registration.GetRestoreReadinessAsync(token);
            token.ThrowIfCancellationRequested();
            if (!readiness.Complete)
            {
                _restoreIncomplete = true;
                Message = "Сначала полностью восстановите ввод. Нажмите «Обновить».";
                return;
            }
            var state = await registration.ReadRebaseStateAsync(DraftScope, token);
            token.ThrowIfCancellationRequested();
            var plan = new DraftFieldMerge<TState>(_draftOriginal, local, fresh, MergeFields, state.RawKeys);
            IReadOnlyDictionary<string, bool> choice = new Dictionary<string, bool>();
            if (plan.Conflicts.Count == 0)
            {
                if (!await ConfirmAsync("Сверка карточки", "Объединить независимые изменения и принять текущую версию за исходную?")) return;
            }
            else
            {
                var parameters = new DialogParameters<DraftConflictDialog> { { d => d.Conflicts, plan.Conflicts } };
                var dialog = await Dialogs.ShowAsync<DraftConflictDialog>("Сверка карточки", parameters);
                if (await dialog.Result is not { Canceled: false, Data: IReadOnlyDictionary<string, bool> selected }) return;
                choice = selected;
            }

            token.ThrowIfCancellationRequested();
            var resolved = plan.Select(choice);
            var json = JsonSerializer.Serialize(new DraftSnapshot(fresh, resolved.State, _freshVersion));
            var persisted = await registration.RebaseScopeAsync(DraftScope, state.Revision, json, resolved.DiscardedRaw, token);
            token.ThrowIfCancellationRequested();
            _draftOriginal = fresh;
            _originalVersion = _freshVersion;
            Draft = resolved.State;
            _baselineConflict = false;
            _storageRetryRequired = StorageBlocked = !persisted;
            Message = persisted ? "Выбор сохранён в черновике." : "Выбор сделан. Для сохранения черновика нажмите «Обновить».";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is JSException or InvalidOperationException or JsonException)
        {
            if (!token.IsCancellationRequested) Message = "Не удалось подтвердить сохранение выбора. Повторите сверку.";
        }
        finally { Busy = false; }
    }

    private async Task RetryStorageAsync(CancellationToken token)
    {
        Busy = true;
        try
        {
            if (_registration is not { } registration)
            {
                await RegisterDraftAsync(token);
            }
            else
            {
                var unresolved = await registration.RetryStorageAsync(_notSentAttemptId, token);
                token.ThrowIfCancellationRequested();
                _unresolvedOperation = unresolved;
                if (Draft is not null) await PersistAsync();
            }
            token.ThrowIfCancellationRequested();
            _notSentAttemptId = null;
            _storageRetryRequired = _initialStorageFailed = false;
            StorageBlocked = _baselineConflict || _readOnlyDraft || _unresolvedOperation is not null;
            Message = _unresolvedOperation is not null
                ? "Черновик сохранён. Нажмите «Обновить» для сверки незавершённой операции."
                : _readOnlyDraft ? "Черновик доступен для просмотра. Сейчас запись запрещена."
                : _baselineConflict ? "Черновик сохранён. Для записи требуется сверка исходных данных." : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is JSException or InvalidOperationException or JsonException)
        {
            if (!token.IsCancellationRequested)
                Message = "Хранилище черновика пока недоступно. Повторите «Обновить».";
        }
        finally { Busy = false; }
    }

    private async Task CompareUnresolvedAsync(ClayDraftOperation operation, CancellationToken token)
    {
        var guidance = $"Проверьте журнал по идентификатору {operation.AttemptId}. Повтор записи заблокирован.";
        Busy = true;
        try
        {
            var snapshot = JsonSerializer.Deserialize<DraftSnapshot>(operation.Data);
            if (snapshot?.Current is not { } proposed)
            {
                Message = $"Снимок отправленных данных недоступен. {guidance}";
                return;
            }

            var principal = await ReadPrincipalAsync();
            token.ThrowIfCancellationRequested();
            var actor = await Employee.GetCurrentWithRolesAsync(Db, principal, token);
            token.ThrowIfCancellationRequested();
            if (actor is null || actor.Id != CurrentUser?.Id)
            {
                Message = $"Текущий сотрудник для сверки не подтверждён. {guidance}";
                return;
            }

            var deleting = operation.Kind.EndsWith("delete", StringComparison.Ordinal);
            var current = await ReadForComparisonAsync(deleting && snapshot.Original is { } before ? before : proposed, actor, token);
            token.ThrowIfCancellationRequested();
            var detail = current.Target is null
                ? "Запись по ключу операции недоступна."
                : deleting
                    ? "Запись по ключу операции видна в базе."
                    : SameComparisonValues(current.Target, proposed)
                        ? "Данные в базе совпадают с отправленными."
                        : snapshot.Original is { } original && SameComparisonValues(current.Target, original)
                            ? "Данные в базе совпадают с исходными."
                            : "Данные в базе отличаются от отправленных и исходных.";
            if (current.ChangedKey)
                detail += current.Source is null ? " Запись по прежнему ключу недоступна." : " Запись по прежнему ключу видна.";
            Message = $"{detail} {guidance}";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (!token.IsCancellationRequested) Message = $"Сверка недоступна. {guidance}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Busy = false; }
    }

    protected Task AfterDraftCleanupAsync(Func<Task> action)
    {
        if (!ConfirmedCleanupPending) return action();
        _afterConfirmedCleanup = action;
        return Task.CompletedTask;
    }

    private async Task RetryConfirmedCleanupAsync(long revision, CancellationToken token)
    {
        if (_registration is not { } registration) return;
        Busy = true;
        try
        {
            await registration.CommitOperationAsync(revision, cancellationToken: Lifetime);
            if (token.IsCancellationRequested) return;
            _confirmedCleanupRevision = null;
            StorageBlocked = false;
            Message = null;
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
                Message = "База подтвердила операцию. Хранилище черновика пока недоступно. Повторите «Обновить».";
            return;
        }
        finally { Busy = false; }

        var complete = _afterConfirmedCleanup;
        _afterConfirmedCleanup = null;
        if (complete is not null) await complete();
    }

    protected void Accept(TState value)
    {
        _draftOriginal = Clean = Draft = value;
        _originalVersion = _freshVersion = ReadDraftRowVersion();
    }

    private async Task LoadAsync(CancellationToken token)
    {
        Loading = true;
        _restoreIncomplete = _readOnlyDraft = false;
        StorageBlocked = false;
        Message = null;
        Validation = [];
        _draftOriginal = Clean = Draft = null;
        _originalVersion = _freshVersion = null;
        var openingDraft = false;
        try
        {
            var principal = await ReadPrincipalAsync();
            token.ThrowIfCancellationRequested();
            var employee = await Employee.GetCurrentWithRolesAsync(Db, principal, token);
            token.ThrowIfCancellationRequested();
            CurrentUser = employee;
            if (CurrentUser is null)
            {
                Message = "Не удалось подтвердить текущего сотрудника.";
                return;
            }
            await LoadAccessAsync(CurrentUser, token);
            token.ThrowIfCancellationRequested();
            var card = await LoadCardAsync(token);
            token.ThrowIfCancellationRequested();
            Clean = Draft = card;
            _freshVersion = ReadDraftRowVersion();
            if (Draft is not null || CanReadMissingDraft)
            {
                openingDraft = true;
                await RegisterDraftAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (token.IsCancellationRequested) return;
            Message = Draft is null ? "Не удалось загрузить карточку. Повторите загрузку."
                : "Не удалось подключить хранилище черновика. Сохранение приостановлено.";
            _storageRetryRequired = _initialStorageFailed = openingDraft && exception is JSException or InvalidOperationException;
            StorageBlocked = openingDraft;
            if (_storageRetryRequired)
                Message = "Хранилище черновика недоступно. Для повтора нажмите «Обновить».";
        }
        finally
        {
            if (!token.IsCancellationRequested && !_disposed)
            {
                Loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task RegisterDraftAsync(CancellationToken token)
    {
        if (CurrentUser is null) return;
        var user = CurrentUser.Id.ToString(CultureInfo.InvariantCulture);
        var key = EntityKey;
        var root = DraftRootSelector;
        var canWrite = CanRegisterDraft;
        await WithDraftKeyAsync(key, async () =>
        {
            token.ThrowIfCancellationRequested();
            // JS может уже выделить блокировку. Получаем handle даже после смены маршрута.
            var registration = await DraftStore.RegisterHandleAsync(FormKey, key, user, 1, root, Lifetime);
            if (token.IsCancellationRequested ||
                (Clean is null || !canWrite) && !registration.Restore.HasDraft && registration.Restore.Operation is null)
            {
                await registration.DisposeAsync();
                return;
            }
            _registration = registration;
            _registrationKey = key;
            await RestoreDraftAsync(registration, token);
            if (!token.IsCancellationRequested && !canWrite)
            {
                _readOnlyDraft = StorageBlocked = true;
                if (_unresolvedOperation is null)
                    Message = "Черновик доступен для просмотра. Сейчас запись запрещена.";
            }
        });
    }

    private async Task RestoreDraftAsync(ClayDraftRegistration registration, CancellationToken token)
    {
        var restore = registration.Restore;
        _draftOriginal = Clean;
        _originalVersion = _freshVersion;
        if (SupportsEditorCancellation && restore.Operation is null && restore.CancelledScopes?.Contains(DraftScope) == true)
        {
            if (Clean is not null && CanRegisterDraft)
            {
                await registration.OpenScopeAsync(DraftScope, token);
                token.ThrowIfCancellationRequested();
                await PersistAsync();
            }
            return;
        }
        if (restore.TypedScopes?.TryGetValue(DraftScope, out var json) == true &&
            JsonSerializer.Deserialize<DraftSnapshot>(json) is { } existing)
        {
            _draftOriginal = existing.Original;
            var sameBaseline = StatesEqual(existing.Original, Clean);
            _originalVersion = existing.Version ?? (SupportsEditorCancellation && sameBaseline ? _freshVersion : null);
            Draft = existing.Current;
            if (!sameBaseline || _originalVersion != _freshVersion)
            {
                _baselineConflict = StorageBlocked = true;
                Message = "Карточка изменилась после создания черновика. Нажмите «Обновить» для сверки.";
            }
        }
        else if (restore.HasDraft || restore.Operation is not null)
        {
            // Старая raw-only запись: исходная версия неизвестна, нельзя принять свежую за исходную.
            _baselineConflict = StorageBlocked = true;
            Message = "Исходный снимок отсутствует. Нажмите «Обновить» для сверки.";
            _draftOriginal = null;
            _originalVersion = null;
        }
        else
        {
            await PersistAsync();
            if (token.IsCancellationRequested) return;
        }
        if (restore.OperationSnapshot is { } pending)
        {
            _unresolvedOperation = pending;
            StorageBlocked = true;
            Message = "Обнаружена незавершённая операция. Нажмите «Обновить» для сверки.";
        }
        _restoreRaw = restore.HasDraft;
    }

    private Task PersistAsync() => _registration!.SaveTypedScopeAsync(DraftScope, JsonSerializer.Serialize(new DraftSnapshot(_draftOriginal, Draft!, _originalVersion)), Lifetime);

    protected async Task<string?> ReadPrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private async Task ReleaseDraftAsync()
    {
        if (_registration is not { } registration || _registrationKey is not { } key) return;
        _registration = null;
        _registrationKey = null;
        await WithDraftKeyAsync(key, () => registration.DisposeAsync().AsTask());
    }

    private async Task WithDraftKeyAsync(string key, Func<Task> work)
    {
        // Очередь только по ключу: B может открыться сразу, а возврат к A дождётся освобождения A.
        _pendingDraftWork.TryGetValue(key, out var previous);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDraftWork[key] = completed.Task;
        try
        {
            if (previous is not null) await previous;
            await work();
        }
        finally
        {
            if (_pendingDraftWork[key] == completed.Task) _pendingDraftWork.Remove(key);
            completed.TrySetResult();
        }
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        _routeLoad?.Dispose();
        await ReleaseDraftAsync();
        _lifetime.Dispose();
    }

    private sealed record DraftSnapshot(TState? Original, TState Current, DraftRowVersion? Version = null);
}
