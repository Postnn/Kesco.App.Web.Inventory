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

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Общие команды и lifecycle двух карточек справочника.</summary>
public abstract class TerritorialEntityCardBase<TState> : ComponentBase, IAsyncDisposable where TState : class
{
    private const string DraftScope = "main";
    private readonly CancellationTokenSource _lifetime = new();
    private ClayDraftRegistration? _registration;
    private bool _restoreRaw;
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
    protected bool Dirty => Draft is not null && !EqualityComparer<TState>.Default.Equals(Draft, Clean);
    protected bool Busy { get; private set; }
    protected bool Loading { get; private set; } = true;
    protected bool StorageBlocked { get; private set; }
    protected bool CanCommit => !Loading && !Busy && !StorageBlocked && _registration is not null;
    protected string? Message { get; set; }
    protected IReadOnlyList<string> Validation { get; set; } = [];
    protected CancellationToken Lifetime => _lifetime.Token;
    protected abstract string FormKey { get; }
    protected abstract string EntityKey { get; }
    protected abstract string RouteKey { get; }
    protected abstract bool Editable { get; }
    protected virtual bool CanRegisterDraft => Editable;
    protected abstract Task<TState?> LoadCardAsync();

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
        _loadPending = true;
        Loading = true;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender || _loadPending)
        {
            _loadPending = false;
            await ReleaseDraftAsync();
            await LoadAsync();
            return;
        }
        if (!_restoreRaw || _registration is null) return;
        _restoreRaw = false;
        try { await _registration.ApplyRawAsync(Lifetime); }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            StorageBlocked = true;
            Message = "Часть черновика недоступна. Сохранение приостановлено.";
            StateHasChanged();
        }
    }

    protected async Task UpdateDraftAsync(TState value)
    {
        if (Busy || !Editable) return;
        Draft = value;
        if (_registration is null) return;
        try { await PersistAsync(); }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            StorageBlocked = true;
            Message = "Черновик не удалось сохранить в браузере. Введённые данные остаются на форме.";
        }
    }

    protected async Task RefreshAsync()
    {
        if (Busy || StorageBlocked) return;
        if (Dirty && !await ConfirmAsync("Обновление", "Отменить несохранённые изменения и загрузить запись повторно?")) return;
        if (_registration is not null)
        {
            await _registration.DiscardAsync(Lifetime);
            await ReleaseDraftAsync();
        }
        await LoadAsync();
    }

    protected async Task CancelAsync()
    {
        if (Busy) return;
        if (Dirty && !await ConfirmAsync("Закрытие формы", "Закрыть форму без сохранения изменений?")) return;
        if (_registration is not null && !StorageBlocked) await _registration.DiscardAsync(Lifetime);
        await CloseAsync();
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
        if (!Dirty) return;
        context.PreventNavigation();
        if (!await ConfirmAsync("Закрытие формы", "Покинуть форму с несохранёнными изменениями?")) return;
        _navigationApproved = true;
        Navigation.NavigateTo(context.TargetLocation);
    }

    protected Task CloseAsync() => JS.InvokeVoidAsync("clayModelEquipmentForm.close", Lifetime).AsTask();
    protected Task RefreshOpenerAsync() => JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", Lifetime).AsTask();

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
        try
        {
            await _registration.EnsureCommandAllowedAsync(Lifetime);
            await PersistAsync();
            var intentData = JsonSerializer.Serialize(new DraftSnapshot(Clean!, Draft!));
            attempt = await _registration.BeginOperationAsync(new ClayDraftOperationIntent(operation, intentData, [DraftScope]), Lifetime);
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
                StorageBlocked = true;
                Message = "База подтвердила операцию. Не удалось завершить обработку черновика. Повтор записи заблокирован.";
                return true;
            }
            var rejected = exception is UnauthorizedAccessException or TerritorialEntityRuleException ||
                exception is SqlException sql && !DbManager.IsConnectivityError(sql) && sql.Number != -2;
            if (attempt is null) StorageBlocked = true;
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
            Message = StorageBlocked ? "Результат операции не подтверждён. Черновик сохранён для сверки. Повтор заблокирован."
                : exception is SqlException ? "База отклонила операцию. Проверьте данные или связанность записи." : exception.Message;
            return false;
        }
        finally { Busy = false; }
    }

    protected void Accept(TState value) => Clean = Draft = value;

    private async Task LoadAsync()
    {
        Loading = true;
        StorageBlocked = false;
        Message = null;
        Validation = [];
        Clean = Draft = null;
        try
        {
            CurrentUser = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), Lifetime);
            if (CurrentUser is null)
            {
                Message = "Не удалось подтвердить текущего сотрудника.";
                return;
            }
            Access = await TerritorialEntityData.ReadAccessAsync(Db, CurrentUser, Lifetime);
            Clean = Draft = await LoadCardAsync();
            if (Draft is not null && CanRegisterDraft) await RegisterDraftAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Message = Draft is null ? "Не удалось загрузить карточку. Повторите загрузку."
                : "Не удалось подключить хранилище черновика. Сохранение приостановлено.";
            StorageBlocked = Draft is not null;
        }
        finally
        {
            Loading = false;
            if (!_disposed) StateHasChanged();
        }
    }

    private async Task RegisterDraftAsync()
    {
        if (CurrentUser is null) return;
        _registration = await DraftStore.RegisterHandleAsync(FormKey, EntityKey,
            CurrentUser.Id.ToString(CultureInfo.InvariantCulture), 1, Lifetime);
        var restore = _registration.Restore;
        if (restore.TypedScopes?.TryGetValue(DraftScope, out var json) == true &&
            JsonSerializer.Deserialize<DraftSnapshot>(json) is { } existing)
        {
            Draft = existing.Current;
            if (!EqualityComparer<TState>.Default.Equals(existing.Original, Clean))
            {
                StorageBlocked = true;
                Message = "Карточка изменилась после создания черновика. Перед записью нужна сверка.";
            }
        }
        if (restore.Operation is not null)
        {
            StorageBlocked = true;
            Message = "Обнаружена незавершённая операция. Проверьте запись перед повтором.";
        }
        _restoreRaw = restore.HasDraft;
    }

    private Task PersistAsync() => _registration!.SaveTypedScopeAsync(DraftScope, JsonSerializer.Serialize(new DraftSnapshot(Clean!, Draft!)), Lifetime);

    protected async Task<string?> ReadPrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private async Task ReleaseDraftAsync()
    {
        if (_registration is not null) await _registration.DisposeAsync();
        _registration = null;
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        await ReleaseDraftAsync();
        _lifetime.Dispose();
    }

    private sealed record DraftSnapshot(TState Original, TState Current);
}
