using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Locations;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
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
    private const string DraftForm = "location-fact";
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
    private readonly Guid _draftInstance = Guid.NewGuid();
    private string DraftRootClass => $"location-fact-draft-{_draftInstance:N}";
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
    private bool _restoreIncomplete;
    private bool _unboundEditorRaw;
    private bool _pageDraftClosed;
    private bool _ready;
    private bool _busy;
    private bool _blocked;
    private bool _storageRetryRequired;
    private bool _automaticStorageRecoveryAttempted;
    private bool _automaticCleanupAttempted;
    private ConfirmedFact? _confirmedCleanup;
    private bool _serverWriteChecked;
    private bool _disposed;
    private (int DialogVersion, int EquipmentId, int SiteId, int EmployeeId, ClayPresenceTarget Target)? _presence;

    private ClayPresenceTarget? GetPresenceTarget()
    {
        if (_disposed || !_dialogVisible || _scanOperation || !CanConfirm ||
            _pending is not { Equipment.EquipmentId: > 0, Site.Id: > 0 } pending ||
            _site?.Id != pending.Site.Id || CurrentUser is not { Id: > 0 } employee)
            return null;

        var generation = _dialogVersion;
        var equipmentId = pending.Equipment.EquipmentId;
        var siteId = pending.Site.Id;
        var employeeId = employee.Id;
        if (_presence is { } previous && previous.DialogVersion == generation && previous.EquipmentId == equipmentId &&
            previous.SiteId == siteId && previous.EmployeeId == employeeId)
            return previous.Target;

        var connectionString = Db.ConnectionString;
        var target = new ClayPresenceTarget(ClayPresenceKey.Create("inventory", "equipment", equipmentId),
            async (principal, token) =>
            {
                bool Current() => !_disposed && _dialogVisible && !_scanOperation && CanConfirm &&
                    _dialogVersion == generation && CurrentUser?.Id == employeeId &&
                    _pending?.Equipment.EquipmentId == equipmentId && _pending?.Site.Id == siteId && _site?.Id == siteId;
                if (!Current() || principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } login })
                    return null;

                try
                {
                    using var db = new DbManager(connectionString);
                    var expected = _pending!;
                    var actor = await LocationFactData.RequirePresenceEditorAsync(db, login, employeeId,
                        expected, token);
                    if (!Current() || !ReferenceEquals(_pending, expected))
                        return null;
                    return new(actor.Id, actor.Name, ClayPresenceActivity.Editing);
                }
                catch (Exception error) when (error is LocationFactConflict or UnauthorizedAccessException)
                {
                    return null;
                }
            });
        _presence = (generation, equipmentId, siteId, employeeId, target);
        return target;
    }

    private bool CanConfirm => CurrentUser?.HasRole(LocationFactData.RequiredRole) == true && _registration is not null && !_blocked && !_unboundEditorRaw;
    private bool CanSubmitPending => CanConfirm && _serverWriteChecked &&
        !_busy && !_conflict && !_restoreRaw && !_restoreIncomplete &&
        _pending is { } expected && _site?.Id == expected.Site.Id;
    private bool CanEditPending => _dialogVisible && _pending is not null && !_blocked && !_busy && !_askRelocation;
    private bool ProtectExternalExit => _registration is not null && !_pageDraftClosed;
    private CancellationToken Token => _lifetime.Token;

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IConfiguration Config { get; set; } = null!;
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;
    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
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
                var actorId = CurrentUser.Id;
                var userKey = actorId.ToString(CultureInfo.InvariantCulture);
                var token = Token;
                bool Current() => !_disposed && !token.IsCancellationRequested && _registration is null &&
                    CurrentUser is { } current && current.Id == actorId && current.HasRole(LocationFactData.RequiredRole);

                var opened = await Drafts.RegisterRamHandleAsync(DraftForm, WorkplaceScope, userKey,
                    1, "." + DraftRootClass, _draftInstance, token);
                if (!Current())
                {
                    await opened.DisposeAsync();
                    return;
                }
                _registration = opened;
                var restore = _registration.Restore;
                _blocked = false;
                FactCommand? sent = null;

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
                    if (!_blocked)
                        await CheckWriteAccessAsync(_pending);
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
                        _unboundEditorRaw = ScopeNeedsRestore(readiness, DraftScope);
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
        if (_disposed) return;
        if (!_busy && !_automaticCleanupAttempted && _confirmedCleanup is not null)
        {
            _automaticCleanupAttempted = true;
            await RetryConfirmedCleanupAsync();
            if (!_disposed) StateHasChanged();
        }
        if (!_busy && !_automaticStorageRecoveryAttempted && _storageRetryRequired)
        {
            _automaticStorageRecoveryAttempted = true;
            if (!_disposed) StateHasChanged();
        }
        if (_restoreWorkplaceRaw && !_busy && _registration is not null)
        {
            _restoreWorkplaceRaw = false;
            await RestoreWorkplaceInputsAsync();
            if (!_disposed) StateHasChanged();
        }
        if (_restoreRaw && !_busy && _registration is not null)
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
            BlockStorage("Не удалось сохранить настройки рабочего места. Подтверждение приостановлено.");
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
            await CheckWriteAccessAsync(_pending);
            _scanOperation = scan;
            _broken = !scan && _pending.Broken;
            _notes = scan ? "" : _pending.Notes;
            _askRelocation = scan && _pending.DifferentLocation;
            await PersistAsync();
            immediate = scan && !_askRelocation && _serverWriteChecked;
            if (!immediate) ShowDialog();
        }
        catch (LocationFactConflict error) { _message = error.Message; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_pending is null) _message = "Не удалось загрузить данные для подтверждения.";
            else { BlockStorage("Не удалось сохранить черновик в браузере."); ShowDialog(); }
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
                BlockStorage("Не удалось сохранить черновик в браузере.");
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
        if (!CanSubmitPending || CurrentUser is not { } actor || _pending is not { } expected ||
            _registration is not { } registration) return;
        var command = new FactCommand(expected, _notes, _broken, move);
        if (LocationFactData.Validate(expected.Equipment.FactName, command.Notes, command.Broken, command.Move) is { } validation)
        { _dialogError = validation; _askRelocation = false; return; }

        var scanner = _scanOperation;
        _busy = true;
        _dialogError = null;
        try
        {
            await PersistAsync();
            var principal = await PrincipalAsync();
            Token.ThrowIfCancellationRequested();
            var checkedAt = await LocationFactData.ConfirmAsync(Db, principal, actor.Id,
                command.Expected, command.Notes, command.Broken, command.Move, Token);
            _confirmedCleanup = new ConfirmedFact(command, checkedAt, scanner);
            await FinishConfirmedAsync(registration);
        }
        catch (Exception e)
        {
            if (_disposed) return;
            _dialogError = e.Message;
            _conflict = e is LocationFactConflict;
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
        _confirmedCleanup = null;
        _automaticCleanupAttempted = false;
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

    private void BlockStorage(string message)
    {
        _storageRetryRequired = true;
        BlockDraft(message);
    }

    private async Task CheckWriteAccessAsync(LocationFactSnapshot expected)
    {
        _serverWriteChecked = false;
        if (CurrentUser is not { } actor || _site?.Id != expected.Site.Id)
            return;

        bool Current() => !_disposed && !Token.IsCancellationRequested && CurrentUser?.Id == actor.Id &&
            _site?.Id == expected.Site.Id && ReferenceEquals(_pending, expected);
        try
        {
            await LocationFactData.RequirePresenceEditorAsync(Db, await PrincipalAsync(), actor.Id, expected, Token);
            if (!Current()) return;
            _serverWriteChecked = true;
        }
        catch (Exception error) when (error is SqlException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (Current()) _dialogError = "Не удалось подтвердить доступ к инвентаризации.";
        }
    }

    private async Task CompareAsync()
    {
        var expected = _pending;
        if (_busy || expected is null) return;
        _busy = true;
        try
        {
            _comparison = await LocationFactData.ReadForConfirmAsync(Db, expected.Site.Id, expected.Equipment.EquipmentId, Token);
            if (!_blocked && _pending is not null)
                await CheckWriteAccessAsync(_pending);
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

            if (_blocked || _confirmedCleanup is not null || scope is null || registration is null)
            {
                if (choice == ClayEditorCancelChoice.CloseWithoutSaving) _dialogVisible = false;
                else _dialogError = "Сначала требуется восстановление черновика. Ввод сохранён.";
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
            await CheckWriteAccessAsync(fresh);
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
        if (_blocked || _confirmedCleanup is not null) return false;
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
        _serverWriteChecked = false;
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

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_busy)
        {
            context.PreventNavigation();
            return;
        }
        if (_registration is not { } registration) return;
        using var exit = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, Token);
        var token = exit.Token;
        var actorId = CurrentUser?.Id;
        bool Current() => !_disposed && !token.IsCancellationRequested && ReferenceEquals(_registration, registration) &&
            CurrentUser?.Id == actorId;

        _busy = true;
        try
        {
            var editor = await registration.ReadEditorScopeAsync(DraftScope, token);
            var workplace = await registration.ReadEditorScopeAsync(WorkplaceScope, token);
            if (!Current() || editor.Revision != workplace.Revision)
            {
                context.PreventNavigation();
                if (Current()) _message = "Ввод изменился. Повторите переход после проверки.";
                return;
            }
            if ((_pending is not null || editor.HasRaw || workplace.HasRaw || _restoreIncomplete ||
                _workplaceRawIncomplete || _unboundEditorRaw) && !await ConfirmPageExitAsync(token))
            {
                context.PreventNavigation();
                return;
            }
            if (!Current() || !await registration.CloseRamIfRevisionAsync(workplace.Revision, token))
            {
                context.PreventNavigation();
                if (Current()) _message = "Ввод изменился. Повторите переход после проверки.";
                return;
            }
            if (ReferenceEquals(_registration, registration))
            {
                _registration = null;
                _pageDraftClosed = true;
                _confirmedCleanup = null;
                _storageRetryRequired = false;
                ClosePending();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { context.PreventNavigation(); }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            context.PreventNavigation();
            if (Current()) _message = "Не удалось удалить временные данные. Повторите переход.";
        }
        finally
        {
            _busy = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<bool> ConfirmPageExitAsync(CancellationToken token)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { dialog => dialog.Title, "Закрытие формы" },
            { dialog => dialog.Message, "Покинуть форму с несохранёнными изменениями?" },
            { dialog => dialog.ConfirmText, "Да" },
            { dialog => dialog.CancelText, "Нет" },
        };
        var dialog = await Dialogs.ShowExAsync<ConfirmDialog>("Закрытие формы", parameters,
            new DialogOptionsEx { CloseOnEscapeKey = true, CloseButton = true, DragMode = MudDialogDragMode.Simple });
        try { return await dialog.Result.WaitAsync(token) is { Canceled: false }; }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            dialog.Close(DialogResult.Cancel());
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync();
        var registration = _registration;
        _registration = null;
        if (registration is not null) await registration.DisposeAsync();
        _lifetime.Dispose();
    }

    private sealed record WorkplaceDraft(int? SiteId, int? ResponsibleId, bool ItOnly, bool Scanner);
    private sealed record FactCommand(LocationFactSnapshot Expected, string Notes, bool Broken, bool Move);
    private sealed record ConfirmedFact(FactCommand Command, DateTime? CheckedAt, bool Scanner);
    private sealed record FactDraft(LocationFactSnapshot Snapshot, string Notes, bool Broken, bool Scanner, bool Conflict = false);
}
