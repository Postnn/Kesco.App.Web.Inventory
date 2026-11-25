using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Dapper;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Maintenance;

/// <summary>Показывает регламентные работы и регистрирует подтверждённое выполнение.</summary>
public partial class MaintenancePage : IAsyncDisposable
{
    private const string DefaultOrder = "ВыполнитьДо ASC, КодРегламентныхРабот ASC";
    private const string ConfirmationScope = "confirmation";
    private const string FilterScope = "InvMaintenanceFilter";
    private const string CompletionKind = "maintenance.complete";
    private const string Question = "Подтвердите выполнение";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _draftRoot = $"maintenance-{Guid.NewGuid():N}";
    private readonly ClayGridOptions _options = new()
    {
        Title = "Регламентные работы",
        ShowToolbar = true,
        ShowTitle = false,
        ShowPagination = false,
        ShowAddButton = false,
        AllowSorting = true,
        AllowColumnReorder = false,
        ColumnMenuMode = ColumnMenuMode.Hidden,
        FilterTrayExpandedByDefault = true,
        FillAvailableHeight = true,
        CompactLayout = true,
        EmptyText = "Ничего не найдено.",
        FilterColumnTypes = new Dictionary<string, ColumnType>
        {
            [MaintenanceColumns.Deadline] = ColumnType.Date,
            [MaintenanceColumns.LastCompleted] = ColumnType.Date,
        },
    };

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;
    [Inject] private IConfiguration Config { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;

    private static readonly HashSet<string> FilterColumns = new(MaintenanceData.FilterColumns, StringComparer.Ordinal);
    private Employee? CurrentUser { get; set; }
    private int? _registrationEmployee;
    private ClayGrid<MaintenanceWork>? _grid;
    private ClayFilterGroupNode? _filter;
    private string _orderBy = DefaultOrder;
    private bool _restoreFilter;
    private bool _applyingFilter;
    private bool _completionRecorded;
    private MaintenanceList? _list;
    private IReadOnlyList<MaintenanceWork> _rows => _list?.Rows ?? [];
    private ClayDraftRegistration? _registration;
    private int? _pendingConfirmation;
    private bool _busy;
    private bool _canComplete;
    private bool _uncertain;
    private bool _loadFailed;
    private bool _disposed;
    private string? _message;
    private Severity _messageSeverity = Severity.Warning;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await ReloadAsync();
            if (!_disposed) StateHasChanged();
        }
        else if (_restoreFilter && _grid is not null && _filter is not null)
        {
            _restoreFilter = false;
            _applyingFilter = true;
            try { _grid.RestoreFilter(_filter); }
            finally { _applyingFilter = false; }
            StateHasChanged();
        }
    }

    private async Task ReloadAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        _loadFailed = false;
        var ct = _lifetime.Token;
        try
        {
            CurrentUser = await Employee.GetCurrentWithRolesAsync(Db, await PrincipalAsync(), ct);
            if (CurrentUser is null)
            {
                _list = null;
                _canComplete = false;
                Report("Не удалось подтвердить текущего сотрудника.", Severity.Error);
                return;
            }

            if (_registration is not null && _registrationEmployee != CurrentUser.Id)
            {
                await _registration.DisposeAsync();
                _registration = null;
                _filter = null;
                _uncertain = false;
                _completionRecorded = false;
                _pendingConfirmation = null;
                _list = null;
            }
            _list = await ReadListAsync(ct);
            _canComplete = await MaintenanceData.CanCompleteAsync(Db, CurrentUser, ct);
            if (_registration is null)
            {
                var registration = await Drafts.RegisterHandleAsync("maintenance", "list",
                    CurrentUser.Id.ToString(CultureInfo.InvariantCulture), 1, "." + _draftRoot, ct);
                if (_disposed)
                {
                    await registration.DisposeAsync();
                    return;
                }

                _registration = registration;
                _registrationEmployee = CurrentUser.Id;
                _uncertain = registration.Restore.Operation is not null;
                if (registration.Restore.TypedScopes?.TryGetValue(ConfirmationScope, out var json) == true)
                    _pendingConfirmation = JsonSerializer.Deserialize<int?>(json);
                if (registration.Restore.TypedScopes?.TryGetValue(FilterScope, out var filterJson) == true)
                {
                    _filter = JsonSerializer.Deserialize<IClayFilterNode>(filterJson) as ClayFilterGroupNode;
                    if (_filter is not null)
                    {
                        _list = await ReadListAsync(ct);
                        _restoreFilter = true;
                    }
                }
            }

            if (_uncertain)
                Report(_completionRecorded
                    ? "Выполнение записано. Предыдущая попытка сохранения результата в браузере требует проверки."
                    : "Список перечитан. Исход предыдущей операции требует проверки по журналу выполнения.");
            else if (!_canComplete)
                Report("Список доступен для просмотра. Подтверждение выполнения недоступно.", Severity.Info);
            else
                _message = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            _canComplete = false;
            _loadFailed = true;
            Report("Не удалось загрузить регламентные работы или восстановить состояние подтверждения.", Severity.Error);
        }
        finally { _busy = false; }
    }

    private Task<MaintenanceList> ReadListAsync(CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        var predicate = ClayCompositeSqlBuilder.Build(_filter, parameters, FilterColumns);
        return MaintenanceData.ReadAsync(Db, ct, predicate, parameters, _orderBy);
    }

    private async Task OnFilterChangedAsync(ClayDataQuery query)
    {
        if (_busy || _disposed || _applyingFilter) return;
        var ct = _lifetime.Token;
        _busy = true;
        try
        {
            _filter = query.CompositeFilter;
            _orderBy = query.BuildOrderBy(DefaultOrder, FilterColumns);
            _list = await ReadListAsync(ct);
            _loadFailed = false;
            if (_registration is { } registration)
                await registration.SaveTypedScopeAsync(FilterScope, JsonSerializer.Serialize<IClayFilterNode?>(_filter), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _loadFailed = true;
            Report("Не удалось применить или сохранить фильтр. Перечитайте список.", Severity.Error);
        }
        finally { _busy = false; }
    }

    private async Task<DistinctValuesResult> LoadFilterValuesAsync(string column, ClayDataQuery _, int limit)
    {
        var values = await MaintenanceData.ReadFilterValuesAsync(Db, column, limit, _lifetime.Token);
        var nonBlank = values.Where(value => value is not null and not "").ToArray();
        return new DistinctValuesResult
        {
            Values = nonBlank.Length > limit ? [] : nonBlank,
            HasBlanks = nonBlank.Length != values.Count,
            Capped = nonBlank.Length > limit,
            TotalDistinct = nonBlank.Length,
        };
    }

    private async Task ConfirmAsync(int id)
    {
        var row = _rows.FirstOrDefault(item => item.Id == id);
        if (row is null || !CanConfirm(row) || _registration is not { } registration) return;

        _busy = true;
        _message = null;
        var ct = _lifetime.Token;
        ClayDraftOperation? attempt = null;
        var beginRequested = false;
        var completed = false;
        try
        {
            await registration.EnsureCommandAllowedAsync(ct);
            _pendingConfirmation = id;
            await registration.SaveTypedScopeAsync(ConfirmationScope, JsonSerializer.Serialize(id), ct);
            var parameters = new DialogParameters<ConfirmDialog>
            {
                { dialog => dialog.Title, Question },
                { dialog => dialog.Message, $"Подтвердите выполнение регламентной работы:\n{row.Name}" },
                { dialog => dialog.CancelText, "Нет" },
            };
            var dialog = await Dialogs.ShowExAsync<ConfirmDialog>(Question, parameters,
                new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, CloseOnEscapeKey = true, CloseButton = true });
            if (await dialog.Result is not { Canceled: false, Data: true })
            {
                await ClearPendingAsync(registration, ct);
                return;
            }

            ct.ThrowIfCancellationRequested();
            var principal = await PrincipalAsync();
            ct.ThrowIfCancellationRequested();
            beginRequested = true;
            attempt = await registration.BeginOperationAsync(
                new ClayDraftOperationIntent(CompletionKind, JsonSerializer.Serialize(id), [ConfirmationScope]), ct);
            await MaintenanceData.CompleteAsync(Db, principal, _registrationEmployee, id, ct);
            completed = true;
            _pendingConfirmation = null;
            await registration.CommitOperationAsync(attempt.Revision, cancellationToken: ct);
            try
            {
                _list = await ReadListAsync(ct);
                Snackbar.Add("Выполнение регламентной работы записано.", Severity.Success);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _loadFailed = true;
                Report("Выполнение записано, список не обновлён. Перечитайте список перед следующим подтверждением.");
            }
        }
        catch (MaintenanceRejectedException e)
        {
            if (attempt is not null)
            {
                _uncertain = !await UpdateMarkerAsync(() => registration.RejectOperationAsync(attempt.Revision, ct));
            }
            Report(_uncertain ? $"{e.Message} Состояние попытки требует проверки." : e.Message, Severity.Error);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _uncertain = beginRequested;
            _completionRecorded = completed;
            if (!completed && attempt is not null)
                await UpdateMarkerAsync(() => registration.UnknownOperationAsync(attempt.Revision, ct));
            _loadFailed = !_uncertain;
            Report(completed
                ? "Выполнение записано. Не удалось завершить сохранение результата в браузере."
                : beginRequested && attempt is null
                    ? "Команда не отправлена. Хранилище не подтвердило начало операции."
                    : _uncertain
                    ? "Исход выполнения требует проверки по журналу. Повтор записи заблокирован."
                    : "Не удалось открыть или сохранить подтверждение. Перечитайте список.", Severity.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _busy = false; }
    }

    private async Task CancelPendingAsync()
    {
        if (_busy || _uncertain || _registration is not { } registration) return;
        _busy = true;
        try { await ClearPendingAsync(registration, _lifetime.Token); }
        catch (Exception) when (!_disposed) { Report("Не удалось удалить сохранённый вопрос."); }
        finally { _busy = false; }
    }

    private async Task ClearPendingAsync(ClayDraftRegistration registration, CancellationToken ct)
    {
        await registration.SaveTypedScopeAsync(ConfirmationScope, "null", ct);
        _pendingConfirmation = null;
    }

    private static async Task<bool> UpdateMarkerAsync(Func<Task> update)
    {
        try { await update(); return true; }
        catch (JSException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    private bool CanConfirm(MaintenanceWork row) =>
        !_disposed && !_busy && !_uncertain && !_loadFailed && _canComplete && _registration is not null && row.IsApproved;

    private async Task<string?> PrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private string? EmployeeLink(MaintenanceWork row) => row.EmployeeId is > 0 && !string.IsNullOrWhiteSpace(row.EmployeeName)
        ? InventoryMenuUrlBuilder.BuildCardLink("@URI_user_form", row.EmployeeId.Value.ToString(CultureInfo.InvariantCulture), Config, Navigation.Uri).Href
        : null;

    private async Task OpenAsync(string url, string key)
    {
        try
        {
            if (!await Windows.OpenAsync(url, key, _lifetime.Token))
                Snackbar.Add("Не удалось открыть отдельное окно. Проверьте разрешение браузера.", Severity.Warning);
        }
        catch (JSException) { Snackbar.Add("Не удалось открыть отдельное окно.", Severity.Warning); }
    }

    private static Color DeadlineColor(DateTime deadline, DateTime now) => MaintenancePresentation.DeadlineSeverity(deadline, now) switch
    {
        MaintenancePresentation.Severity.Error => Color.Error,
        MaintenancePresentation.Severity.Warning => Color.Warning,
        _ => Color.Default,
    };

    private void Report(string message, Severity severity = Severity.Warning)
    {
        _message = message;
        _messageSeverity = severity;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_registration is not null) await _registration.DisposeAsync();
        _lifetime.Dispose();
    }
}

