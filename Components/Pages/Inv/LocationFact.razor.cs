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

namespace Kesco.App.Web.Inventory.Components.Pages.Inv;

/// <summary>Инвентаризация на месте с единым сценарием подтверждения для списка и сканера.</summary>
public partial class LocationFact : IAsyncDisposable
{
    private const string DraftScope = "main";
    private static readonly ClaySelectOptions LocationOptions = new() { Label = "Расположение", Required = true };
    private static readonly ClaySelectOptions EmployeeOptions = new() { Label = "Поиск по ответственному" };
    private static readonly ClayTextAreaOptions NotesOptions = new() { Label = "Примечание", Lines = 3, MaxLength = 500, DraftKey = "fact.notes" };
    private static readonly DialogOptions DialogOptions = new() { CloseOnEscapeKey = true, BackdropClick = false, FullWidth = true, MaxWidth = MaxWidth.Small };
    private static readonly ClayGridOptions GridOptions = new()
    {
        Id = "location-fact-grid", ShowToolbar = false, ShowAddButton = false, ShowPagination = false, PageSize = int.MaxValue,
        AllowColumnReorder = false, AllowSorting = false, EnableValueFilter = false, ColumnMenuMode = ColumnMenuMode.Hidden,
    };
    private readonly ClaySelectOptions _equipmentOptions = new() { Label = "Поиск по SN, Инв:№, коду", Disabled = true, AutoSelectSingle = true };
    private readonly CancellationTokenSource _lifetime = new();
    private ClaySelect<LocationLookup, int?>? _locationSelect;
    private ClaySelect<LocationFactMatch, int?>? _equipmentSelect;
    private LocationFactEquipmentSource _equipmentSource = null!;
    private IReadOnlyList<LocationFactRow> _rows = [];
    private Employee? CurrentUser { get; set; }
    private LocationFactSite? _site;
    private LocationFactSnapshot? _pending;
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
    private bool _ready;
    private bool _busy;
    private bool _blocked;
    private bool _disposed;
    private bool CanConfirm => CurrentUser?.HasRole(LocationFactData.RequiredRole) == true && _registration is not null && !_blocked;
    private CancellationToken Token => _lifetime.Token;

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IConfiguration Config { get; set; } = null!;
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;
    [Inject] private ClayDraftService Drafts { get; set; } = null!;
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
                _ready = true;
                if (!CurrentUser.HasRole(LocationFactData.RequiredRole))
                {
                    _message = "Доступен просмотр учётного списка. Для подтверждения нужна роль специалиста по инвентаризации.";
                    _focusLocation = true;
                    return;
                }
                _registration = await Drafts.RegisterHandleAsync("location-fact", "workplace", CurrentUser.Id.ToString(CultureInfo.InvariantCulture),
                    1, ".location-fact, .location-fact-dialog", Token);
                var restore = _registration.Restore;
                _blocked = restore.Operation is not null;
                if (_blocked) _message = "Предыдущая операция не подтверждена. Сверьте фактическую запись. Повтор заблокирован.";
                if (restore.TypedScopes?.TryGetValue(DraftScope, out var json) == true && JsonSerializer.Deserialize<FactDraft>(json) is { } draft)
                {
                    _pending = draft.Snapshot;
                    _site = draft.Snapshot.Site;
                    _notes = draft.Notes;
                    _broken = draft.Broken;
                    _scanOperation = draft.Scanner;
                    _dialogError = _message;
                    ShowDialog();
                    _restoreRaw = restore.HasDraft;
                    await ReadRowsAsync();
                }
                else _focusLocation = true;
                UpdateSearchOptions();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _blocked = true;
                _message = "Не удалось загрузить данные или восстановить черновик инвентаризации.";
            }
            finally { if (!_disposed) StateHasChanged(); }
            return;
        }
        if (_restoreRaw && _registration is not null)
        {
            _restoreRaw = false;
            try { await _registration.ApplyRawAsync(Token); }
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
            await ReadRowsAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException) { _message = "Не удалось загрузить расположение или список оборудования."; }
        finally { UpdateSearchOptions(); _busy = false; }
    }

    private Task ChangeItAsync(bool? value)
    {
        if (_busy || _pending is not null) return Task.CompletedTask;
        _itOnly = value == true;
        UpdateSearchOptions();
        _focusSearch = true;
        return RefreshAsync();
    }

    private Task ChangeResponsibleAsync(int? value)
    {
        if (_busy || _pending is not null) return Task.CompletedTask;
        _responsibleId = value;
        _focusSearch = true;
        return RefreshAsync();
    }
    private void ChangeScanner(bool? value) { if (!_busy) { _scanner = value == true; _focusSearch = true; } }

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
            else { BlockDraft("Не удалось сохранить черновик в браузере."); ShowDialog(); }
        }
        finally
        {
            _busy = false;
            if (_pending is null) ResetSearch();
        }
        if (immediate) await ConfirmAsync(false);
    }

    private Task ChangeNotesAsync(string? value) { _notes = value ?? ""; return PersistEditAsync(); }
    private Task ChangeBrokenAsync(bool? value) { _broken = value == true; return PersistEditAsync(); }

    private async Task PersistEditAsync()
    {
        if (_busy || _pending is null || _blocked) return;
        try { await PersistAsync(); }
        catch (Exception e) when (e is JSException or InvalidOperationException) { BlockDraft("Не удалось сохранить черновик в браузере."); }
    }

    private Task PersistAsync() => _registration!.SaveTypedScopeAsync(DraftScope,
        JsonSerializer.Serialize(new FactDraft(_pending!, _notes, _broken, _scanOperation)), Token);

    private Task ReviewAsync()
    {
        if (_pending is null || _busy || _blocked) return Task.CompletedTask;
        if (LocationFactData.Validate(_pending.Equipment.FactName, _notes, _broken, false) is { } error)
        { _dialogError = error; return Task.CompletedTask; }
        if (!_pending.DifferentLocation) return ConfirmAsync(false);
        _askRelocation = true;
        _dialogError = null;
        return Task.CompletedTask;
    }

    private async Task ConfirmAsync(bool move)
    {
        if (_busy || !CanConfirm || _pending is not { } expected || _registration is not { } registration || _site?.Id != expected.Site.Id) return;
        if (LocationFactData.Validate(expected.Equipment.FactName, _notes, _broken, move) is { } validation)
        { _dialogError = validation; _askRelocation = false; return; }
        _busy = true;
        _dialogError = null;
        ClayDraftOperation? attempt = null;
        var confirmed = false;
        try
        {
            await PersistAsync();
            await registration.EnsureCommandAllowedAsync(Token);
            var intent = JsonSerializer.Serialize(new { expected.Site.Id, expected.Equipment.EquipmentId, Move = move });
            attempt = await registration.BeginOperationAsync(new ClayDraftOperationIntent("confirm-location", intent, [DraftScope]), Token);
            await LocationFactData.ConfirmAsync(Db, await PrincipalAsync(), expected, _notes, _broken, move, Token);
            confirmed = true;
            await registration.CommitOperationAsync(attempt.Revision, cancellationToken: Token);
        }
        catch (Exception e)
        {
            if (_disposed) return;
            if (confirmed) BlockDraft("Проверка записана, но черновик не удалось очистить. Повтор заблокирован.");
            else await RecordFailureAsync(registration, attempt, e);
        }
        finally { _busy = false; }
        if (_disposed) return;
        if (!confirmed) { if (!_dialogVisible) ShowDialog(); return; }

        var notify = _scanOperation;
        ClosePending();
        if (notify)
        {
            var place = expected.Site.IsWarehouse ? "складе" : "рабочем месте";
            Snackbar.Add($"Подтверждено нахождение оборудования {expected.Equipment.FactName} на {place}!", Severity.Success,
                options => options.VisibleStateDuration = 3000);
        }
        if (expected.Site.IsWarehouse) return;
        _busy = true;
        try { await ReadRowsAsync(); }
        catch (Exception e) when (e is not OperationCanceledException) { _message = "Проверка записана, список не обновлён. Обновите список оборудования."; }
        finally { _busy = false; }
    }

    private async Task RecordFailureAsync(ClayDraftRegistration registration, ClayDraftOperation? attempt, Exception error)
    {
        var rejected = error is LocationFactConflict or UnauthorizedAccessException ||
            error is SqlException sql && !DbManager.IsConnectivityError(sql) && sql.Number != -2;
        try
        {
            if (attempt is null) { BlockDraft("Команда не отправлена. Не удалось подготовить черновик."); return; }
            if (rejected)
            {
                await registration.RejectOperationAsync(attempt.Revision, Token);
                _dialogError = error is SqlException ? "База отклонила подтверждение или перемещение." : error.Message;
                return;
            }
            await registration.UnknownOperationAsync(attempt.Revision, Token);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or OperationCanceledException) { }
        BlockDraft("Результат записи не подтверждён. Черновик сохранён, повтор заблокирован до сверки.");
    }

    private Task OnDialogVisibilityAsync(bool visible) => visible || !_dialogVisible ? Task.CompletedTask : CancelAsync();

    private async Task CancelAsync()
    {
        if (_busy) return;
        _busy = true;
        try { if (!_blocked && _registration is not null) await _registration.DiscardAsync(Token); }
        catch (Exception e) when (e is JSException or InvalidOperationException) { BlockDraft("Не удалось удалить черновик в браузере."); }
        finally { ClosePending(); _busy = false; }
    }

    private void ShowDialog()
    {
        _dialogVersion++;
        _dialogVisible = true;
        _focusNotes = !_askRelocation && !_blocked;
    }

    private void ClosePending()
    {
        _dialogVisible = false;
        _pending = null;
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

    private sealed record FactDraft(LocationFactSnapshot Snapshot, string Notes, bool Broken, bool Scanner);
}
