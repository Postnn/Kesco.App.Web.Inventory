using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Phones;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon;

/// <summary>Создание, просмотр, редактирование, копирование и удаление телефонного номера.</summary>
public partial class Tel : IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "phone-form equipment-form", MaxWidth = "100%" };
    private readonly ClayTextBoxOptions _numberOptions = new() { DraftKey = "tel.number", Required = true, RequiredError = "Укажите номер", MaxLength = 4, Immediate = false };
    private readonly ClayTextBoxOptions _quickCallOptions = new() { DraftKey = "tel.quick-call", MaxLength = 50 };
    private readonly ClayTextBoxOptions _autoDialOptions = new() { DraftKey = "tel.auto-dial", MaxLength = 50 };
    private readonly ClayTextBoxOptions _parallelQuickOptions = new() { DraftKey = "tel.parallel-quick", MaxLength = 50, Placeholder = "Номер параллельного вызова" };
    private readonly ClayTextBoxOptions _connectedOptions = new() { DraftKey = "tel.connected", MaxLength = 100 };
    private readonly ClayTextBoxOptions _atsDescriptionOptions = new() { DraftKey = "tel.ats-description", MaxLength = 100 };
    private readonly ClayTextAreaOptions _notesOptions = new() { DraftKey = "tel.notes", MaxLength = 300, Lines = 3 };
    private readonly ClayDatePickerOptions _returnDateOptions = new() { DraftKey = "tel.return-date" };
    private readonly ClaySelectOptions _stationOptions = new() { DraftKey = "tel.station", Required = true, RequiredError = "Выберите станцию", AutoSelectSingle = true, MaxItems = 100, Placeholder = "Выберите станцию" };
    private readonly ClaySelectOptions _typeOptions = new() { DraftKey = "tel.type", Required = true, RequiredError = "Выберите тип", AutoSelectSingle = true, MaxItems = 20, Placeholder = "Выберите тип номера" };
    private readonly ClaySelectOptions _employeeOptions = new() { DraftKey = "tel.employee", AutoSelectSingle = true, MaxItems = 50, Placeholder = "Выберите сотрудника" };
    private readonly ClaySelectOptions _customerOptions = new() { DraftKey = "tel.customer", Required = true, RequiredError = "Выберите заказчика", AutoSelectSingle = true, MaxItems = 50, Placeholder = "Выберите заказчика" };
    private readonly ClaySelectOptions _drivenOptions = new() { DraftKey = "tel.driven", AutoSelectSingle = true, MaxItems = 100, Placeholder = "Выберите ведущий номер" };
    private readonly ClaySelectOptions _displayedOptions = new() { DraftKey = "tel.displayed", AutoSelectSingle = true, MaxItems = 100, Placeholder = "Выберите отображаемый номер" };
    private readonly ClaySelectOptions _socketOptions = new() { DraftKey = "tel.socket", AutoSelectSingle = true, MaxItems = 50, Placeholder = "Выберите розетку" };
    private readonly ClaySelectOptions _groupChoiceOptions = new() { DraftKey = "tel.group-choice", AutoSelectSingle = true, MaxItems = 50, Placeholder = "Добавить номер в группу" };
    private readonly ClaySelectOptions _equipmentChoiceOptions = new() { DraftKey = "tel.equipment-choice", AutoSelectSingle = true, AlwaysShowAdvancedSearch = true, MaxItems = 50, Placeholder = "Добавить оборудование" };
    private readonly ClayDropDownListOptions _portSelectOptions = new() { DraftKey = "tel.ats-port" };
    private readonly ClayDropDownListOptions _pickupSelectOptions = new() { DraftKey = "tel.pickup-group" };
    private readonly ClayRadioGroupOptions _servicesRadioOptions = new();
    private readonly ClayRadioGroupOptions _directoryRadioOptions = new();
    private readonly ClayGridOptions _parallelGridOptions = DetailGrid("phone-parallel-grid", "Нет параллельных вызовов");
    private readonly ClayGridOptions _groupGridOptions = DetailGrid("phone-group-grid", "Нет членов группы");
    private readonly ClayGridOptions _equipmentGridOptions = DetailGrid("phone-equipment-grid", "Нет оборудования");
    private readonly ClayToolbarButtonOptions _okOptions = new() { Text = "OK", Icon = Icons.Material.Filled.Check, Title = "Сохранить и закрыть", Color = Color.Primary, Variant = Variant.Filled };
    private readonly ClayToolbarButtonOptions _applyOptions = new() { Text = "Применить", Icon = Icons.Material.Filled.Save, Title = "Сохранить без закрытия" };
    private readonly ClayToolbarButtonOptions _copyOptions = new() { Text = "Копировать", Icon = Icons.Material.Filled.ContentCopy, Title = "Создать номер копированием" };
    private readonly ClayToolbarButtonOptions _deleteOptions = new() { Text = "Удалить", Icon = Icons.Material.Filled.Delete, Title = "Удалить номер и его связи" };
    private readonly ClayToolbarButtonOptions _refreshOptions = new() { Text = "Обновить", Icon = Icons.Material.Filled.Refresh, Title = "Загрузить запись повторно" };
    private readonly ClayToolbarButtonOptions _retryOptions = new() { Text = "Повторить", Icon = Icons.Material.Filled.Refresh, Title = "Повторить загрузку" };
    private readonly ClayToolbarButtonOptions _cancelOptions = new() { Text = "Отмена", Icon = Icons.Material.Filled.Close, Title = "Закрыть форму" };
    private readonly IReadOnlyList<ClayRadioOption<int?>> _serviceChoices =
    [
        new(0, "внутри холдинга"), new(1, "местная городская"), new(3, "внутри страны"),
        new(4, "международная"), new(5, "из номера гостиницы"),
    ];
    private readonly IReadOnlyList<ClayRadioOption<int?>> _directoryChoices =
    [
        new(0, "не отображать"), new(1, "отображать"), new(2, "отображать, выделить жирным"),
        new(4, "отображать в начале группы"), new(6, "отображать в начале группы, выделить жирным"), new(8, "отображать как общий"),
    ];

    private MudForm? _form;
    private Phone? _model;
    private Phone _originalPhone = new();
    private PhoneRelationsSnapshot _relations = new();
    private PhoneRelationsSnapshot _originalRelations = new();
    private IReadOnlyList<PhoneGroupMembership> _memberships = [];
    private PhoneTypeLookup? _selectedType;
    private PhoneStationLookup? _selectedStation;
    private PhoneStationLookup? _originalStation;
    private Employee? _selectedEmployee;
    private Employee? CurrentUser;
    private PhoneDirectoryItem? _selectedDrivenBy;
    private PhoneDirectoryItem? _selectedDisplayedFrom;
    private PhoneEquipmentKey? _equipmentChoice;
    private PhoneEquipmentLookup? _selectedEquipmentChoice;
    private int? _groupChoice;
    private PhoneDirectoryItem? _selectedGroupChoice;
    private PhoneCapabilities _capabilities = new();
    private PhoneFormAvailability _availability = PhoneFormRules.Evaluate(new PhoneFormRuleInput());
    private IReadOnlyList<PhoneStringOption> _portOptions = [];
    private IReadOnlyList<PhoneStringOption> _pickupOptions = [];
    private (int Id, string Number)? _defaultEmployeePhone;
    private IReadOnlyList<PhoneSyncOperation> _pendingOperations = [];
    private string _originalRelationsSnapshot = string.Empty;
    private string _lastAutoAtsDescription = string.Empty;
    private string _lastNumber = string.Empty;
    private int? _servicesUserChoice;
    private int? _directoryUserChoice;
    private int _employeeVersion;
    private int _customerVersion;
    private int _atsVersion;
    private int _atsEditVersion;
    private DateOnly _businessDay;
    private bool _hasReturnDate;
    private bool _canEdit;
    private bool _loading = true;
    private bool _saving;
    private bool _loaded;
    private bool _notFound;
    private bool _deleted;
    private bool _versionConflict;
    private bool _allowNavigation;
    private bool _disposed;
    private int _operationVersion;
    private int _groupSelectVersion;
    private int _equipmentSelectVersion;
    private int _socketSelectVersion;
    private (int Id, int CopyId) _loadedKey = (-1, -1);
    private string? _validationMessage;
    private string? _warningMessage;
    private string? _loadError;
    private string _parallelQuickNumber = string.Empty;

    /// <summary>Идентификатор редактируемого номера.</summary>
    [SupplyParameterFromQuery(Name = "id")] public int Id { get; set; }

    /// <summary>Идентификатор исходного номера для явного копирования простых полей.</summary>
    [SupplyParameterFromQuery(Name = "copyId")] public int CopyId { get; set; }

    private const string PendingSyncWarning = "Данные уже сохранены в БД, но синхронизация АТС не завершена. При закрытии повтор из этой формы будет недоступен; потребуется ручная синхронизация.";
    private string Title => _model?.Id > 0 ? $"Телефонный номер {_model.Number}" : CopyId > 0 ? "Копия телефонного номера" : "Новый телефонный номер";
    private bool HasPendingSync => _pendingOperations.Count > 0;
    private bool HasUnsavedDraft => _loaded && _model is not null &&
        (!PhoneSaveBuilder.MainEquals(_originalPhone, _model) || _originalRelationsSnapshot != SnapshotRelations(_relations));
    private bool IsModified => _loaded && _model is not null && (HasPendingSync || HasUnsavedDraft);
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving && !_deleted && !_versionConflict && !TelWriteBlocked;
    private string CloseConfirmation(string draftMessage)
    {
        if (_telMarker is not null && _telProgress?.SqlConfirmed != true)
            return $"{draftMessage} Результат предыдущей попытки требует сверки. Журнал сохранён.";
        if (!HasPendingSync) return draftMessage;
        return HasUnsavedDraft ? $"{draftMessage} {PendingSyncWarning}" : PendingSyncWarning;
    }

    private static string DeleteConfirmation(bool hasPendingSync)
    {
        const string question = "Удалить номер, состав его PBX-группы, связи с оборудованием и параллельные вызовы?";
        return hasPendingSync ? $"{question} {PendingSyncWarning}" : question;
    }
    private PhoneDirectorySelectDataSource GroupSource => Services.GetRequiredKeyedService<PhoneDirectorySelectDataSource>("phoneGroup");
    private PhoneDirectorySelectDataSource DrivenSource => Services.GetRequiredKeyedService<PhoneDirectorySelectDataSource>("drivenBy");
    private PhoneDirectorySelectDataSource DisplayedSource => Services.GetRequiredKeyedService<PhoneDirectorySelectDataSource>("displayedFrom");
    private string? ChangedByUrl => _model?.ChangedById > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? EquipmentIns.BuildEditUrl(Configuration["URI_user_form"]!, _model.ChangedById) : null;

    private static ClayGridOptions DetailGrid(string id, string emptyText) => new()
    {
        Id = id, Title = string.Empty, ShowToolbar = false, ShowPagination = false, AutoHeight = true, ShowHeader = false,
        AllowColumnReorder = false, AllowSorting = false, EmptyText = emptyText, Class = "equipment-detail-grid",
    };

    /// <inheritdoc />
    protected override void OnInitialized() => _entityFormOptions.Class += $" {_telJournalRoot}";

    /// <summary>Загружает запись после рендера и при смене query id без повторной инициализации.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var key = (Id, CopyId);
        if (_loadedKey != key)
        {
            if (_saving || _telJournalBusy)
                return;
            _loadedKey = key;
            await LoadCurrentAsync(key.Id, key.CopyId);
            return;
        }
        if (_telEditorTransition) return;
        if (_telRawRequested)
            await ApplyTelRawAsync();
        else
            await PersistTelInputAsync();
    }

    private async Task LoadCurrentAsync(int id, int copyId, bool preservePending = false)
    {
        var version = ++_operationVersion;
        bool IsLoadCurrent() => IsCurrent(version) && (Id, CopyId) == (id, copyId);
        _loading = true;
        _loaded = false;
        _notFound = false;
        CurrentUser = null;
        _loadError = null;
        _deleted = false;
        ErrorService.Clear();
        if (!preservePending)
        {
            _validationMessage = null;
            _warningMessage = null;
            _pendingOperations = [];
        }
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            if (!IsLoadCurrent()) return;
            var actor = await Service.GetCurrentUserAsync(await ReadPrincipalAsync(), _pageCts.Token);
            if (!IsLoadCurrent()) return;
            CurrentUser = actor;
            if (!_saving && !_telJournalBusy)
            {
                if (actor?.HasRole(Clayzor.Lib.Entities.Роли.Специалист_по_инвентаризации_оборудования) == true)
                    await AttachTelJournalAsync(actor, id, copyId, version);
                else
                    await ResetTelJournalAsync();
            }
            if (!IsLoadCurrent()) return;
            var data = id > 0 ? await Service.GetAsync(id, currentUser: actor)
                : copyId > 0 ? await Service.GetAsync(copyId, copy: true, currentUser: actor) : null;
            if (!IsLoadCurrent()) return;
            var publishingResult = preservePending && _telProgress is { Stage: TelWriteStage.Complete };
            var draft = publishingResult ? null : await ReadTelInputAsync(id, copyId, version);
            if (!IsLoadCurrent()) return;
            if ((id > 0 || copyId > 0) && data is null && draft is null)
            {
                _model = null;
                if (ErrorService.HasError) _loadError = "Не удалось загрузить телефонный номер. Проверьте соединение и повторите попытку.";
                else _notFound = true;
                return;
            }
            var canEdit = actor?.HasRole(Clayzor.Lib.Entities.Роли.Специалист_по_инвентаризации_оборудования) == true;
            var fresh = data?.Phone ?? new Phone();
            var freshRelations = data?.Relations ?? new PhoneRelationsSnapshot();
            if (draft is not null)
            {
                if ((id > 0 || copyId > 0) && data is null)
                {
                    _telInputConflict = true;
                    _telInputNotice = "Исходная запись недоступна. Черновик оставлен для просмотра и сверки.";
                }
                else
                    CompareTelDraftOrigin(draft, fresh, freshRelations);
            }
            var model = draft is null ? fresh : ClonePhone(draft.Current);
            var dependencies = await ReadEditorDependenciesAsync(model, draft is null ? model.StationId : draft.Original.StationId, IsLoadCurrent);
            if (dependencies is null || !IsLoadCurrent()) return;

            var lateRaw = publishingResult && await HasRemainingTelRawAsync();
            if (!IsLoadCurrent()) return;
            if (publishingResult && (lateRaw || _telSubmittedFingerprint is not null && HasLaterTelInput))
            {
                _loaded = true;
                _telJournalNotice = "Результат подтверждён. Поздний ввод оставлен в форме для сверки.";
                return;
            }
            if (!publishingResult && _telJournal is { } inputRegistration && (draft is not null || _telOriginalMissing))
                _appliedTelRestore = inputRegistration.Restore;
            if (draft is null && !_telOriginalMissing)
                _scheduledTelInput = null;
            if (publishingResult)
                _telSubmittedFingerprint = null;
            PublishEditor(actor, canEdit, id, model, freshRelations, data?.Memberships ?? [], draft, dependencies);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            if (!IsLoadCurrent()) return;
            _model = null;
            _loadError = "Не удалось загрузить телефонный номер. Проверьте соединение и повторите попытку.";
        }
        finally
        {
            if (IsLoadCurrent())
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task RetryLoadAsync()
    {
        if (!_loading) await LoadCurrentAsync(Id, CopyId, preservePending: true);
    }

    private bool IsCurrent(int version) => !_disposed && version == _operationVersion;

    private async Task OnNumberChanged(string? value)
    {
        if (_model is null) return;
        var number = value ?? string.Empty;
        if (_relations.ParallelCalls.Count > 0 && number != _lastNumber)
        {
            await ConfirmNumberChangeAsync(number);
            return;
        }
        _model.Number = number;
        _lastNumber = number;
        RecalculateFullNumber();
    }

    private async Task ConfirmNumberChangeAsync(string number)
    {
        if (!await ConfirmAsync("Изменение номера", "При изменении номера список параллельных вызовов будет очищен. Продолжить?"))
        {
            _model!.Number = _lastNumber;
            return;
        }
        _relations.ParallelCalls.Clear();
        _model!.Number = number;
        _lastNumber = number;
        RecalculateFullNumber();
        UpdateDependentState();
    }

    private void OnTypeIdChanged(int? id)
    {
        if (_model is not null) _model.PhoneTypeId = id;
    }

    private async Task OnTypeChangedAsync(PhoneTypeLookup? type)
    {
        if (_model is null || type?.Id == _selectedType?.Id) return;
        var previousType = _selectedType;
        var previousId = _selectedType?.Id;
        if (_relations.Equipment.Count > 0 &&
            !await ConfirmAsync("Изменение типа", "Оборудование другого типа будет удалено из черновика. Продолжить?"))
        {
            _selectedType = previousType;
            _model.PhoneTypeId = previousId;
            return;
        }
        _selectedType = type;
        _model.PhoneTypeId = type?.Id;
        _relations.Equipment.RemoveAll(item => item.PhoneTypeId != type?.Id);
        _model.ServicesKind = PhoneTypeChoiceRules.ResolveServices(type?.Id, _servicesUserChoice);
        _model.ViewInDirectory = PhoneTypeChoiceRules.ResolveDirectory(type?.Id, _directoryUserChoice, Id <= 0 && CopyId <= 0);
        _equipmentSelectVersion++;
        _socketSelectVersion++;
        await RefreshCapabilitiesAsync();
    }

    private void OnStationIdChanged(int? id)
    {
        if (_model is not null) _model.StationId = id;
    }

    private async Task OnStationChangedAsync(PhoneStationLookup? station)
    {
        if (_model is null || station?.Id == _selectedStation?.Id) return;
        var previousStation = _selectedStation;
        var previousId = _selectedStation?.Id;
        if (_relations.GroupMembers.Count > 0 &&
            !await ConfirmAsync("Изменение станции", "Список членов PBX-группы будет очищен. Продолжить?"))
        {
            _selectedStation = previousStation;
            _model.StationId = previousId;
            return;
        }
        _selectedStation = station;
        _model.StationId = station?.Id;
        _relations.GroupMembers.Clear();
        _equipmentSelectVersion++;
        _socketSelectVersion++;
        RecalculateFullNumber();
        await RefreshCapabilitiesAsync();
        await RefreshAutomaticAtsDescriptionAsync();
    }

    private async Task RefreshCapabilitiesAsync()
    {
        if (_model is null) return;
        var version = ++_operationVersion;
        var stationId = _model.StationId;
        var typeId = _model.PhoneTypeId;
        var capabilities = await Service.GetCapabilitiesAsync(stationId, typeId, _model.Id);
        if (!IsCurrent(version) || _model.StationId != stationId || _model.PhoneTypeId != typeId) return;
        _capabilities = capabilities;
        UpdateDependentState();
    }

    private void OnEmployeeIdChanged(int? id)
    {
        if (_model is not null) _model.EmployeeId = id;
    }

    private async Task OnEmployeeChangedAsync(Employee? employee)
    {
        if (_model is null) return;
        var version = ++_employeeVersion;
        var customerVersion = _customerVersion;
        _selectedEmployee = employee;
        _model.EmployeeId = employee?.Id;
        _equipmentSelectVersion++;
        _socketSelectVersion++;
        UpdateDependentState();
        if (employee is null)
        {
            ++_atsVersion;
            _defaultEmployeePhone = null;
            return;
        }
        var organizationId = await Service.GetEmployeeOrganizationAsync(employee.Id);
        if (!IsEmployeeCurrent(version, employee.Id)) return;
        if (organizationId is not null && customerVersion == _customerVersion) _model.CustomerId = organizationId;
        await RefreshAutomaticAtsDescriptionAsync(force: true);
        if (!IsEmployeeCurrent(version, employee.Id)) return;
        var defaultPhone = await Service.GetDefaultEmployeePhoneAsync(employee.Id);
        if (!IsEmployeeCurrent(version, employee.Id)) return;
        _defaultEmployeePhone = defaultPhone;
        UpdateDependentState();
    }

    private bool IsEmployeeCurrent(int version, int employeeId) =>
        !_disposed && version == _employeeVersion && _model?.EmployeeId == employeeId;

    private async Task RefreshAutomaticAtsDescriptionAsync(bool force = false)
    {
        if (_model?.EmployeeId is null) return;
        var version = ++_atsVersion;
        var editVersion = _atsEditVersion;
        var employeeId = _model.EmployeeId.Value;
        var stationId = _model.StationId;
        if (!force && !string.IsNullOrEmpty(_model.AtsDescription) && _model.AtsDescription != _lastAutoAtsDescription) return;
        var description = await Service.GetAtsDescriptionAsync(employeeId, _selectedStation?.AtsDescriptionCapital == true);
        if (_disposed || version != _atsVersion || editVersion != _atsEditVersion ||
            _model?.EmployeeId != employeeId || _model.StationId != stationId) return;
        _model.AtsDescription = description;
        _lastAutoAtsDescription = description;
    }

    private async Task FillAtsDescriptionAsync() => await RefreshAutomaticAtsDescriptionAsync(force: true);

    private void OnCustomerChanged(int? value)
    {
        if (_model is null) return;
        ++_customerVersion;
        _model.CustomerId = value;
        UpdateDependentState();
    }

    private void OnSocketChanged(int? value)
    {
        if (_model is null) return;
        _model.SocketId = value;
        _equipmentSelectVersion++;
        UpdateDependentState();
    }

    private void OnConnectedChanged(string? value)
    {
        if (_model is not null) _model.Connected = value ?? string.Empty;
    }

    private void OnAtsDescriptionChanged(string? value)
    {
        if (_model is null) return;
        ++_atsEditVersion;
        ++_atsVersion;
        _model.AtsDescription = value ?? string.Empty;
    }

    private void OnDrivenByIdChanged(int? id)
    {
        if (_model is not null) _model.DrivenById = id;
    }

    private void OnDrivenByChanged(PhoneDirectoryItem? item)
    {
        _selectedDrivenBy = item;
        if (_model is not null) _model.DrivenById = item?.Id;
        RecalculateFullNumber();
    }

    private void OnDisplayedFromIdChanged(int? id)
    {
        if (_model is not null) _model.DisplayedFromId = id;
    }

    private void OnDisplayedFromChanged(PhoneDirectoryItem? item)
    {
        _selectedDisplayedFrom = item;
        if (_model is not null) _model.DisplayedFromId = item?.Id;
        RecalculateFullNumber();
    }

    private void RecalculateFullNumber()
    {
        if (_model is null) return;
        _model.FullNumber = PhoneFormValidator.CalculateFullNumber(_model.Number,
            _selectedDisplayedFrom?.FullNumber, _selectedDrivenBy?.FullNumber, _selectedStation);
    }

    private void UpdateDependentState(DateOnly? businessDay = null)
    {
        if (_model is null) return;
        _availability = PhoneFormRules.Evaluate(new PhoneFormRuleInput
        {
            PhoneTypeId = _model.PhoneTypeId,
            PhoneStationId = _model.StationId,
            StationHasPorts = _capabilities.HasPorts,
            AutoDialAvailable = _capabilities.AutoDialAvailable,
            StationSupportsDrivenBy = _selectedStation?.SupportsDrivenBy == true,
            StationSupportsDisplayedFrom = _selectedStation?.SupportsDisplayedFrom == true,
            StationSupportsParallelCalls = _selectedStation?.SupportsParallelCalls == true,
            HasSocket = _model.SocketId is not null,
            HasPort = !string.IsNullOrEmpty(_model.AtsPort),
            HasPickupGroup = !string.IsNullOrEmpty(_model.PickupGroup),
            HasAutoDial = !string.IsNullOrEmpty(_model.AutoDialNumber),
            HasDrivenBy = _model.DrivenById is not null,
            HasDisplayedFrom = _model.DisplayedFromId is not null,
            HasParallelCalls = _relations.ParallelCalls.Count > 0,
            HasGroupMembers = _relations.GroupMembers.Count > 0,
            HasEquipment = _relations.Equipment.Count > 0,
            HasDefaultPhone = _model.IsDefault,
            HasEmployee = _model.EmployeeId is not null,
        });
        _portOptions = [new PhoneStringOption(string.Empty), .. _capabilities.AvailablePorts.Select(item => new PhoneStringOption(item))];
        _pickupOptions = [new PhoneStringOption(string.Empty), .. PhonePickupGroups.ForStation(_model.StationId, Configuration["pickupGroups"])
            .Select(item => new PhoneStringOption(item))];
        TypeSource.StationId = _model.StationId;
        foreach (var source in new[] { GroupSource, DrivenSource, DisplayedSource }) source.StationId = _model.StationId;
        GroupSource.ExcludedIds = _relations.GroupMembers.Select(item => item.PhoneId).Concat(_model.Id > 0 ? [_model.Id] : []).ToList();
        DrivenSource.ExcludedIds = _model.Id > 0 ? [_model.Id] : [];
        DisplayedSource.ExcludedIds = _model.Id > 0 ? [_model.Id] : [];
        EquipmentSource.PhoneTypeId = _model.PhoneTypeId;
        EquipmentSource.StationId = _model.StationId;
        EquipmentSource.SocketId = _model.SocketId;
        EquipmentSource.EmployeeId = _model.EmployeeId;
        EquipmentSource.PhoneId = 0;
        EquipmentSource.ExcludedKeys = _relations.Equipment.Select(item => item.Key).ToList();
        SocketSource.StationId = _model.StationId;
        SocketSource.SocketId = _model.SocketId;
        SocketSource.HasDraftEquipment = _relations.Equipment.Count > 0;
        SocketSource.EquipmentLocationIds = _relations.Equipment.Select(item => item.LocationId).Where(id => id > 0).ToList();
        UpdateOptions(businessDay);
    }

    private DateOnly ReadBusinessDay() => DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);

    private string? EquipmentReturnRowClass(PhoneEquipmentLink item) =>
        item.ReturnDate is { } date && DateOnly.FromDateTime(date) < _businessDay ? "equipment-overdue" : null;

    private void UpdateOptions(DateOnly? businessDay = null)
    {
        if (_model is null) return;
        foreach (var options in new ClayInputOptions[] { _numberOptions, _quickCallOptions, _connectedOptions, _atsDescriptionOptions, _notesOptions })
            options.ReadOnly = !_canEdit;
        _autoDialOptions.ReadOnly = !_canEdit || !_availability.AutoDial.IsEditable;
        _returnDateOptions.Disabled = !_canEdit || !_hasReturnDate;
        _businessDay = businessDay ?? ReadBusinessDay();
        _returnDateOptions.MinDate = _businessDay.ToDateTime(TimeOnly.MinValue);
        foreach (var options in new[] { _stationOptions, _typeOptions, _employeeOptions, _customerOptions }) options.ReadOnly = !_canEdit;
        _drivenOptions.ReadOnly = !_canEdit || !_availability.DrivenBy.IsEditable;
        _displayedOptions.ReadOnly = !_canEdit || !_availability.DisplayedFrom.IsEditable;
        _socketOptions.ReadOnly = !_canEdit || !_availability.Socket.IsEditable;
        _portSelectOptions.ReadOnly = !_canEdit || !_availability.Port.IsEditable;
        _pickupSelectOptions.ReadOnly = !_canEdit || !_availability.PickupGroup.IsEditable;
        _groupChoiceOptions.ReadOnly = !_canEdit || !_availability.GroupMembers.IsEditable;
        _equipmentChoiceOptions.ReadOnly = !_canEdit || !_availability.Equipment.IsEditable;
        _parallelGridOptions.ShowAddButton = _canEdit && _availability.ParallelCalls.IsEditable;
        _parallelQuickOptions.ReadOnly = !_canEdit || !_availability.ParallelCalls.IsEditable;
        _servicesRadioOptions.Disabled = !_canEdit || PhoneTypeChoiceRules.ServicesDisabled(_model.PhoneTypeId);
        _directoryRadioOptions.Disabled = !_canEdit;
    }

    private void OnServicesChanged(int? value)
    {
        if (_model is null || PhoneTypeChoiceRules.ServicesDisabled(_model.PhoneTypeId)) return;
        _servicesUserChoice = value;
        _model.ServicesKind = value;
    }

    private void OnDirectoryChanged(int? value)
    {
        if (_model is null) return;
        _directoryUserChoice = value;
        _model.ViewInDirectory = value;
    }

    private void OnDefaultChanged(bool? value)
    {
        if (_model is null) return;
        _model.IsDefault = value == true;
        var directory = PhoneTypeChoiceRules.ResolveDirectoryAfterDefault(_model.IsDefault, _model.ViewInDirectory);
        if (directory != _model.ViewInDirectory)
        {
            _model.ViewInDirectory = directory;
            _directoryUserChoice = directory;
        }
    }

    private void OnHasReturnDateChanged(bool? value)
    {
        if (_model is null) return;
        _hasReturnDate = value == true;
        var businessDay = ReadBusinessDay();
        _model.ReturnDate = _hasReturnDate ? _model.ReturnDate ?? businessDay.ToDateTime(TimeOnly.MinValue) : null;
        UpdateOptions(businessDay);
    }

    private void OnGroupChoiceChanged(int? value) => _groupChoice = value;

    private void OnGroupMemberSelected(PhoneDirectoryItem? item)
    {
        if (item is null) return;
        if (_relations.GroupMembers.Any(member => member.PhoneId == item.Id))
        {
            _warningMessage = "Этот номер уже добавлен в группу.";
            return;
        }
        _relations.GroupMembers.Add(new PhoneGroupMember { GroupPhoneId = _model?.Id ?? 0, PhoneId = item.Id, Name = item.Name });
        _selectedGroupChoice = null;
        _groupChoice = null;
        _groupSelectVersion++;
        UpdateDependentState();
    }

    private async Task RemoveGroupMemberAsync(PhoneGroupMember item)
    {
        if (!await ConfirmAsync("Удаление из группы", $"Удалить номер «{item.Name}» из группы?")) return;
        _relations.GroupMembers.Remove(item);
        UpdateDependentState();
    }

    private void OnEquipmentChoiceChanged(PhoneEquipmentKey? value) => _equipmentChoice = value;

    private void OnEquipmentSelected(PhoneEquipmentLookup? item)
    {
        if (item is null || _model is null) return;
        if (_relations.Equipment.Any(link => link.EquipmentId == item.EquipmentId))
        {
            _warningMessage = "Это оборудование уже добавлено.";
            return;
        }
        _relations.Equipment.Add(new PhoneEquipmentLink
        {
            EquipmentId = item.EquipmentId,
            ConnectionEquipmentId = item.ConnectionEquipmentId,
            Model = item.Model,
            SerialNumber = item.SerialNumber,
            LocationPath = item.LocationPath,
            LocationId = item.LocationId,
            PhoneTypeId = item.PhoneTypeId,
        });
        _selectedEquipmentChoice = null;
        _equipmentChoice = null;
        _equipmentSelectVersion++;
        UpdateDependentState();
    }

    private async Task RemoveEquipmentAsync(PhoneEquipmentLink item)
    {
        if (!await ConfirmAsync("Удаление оборудования", $"Удалить связь с оборудованием «{item.Model}»?")) return;
        _relations.Equipment.Remove(item);
        UpdateDependentState();
    }

    private async Task EditEquipmentReturnAsync(PhoneEquipmentLink item)
    {
        var original = new EquipmentPhoneReturnResult(item.ReturnDate, item.Notes);
        var key = item.Key;
        var changedAt = item.ChangedAtUtc;
        PhoneRelationsSnapshot? Propose(EquipmentPhoneReturnResult draft)
        {
            var index = _relations.Equipment.FindIndex(link => link.Key == key);
            if (index < 0 || _relations.Equipment[index].ChangedAtUtc != changedAt || new EquipmentPhoneReturnResult(_relations.Equipment[index].ReturnDate,
                _relations.Equipment[index].Notes) != original)
                return null;
            var relations = _relations.Clone();
            relations.Equipment[index].ReturnDate = draft.ReturnDate;
            relations.Equipment[index].Notes = draft.Notes;
            return relations;
        }
        var session = await OpenTelRelationDraftAsync(TelEquipmentScope(key), original, changedAt, Propose);
        if (session is null) return;
        try
        {
            var parameters = new DialogParameters<EquipmentPhoneDialog>
            {
                { dialog => dialog.ReturnDate, session.Initial.ReturnDate },
                { dialog => dialog.Notes, session.Initial.Notes },
                { dialog => dialog.TelDraft, session },
            };
            await ShowTelRelationDialogAsync<EquipmentPhoneDialog>("Срок возврата", parameters);
        }
        finally { _telChildOpen = false; }
    }

    private void OnParallelQuickKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs args)
    {
        if (args.Key == "Enter") AddParallelQuick();
    }

    private void AddParallelQuick()
    {
        if (_model is null || !_availability.ParallelCalls.IsEditable || !_canEdit) return;
        var call = new PhoneParallelCall { CallNumber = _parallelQuickNumber.Trim() };
        var errors = PhoneFormValidator.ValidateParallelCall(call, _model.Number,
            _relations.ParallelCalls.Select(item => item.CallNumber));
        if (errors.Count > 0)
        {
            _validationMessage = string.Join(" ", errors);
            return;
        }
        _relations.ParallelCalls.Add(call);
        _parallelQuickNumber = string.Empty;
        _validationMessage = null;
        UpdateDependentState();
    }

    private async Task OpenParallelDialogAsync(PhoneParallelCall? existing)
    {
        if (_model is not { } phone) return;
        var original = existing is null ? new PhoneParallelCall() : TelParallelValues(existing);
        var key = existing?.CallNumber;
        var changedAt = existing?.ChangedAtUtc ?? default;
        PhoneRelationsSnapshot? Propose(PhoneParallelCall draft)
        {
            var index = key is null ? -1 : _relations.ParallelCalls.FindIndex(call => call.CallNumber == key);
            if (key is not null && (index < 0 || _relations.ParallelCalls[index].ChangedAtUtc != changedAt ||
                !SameParallelValues(original, _relations.ParallelCalls[index])))
                return null;
            var others = _relations.ParallelCalls.Where((_, i) => i != index).Select(call => call.CallNumber);
            if (PhoneFormValidator.ValidateParallelCall(draft, phone.Number, others).Count > 0)
                return null;
            var relations = _relations.Clone();
            if (index < 0)
                relations.ParallelCalls.Add(draft.Clone());
            else
            {
                var target = relations.ParallelCalls[index];
                target.CallNumber = draft.CallNumber;
                target.DelayBeforeCall = draft.DelayBeforeCall;
                target.DelayTooQuickResponse = draft.DelayTooQuickResponse;
                target.NotUsed = draft.NotUsed;
            }
            return relations;
        }
        var session = await OpenTelRelationDraftAsync(TelParallelScope(existing), original, changedAt, Propose);
        if (session is null) return;
        try
        {
            var parameters = new DialogParameters<PhoneParallelCallDialog>
            {
                { dialog => dialog.Value, session.Initial.Clone() }, { dialog => dialog.IsEdit, existing is not null },
                { dialog => dialog.CurrentNumber, phone.Number },
                { dialog => dialog.OtherNumbers, _relations.ParallelCalls.Where(item => item.CallNumber != key).Select(item => item.CallNumber).ToList() },
                { dialog => dialog.TelDraft, session },
            };
            await ShowTelRelationDialogAsync<PhoneParallelCallDialog>("Параллельный вызов", parameters);
        }
        finally { _telChildOpen = false; }
    }

    private async Task RemoveParallelAsync(PhoneParallelCall item)
    {
        if (!await ConfirmAsync("Удаление вызова", $"Удалить параллельный вызов «{item.CallNumber}»?")) return;
        _relations.ParallelCalls.Remove(item);
        UpdateDependentState();
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        if (_model is null || CurrentUser is not { } actor || !_loaded || !_canEdit || _loading || _saving || _versionConflict || TelWriteBlocked || HasPendingSync) return;
        var businessDay = ReadBusinessDay();
        var version = ++_operationVersion;
        var model = _model;
        var dispatched = false;
        _saving = true;
        _validationMessage = null;
        _warningMessage = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            if (RejectUnacceptedParallel()) return;
            if (!IsModified || _deleted)
            {
                if (closeAfterSave) await CloseAsync();
                return;
            }
            _capabilities = await Service.GetCapabilitiesAsync(model.StationId, model.PhoneTypeId, model.Id);
            if (!IsCurrent(version) || model.StationId != _model.StationId || model.PhoneTypeId != _model.PhoneTypeId) return;
            UpdateDependentState(businessDay);
            var incompatible = IncompatibleFields();
            if (incompatible.Count > 0 && !await ConfirmAsync("Очистка несовместимых значений",
                    $"При сохранении будут очищены: {string.Join(", ", incompatible)}. Продолжить?")) return;
            if (_form is not null)
            {
                await _form.ValidateAsync();
                if (!IsCurrent(version) || !_form.IsValid) return;
            }
            var submitted = CurrentTelInputFingerprint();
            var request = BuildSaveRequest(normalizeIncompatible: true, businessDay);
            PhoneSaveBuilder.RequireOriginalVersion(request);
            var originalStation = ToSyncStation(_originalStation, _originalPhone.StationId);
            var currentStation = ToSyncStation(_selectedStation, model.StationId);
            var previewOperations = PhoneSyncPlanner.Build(request, originalStation, currentStation);
            var preSaveOperations = originalStation.Id != currentStation.Id
                ? previewOperations.Where(item => item.Kind == PhoneSyncKind.Xorcom && item.Deleting).ToArray() : [];
            if (preSaveOperations.Length > 0)
            {
                var preValidationIssues = PhoneFormValidator.Validate(request).ToList();
                preValidationIssues.AddRange(await Service.ValidateServerAsync(request));
                if (!IsCurrent(version)) return;
                if (preValidationIssues.Any(item => item.Severity == PhoneValidationSeverity.Blocking))
                {
                    ShowIssues(preValidationIssues);
                    return;
                }
            }

            if (RejectUnacceptedParallel()) return;
            _telSubmittedFingerprint = submitted;
            await BeginTelWriteAsync(actor, request.Phone.Id, false, request, preSaveOperations, version);
            if (!await RunJournaledAtsAsync(actor, beforeSql: true, version) || !IsCurrent(version))
                return;
            var result = await Service.SaveAsync(request, await ReadPrincipalAsync(), actor.Id,
                SqlBoundary(request, version, () => dispatched = true));
            if (!result.Saved)
            {
                await HandleSqlRejectionAsync(false);
                if (!IsCurrent(version)) return;
                ShowIssues(result.Issues);
                _versionConflict = result.VersionConflict;
                if (preSaveOperations.Length > 0)
                    _warningMessage = "SQL не записан. Предварительная синхронизация АТС уже выполнялась. Требуется сверка.";
                return;
            }

            _telProgress = _telProgress! with { SqlConfirmed = true, PhoneId = result.PhoneId };
            var operations = PhoneSyncPlanner.Build(request, originalStation, currentStation).Except(preSaveOperations).ToArray();
            await SaveTelProgressAsync(_telProgress! with
            {
                Stage = TelWriteStage.SqlConfirmed, SqlConfirmed = true, PhoneId = result.PhoneId, Pending = operations,
            });
            if (!IsCurrent(version) || !await RunJournaledAtsAsync(actor, beforeSql: false, version))
                return;
            await FinishTelJournalAsync();
            if (IsCurrent(version))
                await PublishCompletedTelWriteAsync(result.PhoneId, deleting: false, closeAfterSave);
        }
        catch (Exception ex) when (IsTelJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            await ReportTelWriteExceptionAsync(dispatched);
            if (IsCurrent(version) && _telProgress is null && _notSentAttemptId is null)
                _validationMessage = ex is Microsoft.Data.SqlClient.SqlException
                    ? "Не удалось подготовить запись телефонного номера." : ex.Message;
        }
        finally
        {
            _saving = false;
            if (_disposed) await ResetTelJournalAsync();
            else StateHasChanged();
        }
    }

    private async Task<string?> ReadPrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private async Task ReportTelWriteExceptionAsync(bool dispatched)
    {
        if (_telProgress is { Stage: TelWriteStage.PreAtsDispatching or TelWriteStage.PostAtsDispatching } progress)
            await MarkTelManualAsync(progress);
        else if (_telProgress is { SqlConfirmed: false })
            await HandleSqlRejectionAsync(dispatched);
        else
            UpdateTelJournalNotice();
    }

    private async Task PublishCompletedTelWriteAsync(int phoneId, bool deleting, bool closeAfterSave)
    {
        await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", _pageCts.Token);
        if (!deleting && (await HasRemainingTelRawAsync() || HasLaterTelInput))
        {
            _telJournalNotice = "Результат подтверждён. Поздний ввод оставлен в форме для сверки.";
            return;
        }
        if (deleting || closeAfterSave)
        {
            await CloseAsync();
            await ClearTelProgressAsync();
            return;
        }
        var source = (Id, CopyId);
        Id = phoneId;
        CopyId = 0;
        _loadedKey = (Id, 0);
        await LoadCurrentAsync(Id, 0, preservePending: true);
        if (!_loaded || _model?.Id != Id || await HasRemainingTelRawAsync() || HasLaterTelInput)
        {
            (Id, CopyId) = source;
            _loadedKey = source;
            return;
        }
        await JS.InvokeVoidAsync("history.replaceState", null, string.Empty, $"/Telefon/Tel?id={phoneId}");
        await ClearTelProgressAsync();
        if (CurrentUser is { } actor)
            await AttachTelJournalAsync(actor, Id, 0, _operationVersion);
    }

    private PhoneSaveRequest BuildSaveRequest(bool normalizeIncompatible, DateOnly businessDay)
    {
        var phone = ClonePhone(_model!);
        var relations = _relations.Clone();
        if (normalizeIncompatible)
        {
            if (!_availability.Socket.IsEditable) phone.SocketId = null;
            if (!_availability.Port.IsEditable) phone.AtsPort = string.Empty;
            if (!_availability.PickupGroup.IsEditable) phone.PickupGroup = string.Empty;
            if (!_availability.AutoDial.IsEditable) phone.AutoDialNumber = string.Empty;
            if (!_availability.DrivenBy.IsEditable) phone.DrivenById = null;
            if (!_availability.DisplayedFrom.IsEditable) phone.DisplayedFromId = null;
            if (!_availability.ParallelCalls.IsEditable) relations.ParallelCalls.Clear();
            if (!_availability.GroupMembers.IsEditable) relations.GroupMembers.Clear();
            if (!_availability.Equipment.IsEditable) relations.Equipment.Clear();
            if (!_availability.DefaultPhone.IsEditable) phone.IsDefault = false;
        }
        return new PhoneSaveRequest
        {
            OriginalPhone = ClonePhone(_originalPhone), Phone = phone,
            OriginalRelations = _originalRelations.Clone(), Relations = relations,
            AvailablePorts = _capabilities.AvailablePorts,
            AvailablePickupGroups = PhonePickupGroups.ForStation(phone.StationId, Configuration["pickupGroups"]),
            Today = businessDay.ToDateTime(TimeOnly.MinValue),
        };
    }

    private List<string> IncompatibleFields()
    {
        var result = new List<string>();
        if (_availability.Socket.HasIncompatibleValue) result.Add("розетка");
        if (_availability.Port.HasIncompatibleValue) result.Add("порт АТС");
        if (_availability.PickupGroup.HasIncompatibleValue) result.Add("группа перехвата");
        if (_availability.AutoDial.HasIncompatibleValue) result.Add("автонабор");
        if (_availability.DrivenBy.HasIncompatibleValue) result.Add("ведомый от");
        if (_availability.DisplayedFrom.HasIncompatibleValue) result.Add("отображаемый от");
        if (_availability.ParallelCalls.HasIncompatibleValue) result.Add("параллельные вызовы");
        if (_availability.GroupMembers.HasIncompatibleValue) result.Add("члены группы");
        if (_availability.Equipment.HasIncompatibleValue) result.Add("оборудование");
        if (_availability.DefaultPhone.HasIncompatibleValue) result.Add("основной номер");
        return result;
    }

    private void ShowIssues(IReadOnlyList<PhoneValidationIssue> issues)
    {
        _validationMessage = string.Join(" ", issues.Where(item => item.Severity == PhoneValidationSeverity.Blocking).Select(item => item.Message));
        _warningMessage = string.Join(" ", issues.Where(item => item.Severity != PhoneValidationSeverity.Blocking).Select(item => item.Message));
    }

    private async Task DeleteAsync()
    {
        if (_model?.Id is not > 0 || !_canEdit || _saving || _versionConflict || TelWriteBlocked || HasPendingSync ||
            !await ConfirmAsync("Удаление номера", DeleteConfirmation(HasPendingSync))) return;
        await DeleteConfirmedAsync();
    }

    private async Task DeleteConfirmedAsync()
    {
        if (_model?.Id is not > 0 || CurrentUser is not { } actor || _saving || _versionConflict ||
            TelWriteBlocked || HasPendingSync)
            return;
        var version = ++_operationVersion;
        var dispatched = false;
        _saving = true;
        _validationMessage = null;
        _warningMessage = null;
        try
        {
            var phoneId = _model.Id;
            var persisted = await Service.GetDeleteSnapshotAsync(phoneId);
            if (!IsCurrent(version))
                return;
            if (persisted is null)
            {
                _validationMessage = "Телефонный номер уже удалён другим пользователем. Синхронизация АТС не выполнялась.";
                return;
            }
            var request = new PhoneSaveRequest
            {
                OriginalPhone = persisted.Phone, Phone = persisted.Phone,
                OriginalRelations = persisted.Relations, Relations = persisted.Relations,
            };
            var originalStation = ToSyncStation(persisted.Station, persisted.Phone.StationId);
            var operations = PhoneSyncPlanner.BuildDelete(request, originalStation);
            var pre = operations.Where(item => item.Kind == PhoneSyncKind.Xorcom).ToArray();
            await BeginTelWriteAsync(actor, phoneId, true, request, pre, version);
            if (!await RunJournaledAtsAsync(actor, beforeSql: true, version) || !IsCurrent(version))
                return;
            await Service.DeleteAsync(phoneId, await ReadPrincipalAsync(), actor.Id, SqlBoundary(request, version, () => dispatched = true));
            _deleted = true;
            await SaveTelProgressAsync(_telProgress! with
            {
                Stage = TelWriteStage.SqlConfirmed, SqlConfirmed = true, Pending = operations.Except(pre).ToArray(),
            });
            if (!IsCurrent(version) || !await RunJournaledAtsAsync(actor, beforeSql: false, version))
                return;
            await FinishTelJournalAsync();
            if (IsCurrent(version))
                await PublishCompletedTelWriteAsync(phoneId, deleting: true, closeAfterSave: true);
        }
        catch (Exception ex) when (IsTelJournalError(ex) || ex is InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.SqlClient.SqlException)
        {
            await ReportTelWriteExceptionAsync(dispatched);
            if (IsCurrent(version) && _telProgress is null && _notSentAttemptId is null)
                _validationMessage = ex is Microsoft.Data.SqlClient.SqlException
                    ? "Не удалось подготовить удаление номера." : ex.Message;
        }
        finally
        {
            _saving = false;
            if (_disposed) await ResetTelJournalAsync();
        }
    }

    private async Task CopyAsync()
    {
        if (Id > 0) await JS.InvokeVoidAsync("clayOpen", $"/Telefon/Tel?copyId={Id}", "_blank");
    }

    private async Task CloseAsync()
    {
        _allowNavigation = true;
        await JS.InvokeVoidAsync("clayModelEquipmentForm.close");
    }

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_allowNavigation) { _allowNavigation = false; return; }
        if (_saving || _telEditorTransition || _telChildOpen) { context.PreventNavigation(); return; }
        if (!HasTelParentInput && !HasUnresolvedTelEditorWrite)
        {
            try { if (!await HasRemainingTelRawAsync()) return; }
            catch (Exception ex) when (IsEditorTransitionError(ex))
            {
                context.PreventNavigation();
                _telInputNotice = "Не удалось проверить черновик перед переходом.";
                return;
            }
        }
        context.PreventNavigation();
        if (!await ConfirmAsync("Переход на другую страницу", CloseConfirmation("Покинуть форму без сохранения изменений?"))) return;
        _allowNavigation = true;
        Navigation.NavigateTo(context.TargetLocation);
    }

    private async Task ShowTelRelationDialogAsync<T>(string title, DialogParameters<T> parameters) where T : ComponentBase
    {
        var dialog = await DialogService.ShowExAsync<T>(title, parameters,
            new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, BackdropClick = false });
        await dialog.Result;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var version = _operationVersion;
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { dialog => dialog.Title, title }, { dialog => dialog.Message, message },
            { dialog => dialog.ConfirmText, "Да" }, { dialog => dialog.CancelText, "Нет" },
        };
        var dialog = await DialogService.ShowAsync<ConfirmDialog>(title, parameters);
        var result = await dialog.Result;
        return result is not null && !result.Canceled && IsCurrent(version);
    }

    private async Task OpenPhoneViewAsync(PhoneDirectoryItem item) => await JS.InvokeVoidAsync("clayOpen", PhoneUrl(item.Id), "_blank");
    private async Task OpenEmployeeViewAsync(Employee item) => await JS.InvokeVoidAsync("clayOpen", EquipmentIns.BuildEditUrl(Configuration["URI_user_form"]!, item.Id), "_blank");
    private async Task OpenStationViewAsync(PhoneStationLookup item) => await JS.InvokeVoidAsync("clayOpen", EquipmentUrl(item.EquipmentId), "_blank");
    private Func<PhoneDirectoryItem, Task>? PhoneViewItem => string.IsNullOrWhiteSpace(Configuration["URI_tel_form"]) ? null : OpenPhoneViewAsync;
    private Func<Employee, Task>? EmployeeViewItem => string.IsNullOrWhiteSpace(Configuration["URI_user_form"]) ? null : OpenEmployeeViewAsync;
    private Func<PhoneStationLookup, Task>? StationViewItem => string.IsNullOrWhiteSpace(Configuration["URI_equipment_form"]) ? null : OpenStationViewAsync;
    private string PhoneUrl(int id) => EquipmentIns.BuildEditUrl(Configuration["URI_tel_form"] ?? "/Telefon/Tel", id);
    private string EquipmentUrl(int id) => EquipmentIns.BuildEditUrl(Configuration["URI_equipment_form"] ?? "/Equipment/EquipmentForm", id);
    private static PhoneSyncStation ToSyncStation(PhoneStationLookup? station, int? id) => new(id ?? 0, station?.XorcomName ?? string.Empty);

    private static Phone ClonePhone(Phone source)
    {
        var clone = source.CopyForNew();
        clone.Id = source.Id;
        clone.ChangedById = source.ChangedById;
        clone.ChangedByName = source.ChangedByName;
        clone.ChangedAtUtc = source.ChangedAtUtc;
        return clone;
    }

    private static string SnapshotRelations(PhoneRelationsSnapshot relations) => TelDraftRelations.From(relations).Canonical();

    /// <summary>Отменяет незавершённые операции формы.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pageCts.Cancel();
        _pageCts.Dispose();
    }

    private sealed record PhoneStringOption(string Value);

    /// <summary>Освобождает регистрацию после завершения либо отмены текущего вызова.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (!_saving)
            await ResetTelJournalAsync();
    }
}
