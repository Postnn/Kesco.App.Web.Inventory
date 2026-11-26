using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Locations;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Inv;

/// <summary>Инвентаризация на месте с единым сценарием подтверждения для списка и сканера.</summary>
public partial class LocationFact : IAsyncDisposable
{
    private const string DraftScope = "main";
    private const string WorkplaceScope = "workplace";
    private const string OperationKind = "confirm-location";
    private static readonly ClayTextBoxOptions LegacyLocationInput = new() { Label = "Расположение, поисковый текст", ReadOnly = true, DraftKey = "fact.location" };
    private static readonly ClayTextBoxOptions LegacyResponsibleInput = new() { Label = "Ответственный, поисковый текст", ReadOnly = true, DraftKey = "fact.responsible" };
    private static readonly ClayTextBoxOptions LegacySearchInput = new() { Label = "Поиск оборудования", ReadOnly = true, DraftKey = "fact.search" };
    private static readonly ClayTextAreaOptions LegacyNotesInput = new() { Label = "Примечание", ReadOnly = true, Lines = 3, DraftKey = "fact.notes" };
    private static readonly ClaySelectOptions LocationOptions = new() { Label = "Расположение", DraftKey = "fact.location", Required = true };
    private static readonly ClaySelectOptions EmployeeOptions = new() { Label = "Поиск по ответственному", DraftKey = "fact.responsible" };
    private static readonly ClayTextAreaOptions NotesOptions = new() { Label = "Примечание", Lines = 3, MaxLength = 500, DraftKey = "fact.notes" };
    private static readonly ClayTextAreaOptions ReadOnlyNotesOptions = new()
    {
        Label = "Примечание", Lines = 3, MaxLength = 500, DraftKey = "fact.notes", ReadOnly = true, Disabled = true,
    };
    private static readonly DialogOptions DialogOptions = new() { CloseOnEscapeKey = true, BackdropClick = false, FullWidth = true, MaxWidth = MaxWidth.Small };
    private static readonly ClayGridOptions GridOptions = new()
    {
        Id = "location-fact-grid", ShowToolbar = false, ShowAddButton = false, ShowPagination = false, PageSize = int.MaxValue,
        AllowColumnReorder = false, AllowSorting = false, EnableValueFilter = false, ColumnMenuMode = ColumnMenuMode.Hidden,
    };
    private readonly ClaySelectOptions _equipmentOptions = new() { Label = "Поиск по SN, Инв:№, коду", DraftKey = "fact.search", Disabled = true, AutoSelectSingle = true };
    private readonly CancellationTokenSource _lifetime = new();
    private ClaySelect<LocationLookup, int?>? _locationSelect;
    private ClaySelect<LocationFactMatch, int?>? _equipmentSelect;
    private LocationFactEquipmentSource _equipmentSource = null!;
    private IReadOnlyList<LocationFactRow> _rows = [];
    private Employee? CurrentUser { get; set; }
    private LocationFactSite? _site;
    private int? _siteChoice;
    private LocationFactSnapshot? _pending;
    private LocationFactSnapshot? _comparison;
    private bool _conflict;
    private ClayDraftRegistration? _registration;
    private int? _responsibleId;
    private int? _equipmentId;
    private int _equipmentVersion;
    private int _dialogVersion;
    private string? _message;
    private string? _dialogError;
    private string _notes = "";
    private bool _itOnly;
    private bool _scanner;
    private bool _scanOperation;
    private bool _broken;
    private bool _askRelocation;
    private bool _dialogVisible;
    private bool _focusLocation;
    private bool _focusSearch;
    private bool _focusNotes;
    private bool _restoreRaw;
    private bool _restoreWorkplaceRaw;
    private bool _workplaceRawIncomplete;
    private bool _legacyRawPending;
    private bool _showLegacyRaw;
    private bool _restoreLegacyRaw;
    private bool _legacyRawIncomplete;
    private bool _restoreIncomplete;
    private bool _ready;
    private bool _busy;
    private bool _blocked;
    private bool _storageRetryRequired;
    private string? _notSentAttemptId;
    private ClayDraftOperation? _pendingOperation;
    private ConfirmedFact? _confirmedCleanup;
    private bool _disposed;
    private bool CanConfirm => CurrentUser?.HasRole(LocationFactData.RequiredRole) == true && _registration is not null && !_blocked && !_legacyRawPending;
    private bool CanSubmitPending => CanConfirm && !_busy && !_conflict && !_restoreRaw && !_restoreIncomplete &&
        _pending is { } expected && _site?.Id == expected.Site.Id;
    private bool CanEditPending => _dialogVisible && _pending is not null && !_blocked && !_busy && !_askRelocation;
    private CancellationToken Token => _lifetime.Token;

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IConfiguration Config { get; set; } = null!;
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;
    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private LocationSelectDataSource Locations { get; set; } = null!;
    [Inject] private EmployeeSelectDataSource Employees { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnInitialized() => _equipmentSource = new(Db);

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                CurrentUser = await Employee.GetCurrentWithRolesAsync(Db, await PrincipalAsync(), Token);
                if (CurrentUser is null) { _message = "Не удалось подтвердить текущего сотрудника."; return; }
                if (!CurrentUser.HasRole(LocationFactData.RequiredRole))
                {
                    _message = "Доступен просмотр учётного списка. Для подтверждения нужна роль специалиста по инвентаризации.";
                    _focusLocation = true;
                    return;
                }
                _registration = await Drafts.RegisterHandleAsync("location-fact", "workplace", CurrentUser.Id.ToString(CultureInfo.InvariantCulture),
                    1, ".location-fact, .location-fact-dialog", Token);
                var restore = _registration.Restore;
                _pendingOperation = restore.OperationSnapshot;
                _blocked = _pendingOperation is not null;
                var sent = ReadCommand(_pendingOperation);
                if (_pendingOperation is { Status: ClayDraftOperationStatus.Confirmed } confirmed && sent is not null)
                {
                    _confirmedCleanup = new ConfirmedFact(confirmed, sent, CheckedAt: null, Scanner: false, ReceiptRecorded: true);
                    _message = "Проверка записана. Завершите обработку черновика.";
                }
                else if (_pendingOperation is { } operation)
                    _message = $"Исход попытки {operation.AttemptId} требует сверки. Повтор записи заблокирован.";

                var draft = restore.TypedScopes?.TryGetValue(DraftScope, out var json) == true
                    ? JsonSerializer.Deserialize<FactDraft>(json)
                    : null;
                var workplace = restore.TypedScopes?.TryGetValue(WorkplaceScope, out var workplaceJson) == true
                    ? JsonSerializer.Deserialize<WorkplaceDraft>(workplaceJson) ?? throw new JsonException()
                    : null;
                var intendedSite = workplace is not null ? workplace.SiteId : (draft?.Snapshot ?? sent?.Expected)?.Site.Id;
                if (intendedSite is > 0)
                    _site = await LocationFactData.ReadSiteAsync(Db, intendedSite.Value, Token);
                _siteChoice = _site?.Id;
                if (workplace is not null)
                {
                    _itOnly = workplace.ItOnly;
                    _scanner = workplace.Scanner;
                    if (_site is not null && workplace.ResponsibleId is > 0)
                        _responsibleId = (await Employees.GetByIdAsync(workplace.ResponsibleId, Token))?.Id;
                }
                if (draft is not null || sent is not null)
                {
                    _pending = draft?.Snapshot ?? sent!.Expected;
                    _notes = draft?.Notes ?? sent!.Notes;
                    _broken = draft?.Broken ?? sent!.Broken;
                    _scanOperation = draft?.Scanner == true;
                    _conflict = draft?.Conflict == true;
                    _dialogError = _message;
                    if (_confirmedCleanup is null)
                    {
                        ShowDialog();
                        _restoreRaw = restore.HasDraft;
                    }
                }
                else
                {
                    _focusLocation = _site is null;
                    _focusSearch = _site is not null;
                    if (restore.HasDraft)
                    {
                        var readiness = await _registration.GetRestoreReadinessAsync(Token);
                        _legacyRawPending = ScopeNeedsRestore(readiness, DraftScope);
                    }
                }

                if (workplace is null) await SaveWorkplaceAsync();
                _restoreWorkplaceRaw = restore.HasDraft;
                if (intendedSite is > 0 && _site is null && _message is null)
                    _message = "Сохранённое расположение закрыто или недоступно.";
                if (_site is not null) await ReadRowsAsync();
                if (_pending is not null && _site?.Id != _pending.Site.Id && _dialogError is null)
                    _dialogError = "Проверьте доступность сохранённого расположения перед подтверждением.";
                UpdateSearchOptions();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _blocked = true;
                _message = "Не удалось загрузить данные или восстановить черновик инвентаризации.";
            }
            finally
            {
                _ready = CurrentUser is not null;
                if (!_disposed) StateHasChanged();
            }
            return;
        }
        if (_showLegacyRaw)
        {
            if (_restoreLegacyRaw && !_busy)
            {
                _restoreLegacyRaw = false;
                await RestoreLegacyInputsAsync();
                if (!_disposed) StateHasChanged();
            }
            return;
        }
        if (_restoreWorkplaceRaw && !_busy && _registration is not null)
        {
            _restoreWorkplaceRaw = false;
            await RestoreWorkplaceInputsAsync();
            if (!_disposed) StateHasChanged();
        }
        if (_restoreRaw && _registration is not null)
        {
            _restoreRaw = false;
            try { await RestoreEditorAsync(); }
            catch (Exception e) when (e is JSException or InvalidOperationException) { BlockDraft("Не удалось полностью восстановить черновик."); StateHasChanged(); }
        }
        if (_focusLocation && _locationSelect is not null)
        {
            _focusLocation = false;
            await _locationSelect.FocusAsync();
        }
        if (_focusSearch && _equipmentSelect is not null && _pending is null && !_equipmentOptions.Disabled)
        {
            _focusSearch = false;
            await _equipmentSelect.FocusAsync();
        }
        if (_focusNotes && _pending is not null && !_askRelocation && !_busy)
        {
            _focusNotes = false;
            await JS.InvokeVoidAsync("clayLocationFact.focusNotes", Token, _dialogVersion);
        }
    }

    private async Task RestoreEditorAsync()
    {
        if (_registration is null) return;
        _restoreIncomplete = true;
        var applied = await _registration.ApplyRawWithResultAsync(DraftScope, Token);
        var readiness = await _registration.GetRestoreReadinessAsync(Token);
        _restoreIncomplete = applied.Pending.Count > 0 || ScopeNeedsRestore(readiness, DraftScope);
        if (!_disposed) StateHasChanged();
    }

    private static bool ScopeNeedsRestore(ClayDraftRestoreReadiness readiness, string scope) =>
        readiness.PendingScopes.Contains(scope, StringComparer.Ordinal) || readiness.FailedScopes.Contains(scope, StringComparer.Ordinal);

    private void ShowLegacyInputs()
    {
        if (_busy || !_legacyRawPending || _registration is null)
            return;

        _showLegacyRaw = _restoreLegacyRaw = true;
        _legacyRawIncomplete = false;
    }

    private void ReturnToWorkplace()
    {
        if (_busy)
            return;

        _showLegacyRaw = false;
        _restoreWorkplaceRaw = true;
    }

    private async Task RestoreLegacyInputsAsync()
    {
        if (!_showLegacyRaw || _busy || _disposed || _registration is not { } registration)
            return;

        _busy = _legacyRawIncomplete = true;
        try
        {
            var applied = await registration.ApplyRawWithResultAsync(DraftScope, Token);
            var readiness = await registration.GetRestoreReadinessAsync(Token);
            _legacyRawIncomplete = applied.Pending.Count != 0 || ScopeNeedsRestore(readiness, DraftScope);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException) { }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }

    private async Task RestoreWorkplaceInputsAsync()
    {
        if (_busy || _disposed || _registration is not { } registration) return;
        _busy = _workplaceRawIncomplete = true;
        try
        {
            var applied = await registration.ApplyRawWithResultAsync(WorkplaceScope, Token);
            var readiness = await registration.GetRestoreReadinessAsync(Token);
            _workplaceRawIncomplete = applied.Pending.Count > 0 || ScopeNeedsRestore(readiness, WorkplaceScope);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException) { }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }

    private Task PersistWorkplaceAsync() => _registration?.SaveTypedScopeAsync(WorkplaceScope,
        JsonSerializer.Serialize(new WorkplaceDraft(_site?.Id, _responsibleId, _itOnly, _scanner)), Token) ?? Task.CompletedTask;

    private async Task SaveWorkplaceAsync()
    {
        try { await PersistWorkplaceAsync(); }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            BlockStorage(null, "Не удалось сохранить настройки рабочего места. Повторите сохранение черновика.");
        }
    }

    private Task ChangeSiteChoiceAsync(int? id)
    {
        _siteChoice = id;
        return id is null || id == _site?.Id ? Task.CompletedTask : ChangeSiteAsync(id);
    }

    private async Task ChangeSiteAsync(int? id)
    {
        if (_busy || _pending is not null) return;
        _busy = true;
        _responsibleId = null;
        _site = null;
        _rows = [];
        try
        {
            if (id is > 0)
            {
                _site = await LocationFactData.ReadSiteAsync(Db, id.Value, Token);
                if (_site is null) { _message = "Расположение закрыто или недоступно."; return; }
            }
            else _siteChoice = null;

            await SaveWorkplaceAsync();
            await ReadRowsAsync();
            if (_site is not null && _workplaceRawIncomplete) _restoreWorkplaceRaw = true;
        }
        catch (Exception e) when (e is not OperationCanceledException) { _message = "Не удалось загрузить расположение или список оборудования."; }
        finally { UpdateSearchOptions(); _busy = false; }
    }

    private async Task ChangeItAsync(bool? value)
    {
        if (_busy || _pending is not null) return;
        _itOnly = value == true;
        UpdateSearchOptions();
        _focusSearch = true;
        await SaveWorkplaceAsync();
        await RefreshAsync();
    }

    private async Task ChangeResponsibleAsync(int? value)
    {
        if (_busy || _pending is not null) return;
        _responsibleId = value;
        _focusSearch = true;
        await SaveWorkplaceAsync();
        await RefreshAsync();
    }

    private async Task ChangeScannerAsync(bool? value)
    {
        if (_busy || _pending is not null) return;
        _scanner = value == true;
        _focusSearch = true;
        await SaveWorkplaceAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try { await ReadRowsAsync(); }
        catch (Exception e) when (e is not OperationCanceledException) { _rows = []; _message = "Не удалось обновить список оборудования."; }
        finally { _busy = false; }
    }

    private async Task ReadRowsAsync()
    {
        _rows = _site is null ? [] : await LocationFactData.ListAsync(Db, _site.Id, _itOnly, _responsibleId, Token);
        if (!_blocked && CanConfirm) _message = null;
    }

    private void UpdateSearchOptions()
    {
        _equipmentSource.ItOnly = _itOnly;
        _equipmentOptions.Disabled = _site is null || !CanConfirm;
        _equipmentId = null;
        _equipmentVersion++;
    }

    private Task ChooseEquipmentAsync(int? id) => id is > 0 ? StartConfirmationAsync(id.Value, _scanner) : Task.CompletedTask;

    private async Task StartConfirmationAsync(int id, bool scan)
    {
        if (_busy || !CanConfirm || _pending is not null || _site is not { } site) return;
        _busy = true;
        _equipmentId = id;
        _dialogError = null;
        _message = null;
        var immediate = false;
        try
        {
            await _registration!.OpenScopeAsync(DraftScope, Token);
            _pending = await LocationFactData.ReadForConfirmAsync(Db, site.Id, id, Token);
            _scanOperation = scan;
            _broken = !scan && _pending.Broken;
            _notes = scan ? "" : _pending.Notes;
            _askRelocation = scan && _pending.DifferentLocation;
            await PersistAsync();
            immediate = scan && !_askRelocation;
            if (!immediate) ShowDialog();
        }
        catch (LocationFactConflict error) { _message = error.Message; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_pending is null) _message = "Не удалось загрузить данные для подтверждения.";
            else { BlockStorage(null, "Не удалось сохранить черновик в браузере."); ShowDialog(); }
        }
        finally
        {
            _busy = false;
            if (_pending is null) ResetSearch();
        }
        if (immediate) await ConfirmAsync(false);
    }

    private Task ChangeNotesAsync(int version, string? value)
    {
        if (!CanEditPending || version != _dialogVersion) return Task.CompletedTask;
        _notes = value ?? "";
        return PersistEditAsync(version);
    }

    private Task ChangeBrokenAsync(int version, bool? value)
    {
        if (!CanEditPending || version != _dialogVersion) return Task.CompletedTask;
        _broken = value == true;
        return PersistEditAsync(version);
    }

    private async Task PersistEditAsync(int version)
    {
        if (!CanEditPending || version != _dialogVersion || _pending is not { } snapshot) return;
        try { await PersistAsync(); }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            if (version == _dialogVersion && ReferenceEquals(snapshot, _pending) && _dialogVisible && !_disposed)
                BlockStorage(null, "Не удалось сохранить черновик в браузере.");
        }
    }

    private Task PersistAsync() => _registration!.SaveTypedScopeAsync(DraftScope,
        JsonSerializer.Serialize(new FactDraft(_pending!, _notes, _broken, _scanOperation, _conflict)), Token);

    private Task ReviewAsync()
    {
        if (!CanSubmitPending || _pending is null) return Task.CompletedTask;
        if (LocationFactData.Validate(_pending.Equipment.FactName, _notes, _broken, false) is { } error)
        { _dialogError = error; return Task.CompletedTask; }
        if (!_pending.DifferentLocation) return ConfirmAsync(false);
        _askRelocation = true;
        _dialogError = null;
        return Task.CompletedTask;
    }

    private async Task ConfirmAsync(bool move)
    {
        if (!CanSubmitPending || _pending is not { } expected || _registration is not { } registration) return;
        var command = new FactCommand(expected, _notes, _broken, move);
        if (LocationFactData.Validate(expected.Equipment.FactName, command.Notes, command.Broken, command.Move) is { } validation)
        { _dialogError = validation; _askRelocation = false; return; }

        var scanner = _scanOperation;
        _busy = true;
        _dialogError = null;
        ClayDraftOperation? attempt = null;
        string? requestedAttemptId = null;
        try
        {
            await PersistAsync();
            await registration.EnsureCommandAllowedAsync(Token);
            var principal = await PrincipalAsync();
            Token.ThrowIfCancellationRequested();
            requestedAttemptId = Guid.NewGuid().ToString("N");
            attempt = await registration.BeginOperationAsync(
                new ClayDraftOperationIntent(OperationKind, JsonSerializer.Serialize(command), [DraftScope], requestedAttemptId), Token);
            _pendingOperation = attempt;
            var checkedAt = await LocationFactData.ConfirmAsync(Db, principal, command.Expected, command.Notes, command.Broken, command.Move, Token);
            _confirmedCleanup = new ConfirmedFact(attempt, command, checkedAt, scanner, ReceiptRecorded: false);
            await FinishConfirmedAsync(registration);
        }
        catch (Exception e)
        {
            if (_disposed) return;
            if (_confirmedCleanup is not null)
            {
                BlockDraft("Проверка записана. Завершите обработку черновика.");
                _dialogVisible = false;
                return;
            }

            if (attempt is null)
                BlockStorage(requestedAttemptId, "Команда не отправлена. Повторите сохранение черновика.");
            else
                await RecordFailureAsync(registration, attempt, e);

            if (!_dialogVisible) ShowDialog();
        }
        finally { _busy = false; }
    }

    private async Task RetryConfirmedCleanupAsync()
    {
        if (_busy || _disposed || _registration is not { } registration || _confirmedCleanup is null) return;
        _busy = true;
        try { await FinishConfirmedAsync(registration); }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }

    private async Task FinishConfirmedAsync(ClayDraftRegistration registration)
    {
        if (_confirmedCleanup is not { } receipt) return;
        try
        {
            if (!receipt.ReceiptRecorded)
            {
                await registration.RecordConfirmedOperationAsync(receipt.Operation.Revision, Token);
                _confirmedCleanup = receipt = receipt with { ReceiptRecorded = true };
            }
            await registration.CommitOperationAsync(receipt.Operation.Revision, cancellationToken: Token);
        }
        catch (Exception) when (!_disposed)
        {
            BlockDraft("Проверка записана. Завершите обработку черновика.");
            _dialogVisible = false;
            return;
        }

        _confirmedCleanup = null;
        _pendingOperation = null;
        _blocked = false;
        _message = null;
        ClosePending();

        var expected = receipt.Command.Expected;
        if (receipt.Scanner)
        {
            var place = expected.Site.IsWarehouse ? "складе" : "рабочем месте";
            Snackbar.Add($"Подтверждено нахождение оборудования {expected.Equipment.FactName} на {place}!", Severity.Success,
                options => options.VisibleStateDuration = 3000);
        }
        if (expected.Site.IsWarehouse) return;
        try { await ReadRowsAsync(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _message = "Проверка записана, список не обновлён. Обновите список оборудования.";
        }
    }

    private async Task RetryStorageAsync()
    {
        if (_busy || _disposed || !_storageRetryRequired || _registration is not { } registration) return;
        _busy = true;
        try
        {
            var remaining = await registration.RetryStorageAsync(_notSentAttemptId, Token);
            if (remaining is not null)
            {
                _pendingOperation = remaining;
                _storageRetryRequired = false;
                _notSentAttemptId = null;
                BlockDraft($"Исход попытки {remaining.AttemptId} требует сверки. Повтор записи заблокирован.");
                return;
            }

            await PersistWorkplaceAsync();
            if (_pending is not null) await PersistAsync();
            _storageRetryRequired = false;
            _notSentAttemptId = null;
            _blocked = false;
            _dialogError = _message = null;
            UpdateSearchOptions();
            if (_pending is not null && !_dialogVisible) ShowDialog();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception) when (!_disposed) { BlockDraft("Хранилище недоступно. Повторите сохранение черновика."); }
        finally { _busy = false; }
    }

    private void BlockStorage(string? notSentAttemptId, string message)
    {
        _storageRetryRequired = true;
        _notSentAttemptId = notSentAttemptId;
        BlockDraft(message);
    }

    private static FactCommand? ReadCommand(ClayDraftOperation? operation)
    {
        if (operation?.Kind != OperationKind) return null;
        try
        {
            return JsonSerializer.Deserialize<FactCommand>(operation.Data) is { Expected: not null, Notes: not null } sent
                ? sent : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task RecordFailureAsync(ClayDraftRegistration registration, ClayDraftOperation attempt, Exception error)
    {
        var rejected = error is LocationFactConflict or UnauthorizedAccessException ||
            error is SqlException sql && !DbManager.IsConnectivityError(sql) && sql.Number != -2;
        if (rejected)
        {
            try { await registration.RejectOperationAsync(attempt.Revision, Token); }
            catch (Exception e) when (e is JSException or InvalidOperationException or OperationCanceledException)
            {
                BlockStorage(null, "База отклонила команду. Повторите сохранение черновика для проверки маркера.");
                return;
            }

            _pendingOperation = null;
            _conflict = error is LocationFactConflict;
            if (_conflict)
            {
                try { await PersistAsync(); }
                catch (Exception e) when (e is JSException or InvalidOperationException or OperationCanceledException)
                {
                    BlockStorage(null, "Команда отклонена. Повторите сохранение черновика для продолжения сверки.");
                    return;
                }
            }
            _dialogError = error is SqlException ? "База отклонила подтверждение или перемещение." : error.Message;
            return;
        }

        try { await registration.UnknownOperationAsync(attempt.Revision, Token); }
        catch (Exception e) when (e is JSException or InvalidOperationException or OperationCanceledException) { }
        BlockDraft($"Исход попытки {attempt.AttemptId} требует сверки. Черновик сохранён, повтор записи заблокирован.");
    }

    private async Task CompareAsync()
    {
        var expected = (_blocked ? ReadCommand(_pendingOperation)?.Expected : null) ?? _pending;
        if (_busy || expected is null) return;
        _busy = true;
        try
        {
            _comparison = await LocationFactData.ReadForConfirmAsync(Db, expected.Site.Id, expected.Equipment.EquipmentId, Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _comparison = null;
            _dialogError = "Не удалось перечитать данные для сверки.";
        }
        finally { _busy = false; }
    }

    private async Task AcceptComparisonAsync()
    {
        if (_busy || _blocked || !_conflict || _comparison is null) return;
        _pending = _comparison;
        _comparison = null;
        _conflict = false;
        _askRelocation = false;
        _dialogError = null;
        await PersistEditAsync(_dialogVersion);
    }

    private Task OnDialogVisibilityAsync(bool visible) => visible || !_dialogVisible ? Task.CompletedTask : CancelAsync();

    private async Task CancelAsync()
    {
        if (_busy || !_dialogVisible) return;
        var registration = _registration;
        var actorId = CurrentUser?.Id;
        var version = _dialogVersion;
        var route = Navigation.Uri;
        var workplace = new WorkplaceDraft(_site?.Id, _responsibleId, _itOnly, _scanner);
        var pending = _pending;
        var scanner = _scanOperation;
        var notes = _notes;
        var broken = _broken;
        var token = Token;
        bool Current() => !_disposed && !token.IsCancellationRequested && _dialogVisible && version == _dialogVersion &&
            route == Navigation.Uri && actorId == CurrentUser?.Id && ReferenceEquals(_registration, registration) &&
            ReferenceEquals(_pending, pending) && notes == _notes && broken == _broken && scanner == _scanOperation &&
            workplace == new WorkplaceDraft(_site?.Id, _responsibleId, _itOnly, _scanner);

        async Task<bool> HasCurrentActorAsync()
        {
            var actor = await Employee.GetCurrentWithRolesAsync(Db, await PrincipalAsync(), token);
            return actor is not null && actor.Id == actorId && actor.HasRole(LocationFactData.RequiredRole);
        }

        _busy = true;
        try
        {
            var scope = registration is null || _blocked ? null : await registration.ReadEditorScopeAsync(DraftScope, token);
            if (!Current()) return;
            var dialog = await Dialogs.ShowExAsync<ClayEditorCancelDialog>("Отмена изменений",
                new DialogParameters(), new DialogOptionsEx
                {
                    DragMode = MudDialogDragMode.Simple, CloseOnEscapeKey = true, CloseButton = true,
                });
            if (await dialog.Result is not { Canceled: false, Data: ClayEditorCancelChoice choice } ||
                !Current() || choice == ClayEditorCancelChoice.Continue)
                return;

            if (_blocked || _pendingOperation is not null || _confirmedCleanup is not null ||
                scope is not { HasOperation: false } || registration is null)
            {
                if (choice == ClayEditorCancelChoice.CloseWithoutSaving) _dialogVisible = false;
                else _dialogError = "Сначала требуется сверка или восстановление. Ввод и журнал сохранены.";
                return;
            }

            var authorized = await HasCurrentActorAsync();
            if (!Current()) return;
            if (!authorized || pending is null || pending.Site.Id != workplace.SiteId)
            {
                _dialogError = "Не удалось подтвердить сотрудника или рабочее место. Ввод сохранён.";
                return;
            }

            if (choice == ClayEditorCancelChoice.CloseWithoutSaving)
            {
                if (await ReplaceFactScopeAsync(registration, scope.Revision, null, Current, token))
                    ClosePending();
                return;
            }

            var fresh = await LocationFactData.ReadForConfirmAsync(Db, pending.Site.Id, pending.Equipment.EquipmentId, token);
            if (!Current()) return;
            if (fresh.Site.Id != pending.Site.Id || fresh.Equipment.EquipmentId != pending.Equipment.EquipmentId)
            {
                _dialogError = "Контекст подтверждения изменился. Ввод сохранён.";
                return;
            }
            authorized = await HasCurrentActorAsync();
            if (!Current()) return;
            if (!authorized)
            {
                _dialogError = "Сотрудник или право подтверждения изменились. Ввод сохранён.";
                return;
            }
            var replacement = new FactDraft(fresh, scanner ? "" : fresh.Notes, !scanner && fresh.Broken, scanner);
            if (!await ReplaceFactScopeAsync(registration, scope.Revision, JsonSerializer.Serialize(replacement), Current, token))
                return;

            _pending = replacement.Snapshot;
            _notes = replacement.Notes;
            _broken = replacement.Broken;
            _comparison = null;
            _conflict = _restoreRaw = _restoreIncomplete = false;
            _askRelocation = scanner && fresh.DifferentLocation;
            _dialogError = null;
            ShowDialog();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is JSException or InvalidOperationException or SqlException or TimeoutException or UnauthorizedAccessException)
        {
            if (Current()) _dialogError = "Не удалось завершить отмену. Ввод сохранён.";
        }
        finally { _busy = false; }
    }

    private async Task<bool> ReplaceFactScopeAsync(ClayDraftRegistration registration, long revision, string? snapshot,
        Func<bool> current, CancellationToken token)
    {
        if (_blocked || _pendingOperation is not null || _confirmedCleanup is not null) return false;
        var replaced = await registration.ReplaceEditorScopeAsync(DraftScope, revision, snapshot, token);
        if (!current()) return false;
        if (!replaced) _dialogError = "Ввод изменился. Повторите отмену.";
        return replaced;
    }

    private void ShowDialog()
    {
        _dialogVersion++;
        _dialogVisible = true;
        _focusNotes = !_askRelocation && !_blocked;
    }

    private void ClosePending()
    {
        _dialogVersion++;
        _restoreIncomplete = false;
        _dialogVisible = false;
        _pending = null;
        _comparison = null;
        _conflict = false;
        _askRelocation = false;
        _dialogError = null;
        _focusNotes = false;
        ResetSearch();
    }
    private void ResetSearch() { UpdateSearchOptions(); _focusSearch = true; }

    private void BlockDraft(string message)
    {
        _blocked = true;
        _message = _dialogError = message;
        UpdateSearchOptions();
    }

    private async Task<string?> PrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private static string? GetRowClass(LocationFactRow row) => row.CheckedOn(DateTime.UtcNow) ? null : "mud-info-text";
    private static string EquipmentUrl(int id) => $"/Equipment/EquipmentForm?id={id}";
    private string? GetLocationUrl(int? id) => BuildUrl("@URI_location_form", id);
    private string? GetOwnerUrl(int? id) => BuildUrl("@URI_person_form", id);
    private string? BuildUrl(string reference, int? id) => id is > 0
        ? InventoryMenuUrlBuilder.BuildCardLink(reference, id.Value.ToString(CultureInfo.InvariantCulture), Config, Navigation.Uri).Href : null;

    private async Task OpenAsync(string url)
    {
        if (!await Windows.OpenAsync(url, "location-fact:" + url, Token)) _message = "Браузер заблокировал открытие карточки.";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_registration is not null) await _registration.DisposeAsync();
        _lifetime.Dispose();
    }

    private sealed record WorkplaceDraft(int? SiteId, int? ResponsibleId, bool ItOnly, bool Scanner);
    private sealed record FactCommand(LocationFactSnapshot Expected, string Notes, bool Broken, bool Move);
    private sealed record ConfirmedFact(ClayDraftOperation Operation, FactCommand Command, DateTime? CheckedAt, bool Scanner, bool ReceiptRecorded);
    private sealed record FactDraft(LocationFactSnapshot Snapshot, string Notes, bool Broken, bool Scanner, bool Conflict = false);
}
