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
    private const string DraftFormKey = "duty";
    private readonly Guid _draftInstance = Guid.NewGuid();
    private const string DateDraftKey = "duty.date";
    private const string TimeOffDraftKey = "duty.time-off";
    private const string PlannedDraftKey = "duty.planned";
    private const string ActualDraftKey = "duty.actual";
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
    private readonly ClayDatePickerOptions _dateOptions = new() { Label = "Дата дежурства:", Required = true, DateFormat = "dd.MM.yyyy", DraftKey = DateDraftKey };
    private readonly ClayDatePickerOptions _timeOffOptions = new() { Label = "Дата отгула:", Required = true, DateFormat = "dd.MM.yyyy", Editable = true, Clearable = true, DraftKey = TimeOffDraftKey };
    private readonly ClaySelectOptions _plannedOptions = new() { Label = "Дежурный инженер:", Required = true, MaxItems = 100, AutoSelectSingle = true, DraftKey = PlannedDraftKey };
    private readonly ClaySelectOptions _actualOptions = new() { Label = "Фактический инженер:", Required = true, MaxItems = 100, AutoSelectSingle = true, DraftKey = ActualDraftKey };
    private readonly ClayRadioGroupOptions _radioOptions = new();

    private static readonly ClayTextBoxOptions ReadOnlyDateInput = new() { Label = "Ввод даты дежурства", ReadOnly = true, DraftKey = DateDraftKey };
    private static readonly ClayTextBoxOptions ReadOnlyTimeOffInput = new() { Label = "Ввод даты отгула", ReadOnly = true, DraftKey = TimeOffDraftKey };
    private static readonly ClayTextBoxOptions ReadOnlyPlannedInput = new() { Label = "Поисковый текст планового инженера", ReadOnly = true, DraftKey = PlannedDraftKey };
    private static readonly ClayTextBoxOptions ReadOnlyActualInput = new() { Label = "Поисковый текст фактического инженера", ReadOnly = true, DraftKey = ActualDraftKey };

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
    private DateOnly? _routeDate;
    private string? _freshReadError;
    private string? _readOnlyComparison;
    private DateOnly? _stalePlanDay;
    private StalePlanReview? _stalePlanReview;
    private DutySnapshot? _recoverySnapshot;
    private bool _rawRecoveryPending;
    private bool _showReadOnlyRaw;
    private bool _restoreReadOnlyRaw;
    private bool _conflict;
    private bool _restoreRaw;
    private bool _retryRaw;
    private bool _applyingRaw;
    private bool _rawRestoreFailed;
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
    private bool HasWriteAccess => _freshReadError is null && (_original is null
        ? _routeDate is null && _context?.CanInsert == true
        : _original.Closed == 0 && _context?.CanUpdate == true);
    private string ReadOnlyRawRootClass => _formOptions.Class + "-raw-recovery";

    private bool CanEdit => !_closingDraft && !_loading && !_busy && !_conflict && !_completed && !_inputClosed &&
        _registration is not null && HasWriteAccess;
    private bool DutyInitialFocusReady => !_disposed && CanEdit && _draft is not null && _baseline is not null &&
        _focusTarget == FocusTarget.None && !_restoreRaw && !_retryRaw && !_applyingRaw && !_storageError &&
        !_calendarPending && (string.IsNullOrWhiteSpace(RouteId)
            ? _routeDate is null
            : DutyKey.TryParse(RouteId, out var routeDate) && _routeDate == routeDate);
    private bool CanSave => CanEdit && !_restoreRaw && !_retryRaw && !_applyingRaw && !_storageError && !_calendarFailed && !_calendarPending && _draft is not null;
    private bool ShowTimeOff => _workDay == false && _draft?.Confirmation is DutyConfirmation.Planned or DutyConfirmation.Other;
    private bool Dirty => !_completed && (_conflict || _draft != _baseline);
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
            await ApplyDutyRawAsync(pendingOnly: false);
            if (_retryRaw) await ApplyDutyRawAsync(pendingOnly: true);
            if (!_disposed) StateHasChanged();
        }
        if (_restoreReadOnlyRaw)
        {
            _restoreReadOnlyRaw = false;
            await RestoreReadOnlyRawAsync();
            if (!_disposed) StateHasChanged();
        }
        await MoveFocusAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading || _disposed || _registration is not null) return;
        _loading = true;
        _failedLoad = false;
        _inputClosed = false;
        _message = null;
        var ct = _lifetime.Token;
        try
        {
            _routeDate = null;
            _context = null;
            _original = null;
            _draft = _baseline = null;
            _freshReadError = null;
            _readOnlyComparison = null;
            _stalePlanDay = null;
            _stalePlanReview = null;
            _recoverySnapshot = null;
            _rawRecoveryPending = _showReadOnlyRaw = _restoreReadOnlyRaw = false;
            _restoreRaw = _retryRaw = _rawRestoreFailed = false;
            if (!string.IsNullOrWhiteSpace(RouteId))
            {
                if (!DutyKey.TryParse(RouteId, out var parsed))
                {
                    _message = "Некорректная дата дежурства в ссылке.";
                    return;
                }
                _routeDate = parsed;
            }

            var principal = await PrincipalAsync();
            CurrentUser = await Employee.GetCurrentAsync(Db, principal, ct);
            if (CurrentUser is null)
            {
                _message = "Не удалось подтвердить текущего сотрудника.";
                return;
            }

            if (_routeDate is { } existing)
                await LoadExistingAsync(existing, ct);
            else
            {
                _context = await DutyData.ReadContextAsync(Db, ct);
                _baseline = new DutyDraft(await DutyData.NextDayAsync(Db, _context.BusinessDay, ct), null);
            }

            _draft = _baseline;
            await RestoreDraftAsync(principal, CurrentUser.Id, ct);
            if (_disposed) return;

            if (_freshReadError is not null)
                _message = _freshReadError;
            else if (_conflict && HasWriteAccess)
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

    private async Task LoadExistingAsync(DateOnly date, CancellationToken ct)
    {
        try
        {
            _context = await DutyData.ReadContextAsync(Db, ct);
            _original = await DutyData.ReadAsync(Db, date, ct);
        }
        catch (SqlException e) when (e.Number == 229)
        {
            _failedLoad = true;
            _freshReadError = "У вас нет права прочитать актуальное дежурство.";
            return;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _failedLoad = true;
            _freshReadError = "Не удалось прочитать актуальное дежурство.";
            return;
        }

        if (_original is null)
        {
            _freshReadError = "Дежурство на указанную дату не найдено.";
            return;
        }

        _baseline = DutyDraft.FromRow(_original);
        if (_original.Closed == 1 && DutyClosedSummary.FromRow(_original).EmployerId is { } employer)
            _employerName = await ReadEmployerNameAsync(employer, ct);
    }

    private string DraftEntityKey => _routeDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "new";

    private Task<ClayDraftRegistration> TransferAndOpenWritableDutyDraftAsync(string? principal, int employeeId,
        string entityKey, DateOnly? routeDate, Func<bool> current, CancellationToken ct)
    {
        var employeeKey = employeeId.ToString(CultureInfo.InvariantCulture);
        return Drafts.RegisterRamHandleAsync(DraftFormKey, entityKey, employeeKey,
            1, "." + _formOptions.Class, _draftInstance, ct);
    }

    private async Task RestoreDraftAsync(string? principal, int employeeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var canWrite = HasWriteAccess;
        var entityKey = DraftEntityKey;
        var routeDate = _routeDate;
        var route = RouteId;
        bool Current() => !_disposed && !ct.IsCancellationRequested && _registration is null &&
            CurrentUser?.Id == employeeId && RouteId == route && _routeDate == routeDate && DraftEntityKey == entityKey;
        bool CurrentWriter() => Current() && HasWriteAccess;

        var registration = canWrite
            ? await TransferAndOpenWritableDutyDraftAsync(principal, employeeId, entityKey, routeDate, CurrentWriter, ct)
            : await Drafts.RegisterRamHandleAsync(DraftFormKey, entityKey, employeeId.ToString(CultureInfo.InvariantCulture),
                1, "." + ReadOnlyRawRootClass, _draftInstance, CancellationToken.None);
        var restore = registration.Restore;
        if (!Current() || canWrite && !HasWriteAccess)
        {
            await registration.DisposeAsync();
            return;
        }
        _registration = registration;
        if (!canWrite && !restore.HasDraft)
        {
            await CloseDutyDraftAsync(registration, ct);
            return;
        }
        if (restore.TypedScopes?.TryGetValue(Scope, out var json) == true)
        {
            var snapshot = ReadSnapshot(json);
            if (HasWriteAccess)
            {
                _conflict = !DutyRow.MatchesVersion(snapshot.Original, _original);
                _draft = snapshot.Draft;
                _restoreRaw = restore.HasDraft && !_conflict;
            }
            else
            {
                _recoverySnapshot = snapshot;
                _conflict = true;
            }
        }
        else if (restore.HasDraft)
            _conflict = true;
        else if (HasWriteAccess)
            await PersistAsync(ct);

        if (canWrite && _conflict) await VerifyDutyRawReadyAsync(registration, ct);

        if (!canWrite && restore.HasDraft)
            _rawRecoveryPending = _showReadOnlyRaw = _restoreReadOnlyRaw = true;
    }

    private DutySnapshot ReadSnapshot(string json)
    {
        var snapshot = JsonSerializer.Deserialize<DutySnapshot>(json);
        if (snapshot?.Draft is null || _routeDate is { } date && snapshot.Draft.Date != date)
            throw new JsonException("Недопустимый снимок дежурства.");
        return snapshot;
    }

    private bool CanCompareReadOnly => _registration is not null && !HasWriteAccess && _routeDate is not null;

    private string? ReadOnlyReason => _freshReadError ?? (_original?.Closed == 1
        ? "Дежурство закрыто на сервере."
        : !HasWriteAccess ? "Актуальная карточка доступна только для чтения." : null);

    private IEnumerable<(string Title, DutySnapshot Snapshot)> RecoverySnapshots()
    {
        if (_recoverySnapshot is { } local) yield return ("Локальный черновик", local);
    }

    private async Task RestoreReadOnlyRawAsync()
    {
        if (_registration is not { } registration)
            return;

        var ct = _lifetime.Token;
        try
        {
            var result = await registration.ApplyRawWithResultAsync(Scope, ct);
            var readiness = await registration.GetRestoreReadinessAsync(ct);
            if (!ct.IsCancellationRequested && ReferenceEquals(_registration, registration))
            {
                _rawRecoveryPending = result.Pending.Count != 0 || !DutyScopeReady(readiness);
                _showReadOnlyRaw = _rawRecoveryPending || result.Applied.Count != 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            // При сбое остаётся доступен typed-снимок и сохраняется сообщение о незавершённом вводе.
        }
    }

    private bool CanRetryRaw => HasWriteAccess && !_loading && !_busy && !_applyingRaw && !_conflict && _registration is not null;

    private static bool DutyScopeReady(ClayDraftRestoreReadiness readiness) =>
        !readiness.PendingScopes.Contains(Scope) && !readiness.FailedScopes.Contains(Scope);

    private bool IsCurrentDutyRaw(ClayDraftRegistration registration, int generation, string? route, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && ReferenceEquals(_registration, registration) &&
        _inputViewRevision == generation && string.Equals(RouteId, route, StringComparison.Ordinal);

    private async Task<bool> VerifyDutyRawReadyAsync(ClayDraftRegistration registration, CancellationToken token)
    {
        var generation = _inputViewRevision;
        var route = RouteId;
        try
        {
            var readiness = await registration.GetRestoreReadinessAsync(token);
            if (!IsCurrentDutyRaw(registration, generation, route, token)) return false;
            _retryRaw = !DutyScopeReady(readiness);
            _rawRestoreFailed = readiness.FailedScopes.Contains(Scope);
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (!IsCurrentDutyRaw(registration, generation, route, token)) return false;
            _retryRaw = _rawRestoreFailed = true;
        }
        return !_retryRaw;
    }

    private async Task ApplyDutyRawAsync(bool pendingOnly)
    {
        if (!CanRetryRaw || _registration is not { } registration) return;
        _retryRaw = _applyingRaw = true;
        _restoreRaw = false;
        var ct = _lifetime.Token;
        var generation = _inputViewRevision;
        var route = RouteId;
        try
        {
            var result = pendingOnly
                ? await registration.ApplyPendingRawAsync(Scope, ct)
                : await registration.ApplyRawWithResultAsync(Scope, ct);
            if (!IsCurrentDutyRaw(registration, generation, route, ct)) return;
            if (!await VerifyDutyRawReadyAsync(registration, ct)) return;
            if (IsCurrentDutyRaw(registration, generation, route, ct))
                _retryRaw = result.Pending.Count > 0;
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrentDutyRaw(registration, generation, route, ct))
                _retryRaw = _rawRestoreFailed = true;
        }
        finally { _applyingRaw = false; }
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
        if (!CanSave || _draft is not { Date: { } date } draft || _registration is not { } registration || CurrentUser is not { } openedBy) return;
        var fact = IsFact;
        var openedBusinessDay = _context?.BusinessDay;
        var validation = draft.Validate(fact, _workDay);
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
        var originalAtStart = _original;
        var created = originalAtStart is null;
        var revision = _revision;
        var route = RouteId;
        var snapshot = JsonSerializer.Serialize(new DutySnapshot(originalAtStart, draft));
        bool CurrentSubmission() => !_disposed && !ct.IsCancellationRequested && ReferenceEquals(_registration, registration) &&
            string.Equals(RouteId, route, StringComparison.Ordinal) && _revision == revision && _draft == draft;
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
            if (!await VerifyDutyRawReadyAsync(registration, ct) || !CurrentSubmission()) return;
            var principal = await PrincipalAsync();
            await registration.SaveTypedScopeAsync(Scope, snapshot, ct);
            if (!await VerifyDutyRawReadyAsync(registration, ct) || !CurrentSubmission()) return;

            var saved = await DutyData.SaveAsync(Db, principal, openedBy.Id, originalAtStart, draft, ct, expectedFact: fact);
            _completed = true;
            _original = saved;
            _draft = _baseline = DutyDraft.FromRow(saved);
            _saveCloseNeedsConfirmation = true;
            _message = "Дежурство сохранено.";
            await CompleteWindowAsync(created, saved, ct);
        }
        catch (DutyRejectedException e)
        {
            if (e.Kind is DutyRejectionKind.Stale or DutyRejectionKind.Closed or DutyRejectionKind.DateOccupied
                or DutyRejectionKind.CalendarChanged or DutyRejectionKind.AccessDenied)
            {
                _conflict = true;
                _recoverySnapshot = new DutySnapshot(_original, draft);
                _stalePlanDay = e.Kind == DutyRejectionKind.Stale && !fact && _original is { Closed: 0 } original &&
                    draft.PlannedId != original.PlannedId && draft with { PlannedId = original.PlannedId } == DutyDraft.FromRow(original)
                    ? openedBusinessDay : null;
                _message = $"{e.Message} Сверьте состояние записи. Черновик сохранён.";
            }
            else
                _message = e.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            _message = "Не удалось сохранить дежурство. Подробности — в сообщении базы данных.";
        }
        finally
        {
            _busy = false;
            UpdateOptions();
        }
    }

    private async Task CompareAsync()
    {
        if (_busy || (_recoverySnapshot?.Draft.Date ?? _draft?.Date ?? _routeDate) is not { } date) return;

        var readOnly = CanCompareReadOnly;
        var registration = _registration;
        var route = RouteId;
        var ct = _lifetime.Token;
        _busy = true;
        _readOnlyComparison = null;
        _stalePlanReview = null;
        try
        {
            var principal = await PrincipalAsync();
            var employee = await Employee.GetCurrentAsync(Db, principal, ct);
            if (!IsCurrentComparison()) return;
            if (employee is null || employee.Id != CurrentUser?.Id)
            {
                _message = "Не удалось подтвердить текущего сотрудника для сверки.";
                return;
            }

            var context = await DutyData.ReadContextAsync(Db, ct);
            if (!IsCurrentComparison()) return;
            var row = await DutyData.ReadAsync(Db, date, ct);
            if (!IsCurrentComparison()) return;
            var detail = row is null ? "Запись на эту дату не найдена."
                : $"Запись на эту дату есть. Плановый инженер: {EmployeeText(row.PlannedId, row.PlannedName)}. " +
                  $"Фактический инженер: {EmployeeText(row.ActualId, row.ActualName)}. " +
                  (row.Closed == 1 ? "Дежурство закрыто." : "Дежурство открыто.");
            detail += $" Серверный день: {context.BusinessDay.ToString("dd.MM.yyyy", Russian)}.";
            if (!IsCurrentComparison()) return;
            if (readOnly)
            {
                _readOnlyComparison = detail;
                _message = null;
            }
            else
            {
                _context = context;
                _message = detail;
                if (_stalePlanDay is not null && row is not null && registration is not null)
                {
                    var revision = CanKeepStalePlan(row, context)
                        ? await ReadTypedPlanRevisionAsync(registration, ct) : null;
                    if (IsCurrentComparison()) _stalePlanReview = new(row, revision);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrentComparison())
                _message = "Не удалось прочитать актуальную запись для сверки.";
        }
        finally { _busy = false; UpdateOptions(); }

        bool IsCurrentComparison() => !_disposed && !ct.IsCancellationRequested &&
            string.Equals(RouteId, route, StringComparison.Ordinal) && ReferenceEquals(registration, _registration);
    }

    private bool CanKeepStalePlan(DutyRow row, DutyContext context) =>
        _stalePlanDay == context.BusinessDay && context.CanUpdate && row.Closed == 0 &&
        _draft is { Date: { } date, PlannedId: > 0 } && date >= context.BusinessDay && DateOnly.FromDateTime(row.DutyDate) == date;

    private async Task<long?> ReadTypedPlanRevisionAsync(ClayDraftRegistration registration, CancellationToken ct)
    {
        try
        {
            if (!await VerifyDutyRawReadyAsync(registration, ct)) return null;
            var state = await registration.ReadRebaseStateAsync(Scope, ct);
            return state.RawKeys.Count == 0 ? state.Revision : null;
        }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task KeepStalePlanAsync()
    {
        if (_busy || !_conflict || _stalePlanReview is not { Revision: { } revision } review ||
            _draft is not { Date: { } date } local || _registration is not { } registration)
            return;

        var token = _lifetime.Token;
        var route = RouteId;
        _busy = true;
        _stalePlanReview = null;
        try
        {
            var actor = await Employee.GetCurrentAsync(Db, await PrincipalAsync(), token);
            if (!IsCurrent()) return;
            if (actor is null || actor.Id != CurrentUser?.Id)
            {
                _message = "Не удалось подтвердить текущего сотрудника. Черновик сохранён.";
                return;
            }

            var context = await DutyData.ReadContextAsync(Db, token);
            var row = await DutyData.ReadAsync(Db, date, token);
            if (!IsCurrent()) return;
            if (row is null || !DutyRow.MatchesVersion(row, review.Row) || !CanKeepStalePlan(row, context))
            {
                _message = "Данные или доступ изменились. Повторите сверку; черновик сохранён.";
                return;
            }

            var baseline = DutyDraft.FromRow(row);
            var rebased = baseline with { PlannedId = local.PlannedId };
            var snapshot = JsonSerializer.Serialize(new DutySnapshot(row, rebased));
            if (!await VerifyDutyRawReadyAsync(registration, token) || !IsCurrent()) return;
            if (!await registration.RebaseScopeAsync(Scope, revision, snapshot, [], token))
            {
                if (IsCurrent()) _message = "Не удалось закрепить свежую версию. Черновик остаётся на форме.";
                return;
            }
            if (!IsCurrent()) return;

            _original = row;
            _context = context;
            _baseline = baseline;
            _draft = rebased;
            _conflict = false;
            _recoverySnapshot = null;
            _stalePlanDay = null;
            _storageError = false;
            _message = "Свежая исходная версия принята. Ваш выбор инженера сохранён в черновике.";
            await RefreshCalendarAsync(++_revision, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent()) _message = "Не удалось принять свежую версию. Повторите сверку; черновик сохранён.";
        }
        finally { _busy = false; UpdateOptions(); }

        bool IsCurrent() => !_disposed && !token.IsCancellationRequested &&
            string.Equals(RouteId, route, StringComparison.Ordinal) && ReferenceEquals(registration, _registration);
    }

    private async Task CompleteWindowAsync(bool created, DutyRow saved, CancellationToken ct)
    {
        if (_registration is not { } registration) return;
        var scope = await registration.ReadEditorScopeAsync(Scope, ct);
        if (scope.HasRaw)
        {
            _message = "Дежурство сохранено. Более поздний ввод остался в открытой форме.";
            return;
        }
        _closingDraft = true;
        if (!await registration.CloseRamIfRevisionAsync(scope.Revision, ct))
        {
            _closingDraft = false;
            _message = "Дежурство сохранено. Более поздний ввод остался в открытой форме.";
            return;
        }
        FinishDutyDraftClose(registration);
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
        var draftTargets = HasWriteAccess;
        _dateOptions.DraftKey = draftTargets ? DateDraftKey : null;
        _plannedOptions.DraftKey = draftTargets ? PlannedDraftKey : null;
        _actualOptions.DraftKey = draftTargets ? ActualDraftKey : null;
        _timeOffOptions.DraftKey = draftTargets ? TimeOffDraftKey : null;
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

    private sealed record StalePlanReview(DutyRow Row, long? Revision);
    private sealed record DutySnapshot(DutyRow? Original, DutyDraft Draft);
    private enum FocusTarget { None, Planned, Actual, Save, TimeOff }
}
