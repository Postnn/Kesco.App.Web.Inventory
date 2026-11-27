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
    private const string CompletionKind = MaintenanceData.CompletionKind;
    private const string Question = "Подтвердите выполнение";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _draftRoot = $"maintenance-{Guid.NewGuid():N}";
    private Guid _draftInstance = Guid.NewGuid();
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
    [Inject] private IJSRuntime JS { get; set; } = null!;
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
    private bool _readingFilter;
    private ListQuery? _pendingQuery;
    private sealed record ListQuery(ClayFilterGroupNode? Filter, string OrderBy);
    private ClayDraftOperation? _confirmedOperation;
    private ClayDraftOperation? _unknownOperation;
    private string? _notSentAttemptId;
    private MaintenanceList? _list;
    private IReadOnlyList<MaintenanceWork> _rows => _list?.Rows ?? [];
    private ClayDraftRegistration? _registration;
    private int? _legacyCheckedEmployee;
    private bool _closingDraft;
    private int? _pendingConfirmation;
    private bool _busy;
    private bool IsBusy => _busy || _readingFilter;
    private bool _canComplete;
    private HashSet<int> _serverPending = [];
    private bool RecoveryRequired => _confirmedOperation is not null || _unknownOperation is not null || _notSentAttemptId is not null;
    private bool _loadFailed;
    private bool _disposed;
    private TimeZoneInfo? _sourceTimeZone;
    private string? _message;
    private Severity _messageSeverity = Severity.Warning;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        if (Config["Maintenance:SourceTimeZoneId"] is { Length: > 0 } zoneId &&
            TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
            _sourceTimeZone = zone;
    }

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
            try { _grid.RestoreFilter((ClayFilterGroupNode)_filter.Clone()); }
            finally { _applyingFilter = false; }
            StateHasChanged();
        }
    }

    private async Task ReloadAsync()
    {
        if (IsBusy || _disposed) return;
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
                _draftInstance = Guid.NewGuid();
                _legacyCheckedEmployee = null;
                _filter = null;
                _orderBy = DefaultOrder;
                _pendingQuery = null;
                _confirmedOperation = null;
                _unknownOperation = null;
                _notSentAttemptId = null;
                _pendingConfirmation = null;
                _list = null;
            }
            _list = await ReadListAsync(ct);
            var actor = CurrentUser;
            var instance = _draftInstance;
            var userKey = actor.Id.ToString(CultureInfo.InvariantCulture);
            var existing = _registration;
            _canComplete = await MaintenanceData.CanCompleteAsync(Db, actor, ct);
            ClayDraftRegistration? opened = null;
            Task<ClayDraftRegistration> OpenRam(CancellationToken token) =>
                Drafts.RegisterRamHandleAsync("maintenance", "list", userKey, 1, "." + _draftRoot, instance, token);
            if (_canComplete && _legacyCheckedEmployee != actor.Id)
            {
                bool Current() => !_disposed && !ct.IsCancellationRequested && CurrentUser?.Id == actor.Id &&
                    _draftInstance == instance && _canComplete && ReferenceEquals(existing, _registration);
                async Task ImportAndReadAsync(IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence, CancellationToken token)
                {
                    await ImportLegacyOperationsAsync(actor, evidence, token);
                    var pending = await MaintenanceData.ReadPendingAsync(Db, token);
                    if (!Current()) throw new InvalidOperationException("Контекст регламентных работ изменился.");
                    _serverPending = pending;
                }
                if (existing is null)
                    opened = await Drafts.TransferLegacyAndRegisterRamAsync("maintenance", "list", userKey,
                        async (evidence, token) =>
                        {
                            await ImportAndReadAsync(evidence, token);
                            return await OpenRam(token);
                        }, Current, ct);
                else
                    await Drafts.TransferLegacyEvidenceAsync("maintenance", "list", userKey, ImportAndReadAsync,
                        ct, retireSnapshotsAfterImport: true);
                _legacyCheckedEmployee = actor.Id;
            }
            else
                _serverPending = _canComplete ? await MaintenanceData.ReadPendingAsync(Db, ct) : [];
            if (_registration is null)
            {
                var registration = opened ?? await OpenRam(ct);
                if (_disposed || ct.IsCancellationRequested || CurrentUser?.Id != actor.Id || _draftInstance != instance)
                {
                    await registration.DisposeAsync();
                    return;
                }

                _registration = registration;
                _registrationEmployee = CurrentUser.Id;
                if (registration.Restore.HasDraft)
                {
                    await ApplyRestoredDraftAsync(registration.Restore, ct);
                }
            }

            if (_registration is { } current)
            {
                if (_notSentAttemptId is { } attemptId)
                {
                    RestoreOperation(await current.RetryStorageAsync(attemptId, ct));
                    _notSentAttemptId = null;
                }
                if (_canComplete && _unknownOperation is { } unknown)
                    await ReconcileReceiptAsync(current, unknown, CurrentUser, ct);
                if (_confirmedOperation is { } confirmed)
                    await FinishConfirmedAsync(current, confirmed, ct);
            }

            if (RecoveryRequired)
                ReportRecovery();
            else if (!_canComplete)
                Report("Список доступен для просмотра. Подтверждение выполнения недоступно.", Severity.Info);
            else if (_rows.Any(row => _serverPending.Contains(row.Id)))
                Report("Для некоторых работ есть незавершённая попытка подтверждения. Повтор для этих работ заблокирован.");
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
        finally
        {
            _busy = false;
            await ApplyPendingQueryAsync();
        }
    }

    private async Task ApplyRestoredDraftAsync(ClayDraftRestore restore, CancellationToken ct)
    {
        _pendingConfirmation = restore.TypedScopes?.TryGetValue(ConfirmationScope, out var json) == true
            ? JsonSerializer.Deserialize<int?>(json) : null;
        RestoreOperation(restore.OperationSnapshot);
        _filter = restore.TypedScopes?.TryGetValue(FilterScope, out var filterJson) == true
            ? JsonSerializer.Deserialize<IClayFilterNode>(filterJson) as ClayFilterGroupNode : null;
        _restoreFilter = _filter is not null;
        if (_restoreFilter) _list = await ReadListAsync(ct);
    }

    private async Task ImportLegacyOperationsAsync(Employee actor, IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence,
        CancellationToken ct)
    {
        var attempts = new List<(int WorkId, Guid AttemptId)>(evidence.Count);
        foreach (var entry in evidence)
        {
            if (entry.Kind != CompletionKind || !Guid.TryParse(entry.AttemptId, out var attemptId) ||
                !int.TryParse(entry.Data, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new InvalidOperationException("Прежняя попытка требует сверки.");
            attempts.Add((id, attemptId));
        }
        await MaintenanceData.ImportLegacyOperationsAsync(Db, await PrincipalAsync(), actor.Id, attempts, ct);
    }

    private Task<MaintenanceList> ReadListAsync(CancellationToken ct) =>
        ReadListAsync(new ListQuery(_filter, _orderBy), ct);

    private async Task<MaintenanceList> ReadListAsync(ListQuery query, CancellationToken ct)
    {
        var filter = await MaintenanceTime.ToSqlFilterAsync(query.Filter, _sourceTimeZone, JS, ct);
        var parameters = new DynamicParameters();
        var predicate = ClayCompositeSqlBuilder.Build(filter, parameters, FilterColumns);
        return await MaintenanceData.ReadAsync(Db, ct, predicate, parameters, query.OrderBy);
    }

    private Task OnFilterChangedAsync(ClayDataQuery query)
    {
        if (_disposed || _closingDraft || _applyingFilter) return Task.CompletedTask;
        _restoreFilter = false;
        _pendingQuery = new ListQuery((ClayFilterGroupNode?)query.CompositeFilter?.Clone(),
            query.BuildOrderBy(DefaultOrder, FilterColumns));
        return ApplyPendingQueryAsync();
    }

    private async Task ApplyPendingQueryAsync()
    {
        if (IsBusy || _disposed || _closingDraft || _pendingQuery is null) return;

        var ct = _lifetime.Token;
        _readingFilter = true;
        try
        {
            while (!_disposed && _pendingQuery is { } query)
            {
                _pendingQuery = null;
                try
                {
                    var list = await ReadListAsync(query, ct);
                    if (_disposed || _pendingQuery is not null) continue;

                    if (_registration is { } registration)
                        await registration.SaveTypedScopeAsync(FilterScope, JsonSerializer.Serialize<IClayFilterNode?>(query.Filter), ct);
                    if (_disposed) break;

                    _filter = query.Filter;
                    _orderBy = query.OrderBy;
                    _list = list;
                    if (_loadFailed && _canComplete && !RecoveryRequired) _message = null;
                    _loadFailed = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    if (_pendingQuery is not null) continue;
                    _loadFailed = true;
                    Report("Не удалось применить или сохранить фильтр. Перечитайте список.", Severity.Error);
                }
            }
        }
        finally { _readingFilter = false; }
    }

    private async Task<DistinctValuesResult> LoadFilterValuesAsync(string column, ClayDataQuery _, int limit)
    {
        var ct = _lifetime.Token;
        var values = await MaintenanceData.ReadFilterValuesAsync(Db, column, limit, ct);
        var nonBlank = new List<object?>();
        var hasBlanks = false;
        foreach (var rawValue in values)
        {
            var value = MaintenanceTime.IsTimeColumn(column) && rawValue is DateTime sourceDate
                ? await MaintenanceTime.FormatDistinctAsync(sourceDate, _sourceTimeZone, JS, ct)
                : rawValue;
            if (value is null or "") hasBlanks = true;
            else nonBlank.Add(value);
        }
        return new DistinctValuesResult
        {
            Values = nonBlank.Count > limit ? [] : nonBlank,
            HasBlanks = hasBlanks,
            Capped = nonBlank.Count > limit,
            TotalDistinct = nonBlank.Count,
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
        string? beginAttemptId = null;
        try
        {
            await registration.OpenScopeAsync(ConfirmationScope, ct);
            await registration.EnsureScopesCanBeAcceptedAsync([ConfirmationScope], ct);
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
            var journalAttemptId = Guid.NewGuid();
            beginAttemptId = journalAttemptId.ToString("N");
            attempt = await registration.BeginOperationAsync(
                new ClayDraftOperationIntent(CompletionKind, JsonSerializer.Serialize(id), [ConfirmationScope], beginAttemptId, BlockOtherDraftOperations: true), ct);
            await MaintenanceData.CompleteAsync(Db, principal, _registrationEmployee, id, journalAttemptId, ct);

            _confirmedOperation = attempt;
            _pendingConfirmation = null;
            await FinishConfirmedAsync(registration, attempt, ct);
        }
        catch (InventoryOperationPendingException e)
        {
            _serverPending.Add(id);
            if (attempt is not null && !await UpdateMarkerAsync(() => registration.RejectOperationAsync(attempt.Revision, ct)))
                _unknownOperation = attempt;
            Report(e.Message, Severity.Warning);
        }
        catch (Exception e) when (e is MaintenanceRejectedException or InventoryOperationReservationException)
        {
            if (e is InventoryOperationReservationException)
                _loadFailed = true;
            if (attempt is not null && !await UpdateMarkerAsync(() => registration.RejectOperationAsync(attempt.Revision, ct)))
                _unknownOperation = attempt;
            Report(_unknownOperation is null ? e.Message : $"{e.Message} Состояние попытки требует проверки.", Severity.Error);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            if (_confirmedOperation is null)
            {
                if (attempt is not null)
                {
                    _unknownOperation = attempt;
                    await UpdateMarkerAsync(() => registration.UnknownOperationAsync(attempt.Revision, ct));
                }
                else
                    _notSentAttemptId = beginAttemptId;
            }

            if (RecoveryRequired)
                ReportRecovery();
            else
            {
                _loadFailed = true;
                Report("Не удалось открыть или сохранить подтверждение. Перечитайте список.", Severity.Error);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _busy = false;
            await ApplyPendingQueryAsync();
        }
    }

    private async Task FinishConfirmedAsync(ClayDraftRegistration registration, ClayDraftOperation attempt, CancellationToken ct)
    {
        if (attempt.Status != ClayDraftOperationStatus.Confirmed)
        {
            await registration.RecordConfirmedOperationAsync(attempt.Revision, ct);
            _confirmedOperation = attempt with { Status = ClayDraftOperationStatus.Confirmed };
        }
        await registration.CommitOperationAsync(attempt.Revision, cancellationToken: ct);
        _confirmedOperation = null;
        try
        {
            _list = await ReadListAsync(ct);
            _loadFailed = false;
            _message = null;
            Snackbar.Add("Выполнение регламентной работы записано.", Severity.Success);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _loadFailed = true;
            Report("Выполнение записано, список не обновлён. Перечитайте список перед следующим подтверждением.");
        }
    }

    private async Task ReconcileReceiptAsync(ClayDraftRegistration registration, ClayDraftOperation unknown,
        Employee actor, CancellationToken ct)
    {
        if (unknown.Kind != CompletionKind || !Guid.TryParse(unknown.AttemptId, out var attemptId) ||
            !int.TryParse(unknown.Data, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            !_rows.Any(row => row.Id == id))
            return;

        var receipt = await MaintenanceData.ReadReceiptAsync(Db, actor, id, attemptId, ct);
        if (receipt is null || receipt.AttemptId != attemptId)
            return;

        if (receipt.State == InventoryOperationState.Confirmed && receipt.ResultEntityId == id)
        {
            _unknownOperation = null;
            _confirmedOperation = unknown;
            _pendingConfirmation = null;
        }
        else if (receipt.State == InventoryOperationState.Rejected &&
                 await UpdateMarkerAsync(() => registration.RejectOperationAsync(unknown.Revision, ct)))
            _unknownOperation = null;
    }

    private void RestoreOperation(ClayDraftOperation? operation) => _unknownOperation = operation;

    private void ReportRecovery()
    {
        if (_confirmedOperation is not null)
            Report("Выполнение записано. Не удалось завершить сохранение результата в браузере.", Severity.Error);
        else if (_unknownOperation is { } attempt)
        {
            var work = attempt.Kind == CompletionKind &&
                int.TryParse(attempt.Data, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? $"Работа №{id}. "
                : "";
            Report($"Исход выполнения требует проверки по журналу. {work}Попытка {attempt.AttemptId}. Повтор записи заблокирован.", Severity.Error);
        }
        else if (_notSentAttemptId is not null)
            Report("Команда не отправлена. Хранилище не подтвердило начало операции.", Severity.Error);
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
        !_disposed && !_closingDraft && !IsBusy && !RecoveryRequired && !_loadFailed && _canComplete && _registration is not null &&
        row.IsApproved && !_serverPending.Contains(row.Id) && (_pendingConfirmation is null || _pendingConfirmation == row.Id);

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
        _pendingQuery = null;
        await _lifetime.CancelAsync();
        if (_registration is not null) await _registration.DisposeAsync();
        _lifetime.Dispose();
    }
}

