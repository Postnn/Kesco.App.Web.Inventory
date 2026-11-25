using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Inventory;
using Clayzor.Lib.Web.DBSelect.Persons;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Duty;

/// <summary>Редактор дежурства для одного календарного ключа.</summary>
public partial class DutyEditor : IAsyncDisposable
{
    private const string Scope = "main";
    private const string OperationKind = "duty.save";
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly ClayToolbarButtonOptions SaveButton = new()
    {
        Text = "Сохранить", Icon = Icons.Material.Filled.Save, Color = Color.Primary, Variant = Variant.Filled,
    };
    private static readonly ClayRadioOption<DutyConfirmation?>[] ConfirmationOptions =
    [
        new(DutyConfirmation.Planned, "замечаний к дежурству нет"),
        new(DutyConfirmation.Other, "дежурил другой сотрудник"),
        new(DutyConfirmation.Nobody, "никто не дежурил"),
    ];

    /// <summary>Строковой ключ для текущего экземпляра редактора.</summary>
    [Parameter] public string? RouteId { get; set; }
    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private IConfiguration Config { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private IServiceProvider Services { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly ClayEntityFormOptions _formOptions = new() { Class = "duty-editor-" + Guid.NewGuid().ToString("N") };
    private readonly ClayDatePickerOptions _dateOptions = new() { Label = "Дата дежурства:", Required = true, DateFormat = "dd.MM.yyyy", DraftKey = "duty.date" };
    private readonly ClayDatePickerOptions _timeOffOptions = new() { Label = "Дата отгула:", Required = true, DateFormat = "dd.MM.yyyy", Editable = true, Clearable = true, DraftKey = "duty.time-off" };
    private readonly ClaySelectOptions _plannedOptions = new() { Label = "Дежурный инженер:", Required = true, MaxItems = 100, AutoSelectSingle = true, DraftKey = "duty.planned" };
    private readonly ClaySelectOptions _actualOptions = new() { Label = "Фактический инженер:", Required = true, MaxItems = 100, AutoSelectSingle = true, DraftKey = "duty.actual" };
    private readonly ClayRadioGroupOptions _radioOptions = new();

    private Employee? CurrentUser { get; set; }
    private DutyContext? _context;
    private DutyRow? _original;
    private DutyDraft? _baseline;
    private DutyDraft? _draft;
    private DutyEngineerDataSource? _engineers;
    private DateOnly? _engineerMonth;
    private ClayDraftRegistration? _registration;
    private ClaySelect<DutyEngineer, int?>? _actual;
    private ClaySelect<DutyEngineer, int?>? _planned;
    private MudForm? _form;
    private ElementReference _saveControl;
    private ElementReference _timeOffControl;
    private IJSObjectReference? _focusModule;
    private FocusTarget _focusTarget;
    private bool _loading;
    private bool _busy;
    private bool _disposed;
    private bool _unknown;
    private DateOnly? _attemptDate;
    private string? _notSentAttemptId;
    private long? _confirmedRevision;
    private bool _conflict;
    private bool _restoreRaw;
    private bool _retryRaw;
    private bool _storageError;
    private bool _calendarPending;
    private bool _calendarFailed;
    private bool _failedLoad;
    private bool _leaveApproved;
    private bool _completed;
    private bool? _workDay;
    private int _revision;
    private string? _message;
    private string? _employerName;

    private bool IsFact => _original is { Closed: 0 } row && _context is { } context &&
        DateOnly.FromDateTime(row.DutyDate) < context.BusinessDay;
    private bool HasWriteAccess => _original is null ? _context?.CanInsert == true : _original.Closed == 0 && _context?.CanUpdate == true;
    private bool CanEdit => !_loading && !_busy && !_unknown && !_conflict && !_completed &&
        _registration is not null && HasWriteAccess;
    private bool CanSave => CanEdit && !_restoreRaw && !_retryRaw && !_storageError && !_calendarFailed && !_calendarPending && _draft is not null;
    private bool ShowTimeOff => _workDay == false && _draft?.Confirmation is DutyConfirmation.Planned or DutyConfirmation.Other;
    private bool Dirty => !_completed && (_unknown || _conflict || _draft != _baseline);
    private string WorkDayLabel => _workDay is null ? "" : _workDay.Value ? "рабочий день" : "выходной день";

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadAsync();
            if (!_disposed) StateHasChanged();
            return;
        }
        if (_restoreRaw)
        {
            await RetryRawAsync();
            if (!_disposed) StateHasChanged();
        }
        await MoveFocusAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading || _disposed || _registration is not null) return;
        _loading = true;
        _failedLoad = false;
        _message = null;
        var ct = _lifetime.Token;
        try
        {
            DateOnly? date = null;
            if (!string.IsNullOrWhiteSpace(RouteId))
            {
                if (!DutyKey.TryParse(RouteId, out var parsed))
                {
                    _message = "Некорректная дата дежурства в ссылке.";
                    return;
                }
                date = parsed;
            }
            CurrentUser = await Employee.GetCurrentAsync(Db, await PrincipalAsync(), ct);
            if (CurrentUser is null)
            {
                _message = "Не удалось подтвердить текущего сотрудника.";
                return;
            }
            _context = await DutyData.ReadContextAsync(Db, ct);
            if (date is { } existing)
            {
                _original = await DutyData.ReadAsync(Db, existing, ct);
                if (_original is null)
                {
                    _message = "Дежурство на указанную дату не найдено.";
                    return;
                }
                _baseline = DutyDraft.FromRow(_original);
                if (_original is { Closed: 1, EmployerId: > 0 })
                    _employerName = await ReadEmployerNameAsync(_original.EmployerId.Value, ct);
            }
            else
                _baseline = new DutyDraft(await DutyData.NextDayAsync(Db, _context.BusinessDay, ct), null);

            _draft = _baseline;
            if (_original?.Closed != 1)
            {
                var entityKey = date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "new";
                var registration = await Drafts.RegisterHandleAsync("duty", entityKey,
                    CurrentUser.Id.ToString(CultureInfo.InvariantCulture), 1, "." + _formOptions.Class, ct);
                if (_disposed)
                {
                    await registration.DisposeAsync();
                    return;
                }
                _registration = registration;
                _unknown = registration.Restore.Operation is not null;
                if (registration.Restore.OperationSnapshot is { Kind: OperationKind } operation)
                    _attemptDate = JsonSerializer.Deserialize<DutySnapshot>(operation.Data)?.Draft.Date;
                if (registration.Restore.TypedScopes?.TryGetValue(Scope, out var json) == true)
                {
                    var snapshot = JsonSerializer.Deserialize<DutySnapshot>(json) ?? throw new JsonException();
                    if (snapshot.Draft is null || (date.HasValue && snapshot.Draft.Date != date))
                        throw new JsonException();
                    _conflict = snapshot.Original != _original;
                    _draft = snapshot.Draft;
                    _restoreRaw = registration.Restore.HasDraft && !_conflict && !_unknown && HasWriteAccess;
                }
                else if (!_unknown && registration.Restore.HasDraft)
                    _conflict = true;
                else if (!_unknown && HasWriteAccess)
                    await PersistAsync(ct);
            }

            if (_unknown)
                _message = "Исход предыдущего сохранения неизвестен. Доступна сверка без повторной записи.";
            else if (_conflict)
                _message = "Дежурство изменилось после создания черновика. Введённые данные сохранены.";
            else if (_original?.Closed != 1 && !HasWriteAccess)
                _message = "Редактирование недоступно для текущей учётной записи или конфигурации БД.";

            await RefreshCalendarAsync(++_revision, ct);
        }
        catch (SqlException e) when (e.Number == 229)
        {
            _failedLoad = true;
            _storageError = true;
            _message = "У Вас нет прав для работы с этой формой!";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            _failedLoad = true;
            _storageError = true;
            _message = "Не удалось загрузить дежурство или восстановить черновик.";
        }
        finally
        {
            _loading = false;
            UpdateOptions();
        }
    }

    private async Task RetryRawAsync()
    {
        if (_busy || _registration is not { } registration || _unknown || _conflict) return;
        _retryRaw = true;
        _restoreRaw = false;
        var ct = _lifetime.Token;
        try
        {
            var result = await registration.ApplyRawWithResultAsync(Scope, ct);
            _retryRaw = result.Pending.Count > 0 || !(await registration.GetRestoreReadinessAsync(ct)).Complete;
            _message = _retryRaw ? "Часть ввода ожидает восстановления." : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            _retryRaw = true;
            _message = "Не удалось восстановить часть ввода.";
        }
    }

    private Task SetDateAsync(DateTime? value) => _original is not null ? Task.CompletedTask
        : ChangeAsync(_draft! with { Date = value is { } day ? DateOnly.FromDateTime(day) : null }, calendar: true);
    private Task SetPlannedAsync(int? value) => IsFact ? Task.CompletedTask
        : ChangeAsync(_draft! with { PlannedId = value }, calendar: true, focus: FocusTarget.Save);
    private Task SetActualAsync(int? value) => ChangeAsync(_draft! with { ActualId = value }, calendar: true);
    private Task SetConfirmationAsync(DutyConfirmation? value) => ChangeAsync(_draft! with
    {
        Confirmation = value,
        ActualId = value == DutyConfirmation.Other && _draft.ActualId == _draft.PlannedId ? null : _draft.ActualId,
    }, calendar: true, focus: value == DutyConfirmation.Other ? FocusTarget.Actual : FocusTarget.None);
    private Task SetTimeOffAsync(DateTime? value) => ChangeAsync(_draft! with { TimeOff = value is { } day ? DateOnly.FromDateTime(day) : null });
    private Task SetWantsTimeOffAsync(bool? value) => ChangeAsync(_draft! with { WantsTimeOff = value == true },
        focus: value == true ? FocusTarget.TimeOff : FocusTarget.None);
    private Task SetCloseAsync(bool? value) => ChangeAsync(_draft! with { Close = value == true });

    private async Task ChangeAsync(DutyDraft value, bool calendar = false, FocusTarget focus = FocusTarget.None)
    {
        if (!CanEdit || _draft == value) return;
        _draft = value;
        var ct = _lifetime.Token;
        var version = ++_revision;
        _message = null;
        UpdateOptions();
        try
        {
            await PersistAsync(ct);
            if (version == _revision) _storageError = false;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            if (version == _revision)
            {
                _storageError = true;
                _message = "Черновик остаётся на форме. Браузер не подтвердил его сохранение.";
            }
        }
        if (calendar || _calendarPending) await RefreshCalendarAsync(version, ct);
        if (version == _revision) _focusTarget = focus;
    }

    private async Task RefreshCalendarAsync(int version, CancellationToken ct)
    {
        if (version != _revision || _draft is null || _context is null) return;
        var day = _draft.Date ?? _context.BusinessDay;
        if (_engineerMonth != new DateOnly(day.Year, day.Month, 1))
        {
            _engineerMonth = new DateOnly(day.Year, day.Month, 1);
            _engineers = new DutyEngineerDataSource(Db, day);
        }
        _calendarPending = true;
        _calendarFailed = false;
        try
        {
            bool? workDay = _original?.Closed == 1 && _original.WorkDay is { } saved ? saved == 1
                : _draft.EffectiveEngineer(IsFact) is { } id ? await DutyData.IsWorkDayAsync(Db, day, id, ct) : null;
            if (version == _revision) _workDay = workDay;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (version == _revision)
            {
                _calendarFailed = true;
                _workDay = null;
                _message = "Не удалось проверить производственный календарь сотрудника.";
            }
        }
        finally { if (version == _revision) _calendarPending = false; }
    }

    private async Task SaveAsync()
    {
        if (!CanSave || _draft is not { } draft || _registration is not { } registration) return;
        var validation = draft.Validate(IsFact, _workDay);
        if (validation is not null)
        {
            _message = validation;
            _focusTarget = draft.PlannedId is null ? FocusTarget.Planned :
                draft.Confirmation == DutyConfirmation.Other && draft.ActualId is null ? FocusTarget.Actual :
                ShowTimeOff && draft.WantsTimeOff ? FocusTarget.TimeOff : FocusTarget.None;
            return;
        }
        _busy = true;
        var ct = _lifetime.Token;
        ClayDraftOperation? attempt = null;
        string? preparingAttemptId = null;
        var created = _original is null;
        try
        {
            if (_form is not null)
            {
                await _form.Validate();
                if (!_form.IsValid)
                {
                    _message = "Проверьте обязательные поля и формат даты.";
                    return;
                }
            }
            if (!(await registration.GetRestoreReadinessAsync(ct)).Complete)
            {
                _retryRaw = true;
                _message = "Завершите восстановление ввода перед сохранением.";
                return;
            }
            var principal = await PrincipalAsync();
            await registration.EnsureCommandAllowedAsync(ct);
            await PersistAsync(ct);
            preparingAttemptId = Guid.NewGuid().ToString("N");
            _attemptDate = draft.Date;
            attempt = await registration.BeginOperationAsync(
                new ClayDraftOperationIntent(OperationKind, SnapshotJson(), [Scope], preparingAttemptId), ct);
            var saved = await DutyData.SaveAsync(Db, principal, CurrentUser!.Id, _original, draft, ct);
            _completed = true;
            _original = saved;
            _draft = _baseline = DutyDraft.FromRow(saved);
            _confirmedRevision = attempt.Revision;
            await registration.CommitOperationAsync(attempt.Revision, cancellationToken: ct);
            _confirmedRevision = null;
            _message = "Дежурство сохранено.";
            await CompleteWindowAsync(created, saved, ct);
        }
        catch (DutyRejectedException e)
        {
            if (attempt is not null)
                _unknown = !await TryMarkAsync(() => registration.RejectOperationAsync(attempt.Revision, ct));
            _message = e.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            _unknown = attempt is not null && !_completed;
            if (!_unknown && !_completed)
            {
                _notSentAttemptId = preparingAttemptId;
                _storageError = true;
            }
            if (_unknown && attempt is not null)
                await TryMarkAsync(() => registration.UnknownOperationAsync(attempt.Revision, ct));
            _message = _completed ? "Дежурство сохранено. Завершение работы с черновиком или окном требует проверки."
                : _unknown ? "Состояние попытки требует сверки. Повторная запись заблокирована."
                : "Подготовка к сохранению завершилась ошибкой. Запись не начата.";
        }
        finally
        {
            _busy = false;
            UpdateOptions();
        }
    }

    private async Task RetryStorageAsync()
    {
        if (_busy || _unknown || _conflict || _registration is not { } registration) return;
        _busy = true;
        var ct = _lifetime.Token;
        try
        {
            if (_confirmedRevision is { } revision && _original is { } saved)
            {
                await registration.CommitOperationAsync(revision, cancellationToken: ct);
                _confirmedRevision = null;
                _message = "Дежурство сохранено.";
                await CompleteWindowAsync(string.IsNullOrWhiteSpace(RouteId), saved, ct);
                return;
            }
            if (await registration.RetryStorageAsync(_notSentAttemptId, ct) is { } remaining)
            {
                _unknown = true;
                _attemptDate = remaining.Kind == OperationKind
                    ? JsonSerializer.Deserialize<DutySnapshot>(remaining.Data)?.Draft.Date : null;
                _message = "В хранилище есть незавершённая попытка. Повторная запись заблокирована.";
                return;
            }
            await PersistAsync(ct);
            _notSentAttemptId = null;
            _storageError = false;
            _message = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            _message = _completed ? "Дежурство записано. Хранилище пока недоступно."
                : "Хранилище черновика пока недоступно.";
        }
        finally { _busy = false; UpdateOptions(); }
    }

    private async Task CompareAsync()
    {
        if (_busy || (_unknown ? _attemptDate : _draft?.Date) is not { } date) return;
        _busy = true;
        var ct = _lifetime.Token;
        try
        {
            if (await Employee.GetCurrentAsync(Db, await PrincipalAsync(), ct) is not { } employee || employee.Id != CurrentUser?.Id)
            {
                _message = "Не удалось подтвердить текущего сотрудника для сверки.";
                return;
            }
            var row = await DutyData.ReadAsync(Db, date, ct);
            _message = row is null ? "Запись на эту дату не найдена."
                : $"Запись на эту дату есть. Плановый инженер: {EmployeeText(row.PlannedId, row.PlannedName)}. " +
                  $"Фактический инженер: {EmployeeText(row.ActualId, row.ActualName)}. " +
                  (row.Closed == 1 ? "Дежурство закрыто." : "Дежурство открыто.");
            if (_unknown) _message += " Для подтверждения исхода требуется проверка журнала.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { _message = "Не удалось прочитать актуальную запись для сверки."; }
        finally { _busy = false; }
    }

    private async Task DiscardConflictAsync()
    {
        if (_busy || !_conflict || _unknown || _registration is not { } registration) return;
        _busy = true;
        var ct = _lifetime.Token;
        try
        {
            var dialog = await Dialogs.ShowExAsync<ConfirmDialog>("Черновик дежурства",
                new DialogParameters<ConfirmDialog> { { x => x.Message, "Отбросить локальные изменения и загрузить актуальную карточку?" } },
                new DialogOptionsEx { DragMode = MudDialogDragMode.Simple });
            if (await dialog.Result is not { Canceled: false, Data: true }) return;
            await registration.DiscardAsync(ct);
            await registration.DisposeAsync();
            _registration = null;
            _conflict = false;
            _storageError = false;
            _draft = _baseline = null;
            await LoadAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { _message = "Не удалось завершить отмену черновика."; }
        finally { _busy = false; UpdateOptions(); }
    }

    private async Task CompleteWindowAsync(bool created, DutyRow saved, CancellationToken ct)
    {
        try { await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", ct); }
        catch (JSException) { _message = "Дежурство сохранено. Родительский список не обновился."; }
        try { await JS.InvokeVoidAsync("clayModelEquipmentForm.close", ct); }
        catch (JSException) { _message = "Дежурство сохранено. Карточка осталась открыта."; }
        if (created && !_disposed)
        {
            _leaveApproved = true;
            Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("id", saved.DutyDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)), replace: true);
        }
    }

    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_leaveApproved)
        {
            _leaveApproved = false;
            return;
        }
        if (_busy) { context.PreventNavigation(); return; }
        if (!Dirty) return;
        context.PreventNavigation();
        var parameters = new DialogParameters<ConfirmDialog> { { x => x.Message, "Покинуть карточку? Черновик останется в браузере." } };
        var dialog = await Dialogs.ShowExAsync<ConfirmDialog>("Закрытие карточки", parameters,
            new DialogOptionsEx { DragMode = MudDialogDragMode.Simple });
        if (await dialog.Result is not { Canceled: false } || context.CancellationToken.IsCancellationRequested) return;
        _leaveApproved = true;
        Navigation.NavigateTo(context.TargetLocation);
    }

    private async Task MoveFocusAsync()
    {
        var target = _focusTarget;
        _focusTarget = FocusTarget.None;
        if (_disposed || !CanEdit || target == FocusTarget.None) return;
        try
        {
            if (target == FocusTarget.Actual && _draft?.Confirmation == DutyConfirmation.Other && _actual is not null)
                await _actual.FocusAsync();
            else if (target == FocusTarget.Planned && !IsFact && _planned is not null)
                await _planned.FocusAsync();
            else if (target == FocusTarget.Save || target == FocusTarget.TimeOff && ShowTimeOff && _draft?.WantsTimeOff == true)
            {
                var ct = _lifetime.Token;
                _focusModule ??= await JS.InvokeAsync<IJSObjectReference>("import", ct, "./js/dutyForm.js");
                await _focusModule.InvokeVoidAsync("focusControl", ct,
                    target == FocusTarget.Save ? _saveControl : _timeOffControl, target == FocusTarget.Save ? "button" : "input");
            }
        }
        catch (JSException) { }
        catch (OperationCanceledException) when (_disposed) { }
    }

    private void UpdateOptions()
    {
        var editable = CanEdit;
        _dateOptions.ReadOnly = !editable || _draft?.Date is not null;
        _plannedOptions.ReadOnly = !editable || IsFact;
        _actualOptions.ReadOnly = !editable;
        _timeOffOptions.ReadOnly = !editable;
        _radioOptions.ReadOnly = !editable;
    }

    private Task PersistAsync(CancellationToken ct) => _registration!.SaveTypedScopeAsync(Scope, SnapshotJson(), ct);
    private string SnapshotJson() => JsonSerializer.Serialize(new DutySnapshot(_original, _draft!));
    private async Task<string?> PrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private static async Task<bool> TryMarkAsync(Func<Task> mark)
    {
        try { await mark(); return true; }
        catch (JSException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    private async Task<string?> ReadEmployerNameAsync(int id, CancellationToken ct)
    {
        try
        {
            return Services.GetService(typeof(PersonSelectDataSource)) is PersonSelectDataSource people
                ? (await people.GetByIdAsync(id, ct))?.Name : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    private static DateTime? AsPickerDate(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue);
    private static string EmployeeText(int? id, string? name) => name ?? (id is > 0 ? $"#{id}" : "—");
    private string? EmployeeUrl(int? id) => CardUrl("@URI_user_form", id);
    private string? PersonUrl(int id) => CardUrl("@URI_person_form", id);
    private string? CardUrl(string key, int? id) => id is > 0
        ? InventoryMenuUrlBuilder.BuildCardLink(key, id.Value.ToString(CultureInfo.InvariantCulture), Config, Navigation.Uri).Href
        : null;
    private Task OpenEmployeeAsync(int id) => EmployeeUrl(id) is { } url ? OpenAsync(url, $"duty-engineer-{id}") : Task.CompletedTask;

    private async Task OpenAsync(string url, string key)
    {
        try
        {
            if (!await Windows.OpenAsync(url, key, _lifetime.Token))
                _message = "Браузер не открыл отдельную карточку.";
        }
        catch (JSException) { _message = "Не удалось открыть отдельную карточку."; }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_registration is not null) await _registration.DisposeAsync();
        if (_focusModule is not null)
        {
            try { await _focusModule.DisposeAsync(); } catch (JSDisconnectedException) { }
        }
        _lifetime.Dispose();
    }

    private sealed record DutySnapshot(DutyRow? Original, DutyDraft Draft);
    private enum FocusTarget { None, Planned, Actual, Save, TimeOff }
}

