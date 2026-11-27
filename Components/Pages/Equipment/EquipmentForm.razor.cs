using System.Text.Json;
using System.Text.Json.Nodes;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Documents;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Редактирование основной записи оборудования.</summary>
public partial class EquipmentForm : IDisposable, IAsyncDisposable
{
    private const int OnHandsLocationId = (int)Расположения.На_руках;

    [Inject] private ILogger<EquipmentForm> Logger { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "equipment-form", MaxWidth = "100%" };
    private readonly ClaySelectOptions _typeOptions = new() { AdvanceFocusOnSelection = true, Required = true, AutoSelectSingle = true, MaxItems = 300, Placeholder = "Выберите тип оборудования" };
    private readonly ClaySelectOptions _modelOptions = new() { AdvanceFocusOnSelection = true, Required = true, AutoSelectSingle = true, Placeholder = "Выберите модель" };
    private readonly ClaySelectOptions _phonePoolOptions = new() { AdvanceFocusOnSelection = true, AutoSelectSingle = true, Placeholder = "Выберите телефонный пул" };
    private readonly ClaySelectOptions _phoneOptions = new() { AutoSelectSingle = true, AlwaysShowAdvancedSearch = true, Placeholder = "Выберите телефонный номер" };
    private readonly ClayGridOptions _phoneGridOptions = new() { EmptyText = "Нет телефонных номеров", ShowAddButton = false, ShowHeader = false };
    private int _hotelPhoneCount;
    private readonly ClayGridOptions _locationGridOptions = new() { EmptyText = "Нет расположений", AddTooltip = "Добавить расположение" };
    private List<EquipmentLocationPeriod> _locations = [];
    private readonly Dictionary<DateTime, DateTime> _localDates = new();
    private DateTime _clientToday = DateTime.Today;
    private DateTime? _simServicesDay;
    private Task? _businessDayWatch;
    private TimeProvider BusinessClock => Services.GetService<TimeProvider>() ?? TimeProvider.System;
    private readonly ClayGridOptions _responsibleGridOptions = new() { EmptyText = "Нет ответственных", AddTooltip = "Выдать оборудование" };
    private List<EquipmentResponsiblePeriod> _responsibles = [];
    private readonly ClayGridOptions _accountGridOptions = new() { EmptyText = "Нет учётных записей", AddTooltip = "Добавить учётную запись" };
    private List<EquipmentAccount> _accounts = [];
    private readonly Dictionary<string, string> _accountPasswords = new();
    private int _accountSecretVersion;
    private bool _accountsVisible;
    private readonly ClayGridOptions _computerGridOptions = new() { EmptyText = "Нет снимков конфигурации", AddTooltip = "Добавить или обновить текущий снимок" };
    private List<EquipmentComputerSnapshot> _computerHistory = [];
    private IReadOnlyList<StandardConfiguration> _standardConfigurations = [];
    private readonly ClayGridOptions _simGridOptions = new() { EmptyText = "Нет сведений SIM", AddTooltip = "Добавить сведения SIM" };
    private List<EquipmentSimCard> _simCards = [];
    private readonly ClayGridOptions _simServiceGridOptions = new() { EmptyText = "Нет дополнительных услуг", AddTooltip = "Добавить дополнительную услугу" };
    private List<EquipmentSimService> _simServices = [];
    private IReadOnlyList<SimServiceType> _simServiceTypes = [];
    private bool _showServiceHistory;
    private bool _showCompanyRequiredServices;
    private readonly ClayGridOptions _licenseGridOptions = new() { EmptyText = "Нет лицензии", AddTooltip = "Добавить лицензию" };
    private List<EquipmentLicense> _licenses = [];
    private IReadOnlyList<LicenseType> _licenseTypes = [];
    private readonly ClayGridOptions _softwareGridOptions = new() { EmptyText = "Нет установленного ПО", AddTooltip = "Добавить установленное ПО" };
    private List<EquipmentSoftwareInstallation> _softwareInstallations = [];
    private readonly ClayGridOptions _accountingGridOptions = new() { EmptyText = "Нет записей бухгалтерского учёта", AddTooltip = "Добавить запись бухгалтерского учёта" };
    private List<EquipmentAccounting> _accounting = [];
    private bool _canUseAccounting;
    private EquipmentRouterInfo? _routerInfo;
    private readonly ClayGridOptions _routerGridOptions = new() { EmptyText = "Нет данных маршрутизатора", ShowAddButton = false };
    private List<EquipmentRouterInventoryItem> _routerInventory = [];
    private List<UpsServerLink> _upsLinks = [];
    private EquipmentActualInventory? _lastInventory;
    private readonly ClaySelectOptions _ownerOptions = new() { AdvanceFocusOnSelection = true, Required = true, AutoSelectSingle = true, Placeholder = "Выберите владельца" };
    private readonly ClaySelectOptions _receiptDocumentOptions = new() { AdvanceFocusOnSelection = true, Placeholder = "Выберите документ поступления" };
    private readonly ClaySelectOptions _writeOffDocumentOptions = new() { AdvanceFocusOnSelection = true, Placeholder = "Выберите документ списания" };
    private readonly ClaySelectOptions _shipperOptions = new() { AdvanceFocusOnSelection = true, AutoSelectSingle = true, Placeholder = "Выберите поставщика" };
    private readonly ClaySelectOptions _tenantOptions = new() { AdvanceFocusOnSelection = true, AutoSelectSingle = true, Placeholder = "Выберите арендатора" };
    private readonly ClayTextBoxOptions _snOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _networkNameOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _macOptions = new() { MaxLength = 17 };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { MaxLength = 300, Lines = 2 };
    private readonly ClayDatePickerOptions _writeOffDateOptions = new();
    private readonly ClayTimeOptions _writeOffTimeOptions = new();
    private readonly ClayToolbarButtonOptions _okOptions = new() { Text = "OK", Icon = Icons.Material.Filled.Check, Title = "Сохранить и закрыть", Color = Color.Primary, Variant = Variant.Filled };
    private readonly ClayToolbarButtonOptions _applyOptions = new() { Text = "Применить", Icon = Icons.Material.Filled.Save, Title = "Сохранить без закрытия" };
    private readonly ClayToolbarButtonOptions _refreshOptions = new() { Text = "Обновить", Icon = Icons.Material.Filled.Refresh, Title = "Загрузить запись повторно" };
    private readonly ClayToolbarButtonOptions _cancelOptions = new() { Text = "Отмена", Icon = Icons.Material.Filled.Close, Title = "Закрыть форму" };
    private readonly ClayToolbarButtonOptions _copyOptions = new() { Text = "Копировать", Icon = Icons.Material.Filled.ContentCopy, Title = "Создать оборудование копированием" };
    private readonly ClayToolbarButtonOptions _operationsOptions = new() { Text = "Операции", Icon = Icons.Material.Filled.ArrowDropDown, Title = "Операции с оборудованием" };
    private readonly ClayToolbarButtonOptions _phoneConfigOptions = new() { Text = "Конфигурация", Icon = Icons.Material.Filled.SettingsPhone, Title = "Открыть конфигурацию IP-телефона" };
    private readonly ClayToolbarButtonOptions _simDetailOptions = new() { Text = "Детализация", Icon = Icons.Material.Filled.ReceiptLong, Title = "Показать детализацию мобильной связи за текущий период" };
    private readonly ClayToolbarButtonOptions _networkHistoryOptions = new() { Text = "История имён", Icon = Icons.Material.Filled.History, Title = "Показать историю сетевых имён" };
    private readonly ClayToolbarButtonOptions _keepDraftOptions = new() { Text = "Оставить мой ввод", Title = "Разрешить конфликт в пользу локальных значений" };
    private readonly ClayToolbarButtonOptions _takeFreshOptions = new() { Text = "Взять конфликтные поля из БД", Title = "Заменить только конфликтные поля свежими значениями" };
    private readonly ClayToolbarButtonOptions _reconcileOptions = new() { Text = "Сверить без повторной записи", Title = "Проверить результат только чтением" };
    private readonly ClayToolbarButtonOptions _reconcileAtsOptions = new() { Text = "Сверить ATS без повтора", Title = "Проверить результат ATS только чтением" };
    private int _networkAttachmentCount;
    private int _networkVersion;
    private IReadOnlyList<EquipmentNetworkNameHistory> _networkNameHistory = [];
    private IReadOnlyList<EquipmentNetworkPortLink> _networkPorts = [];

    private MudForm? _form;
    private ClayEditorFocusScope? _equipmentFocusScope;
    private Employee? CurrentUser { get; set; }
    private EquipmentEntity? _model;
    private EquipmentType? _selectedType;
    private EquipmentType? _persistedType;
    private EquipmentModel? _selectedModel;
    private List<EquipmentPhoneLink> _phones = [];
    private List<EquipmentPhoneLink> _originalPhones = [];
    private PendingPhoneSync? _pendingPhoneSync;
    private int? _phoneToAdd;
    private int _phoneVersion;
    private int _locationVersion;
    private int _accountVersion;
    private int _computerVersion;
    private int _simVersion;
    private int _simServiceVersion;
    private int _licenseVersion;
    private int _softwareVersion;
    private int _accountingVersion;
    private int _upsVersion;
    private string? _warningMessage;
    private DateTime? _writeOffDate;
    private TimeSpan? _writeOffTime;
    private IReadOnlyList<DocumentLookup> _writeOffActs = [];
    private bool _sortTypesByName;
    private bool _canEdit;
    private bool _canEditSim;
    private bool _canEditLicense;
    private bool _canViewPhoneConfig;
    private bool _canViewAccounts;
    private bool _canManageUps;
    private bool _canChangeNetwork;
    private bool _loading = true;
    private bool _saving;
    private bool _loaded;
    private bool _notFound;
    private string? _loadError;
    private bool _allowNavigation;
    private bool _disposed;
    private int _loadedId = -1;
    private int _operationVersion;
    private long _editorGeneration;
    private int _modelVersion;
    private int _ownerVersion;
    private int _shipperVersion;
    private int? _confirmedOwnerId;
    private int? _originalOwnerId;
    private int? _originalTenantId;
    private int? _originalReceiptDocumentId;
    private int? _originalWriteOffDocumentId;
    private string _originalSnapshot = string.Empty;
    private string? _mainConflictMessage;
    private string? _validationMessage;

    /// <summary>Идентификатор редактируемого оборудования.</summary>
    [SupplyParameterFromQuery(Name = "id")]
    public int Id { get; set; }

    private DocumentSelectDataSource ReceiptDocumentSource => Services.GetRequiredKeyedService<DocumentSelectDataSource>("receipt");
    private DocumentSelectDataSource WriteOffDocumentSource => Services.GetRequiredKeyedService<DocumentSelectDataSource>("writeoff");
    private string Title => _model is null ? $"Оборудование #{Id}" : $"[{Id}] {_model.EquipmentTypeName} {_model.ModelName}";
    private bool HasPendingPhoneSync => _pendingPhoneSync is not null;
    private bool HasUnsavedDraft => _loaded && _model is not null && Snapshot() != _originalSnapshot;
    private bool IsModified => HasUnsavedDraft || HasPendingPhoneSync;
    private bool EquipmentFocusReady => !_disposed && _loaded && _canEdit && !_loading && !_saving &&
        MainRawReady && !EquipmentRecoveryWriteBlocked && _phoneSyncCheckpoint is null && !_mainTransition && _activeEditor is null;
    private bool CanSave => CanResumeConfigure || !ConfigureNeedsReconciliation &&
        IsModified && !EquipmentRecoveryWriteBlocked &&
        _canEdit && !_loading && !_saving && !_mainTransition && MainRawReady && _mainConflictMessage is null;
    private bool HasSelectedModel => _model?.ModelId is > 0 && _selectedModel is not null;
    private bool ShowNetworkName => HasSelectedModel && _selectedType?.HasNetworkName > 0;
    private bool HideNetworkAndUps => EquipmentNetworkDiagnostics.HideSection(_networkPorts.Count, _model?.EquipmentTypeId);
    private bool NetworkPortsHaveWarning => _selectedModel is not null && EquipmentNetworkDiagnostics.HasWarning(
        _networkPorts.Count, _selectedModel.NetworkInterfaces, _model?.MacAddress, _model?.MacAddress2, _model?.EquipmentTypeId);
    private bool CanConfigurePhone => ShowPhoneConfig && _canEdit;
    private bool ShowOperations => _canManageUps || _canChangeNetwork || CanConfigurePhone;
    private bool ShowPhones => HasSelectedModel && _selectedType?.HasPhoneNumber > 0 ||
                               _phones.Count > 0 && _persistedType?.HasPhoneNumber > 0;
    private bool ShowHotelPhoneWarning => _hotelPhoneCount > 1;
    private bool ShowPhonePool => HasSelectedModel && _selectedType?.HasPhoneNumber > 0 && _selectedType.Id == (int)ТипыОборудования.IP_телефон;
    private bool ShowPhoneConfig => _selectedType?.Id == (int)ТипыОборудования.IP_телефон;
    private bool ShowComputer => _selectedType?.HasComputerCharacteristics > 0 ||
                                 _persistedType?.HasComputerCharacteristics > 0 &&
                                 (_computerHistory.Count > 0 || _softwareInstallations.Count > 0);
    private bool ShowAccounts => _canViewAccounts && (_selectedType?.MacAddressCount > 0 ||
                                 _persistedType?.MacAddressCount > 0 && _accounts.Count > 0);
    private bool ShowSim => _selectedType?.HasSimCharacteristics > 0 ||
                            _persistedType?.HasSimCharacteristics > 0 && _simCards.Count > 0;
    private bool ShowSimDetail => ShowSim &&
                                  _simCards.Any(item => item.ExecutorPersonId == (int)Clayzor.Lib.Entities.Лица.Билайн) &&
                                  !string.IsNullOrWhiteSpace(Configuration["URI_accounting_phone_form"]);
    private bool ShowLicense => _selectedType?.HasSoftwareCharacteristics > 0 ||
                                _persistedType?.HasSoftwareCharacteristics > 0 && _licenses.Count > 0;
    private bool ShowRouter => _routerInfo is not null || _routerInventory.Count > 0;
    private bool PhonesModified => JsonSerializer.Serialize(_phones) != JsonSerializer.Serialize(_originalPhones);
    private bool ShowCamera => HasSelectedModel && _selectedType?.HasPhoneNumber > 0 && _selectedType.Id == (int)ТипыОборудования.IP_телефон;
    private bool CanEditPhones => _canEdit && _selectedType?.HasPhoneNumber > 0;
    private bool CanEditAccounts => _canEdit && _selectedType?.MacAddressCount > 0;
    private bool CanEditComputer => _canEdit && _selectedType?.HasComputerCharacteristics > 0;
    private bool CanEditSim => _canEditSim && _selectedType?.HasSimCharacteristics > 0;
    private bool CanEditLicense => _canEditLicense && _selectedType?.HasSoftwareCharacteristics > 0;
    private int MacCount => HasSelectedModel ? Math.Min(3, _selectedModel!.NetworkInterfaces) : 0;
    private string? ChangedByUrl => _model?.ChangedById > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? EquipmentIns.BuildEditUrl(Configuration["URI_user_form"]!, _model.ChangedById)
        : null;
    private Func<EquipmentModel, Task>? ModelViewItem => !string.IsNullOrWhiteSpace(Configuration["URI_model_form"]) ? OpenModelViewAsync : null;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _typeOptions.DraftKey = "main.type";
        _modelOptions.DraftKey = "main.model";
        _phonePoolOptions.DraftKey = "main.phone-pool";
        _phoneOptions.DraftKey = "main.phone";
        _ownerOptions.DraftKey = "main.owner";
        _receiptDocumentOptions.DraftKey = "main.receipt-document";
        _writeOffDocumentOptions.DraftKey = "main.writeoff-document";
        _shipperOptions.DraftKey = "main.shipper";
        _tenantOptions.DraftKey = "main.tenant";
        _snOptions.DraftKey = "main.sn";
        _networkNameOptions.DraftKey = "main.network-name";
        _macOptions.DraftKey = "main.mac";
        _descriptionOptions.DraftKey = "main.description";
        _writeOffDateOptions.DraftKey = "main.writeoff-date";
        _writeOffTimeOptions.DraftKey = "main.writeoff-time";
        ConfigureSectionGrid(_phoneGridOptions, "equipment-phones");
        ConfigureSectionGrid(_locationGridOptions, "equipment-locations");
        ConfigureSectionGrid(_responsibleGridOptions, "equipment-responsibles");
        ConfigureSectionGrid(_accountGridOptions, "equipment-accounts");
        ConfigureSectionGrid(_computerGridOptions, "equipment-computer");
        ConfigureSectionGrid(_simGridOptions, "equipment-sim");
        ConfigureSectionGrid(_simServiceGridOptions, "equipment-sim-services");
        ConfigureSectionGrid(_licenseGridOptions, "equipment-license");
        ConfigureSectionGrid(_softwareGridOptions, "equipment-software");
        ConfigureSectionGrid(_accountingGridOptions, "equipment-accounting");
        ConfigureSectionGrid(_routerGridOptions, "equipment-router");
    }

    private static void ConfigureSectionGrid(ClayGridOptions options, string id)
    {
        options.Id = id;
        options.Class = "equipment-detail-grid";
        options.ShowToolbar = false;
        options.ShowPagination = false;
        options.AutoHeight = true;
        options.AllowColumnReorder = false;
        options.AllowSorting = false;
        options.ColumnMenuMode = ColumnMenuMode.Hidden;
    }

    private string PhoneGridKey => $"{_canEdit}:{_phones.Any(item => item.ConnectionEquipmentId is > 0)}";
    private int ComputerRamColumns => ComputerComponentCount(item => item.RamComponents);
    private int ComputerHdColumns => ComputerComponentCount(item => item.HdComponents);
    private int ComputerSsdColumns => ComputerComponentCount(item => item.SsdComponents);

    private int ComputerComponentCount(Func<EquipmentComputerSnapshot, IReadOnlyList<decimal>> components) =>
        _computerHistory.Count == 0 ? 0 : _computerHistory.Max(item => components(item).Count);

    private static string ComputerComponent(IReadOnlyList<decimal> components, int index) =>
        index < components.Count ? components[index].ToString("0.###") : "0";

    private static string FormatConfiguration(string name, DateTime? date) => string.IsNullOrWhiteSpace(name)
        ? string.Empty
        : $"{name} [{date:dd.MM.yy}]";

    private static string FormatPeriod(DateTime? from, DateTime? to) => from is null
        ? string.Empty
        : $"{from:dd.MM.yyyy} — {(to is null ? "по н.в." : $"{to:dd.MM.yyyy}")}";

    private bool SimIsBlocked => _simServices.Any(item => item.IsActiveBlock(_clientToday));
    private decimal SimDailyTotal => _simServices.Sum(item => item.DailyCost);
    private decimal SimMonthlyTotal => _simServices.Sum(item => item.MonthlyCost);

    private string? SimServiceRowClass(EquipmentSimService item) =>
        item.GetWarning(_clientToday) is not null
            ? "mud-theme-error"
            : item.IsTariffPlan > 0 ? "equipment-recommended" : null;

    private string? LocationRowClass(EquipmentLocationPeriod item) =>
        ReferenceEquals(_locations.LastOrDefault(), item) && item.ReturnDate?.Date < _clientToday
            ? "mud-theme-error"
            : null;

    private string? ResponsibleRowClass(EquipmentResponsiblePeriod item) =>
        item.ToUtc is null && item.ReturnDate?.Date < _clientToday
            ? "mud-theme-error"
            : null;

    private string FormatLocationDate(EquipmentLocationPeriod item)
    {
        var text = FormatDisplayDate(item.FromUtc, true);
        if (ReferenceEquals(_locations.LastOrDefault(), item) && item.ReturnDate is not null)
            text += $" - срок возврата {item.ReturnDate:dd.MM.yyyy}";
        return text;
    }

    private string FormatResponsiblePeriod(EquipmentResponsiblePeriod item)
    {
        var text = FormatDisplayDate(item.FromUtc, true) + " — " +
                   (item.ToUtc is null ? "сейчас" : FormatDisplayDate(item.ToUtc.Value, true));
        if (item.ReturnDate is not null)
            text += $" — срок возврата {item.ReturnDate:dd.MM.yyyy}";
        return text;
    }

    private void ShowAccountGrid() => _accountsVisible = true;

    private string AccountPasswordDisplay(EquipmentAccount account) =>
        !_accountPasswords.TryGetValue(account.Login, out var password)
            ? "*****"
            : string.IsNullOrEmpty(password) ? "(пустой пароль)" : password;

    private async Task ToggleAccountPasswordAsync(EquipmentAccount account)
    {
        if (_accountPasswords.Remove(account.Login))
        {
            _accountSecretVersion++;
            StateHasChanged();
            return;
        }
        if (_model is null || _saving)
            return;
        var version = ++_accountSecretVersion;
        var equipmentId = _model.Id;
        var login = account.Login;
        _saving = true;
        try
        {
            var password = await Service.GetAccountPasswordAsync(equipmentId, login);
            if (_disposed || version != _accountSecretVersion || _model?.Id != equipmentId ||
                !_accounts.Any(item => item.Login == login))
                return;
            _accountPasswords[login] = password;
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _accountSecretVersion)
                _validationMessage = ex is Microsoft.Data.SqlClient.SqlException
                    ? "Не удалось прочитать пароль. Повторите попытку."
                    : ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private void ClearAccountSecrets()
    {
        _accountSecretVersion++;
        _accountPasswords.Clear();
    }

    private string? EquipmentUrl(int? id) => id is > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_equipment_form"])
        ? EquipmentIns.BuildEditUrl(Configuration["URI_equipment_form"]!, id.Value)
        : null;

    private string? NetUrl(int id) => !string.IsNullOrWhiteSpace(Configuration["URI_net_form"])
        ? EquipmentIns.BuildEditUrl(Configuration["URI_net_form"]!, id)
        : null;

    private string? PhoneUrl(int id) => !string.IsNullOrWhiteSpace(Configuration["URI_tel_form"])
        ? EquipmentIns.BuildEditUrl(Configuration["URI_tel_form"]!, id)
        : null;

    private string? EmployeeUrl(int id) => !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? EquipmentIns.BuildEditUrl(Configuration["URI_user_form"]!, id)
        : null;

    private static string? SafeExternalUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        ? url
        : null;

    private string PhoneChangedTooltip(EquipmentPhoneLink item) =>
        $"Изменил: {item.ChangedByName} {FormatDisplayDate(item.ChangedAt, true)}".Trim();

    private static RenderFragment RenderLink(string? url, string? text, string? title = null) => builder =>
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            builder.AddContent(0, text);
            return;
        }
        builder.OpenElement(0, "a");
        builder.AddAttribute(1, "href", url);
        builder.AddAttribute(2, "target", "_blank");
        builder.AddAttribute(3, "rel", "noopener");
        if (!string.IsNullOrWhiteSpace(title))
            builder.AddAttribute(4, "title", title);
        builder.AddContent(5, text);
        builder.CloseElement();
    };

    private RenderFragment EmployeeLink(int employeeId, string? employeeName) => builder =>
    {
        var url = EmployeeUrl(employeeId);
        if (url is null)
        {
            builder.AddContent(0, employeeName);
            return;
        }
        builder.OpenElement(0, "a");
        builder.AddAttribute(1, "href", url);
        builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => OpenEmployeeAsync(employeeId)));
        builder.AddEventPreventDefaultAttribute(3, "onclick", true);
        builder.AddContent(4, employeeName);
        builder.CloseElement();
    };

    private async Task OpenEmployeeAsync(int employeeId)
    {
        if (EmployeeUrl(employeeId) is string url)
            await JS.InvokeVoidAsync("clayOpen", url, "user-form");
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            _businessDayWatch = WatchBusinessDayAsync(_pageCts.Token);

        if (_loadedId != Id)
        {
            await ReleaseDraftRegistrationAsync();
            _mainDraftCancelled = false;
            _pendingMainClose = null;
            _draftInstance = Guid.NewGuid();
            _draftStorageError = null;
            _loadedId = Id;
            await LoadCurrentAsync(Id);
            await InitializeDraftAsync();
            return;
        }
        await PersistDraftAsync();
    }

    private int PrepareEquipmentLoad(int id)
    {
        ResetEquipmentReachability();
        if (_model?.Id != id)
        {
            _configureAttemptId = null;
            _configureCheckRequired = false;
            _legacyConfigurePending = false;
        }
        if (_pendingPhoneSync?.EquipmentId != id)
            _pendingPhoneSync = null;
        var version = ++_operationVersion;
        _mainConflictMessage = null;
        _modelVersion++;
        _ownerVersion++;
        _shipperVersion++;
        _accountVersion++;
        _computerVersion++;
        _simVersion++;
        _simServiceVersion++;
        _licenseVersion++;
        _softwareVersion++;
        _accountingVersion++;
        _upsVersion++;
        _networkVersion++;
        _loading = true;
        _loaded = false;
        _notFound = false;
        _loadError = null;
        _validationMessage = null;
        _accountsVisible = false;
        ClearAccountSecrets();
        return version;
    }

    private Task LoadCurrentAsync(int id) => LoadEquipmentAsync(id, null);

    private async Task LoadEquipmentAsync(int id, MainEditorDecision? reset)
    {
        var version = reset?.Version ?? PrepareEquipmentLoad(id);
        if (reset is null)
        {
            StateHasChanged();
            await Task.Delay(100);
        }
        try
        {
            var employee = await Service.GetCurrentUserWithRolesAsync();
            if (!IsCurrent(version, id))
                return;
            var canEdit = employee?.HasRole(Роли.Специалист_по_инвентаризации_оборудования) == true;
            if (reset is null)
                CurrentUser = employee;
            else if (!IsCurrent(reset) || employee?.Id != reset.EmployeeId || !canEdit)
            {
                if (IsCurrent(reset))
                    _validationMessage = "Не удалось подтвердить доступ к оборудованию. Черновик сохранён.";
                return;
            }

            var model = id > 0 ? await Service.GetAsync(id) : null;
            if (!IsCurrent(version, id))
                return;
            if (model is null || (reset is not null && model.Id != id))
            {
                if (reset is null)
                {
                    _model = null;
                    _notFound = true;
                }
                else if (IsCurrent(reset))
                    _validationMessage = "Не удалось перечитать оборудование. Черновик сохранён.";
                return;
            }
            var selectedType = model.EquipmentTypeId is > 0
                ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, id))
                return;
            var canEditSim = selectedType?.HasSimCharacteristics > 0 &&
                             employee?.HasRole(Роли.Администратор_тарифов_связи) == true;
            var canEditLicense = selectedType?.HasSoftwareCharacteristics > 0 &&
                                 (employee?.HasRole(Роли.Специалист_по_инвентаризации_по,
                                      EmployeeRoleScope.PersonOrGlobal, model.OwnerId) == true ||
                                  model.TenantId is not null && model.TenantId != model.OwnerId &&
                                  employee?.HasRole(Роли.Специалист_по_инвентаризации_по,
                                      EmployeeRoleScope.PersonOrGlobal, model.TenantId) == true);
            var canViewPhoneConfig = selectedType?.Id == (int)ТипыОборудования.IP_телефон &&
                                     employee?.HasRole(Роли.Дежурный_инженер) == true;
            var canViewAccounts = selectedType?.MacAddressCount > 0 &&
                                  employee?.HasRole(Роли.Дежурный_инженер, EmployeeRoleScope.Global) == true;
            var canManageUps = model.EquipmentTypeId is < 100 && selectedType?.HasSimCharacteristics == 0 &&
                               employee?.HasRole(Роли.Специалист_по_инвентаризации_оборудования,
                                   EmployeeRoleScope.PersonOrGlobal, model.OwnerId) == true;
            var canChangeNetwork = selectedType?.HasComputerCharacteristics > 0 &&
                                   employee?.HasRole(Роли.Исполнитель_заявок_службы_поддержки_пользователей,
                                       EmployeeRoleScope.Global) == true;
            var canUseAccounting = await Service.CanUseAccountingAsync(model.OwnerId);
            if (!IsCurrent(version, id))
                return;
            var selectedModel = model.ModelId is > 0
                ? await ModelSource.GetByIdAsync(model.ModelId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, id))
                return;
            if (reset is not null && ((model.EquipmentTypeId is > 0 && selectedType is null) ||
                                      (model.ModelId is > 0 && selectedModel is null)))
            {
                if (IsCurrent(reset))
                    _validationMessage = "Не удалось перечитать тип или модель оборудования. Черновик сохранён.";
                return;
            }
            var clientToday = (await GetClientNowAsync()).Date;
            if (!IsCurrent(version, id))
                return;
            var writeOffActsTask = model.DocumentWriteOffId is null
                ? WriteOffDocumentSource.GetWriteOffActsAsync(id, _pageCts.Token)
                : Task.FromResult<IReadOnlyList<DocumentLookup>>([]);
            var details = await Service.GetFormDataAsync(new EquipmentFormReadRequest
            {
                EquipmentId = id,
                IncludePhones = selectedType?.HasPhoneNumber > 0,
                IncludeHotelPhoneCount = selectedModel is not null && selectedType?.HasPhoneNumber > 0,
                IncludeAccounts = canViewAccounts,
                IncludeComputer = selectedType?.HasComputerCharacteristics > 0,
                IncludeSim = selectedType?.HasSimCharacteristics > 0,
                IncludeLicense = selectedType?.HasSoftwareCharacteristics > 0,
                IncludeNetworkName = selectedType?.HasNetworkName > 0,
                NetworkName = model.NetworkName,
                ShowServiceHistory = _showServiceHistory,
                ShowCompanyRequiredServices = _showCompanyRequiredServices
            }, clientToday);
            var writeOffActs = await writeOffActsTask;
            if (!IsCurrent(version, id))
                return;
            var phones = details.Phones;
            var hotelPhoneCount = details.HotelPhoneCount;
            var locations = details.Locations;
            var responsibles = details.Responsibles;
            var accounts = details.Accounts;
            var computerHistory = details.ComputerHistory;
            var configurations = details.Configurations;
            var simCards = details.SimCards;
            var simServices = details.SimServices;
            var simServiceTypes = details.SimServiceTypes;
            var licenses = details.Licenses;
            var licenseTypes = details.LicenseTypes;
            var softwareInstallations = details.SoftwareInstallations;
            var accounting = details.Accounting;
            var routerInfo = details.RouterInfo;
            var routerInventory = details.RouterInventory;
            var upsLinks = details.UpsLinks;
            var lastInventory = details.LastInventory;
            var networkAttachmentCount = details.NetworkAttachmentCount;
            var networkPorts = details.NetworkPorts;
            var networkNameHistory = details.NetworkNameHistory;
            DateTime? localWriteOff = null;
            if (model.WriteOffAtUtc is not null)
            {
                var utc = DateTime.SpecifyKind(model.WriteOffAtUtc.Value, DateTimeKind.Utc);
                var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
                localWriteOff = utc.AddMinutes(offset);
                if (!IsCurrent(version, id))
                    return;
            }
            var localDates = await LoadLocalDatesAsync(GetDisplayDates(
                locations, responsibles, simCards, networkNameHistory, lastInventory,
                phones, computerHistory, simServices, licenses, softwareInstallations, accounting));
            if (!IsCurrent(version, id))
                return;

            if (reset is not null)
            {
                if (!await AcceptMainResetAsync(reset, model, localWriteOff, phones))
                    return;
                version = PrepareEquipmentLoad(id);
                CurrentUser = employee;
                _mainRawRestorePending = false;
                _mainFormRevision++;
            }
            _clientToday = clientToday;
            _simServicesDay = clientToday;
            _localDates.Clear();
            foreach (var item in localDates)
                _localDates[item.Key] = item.Value;
            _model = model;
            _selectedType = selectedType;
            _persistedType = selectedType;
            _selectedModel = selectedModel;
            _phones = phones.ToList();
            _originalPhones = ClonePhones(phones);
            _hotelPhoneCount = hotelPhoneCount;
            _locations = locations.ToList();
            _responsibles = responsibles.ToList();
            _accounts = accounts.ToList();
            _computerHistory = computerHistory.ToList();
            _standardConfigurations = configurations;
            _simCards = simCards.ToList();
            _simServices = simServices.ToList();
            _simServiceTypes = simServiceTypes;
            _licenses = licenses.ToList();
            _licenseTypes = licenseTypes;
            _softwareInstallations = softwareInstallations.ToList();
            _accounting = accounting.ToList();
            _routerInfo = routerInfo;
            _routerInventory = routerInventory.ToList();
            _upsLinks = upsLinks.ToList();
            _lastInventory = lastInventory;
            _networkAttachmentCount = networkAttachmentCount;
            _networkPorts = networkPorts;
            _networkNameHistory = networkNameHistory;
            _phoneToAdd = null;
            _phoneVersion++;
            _confirmedOwnerId = model.OwnerId;
            ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
            ReceiptDocumentSource.PersonId = model.OwnerId;
            WriteOffDocumentSource.PersonId = model.OwnerId;
            _writeOffDate = localWriteOff?.Date;
            _writeOffTime = localWriteOff?.TimeOfDay;
            _writeOffActs = writeOffActs;
            _canEdit = canEdit;
            _canEditSim = canEditSim;
            _canEditLicense = canEditLicense;
            _canViewPhoneConfig = canViewPhoneConfig;
            _canViewAccounts = canViewAccounts;
            _canManageUps = canManageUps;
            _canChangeNetwork = canChangeNetwork;
            _canUseAccounting = canUseAccounting;
            UpsSource.LocationId = model.LocationId;
            SetAccountingBaseline();
            _loaded = true;
            UpdateOptions();
            _originalSnapshot = Snapshot();
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrent(version, id))
        {
        }
        catch (Microsoft.Data.SqlClient.SqlException) when (IsCurrent(version, id))
        {
            if (reset is null)
            {
                _model = null;
                _loadError = "Не удалось загрузить оборудование из-за ошибки соединения или SQL. Повторите загрузку после восстановления соединения.";
            }
            else if (IsCurrent(reset))
                _validationMessage = "Не удалось перечитать оборудование из-за ошибки SQL. Черновик сохранён.";
        }
        finally
        {
            if (IsCurrent(version, id))
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task RetryLoadAsync()
    {
        if (_loading)
            return;
        await ReleaseDraftRegistrationAsync();
        _draftStorageError = null;
        await LoadCurrentAsync(Id);
        await InitializeDraftAsync();
    }

    private bool IsCurrent(int version, int id) => !_disposed && version == _operationVersion && Id == id;

    private void OnTypeIdChanged(int? id)
    {
        if (_model is null)
            return;
        _modelVersion++;
        if (id is null)
        {
            _model.EquipmentTypeId = null;
            UpdateOptions();
            return;
        }
        if (_selectedType?.Id == id)
        {
            _model.EquipmentTypeId = id;
            ModelSource.Filter.EquipmentTypeId = id;
            return;
        }
        _model.EquipmentTypeId = id;
        _model.ModelId = null;
        _selectedType = null;
        _selectedModel = null;
        ModelSource.Filter.EquipmentTypeId = id;
        UpdateOptions();
    }

    private void OnTypeClearConfirmed()
    {
        if (_model is null)
            return;
        _modelVersion++;
        _phoneVersion++;
        _model.EquipmentTypeId = null;
        _model.ModelId = null;
        _selectedType = null;
        _selectedModel = null;
        ModelSource.Filter.EquipmentTypeId = null;
        UpdateOptions();
    }

    private async Task OnTypeChanged(EquipmentType? type)
    {
        if (_model is null || type is null)
            return;
        if (_selectedType?.Id == type.Id)
        {
            _model.EquipmentTypeId = type.Id;
            ModelSource.Filter.EquipmentTypeId = type.Id;
            return;
        }
        _selectedType = type;
        _model.EquipmentTypeId = type.Id;
        ModelSource.Filter.EquipmentTypeId = type.Id;
        UpdateOptions();
        await LoadPhonesForCurrentTypeAsync();
    }

    private void OnModelIdChanged(int? id)
    {
        if (_model is null)
            return;
        _modelVersion++;
        _model.ModelId = id;
        if (id is not null && _selectedModel?.Id != id)
            _selectedModel = null;
        UpdateOptions();
    }

    private async Task OnModelChangedAsync(EquipmentModel? model)
    {
        if (_model is null || model is null)
            return;
        var version = ++_modelVersion;
        _selectedModel = model;
        _model.ModelId = model.Id;
        var typeChanged = _model.EquipmentTypeId != model.EquipmentTypeId;
        UpdateOptions();
        try
        {
            if (typeChanged)
            {
                _model.EquipmentTypeId = model.EquipmentTypeId;
                ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
                var selectedType = await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token);
                if (_disposed || version != _modelVersion || _model.ModelId != model.Id)
                    return;
                _selectedType = selectedType;
            }
            UpdateOptions();
            if (typeChanged)
                await LoadPhonesForCurrentTypeAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _modelVersion && _model.ModelId == model.Id)
                _validationMessage = $"Не удалось обновить тип и телефонные связи модели. Выбор сохранён; повторите чтение. {ex.Message}";
        }
    }

    private async Task OnOwnerChangedAsync(int? id)
    {
        var model = _model;
        if (model is null)
            return;
        var equipmentId = model.Id;
        var version = ++_ownerVersion;
        _shipperVersion++;
        model.OwnerId = id;
        SetDocumentOwner(id);
        if (id is null)
        {
            _accountingVersion++;
            _canUseAccounting = false;
            _canEditLicense = false;
            return;
        }
        try
        {
            if (_confirmedOwnerId != id)
            {
                var currentUser = await Service.GetCurrentUserWithRolesAsync();
                if (_disposed || version != _ownerVersion || !ReferenceEquals(model, _model) ||
                    model.Id != equipmentId || model.OwnerId != id)
                    return;
                CurrentUser = currentUser;
                _confirmedOwnerId = id;
            }
            await RefreshAccountingAccessAsync(id);
            await RefreshLicenseAccessAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (_disposed || version != _ownerVersion || !ReferenceEquals(model, _model) ||
                model.Id != equipmentId || model.OwnerId != id)
                return;
            _canUseAccounting = false;
            _canEditLicense = false;
            _validationMessage = $"Не удалось проверить права владельца. Выбор сохранён; повторите чтение. {ex.Message}";
        }
    }

    private async Task RefreshAccountingAccessAsync(int? personId)
    {
        var version = ++_accountingVersion;
        var available = await Service.CanUseAccountingAsync(personId);
        if (_disposed || version != _accountingVersion || _model?.OwnerId != personId)
            return;
        _canUseAccounting = available;
    }

    private Task RefreshLicenseAccessAsync()
    {
        if (_model is null)
            return Task.CompletedTask;
        var model = _model;
        var version = _ownerVersion;
        var ownerId = model.OwnerId;
        var tenantId = model.TenantId;
        var available = _selectedType?.HasSoftwareCharacteristics > 0 &&
                        (CurrentUser?.HasRole(Роли.Специалист_по_инвентаризации_по,
                             EmployeeRoleScope.PersonOrGlobal, ownerId) == true ||
                         tenantId is not null && tenantId != ownerId &&
                         CurrentUser?.HasRole(Роли.Специалист_по_инвентаризации_по,
                             EmployeeRoleScope.PersonOrGlobal, tenantId) == true);
        if (!_disposed && version == _ownerVersion && ReferenceEquals(model, _model) &&
            model.OwnerId == ownerId && model.TenantId == tenantId)
            _canEditLicense = available;
        return Task.CompletedTask;
    }

    private void SetDocumentOwner(int? id) => ReceiptDocumentSource.PersonId = WriteOffDocumentSource.PersonId = id;

    private void UpdatePhoneSourceFilters()
    {
        PhoneNumberSource.LocationId = _model?.LocationId;
        PhoneNumberSource.EmployeeId = _model?.EmployeeId;
        PhoneNumberSource.EquipmentTypeId = _model?.EquipmentTypeId;
        PhoneNumberSource.EquipmentId = _model?.Id;
        PhoneNumberSource.PhoneIdBase = _phones.FirstOrDefault(item => item.IsDefault)?.PhoneId;
        PhoneNumberSource.ExcludedPhoneIds = _phones.Select(item => item.PhoneId).ToArray();
    }

    private void SetAccountingBaseline()
    {
        if (_model is null)
            return;
        _originalOwnerId = _model.OwnerId;
        _originalTenantId = _model.TenantId;
        _originalReceiptDocumentId = _model.DocumentReceiptOuId;
        _originalWriteOffDocumentId = _model.DocumentWriteOffId;
    }

    private async Task OnReceiptDocumentChangedAsync(int? id)
    {
        if (_model is null)
            return;
        var version = ++_shipperVersion;
        _model.DocumentReceiptOuId = id;
        if (id is null || _model.OwnerId is null)
            return;
        var ownerId = _model.OwnerId.Value;
        try
        {
            var shipperId = await ReceiptDocumentSource.GetSingleShipperIdAsync(id.Value, ownerId);
            if (!_disposed && version == _shipperVersion && _model.DocumentReceiptOuId == id &&
                _model.OwnerId == ownerId && shipperId is not null)
                _model.ShipperId = shipperId;
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _shipperVersion && _model.DocumentReceiptOuId == id && _model.OwnerId == ownerId)
                _validationMessage = $"Не удалось определить поставщика по документу. Выбор сохранён; повторите чтение. {ex.Message}";
        }
    }

    private void OnShipperChanged(int? id)
    {
        _shipperVersion++;
        if (_model is not null)
            _model.ShipperId = id;
    }

    private void OnCameraChanged(bool? value)
    {
        if (_model is not null)
            _model.CameraIsConnected = value == true ? (byte)1 : (byte)0;
    }

    private void OnTypeSortChanged(bool? value) => EquipmentTypeSource.SortByName = _sortTypesByName = value == true;

    private void OnMacChanged(string? value, int index)
    {
        if (_model is null)
            return;
        var normalized = global::Clayzor.Lib.Entities.Inventory.Equipment.NormalizeMacAddress(value);
        if (index == 1) _model.MacAddress = normalized;
        if (index == 2) _model.MacAddress2 = normalized;
        if (index == 3) _model.MacAddressIlo = normalized;
    }

    private async Task OnPhoneToAddChangedAsync(int? id)
    {
        _phoneToAdd = id;
        if (id is not null)
            await AddPhoneAsync();
    }

    private async Task AddPhoneAsync()
    {
        var model = _model;
        if (model is null || _phoneToAdd is null || !_canEdit || _saving)
            return;
        var version = ++_phoneVersion;
        var phoneId = _phoneToAdd.Value;
        if (_phones.Any(item => item.PhoneId == phoneId))
        {
            _phoneToAdd = null;
            return;
        }
        try
        {
            var phone = await PhoneNumberSource.GetByIdAsync(phoneId, _pageCts.Token);
            if (_disposed || version != _phoneVersion || !ReferenceEquals(model, _model) || phone is null)
                return;
            var connectionId = model.LocationId is > 0
                ? await Service.GetConnectionEquipmentIdAsync(phoneId, model.EmployeeId, model.LocationId.Value)
                : null;
            if (_disposed || version != _phoneVersion || !ReferenceEquals(model, _model))
                return;
            _phones.Add(new EquipmentPhoneLink
            {
                PhoneId = phone.Id,
                EquipmentId = model.Id,
                Description = phone.Name,
                ConnectionEquipmentId = connectionId,
                AutoDialNumber = string.Empty,
                Notes = string.Empty,
            });
            _phoneToAdd = null;
            UpdatePhoneSourceFilters();
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _phoneVersion && ReferenceEquals(model, _model))
                _validationMessage = $"Не удалось прочитать телефонную связь. Выбор сохранён; повторите чтение. {ex.Message}";
        }
    }

    private async Task EditPhoneReturnAsync(EquipmentPhoneLink item)
    {
        if (_model is not { } model || CurrentUser is not { } actor || !CanEditPhones || _saving ||
            item.EquipmentId != model.Id || item.PhoneId <= 0 || !_phones.Contains(item))
            return;

        var opening = CaptureEditorOpening();
        var phoneId = item.PhoneId;
        var connectionId = item.ConnectionEquipmentId;
        var initial = new EquipmentPhoneReturnResult(item.ReturnDate, item.Notes);
        var originals = _originalPhones.Where(link => link.EquipmentId == model.Id && link.PhoneId == phoneId)
            .Take(2).ToArray();
        if (originals.Length > 1 || originals is [var stored] && stored.ConnectionEquipmentId != connectionId)
        {
            _validationMessage = "Связь изменилась. Сверьте данные основной карточки.";
            return;
        }

        var original = originals.SingleOrDefault();
        var parameters = new DialogParameters<EquipmentPhoneDialog>
        {
            { dialog => dialog.ReturnDate, initial.ReturnDate },
            { dialog => dialog.Notes, initial.Notes },
            { dialog => dialog.PresenceTarget, GetPresenceTarget() },
        };
        bool OwnsLink() => IsCurrentCore(opening) && CurrentUser?.Id == actor.Id && CanEditPhones && _phones.Contains(item) &&
            item.PhoneId == phoneId && item.ConnectionEquipmentId == connectionId &&
            new EquipmentPhoneReturnResult(item.ReturnDate, item.Notes) == initial;
        var result = await ShowDialogAsync<EquipmentPhoneDialog>("Срок возврата", parameters,
            draftKey: phoneId.ToString(System.Globalization.CultureInfo.InvariantCulture), openingContext: opening,
            readFreshCancellationState: ReadCancellationStateAsync, newCancellationRecord: original is null);
        if (result is null || result.Canceled || result.Data is not EquipmentPhoneReturnResult value || !OwnsLink())
            return;
        item.ReturnDate = value.ReturnDate;
        item.Notes = value.Notes;

        async Task<string?> ReadCancellationStateAsync(int equipmentId, int employeeId, CancellationToken ct)
        {
            if (!OwnsLink() || !IsCurrent(opening))
                return null;
            var fresh = await Service.ReadPhoneReturnEditorResetAsync(equipmentId, phoneId, connectionId,
                original is not null, employeeId, ct);
            if (!OwnsLink() || !IsCurrent(opening) || !fresh.CanReset)
                return null;

            EquipmentPhoneReturnResult reset;
            if (original is null)
                reset = initial;
            else if (fresh.Link is { } row && row.ChangedAt == original.ChangedAt &&
                     row.ReturnDate == original.ReturnDate && row.Notes == original.Notes &&
                     row.IsDefault == original.IsDefault && row.AutoDialNumber == original.AutoDialNumber &&
                     row.AutoDialDelay == original.AutoDialDelay)
                reset = new(row.ReturnDate, row.Notes);
            else
                return null;

            return JsonSerializer.Serialize(new { reset.ReturnDate, reset.Notes });
        }
    }

    private async Task RemovePhoneAsync(EquipmentPhoneLink item)
    {
        if (!_canEdit || _saving)
            return;
        if (!await ConfirmAsync("Удаление связи", $"Удалить связь с номером «{item.Description}»?"))
            return;
        _phones.Remove(item);
        UpdatePhoneSourceFilters();
    }

    private async Task LoadPhonesForCurrentTypeAsync()
    {
        if (_model is null)
            return;
        var version = ++_phoneVersion;
        if (!ShowPhones)
            return;
        if (PhonesModified)
        {
            UpdatePhoneSourceFilters();
            return;
        }
        var id = _model.Id;
        try
        {
            var phones = await Service.GetPhonesAsync(id);
            if (_disposed || version != _phoneVersion || _model.Id != id)
                return;
            _phones = phones.ToList();
            _originalPhones = ClonePhones(phones);
            UpdatePhoneSourceFilters();
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _phoneVersion && _model.Id == id)
                _validationMessage = $"Не удалось обновить телефонные связи. Прежние данные сохранены; повторите чтение. {ex.Message}";
        }
    }

    private sealed record PendingPhoneSync(
        int EquipmentId,
        List<EquipmentPhoneLink> Original,
        List<EquipmentPhoneLink> Current,
        IReadOnlyList<PhoneSyncOperation>? Operations = null,
        bool Unknown = false,
        string? UnknownMessage = null)
    {
        public PhoneSyncAttempt? Attempt { get; init; }
    }

    private static List<EquipmentPhoneLink> ClonePhones(IEnumerable<EquipmentPhoneLink> phones) =>
        phones.Select(item => new EquipmentPhoneLink
        {
            PhoneId = item.PhoneId,
            EquipmentId = item.EquipmentId,
            Description = item.Description,
            IsDefault = item.IsDefault,
            ConnectionEquipmentId = item.ConnectionEquipmentId,
            AutoDialNumber = item.AutoDialNumber,
            AutoDialDelay = item.AutoDialDelay,
            ReturnDate = item.ReturnDate,
            Notes = item.Notes,
            ChangedByName = item.ChangedByName,
            ChangedAt = item.ChangedAt,
        }).ToList();

    private async Task OpenLocationDialogAsync(EquipmentLocationPeriod? existing)
    {
        if (_model is null || !_canEdit || _saving)
            return;
        var opening = CaptureEditorOpening();
        var restoredOriginal = _activeEditor?.Kind == nameof(EquipmentLocationDialog)
            ? EquipmentPeriodOriginal.Read(_restoredEditorState)
            : null;
        var isEdit = existing is not null || restoredOriginal is not null;
        var freshActive = existing is not null && ReferenceEquals(existing, _locations.LastOrDefault());
        var original = restoredOriginal ?? (existing is null ? null : EquipmentPeriodOriginal.FromLocation(existing, freshActive));
        var requireResponsible = !isEdit && !_responsibles.Any(item => item.ToUtc is null);
        var fromLocal = existing is null ? (DateTime?)null : await ConvertUtcToLocalAsync(existing.FromUtc);
        var clientNow = await GetClientNowAsync();
        if (!IsCurrent(opening))
            return;
        var parameters = new DialogParameters<EquipmentLocationDialog>
        {
            { dialog => dialog.EquipmentId, opening.EquipmentId },
            { dialog => dialog.IsEdit, isEdit },
            { dialog => dialog.RequireResponsible, requireResponsible },
            { dialog => dialog.Now, clientNow },
            { dialog => dialog.GetNow, (Func<Task<DateTime>>)GetClientNowAsync },
            { dialog => dialog.LocationId, existing?.LocationId ?? original?.SubjectId },
            { dialog => dialog.FromDate, fromLocal?.Date },
            { dialog => dialog.FromTime, fromLocal?.TimeOfDay },
            { dialog => dialog.ReturnDate, existing?.ReturnDate },
            { dialog => dialog.Notes, existing?.Notes ?? string.Empty },
            { dialog => dialog.Original, original },
            { dialog => dialog.FreshOriginal, existing },
            { dialog => dialog.FreshOriginalActive, freshActive },
            { dialog => dialog.Save, (Func<EquipmentLocationDialogResult, Task<EquipmentEditorSaveResult>>)(value => SaveLocationDraftAsync(existing, value)) },
        };
        string? resetState = null;
        EquipmentLocationPeriod? resetPeriod = null;
        await ShowDialogAsync<EquipmentLocationDialog>("Расположение", parameters,
            draftKey: (existing?.FromUtc ?? original?.FromUtc)?.Ticks.ToString(), openingContext: opening,
            presence: opening.EquipmentId > 0 && existing is { FromUtc.Ticks: > 0 }
                ? Service.CreateLocationPresence(opening.EquipmentId, existing.FromUtc) : null,
            readFreshCancellationState: ReadResetAsync, newCancellationRecord: !isEdit,
            acceptFreshCancellationState: (component, state) =>
            {
                if (component is EquipmentLocationDialog dialog && state == resetState)
                {
                    existing = resetPeriod;
                    dialog.FreshOriginal = resetPeriod;
                    dialog.FreshOriginalActive = resetPeriod is not null;
                }
            });

        async Task<string?> ReadResetAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            var context = await Service.ReadPeriodEditorResetAsync(equipmentId, employeeId, token);
            if (context is null) return null;
            var target = isEdit ? context.Locations.LastOrDefault() : null;
            if (isEdit && (target is null || original is null || target.FromUtc != original.FromUtc ||
                target.LocationId != original.SubjectId || context.Locations.Count(row => row.FromUtc == target.FromUtc) != 1))
                return null;
            var local = target is null ? (DateTime?)null : await ConvertUtcToLocalAsync(target.FromUtc);
            token.ThrowIfCancellationRequested();
            resetPeriod = target;
            return resetState = JsonSerializer.Serialize(new
            {
                LocationId = (int?)target?.LocationId,
                EmployeeId = (int?)null,
                FromDate = local?.Date,
                FromTime = local?.TimeOfDay,
                ReturnDate = target?.ReturnDate,
                Notes = target?.Notes ?? string.Empty,
                RequireResponsible = !isEdit && !context.Responsibles.Any(item => item.ToUtc is null),
                Original = target is null ? null : EquipmentPeriodOriginal.FromLocation(target, true),
            });
        }
    }

    private async Task<EquipmentEditorSaveResult> SaveLocationDraftAsync(
        EquipmentLocationPeriod? existing,
        EquipmentLocationDialogResult value)
    {
        if (_model is not { } model)
            return EquipmentEditorSaveResult.Reject("Оборудование не загружено.");
        var isEdit = existing is not null || value.Original is not null;
        if (!isEdit && _locations.LastOrDefault()?.LocationId == value.LocationId)
            return EquipmentEditorSaveResult.Reject("Оборудование уже находится в выбранном расположении.");
        if (isEdit)
        {
            if (value.Original is null || value.Original.EquipmentId != _model.Id)
                return EquipmentEditorSaveResult.Reject("Исходный период расположения не подтверждён. Запись не отправлена.");
            var fresh = existing ?? _locations.FirstOrDefault(item => item.FromUtc == value.Original.FromUtc);
            var latest = _locations.LastOrDefault();
            var originalError = value.Original.ValidateFresh(fresh,
                fresh is not null && latest is not null && fresh.EquipmentId == latest.EquipmentId &&
                fresh.FromUtc == latest.FromUtc && fresh.LocationId == latest.LocationId);
            if (originalError is not null)
                return EquipmentEditorSaveResult.Reject(originalError);
        }
        var version = ++_locationVersion;
        var pageVersion = _operationVersion;
        var employeeId = CurrentUser?.Id;
        var equipmentId = model.Id;
        bool requireResponsible;
        bool expectedActiveResponsible;
        DateTime fromUtc;
        try
        {
            expectedActiveResponsible = !isEdit && await Service.HasCurrentResponsibleAsync(equipmentId);
            requireResponsible = !isEdit && value.LocationId == OnHandsLocationId && !expectedActiveResponsible;
            if (requireResponsible && value.EmployeeId is null)
                return EquipmentEditorSaveResult.Reject("Для расположения «На руках» укажите ответственного сотрудника.");
            fromUtc = await ConvertLocalToUtcAsync(value.FromLocal);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            return EquipmentEditorSaveResult.Reject($"Не удалось проверить актуальный контекст расположения. Запись не отправлена. {ex.Message}");
        }
        var location = new EquipmentLocationPeriod
        {
            EquipmentId = equipmentId,
            LocationId = value.Original?.SubjectId ?? value.LocationId,
            FromUtc = value.Original?.FromUtc ?? fromUtc,
            ChangedAtUtc = value.Original?.ChangedAtUtc ?? default,
            ReturnDate = value.ReturnDate,
            Notes = value.Notes,
        };
        if (isEdit)
        {
            if (!IsCurrentSave(model, pageVersion) || CurrentUser?.Id != employeeId || version != _locationVersion)
                return EquipmentEditorSaveResult.Reject("Контекст расположения изменился. Запись не отправлена.");
            return await SavePeriodDraftAsync(UpdateLocationPeriodCommand, location.FromUtc,
                equipmentId, version, (actorId) => Service.UpdateLocationAsync(location, actorId, _pageCts.Token));
        }
        if (!IsCurrentSave(model, pageVersion) || CurrentUser?.Id != employeeId || version != _locationVersion)
            return EquipmentEditorSaveResult.Reject("Контекст расположения изменился. Запись не отправлена.");
        var responsible = requireResponsible && value.EmployeeId is not null
            ? new EquipmentResponsiblePeriod
            {
                EquipmentId = equipmentId,
                EmployeeId = value.EmployeeId.Value,
                FromUtc = fromUtc,
                Notes = string.Empty,
            }
            : null;
        return await SavePeriodDraftAsync(AddLocationPeriodCommand, location.FromUtc,
            equipmentId, version, (actorId) => Service.AddLocationAsync(location, responsible,
                expectedActiveResponsible, actorId, _pageCts.Token));
    }

    private async Task RefreshLocationsAsync()
    {
        if (_model is not { } model)
            return;
        var pageVersion = _operationVersion;
        var version = _locationVersion;
        var locations = await Service.GetLocationsAsync(model.Id);
        if (!IsCurrentSave(model, pageVersion) || version != _locationVersion)
            return;
        _locations = locations.ToList();
        model.LocationId = _locations.LastOrDefault()?.LocationId;
        UpdatePhoneSourceFilters();
        await RefreshLocationDisplayDatesAsync();
    }

    private async Task RefreshResponsiblesAsync()
    {
        if (_model is not { } model)
            return;
        var pageVersion = _operationVersion;
        var version = _locationVersion;
        var responsibles = await Service.GetResponsiblesAsync(model.Id);
        if (!IsCurrentSave(model, pageVersion) || version != _locationVersion)
            return;
        _responsibles = responsibles.ToList();
        model.EmployeeId = _responsibles.LastOrDefault(item => item.ToUtc is null)?.EmployeeId;
        UpdatePhoneSourceFilters();
        await RefreshLocationDisplayDatesAsync();
    }

    private async Task OpenResponsibleDialogAsync(
        EquipmentResponsibleDialogMode mode,
        EquipmentResponsiblePeriod? existing)
    {
        if (_model is null || !_canEdit || _saving)
            return;
        var opening = CaptureEditorOpening();
        var restoredOriginal = mode != EquipmentResponsibleDialogMode.Issue && _activeEditor?.Kind == nameof(EquipmentResponsibleDialog)
            ? EquipmentPeriodOriginal.Read(_restoredEditorState)
            : null;
        var original = restoredOriginal ?? (existing is null ? null : EquipmentPeriodOriginal.FromResponsible(existing, existing.ToUtc is null));
        var originalFromUtc = existing?.FromUtc ?? original?.FromUtc;
        var originalToUtc = existing?.ToUtc ?? original?.ToUtc;
        var fromLocal = originalFromUtc is null ? (DateTime?)null : await ConvertUtcToLocalAsync(originalFromUtc.Value);
        var toLocal = originalToUtc is DateTime toUtcValue
            ? await ConvertUtcToLocalAsync(toUtcValue)
            : (DateTime?)null;
        var requireLocation = mode == EquipmentResponsibleDialogMode.Withdraw && _model.LocationId == OnHandsLocationId;
        var clientNow = await GetClientNowAsync();
        if (!IsCurrent(opening))
            return;
        var parameters = new DialogParameters<EquipmentResponsibleDialog>
        {
            { dialog => dialog.Mode, mode },
            { dialog => dialog.RequireLocation, requireLocation },
            { dialog => dialog.CurrentEmployeeId, existing?.EmployeeId ?? original?.SubjectId },
            { dialog => dialog.Now, clientNow },
            { dialog => dialog.GetNow, (Func<Task<DateTime>>)GetClientNowAsync },
            { dialog => dialog.EmployeeId, mode == EquipmentResponsibleDialogMode.Withdraw ? null : existing?.EmployeeId ?? original?.SubjectId },
            { dialog => dialog.FromDate, fromLocal?.Date },
            { dialog => dialog.FromTime, fromLocal?.TimeOfDay },
            { dialog => dialog.ToDate, toLocal?.Date },
            { dialog => dialog.ToTime, toLocal?.TimeOfDay },
            { dialog => dialog.ReturnDate, existing?.ReturnDate },
            { dialog => dialog.Notes, existing?.Notes ?? string.Empty },
            { dialog => dialog.Original, original },
            { dialog => dialog.FreshOriginal, existing },
            { dialog => dialog.FreshOriginalActive, existing is not null && existing.ToUtc is null },
            { dialog => dialog.Save, (Func<EquipmentResponsibleDialogResult, Task<EquipmentEditorSaveResult>>)(value => SaveResponsibleDraftAsync(mode, existing, value)) },
        };
        if (mode == EquipmentResponsibleDialogMode.Withdraw)
        {
            parameters.Add(dialog => dialog.FromDate, toLocal?.Date);
            parameters.Add(dialog => dialog.FromTime, toLocal?.TimeOfDay);
        }
        string? resetState = null;
        EquipmentResponsiblePeriod? resetPeriod = null;
        await ShowDialogAsync<EquipmentResponsibleDialog>("Ответственный", parameters,
            draftKey: $"{mode}:{(existing?.FromUtc ?? original?.FromUtc)?.Ticks}", openingContext: opening,
            presence: opening.EquipmentId > 0 && mode != EquipmentResponsibleDialogMode.Issue && existing is { FromUtc.Ticks: > 0 }
                ? Service.CreateResponsiblePresence(opening.EquipmentId, existing.FromUtc,
                    mode == EquipmentResponsibleDialogMode.Withdraw) : null,
            readFreshCancellationState: ReadResetAsync, newCancellationRecord: mode == EquipmentResponsibleDialogMode.Issue,
            acceptFreshCancellationState: (component, state) =>
            {
                if (component is EquipmentResponsibleDialog dialog && state == resetState)
                {
                    existing = resetPeriod;
                    dialog.FreshOriginal = resetPeriod;
                    dialog.FreshOriginalActive = resetPeriod is { ToUtc: null };
                }
            });

        async Task<string?> ReadResetAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            var context = await Service.ReadPeriodEditorResetAsync(equipmentId, employeeId, token);
            if (context is null) return null;
            var isIssue = mode == EquipmentResponsibleDialogMode.Issue;
            var withdraw = mode == EquipmentResponsibleDialogMode.Withdraw;
            var active = context.Responsibles.Where(item => item.ToUtc is null).Take(2).ToArray();
            if (isIssue && active.Length != 0 || withdraw && active.Length != 1)
                return null;
            var target = isIssue ? null : withdraw ? active[0] : context.Responsibles.LastOrDefault();
            if (!isIssue && (target is null || original is null || target.FromUtc != original.FromUtc ||
                target.EmployeeId != original.SubjectId || original.ToUtc is null && target.ToUtc is not null ||
                context.Responsibles.Count(row => row.FromUtc == target.FromUtc) != 1))
                return null;

            var local = target is null || withdraw ? (DateTime?)null : await ConvertUtcToLocalAsync(target.FromUtc);
            var toLocal = !withdraw && target?.ToUtc is { } to ? await ConvertUtcToLocalAsync(to) : (DateTime?)null;
            token.ThrowIfCancellationRequested();
            resetPeriod = target;
            return resetState = JsonSerializer.Serialize(new
            {
                EmployeeId = withdraw ? null : target?.EmployeeId,
                CurrentEmployeeId = target?.EmployeeId,
                LocationId = (int?)null,
                FromDate = local?.Date,
                FromTime = local?.TimeOfDay,
                ToDate = toLocal?.Date,
                ToTime = toLocal?.TimeOfDay,
                ReturnDate = target?.ReturnDate,
                Notes = target?.Notes ?? string.Empty,
                RequireLocation = withdraw && context.Locations.LastOrDefault()?.LocationId == OnHandsLocationId,
                Original = target is null ? null : EquipmentPeriodOriginal.FromResponsible(target, target.ToUtc is null),
            });
        }
    }

    private async Task<EquipmentEditorSaveResult> SaveResponsibleDraftAsync(
        EquipmentResponsibleDialogMode mode,
        EquipmentResponsiblePeriod? existing,
        EquipmentResponsibleDialogResult value)
    {
        if (_model is not { } model)
            return EquipmentEditorSaveResult.Reject("Оборудование не загружено.");
        EquipmentResponsiblePeriod? baseline = null;
        if (mode != EquipmentResponsibleDialogMode.Issue)
        {
            if (value.Original is null || value.Original.EquipmentId != _model.Id)
                return EquipmentEditorSaveResult.Reject("Исходный период ответственности не подтверждён. Запись не отправлена.");
            var fresh = existing ?? _responsibles.FirstOrDefault(item => item.FromUtc == value.Original.FromUtc);
            var originalError = value.Original.ValidateFresh(fresh, fresh is not null && fresh.ToUtc is null);
            if (originalError is not null)
                return EquipmentEditorSaveResult.Reject(originalError);
            baseline = new EquipmentResponsiblePeriod
            {
                EquipmentId = value.Original.EquipmentId,
                EmployeeId = value.Original.SubjectId,
                EmployeeName = existing?.EmployeeName ?? string.Empty,
                FromUtc = value.Original.FromUtc,
                ToUtc = value.Original.ToUtc,
                ReturnDate = existing?.ReturnDate,
                Notes = existing?.Notes ?? string.Empty,
                ChangedAtUtc = value.Original.ChangedAtUtc,
                ChangedById = existing?.ChangedById ?? 0,
                ChangedByName = existing?.ChangedByName ?? string.Empty,
            };
        }
        if (mode == EquipmentResponsibleDialogMode.Withdraw && value.EmployeeId is not null &&
            value.EmployeeId == value.Original?.SubjectId)
            return EquipmentEditorSaveResult.Reject("Следующий ответственный должен отличаться от действующего.");
        var version = ++_locationVersion;
        var pageVersion = _operationVersion;
        var employeeId = CurrentUser?.Id;
        var equipmentId = model.Id;
        DateTime? fromUtc;
        DateTime? toUtc;
        try
        {
            fromUtc = value.FromLocal is null ? null : await ConvertLocalToUtcAsync(value.FromLocal.Value);
            toUtc = value.ToLocal is null ? null : await ConvertLocalToUtcAsync(value.ToLocal.Value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return EquipmentEditorSaveResult.Reject($"Не удалось преобразовать локальное время. Запись не отправлена. {ex.Message}");
        }

        if (!IsCurrentSave(model, pageVersion) || CurrentUser?.Id != employeeId || version != _locationVersion)
            return EquipmentEditorSaveResult.Reject("Контекст ответственности изменился. Запись не отправлена.");
        if (mode == EquipmentResponsibleDialogMode.Issue)
        {
            if (value.EmployeeId is not > 0 || fromUtc is null)
                return EquipmentEditorSaveResult.Reject("Укажите сотрудника, дату и время выдачи.");
            var issued = new EquipmentResponsiblePeriod
            {
                EquipmentId = equipmentId, EmployeeId = value.EmployeeId.Value, FromUtc = fromUtc.Value,
                ReturnDate = value.ReturnDate, Notes = value.Notes,
            };
            return await SavePeriodDraftAsync(IssueResponsiblePeriodCommand, issued.FromUtc,
                equipmentId, version, (actorId) => Service.IssueResponsibleAsync(issued, actorId, _pageCts.Token));
        }
        if (mode == EquipmentResponsibleDialogMode.Edit)
        {
            var edited = baseline!;
            edited.ReturnDate = value.ReturnDate;
            edited.Notes = value.Notes;
            return await SavePeriodDraftAsync(UpdateResponsiblePeriodCommand, edited.FromUtc,
                equipmentId, version, (actorId) => Service.UpdateResponsibleAsync(edited, actorId, _pageCts.Token));
        }

        if (mode != EquipmentResponsibleDialogMode.Withdraw || toUtc is null || value.EmployeeId is not null && fromUtc is null)
            return EquipmentEditorSaveResult.Reject("Укажите фактическое время изъятия и дату следующей выдачи.");
        var current = baseline!;
        current.ToUtc = toUtc;
        var next = value.EmployeeId is null ? null : new EquipmentResponsiblePeriod
        {
            EquipmentId = equipmentId, EmployeeId = value.EmployeeId.Value, FromUtc = fromUtc!.Value,
            ReturnDate = value.ReturnDate, Notes = value.Notes,
        };
        var location = value.LocationId is null ? null : new EquipmentLocationPeriod
        {
            EquipmentId = equipmentId, LocationId = value.LocationId.Value, FromUtc = toUtc.Value,
            ReturnDate = null, Notes = string.Empty,
        };
        return await SavePeriodDraftAsync(WithdrawResponsiblePeriodCommand, current.FromUtc,
            equipmentId, version, (actorId) => Service.WithdrawResponsibleAsync(current, next, location,
                value.FromLocal?.Date, actorId, _pageCts.Token));
    }

    private async Task OpenAccountDialogAsync(EquipmentAccount? existing)
    {
        if (_model is null || !_canEdit || !ShowAccounts || _saving)
            return;
        var equipmentId = _model.Id;
        if (existing is not null)
        {
            _accountPasswords.Remove(existing.Login);
            _accountSecretVersion++;
        }
        var parameters = new DialogParameters<EquipmentAccountDialog>
        {
            { dialog => dialog.EquipmentId, equipmentId },
            { dialog => dialog.OriginalLogin, existing?.Login },
            { dialog => dialog.Login, existing?.Login ?? string.Empty },
            { dialog => dialog.LimitedRights, existing?.LimitedRights > 0 },
            { dialog => dialog.Notes, existing?.Notes ?? string.Empty },
            { dialog => dialog.Save, (Func<EquipmentAccountDialogResult, Task<EquipmentEditorSaveResult>>)(value => SaveAccountDraftAsync(existing, value)) },
        };
        var originalLogin = existing?.Login;
        (string State, string Password)? pendingCredential = null;
        try
        {
            await ShowDialogAsync<EquipmentAccountDialog>(
                "Учётная запись", parameters, new DialogOptionsEx { DragMode = MudDialogDragMode.Simple },
                originalLogin ?? string.Empty, presence: existing is not null && existing.EquipmentId == equipmentId
                    ? Service.CreateAccountPresence(equipmentId, originalLogin!, Configuration[EquipmentFormService.AccountPresenceKeySetting])
                    : null, readFreshCancellationState: ReadAccountCancellationStateAsync, newCancellationRecord: existing is null,
                acceptFreshCancellationState: (component, state) =>
                {
                    if (component is EquipmentAccountDialog editor && pendingCredential is { } pending && pending.State == state)
                        editor.AcceptFreshResetPassword(pending.Password);
                    pendingCredential = null;
                },
                discardFreshCancellationState: () => pendingCredential = null);
        }
        finally { pendingCredential = null; }

        async Task<string?> ReadAccountCancellationStateAsync(int id, int employeeId, CancellationToken token)
        {
            pendingCredential = null;
            if (id != equipmentId)
                return null;
            var row = await Service.ReadAccountEditorResetAsync(id, originalLogin, employeeId, token);
            if (row is null || row.EquipmentId != id || originalLogin is not null &&
                (!string.Equals(row.Login, originalLogin, StringComparison.Ordinal) || string.IsNullOrEmpty(row.ExpectedVersion)))
                return null;

            var state = JsonSerializer.Serialize(new
            {
                EquipmentId = id,
                OriginalLogin = originalLogin,
                Login = originalLogin ?? string.Empty,
                OriginalVersion = row.ExpectedVersion,
                LimitedRights = row.LimitedRights > 0,
                Notes = row.Notes ?? string.Empty,
            });
            pendingCredential = (state, originalLogin is null ? string.Empty : row.Password);
            return state;
        }
    }

    private async Task<EquipmentEditorSaveResult> SaveAccountDraftAsync(
        EquipmentAccount? existing,
        EquipmentAccountDialogResult value)
    {
        if (_model is not { } model || CurrentUser is not { } actor || !_canEdit || !ShowAccounts ||
            _activeEditor is not { } editor || ActiveDraftScope != AccountDraftScope(existing?.Login ?? string.Empty))
            return EquipmentEditorSaveResult.Reject("Контекст учётной записи изменился. Откройте редактор заново.");
        var equipmentId = model.Id;
        if (existing is not null && (existing.EquipmentId != equipmentId ||
            !string.Equals(existing.Login, value.Login, StringComparison.Ordinal) || value.OriginalVersion is null))
            return EquipmentEditorSaveResult.Reject("Исходная версия учётной записи не подтверждена. Сверьте актуальные данные.");
        var version = ++_accountVersion;
        var pageVersion = _operationVersion;
        var account = new EquipmentAccount
        {
            EquipmentId = equipmentId,
            Login = value.Login,
            Password = value.Password,
            ExpectedVersion = value.OriginalVersion,
            LimitedRights = value.LimitedRights ? (byte)1 : (byte)0,
            Notes = value.Notes,
        };
        return await SaveChildDraftAsync(
            existing is null ? "Добавление учётной записи" : "Изменение учётной записи",
            "Учётная запись сохранена.", new { account.Login },
            () => existing is null ? Service.AddAccountAsync(account, actor.Id, _pageCts.Token) :
                Service.UpdateAccountAsync(account, actor.Id, _pageCts.Token),
            RefreshAccountsAsync,
            () => IsCurrent(pageVersion, equipmentId) && version == _accountVersion && _model?.Id == equipmentId &&
                CurrentUser?.Id == actor.Id && ReferenceEquals(_activeEditor, editor),
            commandKind: existing is null ? EquipmentFormService.InsertAccountCommand : EquipmentFormService.UpdateAccountCommand);
    }

    private async Task DeleteAccountAsync(EquipmentAccount account)
    {
        if (_model is not { } model || CurrentUser is not { } actor || model.Id != account.EquipmentId ||
            !_canEdit || !ShowAccounts || _saving || _activeEditor is not null)
            return;

        var openingVersion = _accountVersion;
        var pageVersion = _operationVersion;
        var login = account.Login;
        bool Current() => IsCurrentSave(model, pageVersion) && openingVersion == _accountVersion && !_saving &&
            CurrentUser?.Id == actor.Id && _canEdit && ShowAccounts && _activeEditor is null;

        string? expectedVersion;
        try
        {
            var snapshot = await Service.GetAccountEditSnapshotAsync(model.Id, login);
            if (!Current()) return;
            if (snapshot.LimitedRights != account.LimitedRights || snapshot.Notes != account.Notes ||
                snapshot.ExpectedVersion is null)
            {
                _validationMessage = "Учётная запись изменилась. Сверьте актуальные данные перед удалением.";
                return;
            }
            expectedVersion = snapshot.ExpectedVersion;
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (Current()) _validationMessage = "Не удалось подтвердить исходную учётную запись. Удаление не отправлено.";
            return;
        }

        if (!await ConfirmAsync("Удаление учётной записи", $"Удалить учётную запись «{login}»?") || !Current())
            return;

        var version = ++_accountVersion;
        var target = new EquipmentAccount { EquipmentId = model.Id, Login = login, ExpectedVersion = expectedVersion };
        bool SameWrite() => IsCurrent(pageVersion, model.Id) && version == _accountVersion && _model?.Id == model.Id &&
            CurrentUser?.Id == actor.Id && _activeEditor is null;
        var result = await SaveChildDraftAsync("Удаление учётной записи", "Учётная запись удалена.", new { login },
            () => Service.DeleteAccountAsync(target, actor.Id, _pageCts.Token), RefreshAccountsAsync,
            SameWrite, commandKind: EquipmentFormService.DeleteAccountCommand);
        if (SameWrite() && !result.CanClose && result.Message is not null)
        {
            _validationMessage = result.Message;
            StateHasChanged();
        }
    }

    private async Task RefreshAccountsAsync()
    {
        if (_model is null)
            return;
        var version = _accountVersion;
        var equipmentId = _model.Id;
        var accounts = await Service.GetAccountsAsync(equipmentId);
        if (_disposed || version != _accountVersion || _model.Id != equipmentId)
            return;
        _accounts = accounts.ToList();
        ClearAccountSecrets();
    }

    private async Task OpenComputerDialogAsync()
    {
        if (_model is null || !_canEdit || _saving)
            return;
        var opening = CaptureEditorOpening();
        var expectedModelId = opening.Model.ModelId;
        var latest = _computerHistory.LastOrDefault();
        var memoryBanks = _selectedModel?.MemoryBanks ?? 0;
        var clientNow = await GetClientNowAsync();
        if (!IsCurrent(opening))
            return;
        var openedDay = clientNow.Date;
        var expectedExisting = latest?.Date.Date == openedDay;
        var initialComputer = expectedExisting || latest is null ? null : FreezeChildValue(latest);
        var parameters = new DialogParameters<EquipmentComputerDialog>
        {
            { dialog => dialog.EquipmentId, opening.EquipmentId },
            { dialog => dialog.Date, clientNow.Date },
            { dialog => dialog.ExpectedExisting, expectedExisting },
            { dialog => dialog.ChangedAt, expectedExisting ? latest!.ChangedAt : default },
            { dialog => dialog.Today, clientNow.Date },
            { dialog => dialog.GetToday, async () => (await GetClientNowAsync()).Date },
            { dialog => dialog.MemoryBanks, memoryBanks },
            { dialog => dialog.RamDetail, latest?.RamDetail ?? string.Empty },
            { dialog => dialog.HdDetail, latest?.HdDetail ?? string.Empty },
            { dialog => dialog.SsdDetail, latest?.SsdDetail ?? string.Empty },
            { dialog => dialog.Configuration1Id, latest?.Configuration1Id },
            { dialog => dialog.Configuration1Date, latest?.Configuration1Date },
            { dialog => dialog.Configuration2Id, latest?.Configuration2Id },
            { dialog => dialog.Configuration2Date, latest?.Configuration2Date },
            { dialog => dialog.License, latest?.License ?? string.Empty },
            { dialog => dialog.Notes, latest?.Notes ?? string.Empty },
            { dialog => dialog.Configurations, _standardConfigurations },
            { dialog => dialog.Save, (Func<EquipmentComputerSnapshot, Task<EquipmentEditorSaveResult>>)(snapshot => SaveComputerDraftAsync(snapshot, opening, expectedModelId)) },
            { dialog => dialog.HasUnresolvedComputerOperation, false },
        };
        await ShowDialogAsync<EquipmentComputerDialog>(
            "Компьютер", parameters, new DialogOptionsEx { MaxWidth = MaxWidth.Medium, FullWidth = true },
            openingContext: opening, presence: opening.EquipmentId > 0 && expectedExisting
                ? Service.CreateComputerPresence(opening.EquipmentId, openedDay, expectedModelId) : null,
            readFreshCancellationState: ReadComputerCancellationStateAsync, newCancellationRecord: !expectedExisting);

        async Task<string?> ReadComputerCancellationStateAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            if ((await GetClientNowAsync()).Date != openedDay || !IsCurrent(opening))
                return null;
            var fresh = await Service.ReadComputerEditorResetAsync(equipmentId, employeeId, expectedModelId,
                openedDay, expectedExisting, token);
            if (fresh is not { } source || !IsCurrent(opening) || (await GetClientNowAsync()).Date != openedDay ||
                !IsCurrent(opening))
                return null;
            return EquipmentComputerDialog.SerializeResetState(source.Snapshot ?? initialComputer,
                equipmentId, openedDay, expectedExisting, source.MemoryBanks);
        }
    }

    private async Task<EquipmentEditorSaveResult> SaveComputerDraftAsync(
        EquipmentComputerSnapshot snapshot,
        EquipmentEditorOpening opening,
        int? expectedModelId)
    {
        if (!IsCurrent(opening) || CurrentUser is not { } actor || _activeEditor is not { } editor ||
            ActiveDraftScope != ComputerDraftScope || snapshot.EquipmentId is not 0 && snapshot.EquipmentId != opening.EquipmentId)
        {
            Logger.LogWarning("ComputerSave rejected EquipmentId={EquipmentId}: context changed", snapshot.EquipmentId);
            return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Запись не отправлена.");
        }
        var version = ++_computerVersion;
        var attemptId = Guid.NewGuid();
        snapshot = FreezeChildValue(snapshot);
        snapshot.EquipmentId = opening.EquipmentId;
        try
        {
            DateTime businessDay = default;
            var date = snapshot.Date.Date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            var result = await SaveChildDraftAsync(
                "Сохранение конфигурации компьютера", "Конфигурация компьютера сохранена.", new { Date = date },
                () => Service.SaveComputerAsync(snapshot, businessDay, expectedModelId, actor.Id, _pageCts.Token),
                RefreshComputerAsync, () => IsCurrent(opening) && version == _computerVersion && CurrentUser?.Id == actor.Id &&
                    ReferenceEquals(_activeEditor, editor),
                async () =>
                {
                    businessDay = (await GetClientNowAsync()).Date;
                    return new { Date = date };
                }, attemptId: attemptId, commandKind: snapshot.ExpectedExisting ? EquipmentFormService.UpdateComputerCommand :
                    EquipmentFormService.InsertComputerCommand);
            Logger.LogInformation("ComputerSave result EquipmentId={EquipmentId}, CanClose={CanClose}, CanRetry={CanRetry}, Message={Message}",
                snapshot.EquipmentId, result.CanClose, result.CanRetry, result.Message);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ComputerSave failed EquipmentId={EquipmentId}", snapshot.EquipmentId);
            throw;
        }
    }

    private async Task RefreshComputerAsync()
    {
        if (_model is null)
            return;
        var version = _computerVersion;
        var equipmentId = _model.Id;
        var history = await Service.GetComputerHistoryAsync(equipmentId);
        if (_disposed || version != _computerVersion || _model.Id != equipmentId)
            return;
        _computerHistory = history.ToList();
        await RefreshLocationDisplayDatesAsync();
    }

    private async Task OpenSimDialogAsync()
    {
        if (_model is null || !CanEditSim || _saving)
            return;
        var opening = CaptureEditorOpening();
        var existing = _simCards.SingleOrDefault();
        var currentEmployeeId = _responsibles.LastOrDefault(item => item.ToUtc is null)?.EmployeeId;
        DateTime? localActivation = null;
        if (existing is not null)
        {
            var utc = DateTime.SpecifyKind(existing.ActivatedAtUtc, DateTimeKind.Utc);
            var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
            localActivation = utc.AddMinutes(offset);
        }
        if (!IsCurrent(opening))
            return;
        var clientNow = await GetClientNowAsync();
        if (!IsCurrent(opening))
            return;
        var parameters = new DialogParameters<EquipmentSimDialog>
        {
            { dialog => dialog.EquipmentId, opening.EquipmentId },
            { dialog => dialog.HasExisting, existing is not null },
            { dialog => dialog.Original, existing?.CaptureOriginal() },
            { dialog => dialog.Now, clientNow },
            { dialog => dialog.GetNow, (Func<Task<DateTime>>)GetClientNowAsync },
            { dialog => dialog.PhoneNumber, existing?.PhoneNumber ?? string.Empty },
            { dialog => dialog.ActivatedDate, localActivation?.Date },
            { dialog => dialog.ActivatedTime, localActivation?.TimeOfDay },
            { dialog => dialog.ContractId, existing?.ContractId },
            { dialog => dialog.PersonalDataPersonId, existing?.PersonalDataPersonId },
            { dialog => dialog.CurrentEmployeeId, currentEmployeeId },
            { dialog => dialog.Save, (Func<EquipmentSimDialogResult, Task<EquipmentEditorSaveResult>>)(value => SaveSimDraftAsync(value, opening)) },
        };
        var expectedExisting = existing is not null;
        (string State, EquipmentSimSecrets Secrets, int CustomerType)? pendingReset = null;
        try
        {
            await ShowDialogAsync<EquipmentSimDialog>(
                "SIM-карта", parameters, new DialogOptionsEx { DragMode = MudDialogDragMode.Simple },
                openingContext: opening, presence: expectedExisting ? Service.CreateSimPresence(opening.EquipmentId) : null,
                readFreshCancellationState: ReadSimCancellationStateAsync, newCancellationRecord: !expectedExisting,
                acceptFreshCancellationState: (component, state) =>
                {
                    if (component is EquipmentSimDialog editor && pendingReset is { } pending && pending.State == state)
                        editor.AcceptFreshResetSecrets(pending.Secrets, pending.CustomerType);
                    pendingReset = null;
                },
                discardFreshCancellationState: () => pendingReset = null);
        }
        finally { pendingReset = null; }

        async Task<string?> ReadSimCancellationStateAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            pendingReset = null;
            if (equipmentId != opening.EquipmentId || !IsCurrent(opening) || CurrentUser?.Id != employeeId)
                return null;
            var reset = await Service.ReadSimEditorResetAsync(equipmentId, expectedExisting, employeeId, token);
            if (reset is not { } fresh || !IsCurrent(opening) || CurrentUser?.Id != employeeId ||
                fresh.Snapshot.EquipmentId != equipmentId ||
                expectedExisting && string.IsNullOrEmpty(fresh.Snapshot.EditorVersion))
                return null;

            DateTime? activated = null;
            if (expectedExisting)
            {
                var utc = DateTime.SpecifyKind(fresh.Snapshot.ActivatedAtUtc, DateTimeKind.Utc);
                var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", token, utc.ToString("O"));
                if (!IsCurrent(opening) || CurrentUser?.Id != employeeId) return null;
                activated = utc.AddMinutes(offset);
            }

            var state = JsonSerializer.Serialize(new
            {
                EquipmentId = equipmentId,
                HasExisting = expectedExisting,
                Original = expectedExisting ? fresh.Snapshot.CaptureOriginal() : null,
                OriginalVersion = fresh.Snapshot.EditorVersion,
                PhoneNumber = fresh.Snapshot.PhoneNumber ?? string.Empty,
                ActivatedDate = activated?.Date,
                ActivatedTime = activated?.TimeOfDay,
                ContractId = fresh.Snapshot.ContractId,
                PersonalDataPersonId = fresh.Snapshot.PersonalDataPersonId,
                CurrentEmployeeId = currentEmployeeId,
            });
            pendingReset = (state, fresh.Snapshot.Secrets, fresh.CustomerType);
            return state;
        }
    }

    private async Task<EquipmentEditorSaveResult> SaveSimDraftAsync(
        EquipmentSimDialogResult value,
        EquipmentEditorOpening opening)
    {
        if (!IsCurrent(opening) || CurrentUser is not { } actor ||
            _activeEditor is not { } editor || ActiveDraftScope != SimDraftScope)
            return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Запись не отправлена.");
        if (value.Mode is not (EquipmentSimWriteMode.Add or EquipmentSimWriteMode.Edit) ||
            (value.Mode == EquipmentSimWriteMode.Edit && string.IsNullOrWhiteSpace(value.OriginalVersion)) ||
            (value.Mode == EquipmentSimWriteMode.Add && value.OriginalVersion is not null))
            return EquipmentEditorSaveResult.Reject("Исходное состояние SIM не подтверждено. Сверьте актуальные данные.");
        var version = ++_simVersion;
        int? duplicateId;
        DateTime activatedUtc;
        try
        {
            duplicateId = await Service.GetDuplicateSimIdAsync(opening.EquipmentId, value.PhoneNumber);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            return EquipmentEditorSaveResult.Reject("Не удалось проверить дубликат SIM. Запись не отправлена.");
        }
        if (!IsCurrent(opening) || version != _simVersion)
            return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Запись не отправлена.");
        if (duplicateId is not null &&
            !await ConfirmAsync("Другая SIM с тем же номером",
                $"Списать SIM #{duplicateId} с тем же номером на момент активации текущей?"))
            return EquipmentEditorSaveResult.Reject("Списание другой SIM отменено. Введённые данные сохранены; подтвердите операцию или измените номер.");
        if (!IsCurrent(opening) || version != _simVersion)
            return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Запись не отправлена.");
        try
        {
            activatedUtc = await ConvertLocalToUtcAsync(value.ActivatedLocal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return EquipmentEditorSaveResult.Reject($"Не удалось преобразовать дату активации. Запись не отправлена. {ex.Message}");
        }
        if (!IsCurrent(opening) || version != _simVersion)
            return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Запись не отправлена.");
        var attemptId = Guid.NewGuid();
        var sim = new EquipmentSimCard
        {
            EquipmentId = opening.EquipmentId,
            PhoneNumber = value.PhoneNumber,
            ActivatedAtUtc = activatedUtc,
            ContractId = value.ContractId,
            PersonalDataPersonId = value.PersonalDataPersonId,
            Secrets = FreezeChildValue(value.Secrets),
        };
        return await SaveChildDraftAsync(
            "Сохранение SIM", "Сведения SIM сохранены.",
            new { DuplicateEquipmentId = duplicateId },
            () => Service.SaveSimAsync(sim, value.Mode, value.OriginalVersion, duplicateId, actor.Id, _pageCts.Token),
            RefreshSimAsync, () => IsCurrent(opening) && version == _simVersion && CurrentUser?.Id == actor.Id &&
                ReferenceEquals(_activeEditor, editor), attemptId: attemptId,
            commandKind: value.Mode == EquipmentSimWriteMode.Add ? EquipmentFormService.InsertSimCommand : EquipmentFormService.UpdateSimCommand);
    }

    private async Task DeleteSimAsync()
    {
        if (_model is null || CurrentUser is not { } actor || !CanEditSim || _saving || _activeEditor is not null ||
            _simCards.SingleOrDefault() is not { } existing)
            return;

        var opening = CaptureEditorOpening();
        var openingVersion = _simVersion;
        string? expectedVersion;
        try
        {
            var snapshot = await Service.GetSimEditorSnapshotAsync(opening.EquipmentId);
            if (!IsCurrent(opening) || openingVersion != _simVersion) return;
            if (snapshot?.EditorVersion is null || snapshot.CaptureOriginal() != existing.CaptureOriginal())
            {
                _validationMessage = "Сведения SIM изменились. Сверьте актуальные данные перед удалением.";
                return;
            }
            expectedVersion = snapshot.EditorVersion;
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (IsCurrent(opening) && openingVersion == _simVersion)
                _validationMessage = "Не удалось подтвердить исходные сведения SIM. Удаление не отправлено.";
            return;
        }

        if (!await ConfirmAsync("Удаление сведений SIM", "Удалить сведения SIM без удаления самого оборудования?") ||
            !IsCurrent(opening) || openingVersion != _simVersion || _activeEditor is not null || CurrentUser?.Id != actor.Id)
            return;

        var version = ++_simVersion;
        var attemptId = Guid.NewGuid();
        bool Current() => IsCurrent(opening) && version == _simVersion && CurrentUser?.Id == actor.Id && _activeEditor is null;
        var result = await SaveChildDraftAsync(
            "Удаление сведений SIM", "Сведения SIM удалены.",
            new { DuplicateEquipmentId = (int?)null },
            () => Service.DeleteSimAsync(opening.EquipmentId, expectedVersion, actor.Id, _pageCts.Token),
            RefreshSimAsync, Current, attemptId: attemptId, commandKind: EquipmentFormService.DeleteSimCommand);
        if (Current() && !result.CanClose && result.Message is not null)
        {
            _validationMessage = result.Message;
            StateHasChanged();
        }
    }

    private async Task RefreshSimAsync()
    {
        if (_model is null)
            return;
        var version = _simVersion;
        var equipmentId = _model.Id;
        var simCards = await Service.GetSimCardsAsync(equipmentId);
        if (_disposed || version != _simVersion || _model.Id != equipmentId)
            return;
        _simCards = simCards.ToList();
        UpdateOptions();
        await RefreshLocationDisplayDatesAsync();
        await RefreshSimServicesAsync();
    }

    private async Task OpenSimServiceDialogAsync(EquipmentSimService? existing)
    {
        var sim = _simCards.SingleOrDefault();
        if (_model is null || sim is null || !CanEditSim || _saving)
            return;
        var opening = CaptureEditorOpening();
        var openingPhone = sim.PhoneNumber;
        var value = existing is null
            ? new EquipmentSimService { EquipmentId = opening.EquipmentId, PhoneNumber = sim.PhoneNumber }
            : new EquipmentSimService
            {
                Id = existing.Id,
                EquipmentId = opening.EquipmentId,
                PhoneNumber = sim.PhoneNumber,
                ChangedAt = existing.ChangedAt,
                ServiceTypeId = existing.ServiceTypeId,
                ServiceName = existing.ServiceName,
                DateFrom = existing.DateFrom,
                DateTo = existing.DateTo,
                ActivationCost = existing.ActivationCost,
                DailyCost = existing.DailyCost,
                MonthlyCost = existing.MonthlyCost,
            };
        var clientNow = await GetClientNowAsync();
        if (!IsCurrent(opening))
            return;
        var parameters = new DialogParameters<EquipmentSimServiceDialog>
        {
            { dialog => dialog.Value, value },
            { dialog => dialog.Types, _simServiceTypes },
            { dialog => dialog.Today, clientNow.Date },
            { dialog => dialog.GetToday, async () => (await GetClientNowAsync()).Date },
            { dialog => dialog.Save, (Func<EquipmentSimService, Task<EquipmentEditorSaveResult>>)(service => SaveSimServiceDraftAsync(openingPhone, service)) },
        };
        await ShowDialogAsync<EquipmentSimServiceDialog>("Дополнительная услуга SIM", parameters,
            draftKey: existing?.Id.ToString(), openingContext: opening,
            presence: existing is not null ? Service.CreateSimServicePresence(opening.EquipmentId, existing.Id) : null,
            readFreshCancellationState: ReadResetAsync, newCancellationRecord: existing is null);

        async Task<string?> ReadResetAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            var day = (await GetClientNowAsync()).Date;
            token.ThrowIfCancellationRequested();
            var fresh = await Service.ReadSimServiceEditorResetAsync(equipmentId, existing?.Id ?? 0,
                openingPhone, day, employeeId, token);
            return fresh is null ? null : JsonSerializer.Serialize(new { Value = fresh });
        }
    }

    private Task<EquipmentEditorSaveResult> SaveSimServiceDraftAsync(
        string phoneNumber, EquipmentSimService service)
    {
        if (_model is not { } model || CurrentUser is not { } actor || _activeEditor is not { } editor ||
            service.Id < 0 || ActiveDraftScope != SimServiceDraftScope(service.Id))
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Контекст дополнительной услуги SIM изменился."));
        var version = ++_simServiceVersion;
        var pageVersion = _operationVersion;
        var equipmentId = model.Id;
        var attemptId = Guid.NewGuid();
        service = FreezeChildValue(service);
        service.EquipmentId = equipmentId;
        service.PhoneNumber = phoneNumber;
        DateTime businessDay = default;
        return SaveChildDraftAsync(
            "Сохранение дополнительной услуги SIM", "Дополнительная услуга SIM сохранена.", new { ServiceId = service.Id },
            () => Service.SaveSimServiceAsync(service, businessDay, actor.Id, _pageCts.Token), RefreshSimServicesAsync,
            () => IsCurrent(pageVersion, equipmentId) && version == _simServiceVersion && _model?.Id == equipmentId &&
                CurrentUser?.Id == actor.Id && ReferenceEquals(_activeEditor, editor),
            async () =>
            {
                businessDay = (await GetClientNowAsync()).Date;
                return new { ServiceId = service.Id };
            }, attemptId: attemptId, commandKind: service.Id == 0 ? EquipmentFormService.InsertSimServiceCommand :
                EquipmentFormService.UpdateSimServiceCommand);
    }

    private async Task OnSimServiceFiltersChangedAsync()
    {
        var model = _model;
        var equipmentId = model?.Id;
        var version = ++_simServiceVersion;
        try
        {
            await RefreshSimServicesAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _simServiceVersion && ReferenceEquals(model, _model) &&
                equipmentId is not null && _model.Id == equipmentId)
                _validationMessage = $"Не удалось обновить услуги SIM. Прежние данные сохранены; повторите чтение. {ex.Message}";
        }
        if (!_disposed && version == _simServiceVersion && ReferenceEquals(model, _model))
            StateHasChanged();
    }

    private Task RefreshSimServicesAsync() => RefreshSimServicesForDayAsync(null);

    private async Task RefreshSimServicesForDayAsync(DateTime? observedDay)
    {
        if (_model is not { } model)
            return;
        var version = _simServiceVersion;
        var operationVersion = _operationVersion;
        var businessToday = observedDay ?? (await GetClientNowAsync()).Date;

        bool Current() => IsCurrent(operationVersion, model.Id) && ReferenceEquals(model, _model) &&
            version == _simServiceVersion;

        if (!Current())
            return;
        var services = await Service.GetSimServicesAsync(
            model.Id, _showServiceHistory, _showCompanyRequiredServices, businessToday);
        if (!Current())
            return;
        _clientToday = businessToday;
        _simServicesDay = businessToday;
        _simServices = services.ToList();
        await RefreshLocationDisplayDatesAsync();
    }

    private async Task OpenLicenseDialogAsync()
    {
        if (_model is not { } model || CurrentUser is not { } actor || !_canEditLicense || _saving)
            return;
        var version = _operationVersion;
        if (!IsCurrentSave(model, version) || CurrentUser?.Id != actor.Id || !_canEditLicense || _saving)
            return;
        var existing = _licenses.SingleOrDefault();
        var value = existing is null
            ? new EquipmentLicense { EquipmentId = _model.Id }
            : new EquipmentLicense
            {
                EquipmentId = _model.Id,
                ExpectedExisting = true,
                ChangedAt = existing.ChangedAt,
                LicenseTypeId = existing.LicenseTypeId,
                LicenseTypeName = existing.LicenseTypeName,
                LicenseeNumber = existing.LicenseeNumber,
                VerificationUrl = existing.VerificationUrl,
                ServerCount = existing.ServerCount,
                ClientCount = existing.ClientCount,
                DateFrom = existing.DateFrom,
                DateTo = existing.DateTo,
                Distribution = existing.Distribution,
            };
        var parameters = new DialogParameters<EquipmentLicenseDialog>
        {
            { dialog => dialog.Value, value },
            { dialog => dialog.Types, _licenseTypes },
            { dialog => dialog.Save, SaveLicenseDraftAsync },
        };
        await ShowDialogAsync<EquipmentLicenseDialog>("Лицензия", parameters,
            presence: model.Id > 0 && existing is not null ? Service.CreateLicensePresence(model.Id) : null,
            readFreshCancellationState: ReadLicenseCancellationStateAsync, newCancellationRecord: existing is null);

        async Task<string?> ReadLicenseCancellationStateAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            var fresh = await Service.ReadLicenseEditorResetAsync(equipmentId, existing is not null, employeeId, token);
            return fresh is null ? null : JsonSerializer.Serialize(new { Value = fresh });
        }
    }

    private Task<EquipmentEditorSaveResult> SaveLicenseDraftAsync(EquipmentLicense license)
    {
        if (_model is not { } model || CurrentUser is not { } actor || !_canEditLicense ||
            _activeEditor is not { } editor || ActiveDraftScope != LicenseDraftScope)
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Контекст лицензии изменился. Откройте редактор заново."));
        var version = ++_licenseVersion;
        var pageVersion = _operationVersion;
        var equipmentId = model.Id;
        var attemptId = Guid.NewGuid();
        license = FreezeChildValue(license);
        license.EquipmentId = equipmentId;
        var licenseType = _licenseTypes.FirstOrDefault(item => item.Id == license.LicenseTypeId);
        return SaveChildDraftAsync(
            "Сохранение лицензии", "Лицензия сохранена.", new { license.ExpectedExisting },
            () => Service.SaveLicenseAsync(license, licenseType, actor.Id, _pageCts.Token),
            RefreshLicensesAsync,
            () => IsCurrent(pageVersion, equipmentId) && version == _licenseVersion && _model?.Id == equipmentId &&
                CurrentUser?.Id == actor.Id && ReferenceEquals(_activeEditor, editor),
            attemptId: attemptId, commandKind: license.ExpectedExisting ? EquipmentFormService.UpdateLicenseCommand :
                EquipmentFormService.InsertLicenseCommand);
    }

    private async Task RefreshLicensesAsync()
    {
        if (_model is null)
            return;
        var version = _licenseVersion;
        var equipmentId = _model.Id;
        var licenses = await Service.GetLicensesAsync(equipmentId);
        if (_disposed || version != _licenseVersion || _model.Id != equipmentId)
            return;
        _licenses = licenses.ToList();
        await RefreshLocationDisplayDatesAsync();
        UpdateOptions();
    }

    private async Task OpenSoftwareDialogAsync(EquipmentSoftwareInstallation? existing)
    {
        if (_model is null || !_canEdit || _saving)
            return;
        var value = existing is null
            ? new EquipmentSoftwareInstallation { EquipmentId = _model.Id }
            : new EquipmentSoftwareInstallation
            {
                Id = existing.Id,
                EquipmentId = _model.Id,
                ChangedAt = existing.ChangedAt,
                SoftwareId = existing.SoftwareId,
                SoftwareName = existing.SoftwareName,
                InstalledAt = existing.InstalledAt,
                UninstalledAt = existing.UninstalledAt,
                Installed = existing.Installed,
                Distribution = existing.Distribution,
                Notes = existing.Notes,
            };
        var parameters = new DialogParameters<EquipmentSoftwareDialog>
        {
            { dialog => dialog.Value, value },
            { dialog => dialog.Save, SaveSoftwareDraftAsync },
        };
        await ShowDialogAsync<EquipmentSoftwareDialog>("Установленное ПО", parameters,
            draftKey: existing?.Id.ToString(),
            presence: _model.Id > 0 && existing is { Id: > 0 } ? Service.CreateSoftwarePresence(_model.Id, existing.Id) : null,
            readFreshCancellationState: ReadSoftwareCancellationStateAsync, newCancellationRecord: existing is null);

        async Task<string?> ReadSoftwareCancellationStateAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            var fresh = await Service.ReadSoftwareEditorResetAsync(equipmentId, existing?.Id ?? 0, employeeId, token);
            return fresh is null ? null : JsonSerializer.Serialize(new { Value = fresh });
        }
    }

    private Task<EquipmentEditorSaveResult> SaveSoftwareDraftAsync(EquipmentSoftwareInstallation installation)
    {
        if (_model is not { } model || CurrentUser is not { } actor || _activeEditor is not { } editor ||
            installation.Id < 0 || ActiveDraftScope != SoftwareDraftScope(installation.Id))
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Контекст установки ПО изменился. Откройте редактор заново."));
        var version = ++_softwareVersion;
        var pageVersion = _operationVersion;
        var equipmentId = model.Id;
        var attemptId = Guid.NewGuid();
        installation = FreezeChildValue(installation);
        installation.EquipmentId = equipmentId;
        return SaveChildDraftAsync(
            "Сохранение установленного ПО", "Установленное ПО сохранено.", new { InstallationId = installation.Id },
            () => Service.SaveSoftwareInstallationAsync(installation, actor.Id, _pageCts.Token), RefreshSoftwareAsync,
            () => IsCurrent(pageVersion, equipmentId) && version == _softwareVersion && _model?.Id == equipmentId &&
                CurrentUser?.Id == actor.Id && ReferenceEquals(_activeEditor, editor),
            attemptId: attemptId, commandKind: installation.Id == 0 ? EquipmentFormService.InsertSoftwareCommand : EquipmentFormService.UpdateSoftwareCommand);
    }

    private async Task RefreshSoftwareAsync()
    {
        if (_model is null)
            return;
        var version = _softwareVersion;
        var equipmentId = _model.Id;
        var installations = await Service.GetSoftwareInstallationsAsync(equipmentId);
        if (_disposed || version != _softwareVersion || _model.Id != equipmentId)
            return;
        _softwareInstallations = installations.ToList();
        await RefreshLocationDisplayDatesAsync();
    }

    private async Task OpenAccountingDialogAsync(EquipmentAccounting? existing)
    {
        if (_model is null || !_canUseAccounting || _saving)
            return;
        var value = existing is null
            ? new EquipmentAccounting { EquipmentId = _model.Id, OwnerId = _model.OwnerId, DateFrom = DateTime.Today }
            : new EquipmentAccounting
            {
                Id = existing.Id,
                EquipmentId = _model.Id,
                OwnerId = existing.OwnerId,
                OwnerName = existing.OwnerName,
                IsMaterial = existing.IsMaterial,
                TenantId = existing.TenantId,
                TenantName = existing.TenantName,
                InventoryNumber = existing.InventoryNumber,
                DateFrom = existing.DateFrom,
                ReceiptDocumentId = existing.ReceiptDocumentId,
                DateTo = existing.DateTo,
                WriteOffDocumentId = existing.WriteOffDocumentId,
                Notes = existing.Notes,
                ChangedAt = existing.ChangedAt,
            };
        var openingOwnerId = value.OwnerId;
        var openingDate = value.DateFrom;
        var parameters = new DialogParameters<EquipmentAccountingDialog>
        {
            { dialog => dialog.Value, value },
            { dialog => dialog.Save, SaveAccountingDraftAsync },
        };
        await ShowDialogAsync<EquipmentAccountingDialog>(
            "Бухгалтерский учёт", parameters,
            new DialogOptionsEx { MaxWidth = MaxWidth.Medium, FullWidth = true },
            existing?.Id.ToString(),
            presence: _model.Id > 0 && existing is { Id: > 0 } ? Service.CreateAccountingPresence(_model.Id, existing.Id) : null,
            readFreshCancellationState: ReadAccountingCancellationStateAsync, newCancellationRecord: existing is null);

        async Task<string?> ReadAccountingCancellationStateAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            var fresh = await Service.ReadAccountingEditorResetAsync(equipmentId, existing?.Id ?? 0,
                employeeId, openingOwnerId, openingDate, token);
            return fresh is null ? null : JsonSerializer.Serialize(new { Value = fresh });
        }
    }

    private async Task<EquipmentEditorSaveResult> SaveAccountingDraftAsync(EquipmentAccounting accounting)
    {
        if (_model is not { } model || CurrentUser is not { } actor || !_canUseAccounting || _activeEditor is not { } editor ||
            accounting.Id < 0 || ActiveDraftScope != AccountingDraftScope(accounting.Id))
            return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Откройте форму повторно.");
        var version = ++_accountingVersion;
        var pageVersion = _operationVersion;
        var equipmentId = model.Id;
        var attemptId = Guid.NewGuid();
        accounting = FreezeChildValue(accounting);
        accounting.EquipmentId = equipmentId;
        return await SaveChildDraftAsync(
            "Сохранение бухгалтерского учёта", "Бухгалтерский учёт сохранён.", new { AccountingId = accounting.Id },
            () => Service.SaveAccountingAsync(accounting, actor.Id, _pageCts.Token), RefreshAccountingAsync,
            () => IsCurrent(pageVersion, equipmentId) && version == _accountingVersion && _model?.Id == equipmentId &&
                CurrentUser?.Id == actor.Id && ReferenceEquals(_activeEditor, editor),
            attemptId: attemptId, commandKind: accounting.Id == 0 ? EquipmentFormService.InsertAccountingCommand : EquipmentFormService.UpdateAccountingCommand);
    }

    private void ShowStatus(string message) => Snackbar.Add(message, Severity.Info, options =>
    {
        options.VisibleStateDuration = 4000;
        options.ShowCloseIcon = true;
    });

    private async Task<EquipmentEditorSaveResult> SaveChildDraftAsync(
        string operationName,
        string successMessage,
        object intent,
        Func<Task> write,
        Func<Task> refresh,
        Func<bool> contextIsCurrent,
        Func<Task<object>>? prepareIntent = null,
        Guid? attemptId = null,
        string? commandKind = null)
    {
        _saving = true;
        _validationMessage = null;
        var original = OriginalSnapshotModel();
        StateHasChanged();
        try
        {
            try
            {
                if (prepareIntent is not null) await prepareIntent();
                if (!contextIsCurrent())
                    return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Команда не отправлена.");
                await write();
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                return EquipmentEditorSaveResult.Reject($"Команда «{operationName}» не выполнена. Подробности — в сообщении базы данных.");
            }
            catch (Exception ex) when (ex is System.Data.SqlTypes.SqlTypeException or InvalidOperationException or UnauthorizedAccessException)
            {
                return EquipmentEditorSaveResult.Reject(ex.Message);
            }

            ShowStatus(successMessage);
            if (!contextIsCurrent())
                return EquipmentEditorSaveResult.RefreshFailed("Запись подтверждена, но контекст формы изменился. Обновите данные.");
            try
            {
                await RefreshMainAfterChildWriteAsync(original);
                await refresh();
                return EquipmentEditorSaveResult.Commit();
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
            {
                _validationMessage = $"{operationName}: запись подтверждена, но экран не обновлён. Повторите только обновление данных.";
                return EquipmentEditorSaveResult.RefreshFailed(_validationMessage);
            }
        }
        finally
        {
            if (!_disposed)
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private async Task DeleteAccountingAsync(EquipmentAccounting latest)
    {
        if (_model is not { } model || CurrentUser is not { } actor || !_canUseAccounting || _saving || _activeEditor is not null ||
            latest.Id <= 0 || _accounting.LastOrDefault()?.Id != latest.Id)
            return;
        var pageVersion = _operationVersion;
        var accountingId = latest.Id;
        var equipmentId = model.Id;
        if (!await ConfirmAsync("Удаление бухгалтерского учёта", "Удалить последнюю запись бухгалтерского учёта?") ||
            !IsCurrentSave(model, pageVersion) || CurrentUser?.Id != actor.Id || !_canUseAccounting || _saving ||
            _activeEditor is not null || _accounting.LastOrDefault()?.Id != accountingId)
            return;

        var version = ++_accountingVersion;
        var attemptId = Guid.NewGuid();
        bool Current() => IsCurrent(pageVersion, equipmentId) && version == _accountingVersion && _model?.Id == equipmentId &&
            CurrentUser?.Id == actor.Id && _activeEditor is null;
        var result = await SaveChildDraftAsync(
            "Удаление бухгалтерского учёта", "Запись бухгалтерского учёта удалена.",
            new { AccountingId = accountingId },
            () => Service.DeleteAccountingAsync(equipmentId, accountingId, actor.Id, _pageCts.Token),
            RefreshAccountingAsync, Current, attemptId: attemptId, commandKind: EquipmentFormService.DeleteAccountingCommand);
        if (Current() && !result.CanClose && result.Message is not null)
        {
            _validationMessage = result.Message;
            StateHasChanged();
        }
    }

    private async Task RefreshAccountingAsync()
    {
        if (_model is null)
            return;
        var preserveDraft = HasUnsavedDraft;
        var version = _accountingVersion;
        var equipmentId = _model.Id;
        var accounting = await Service.GetAccountingAsync(equipmentId);
        if (_disposed || version != _accountingVersion || _model.Id != equipmentId)
            return;
        _accounting = accounting.ToList();
        await RefreshLocationDisplayDatesAsync();
        if (_disposed || version != _accountingVersion || _model.Id != equipmentId)
            return;
        var current = _accounting.LastOrDefault(item => item.DateTo is null) ?? _accounting.LastOrDefault();
        if (current is not null)
        {
            if (_model.OwnerId == _originalOwnerId)
            {
                _model.OwnerId = current.OwnerId;
                _originalOwnerId = current.OwnerId;
            }
            if (_model.TenantId == _originalTenantId)
            {
                _model.TenantId = current.TenantId;
                _originalTenantId = current.TenantId;
            }
            if (_model.DocumentReceiptOuId == _originalReceiptDocumentId)
            {
                _model.DocumentReceiptOuId = current.ReceiptDocumentId;
                _originalReceiptDocumentId = current.ReceiptDocumentId;
            }
            if (_model.DocumentWriteOffId == _originalWriteOffDocumentId)
            {
                _model.DocumentWriteOffId = current.WriteOffDocumentId;
                _originalWriteOffDocumentId = current.WriteOffDocumentId;
            }
            _confirmedOwnerId = _model.OwnerId;
            SetDocumentOwner(_model.OwnerId);
        }
        var canUseAccounting = await Service.CanUseAccountingAsync(_model.OwnerId);
        if (_disposed || version != _accountingVersion || _model.Id != equipmentId)
            return;
        _canUseAccounting = canUseAccounting;
        if (!preserveDraft)
            _originalSnapshot = Snapshot();
    }

    private async Task OpenSimDetailAsync()
    {
        if (_model is null || !ShowSimDetail || _saving)
            return;
        var equipmentId = _model.Id;
        _saving = true;
        _validationMessage = null;
        try
        {
            var url = await Service.GetSimDetailUrlAsync(equipmentId, Configuration["URI_accounting_phone_form"]!);
            if (_disposed || _model is null || _model.Id != equipmentId)
                return;
            if (url is null)
            {
                _warningMessage = "Детализация недоступна: нет актуального ответственного или доступной SIM.";
                return;
            }
            await JS.InvokeVoidAsync("clayOpen", url, "equipment-sim-detail");
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed)
                _validationMessage = "Не удалось получить параметры детализации. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or UriFormatException)
        {
            if (!_disposed)
                _validationMessage = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private async Task OpenPhoneConfigAsync()
    {
        if (!ShowPhoneConfig || !_canViewPhoneConfig || _model is null || _saving)
            return;
        if (IsModified)
        {
            _validationMessage = "Сохраните оборудование перед просмотром конфигурации.";
            return;
        }
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(_model.MacAddress))
            errors.Add("Укажите MAC-адрес.");
        if (_model.PhonePoolId is null or <= 0)
            errors.Add("Выберите телефонный пул.");
        if (string.IsNullOrWhiteSpace(_selectedModel?.PhoneTemplate))
            errors.Add("Для модели не задан шаблон конфигурации.");
        if (errors.Count > 0)
        {
            _validationMessage = string.Join(" ", errors);
            return;
        }
        await JS.InvokeVoidAsync("clayOpen", $"/Equipment/PhoneConfigHandler.ashx?mac={Uri.EscapeDataString(_model.MacAddress!)}", "_blank");
    }

    private async Task OpenNetAsync(int id)
    {
        if (NetUrl(id) is string url)
            await JS.InvokeVoidAsync("clayOpen", url, "net-form");
    }

    private async Task OpenNetworkNameHistoryAsync()
    {
        if (_model is null || !ShowNetworkName)
            return;
        var parameters = new DialogParameters<EquipmentNetworkNameHistoryDialog>
        {
            { dialog => dialog.Items, _networkNameHistory },
            { dialog => dialog.FormatDate, utc => FormatDisplayDate(utc, true) },
        };
        await DialogService.ShowExAsync<EquipmentNetworkNameHistoryDialog>(
            "История сетевых имён", parameters,
            new DialogOptionsEx { MaxWidth = MaxWidth.Medium, FullWidth = true, DragMode = MudDialogDragMode.Simple });
    }

    private async Task SetNetworkAttachmentAsync(bool attach)
    {
        if (_model is not { } model || CurrentUser is not { } actor || !_canChangeNetwork || _saving || _activeEditor is not null)
            return;
        var equipmentId = model.Id;
        var editorVersion = _operationVersion;
        var expectedCount = _networkAttachmentCount;
        if (!attach && !await ConfirmAsync("Отключение сети", "Отключить оборудование от рабочей сети?"))
            return;
        if (!IsCurrentSave(model, editorVersion) || CurrentUser?.Id != actor.Id || _saving || _activeEditor is not null)
            return;
        if (IsModified)
        {
            await SaveAsync(false);
            if (IsModified || !_canChangeNetwork || _disposed || _model?.Id != equipmentId || CurrentUser?.Id != actor.Id ||
                _networkAttachmentCount != expectedCount)
                return;
        }
        var version = ++_networkVersion;
        editorVersion = _operationVersion;
        var attemptId = Guid.NewGuid();
        bool Current() => IsCurrent(editorVersion, equipmentId) && _networkVersion == version &&
            _model?.Id == equipmentId && CurrentUser?.Id == actor.Id;
        var result = await SaveChildDraftAsync(
            attach ? "Подключение к локальной сети" : "Отключение от локальной сети",
            attach ? "Оборудование подключено к рабочей сети." : "Оборудование отключено от рабочей сети.",
            new { EquipmentId = equipmentId, Attach = attach, ExpectedCount = expectedCount },
            () => Service.SetNetworkAttachmentAsync(equipmentId, actor.Id, attach, expectedCount, _pageCts.Token),
            async () =>
            {
                var count = await Service.GetNetworkAttachmentCountAsync(equipmentId);
                if (Current())
                    _networkAttachmentCount = count;
            },
            Current, attemptId: attemptId,
            commandKind: attach ? EquipmentFormService.AttachNetworkCommand : EquipmentFormService.DetachNetworkCommand);
        if (Current() && !result.CanClose && result.Message is not null)
        {
            _validationMessage = result.Message;
            StateHasChanged();
        }
    }

    private UpsEquipmentSelectDataSource UpsSource => Services.GetRequiredKeyedService<UpsEquipmentSelectDataSource>("ups");

    private async Task OpenUpsLinkDialogAsync()
    {
        if (_model is not { } model || CurrentUser is not { } actor || !_canManageUps || _saving)
            return;
        var version = _operationVersion;
        var result = await ShowDialogAsync<EquipmentUpsLinkDialog>(
            "Подключение UPS", new DialogParameters<EquipmentUpsLinkDialog>(),
            readFreshCancellationState: ReadResetAsync, newCancellationRecord: true);
        if (result is null || result.Canceled || result.Data is not int upsId ||
            !IsCurrentSave(model, version) || CurrentUser?.Id != actor.Id)
            return;
        await AddUpsLinkAsync(upsId);

        async Task<string?> ReadResetAsync(int equipmentId, int employeeId, CancellationToken token) =>
            await Service.CanResetUpsLinkEditorAsync(equipmentId, employeeId, token)
                ? JsonSerializer.Serialize(new { Value = (int?)null })
                : null;
    }

    private async Task AddUpsLinkAsync(int upsId)
    {
        if (_model is not { } model || CurrentUser is not { } actor || !_canManageUps || _saving || _activeEditor is not null)
            return;
        var version = ++_upsVersion;
        var pageVersion = _operationVersion;
        var equipmentId = model.Id;
        var attemptId = Guid.NewGuid();
        bool Current() => IsCurrent(pageVersion, equipmentId) && version == _upsVersion && _model?.Id == equipmentId &&
            CurrentUser?.Id == actor.Id && _activeEditor is null;
        var result = await SaveChildDraftAsync(
            "Подключение UPS", "UPS подключён.",
            new { UpsEquipmentId = upsId },
            () => Service.AddUpsLinkAsync(upsId, equipmentId, actor.Id, _pageCts.Token), RefreshUpsLinksAsync, Current,
            attemptId: attemptId, commandKind: EquipmentFormService.AddUpsLinkCommand);
        if (Current() && !result.CanClose && result.Message is not null)
        {
            _validationMessage = result.Message;
            StateHasChanged();
        }
    }

    private async Task OpenUpsDialogAsync(UpsServerLink link)
    {
        if (_model is null || !_canManageUps || _saving)
            return;
        var version = ++_upsVersion;
        var opening = CaptureEditorOpening();
        var draftKey = link.UpsEquipmentId.ToString();
        var restored = _activeEditor is { Kind: nameof(EquipmentUpsDialog) } editor && editor.Key == draftKey;
        EquipmentUpsOriginal? original;
        List<UpsServerLink> dialogServers;
        var dialogTests = new List<UpsTest>();
        try
        {
            var freshServers = await Service.GetUpsServersAsync(link.UpsEquipmentId);
            var freshTests = await Service.GetUpsTestsAsync(link.UpsEquipmentId);
            if (!IsCurrent(opening) || version != _upsVersion)
                return;
            if (restored)
            {
                original = _upsDraftOriginal is { Servers: not null, Tests: not null } snapshot &&
                    snapshot.UpsEquipmentId == link.UpsEquipmentId &&
                    _activeUpsServers is not null && _activeUpsTests is not null ? snapshot : null;
                dialogServers = _activeUpsServers ?? [];
                dialogTests = _activeUpsTests ?? [];
            }
            else
            {
                original = new EquipmentUpsOriginal(link.UpsEquipmentId, CloneUpsServers(freshServers),
                    freshTests.Select(test => CloneUpsTest(test, test.TestedAt)).ToList());
                dialogServers = CloneUpsServers(freshServers);
                foreach (var test in freshTests)
                {
                    var utc = DateTime.SpecifyKind(test.TestedAt, DateTimeKind.Utc);
                    var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
                    dialogTests.Add(CloneUpsTest(test, utc.AddMinutes(offset)));
                }
            }
            if (!IsCurrent(opening) || version != _upsVersion)
                return;
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrent(opening) && version == _upsVersion)
            {
                _validationMessage = "Не удалось загрузить подключение UPS. Повторите открытие диалога.";
                StateHasChanged();
            }
            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException or JSDisconnectedException)
        {
            return;
        }
        _activeUpsServers = dialogServers;
        _activeUpsTests = dialogTests;
        _upsDraftOriginal = original;
        var baselineError = original is null
            ? "В черновике отсутствуют исходные строки UPS. Проверьте локальный ввод и откройте свежий диалог для записи."
            : null;
        var parentScope = $"child:{nameof(EquipmentUpsDialog)}:{draftKey}:root";
        var restoreTestId = restored ? _activeEditor?.NestedTestId : null;
        if (restoreTestId is { } savedTest && _draftRegistration?.Restore.CancelledScopes?.Contains(UpsTestScope(parentScope, savedTest)) == true)
        {
            _activeEditor = _activeEditor! with { NestedTestId = null };
            _upsTestDraft = null;
            restoreTestId = null;
        }
        var parameters = new DialogParameters<EquipmentUpsDialog>
        {
            { dialog => dialog.UpsEquipmentId, link.UpsEquipmentId },
            { dialog => dialog.Servers, dialogServers },
            { dialog => dialog.Tests, dialogTests },
            { dialog => dialog.GetNow, (Func<Task<DateTime>>)GetClientNowAsync },
            { dialog => dialog.DraftChanged, PersistDraftAsync },
            { dialog => dialog.AdoptResetLists, (List<UpsServerLink> servers, List<UpsTest> tests) =>
                {
                    _activeUpsServers = servers;
                    _activeUpsTests = tests;
                } },
            { dialog => dialog.DraftScope, parentScope },
            { dialog => dialog.DraftOwnerClass, $"equipment-draft-dialog-{_draftOwner}" },
            { dialog => dialog.PublishTestDraft, state => PublishUpsTestDraftAsync(opening, parentScope, state) },
            { dialog => dialog.RestoreTestRaw, (scope, retry) => RestoreUpsTestRawAsync(opening, scope, retry) },
            { dialog => dialog.CanAcceptTest, scope => CanAcceptUpsTestAsync(opening, scope) },
            { dialog => dialog.CreateTestCancellation, (int id, UpsTest originalTest) =>
                CreateUpsTestCancellation(opening, parentScope, link.UpsEquipmentId, id, originalTest) },
            { dialog => dialog.FinishTestDraft, (id, accepted) => FinishUpsTestDraftAsync(opening, parentScope, id, accepted) },
            { dialog => dialog.RestoreTestId, restoreTestId },
            { dialog => dialog.RestoredTestDraft, restored ? _upsTestDraft : null },
            { dialog => dialog.BaselineError, baselineError },
            { dialog => dialog.Save, (Func<EquipmentUpsDialogResult, Task<EquipmentEditorSaveResult>>)(value =>
                original is null ? Task.FromResult(EquipmentEditorSaveResult.Reject(baselineError!)) :
                !IsCurrent(opening) || version != _upsVersion
                    ? Task.FromResult(EquipmentEditorSaveResult.Reject("Контекст UPS изменился. Запись не отправлена."))
                    : SaveUpsDraftAsync(link, original.Servers, original.Tests, value)) },
        };
        if (original is not null)
            parameters.Add(dialog => dialog.CreateTestPresenceTarget,
                id => BindChildPresence(Service.CreateUpsTestPresence(link.UpsEquipmentId, opening.EquipmentId, id), opening));
        EquipmentUpsOriginal? resetOriginal = null;
        List<UpsServerLink>? resetServers = null;
        List<UpsTest>? resetTests = null;
        string? resetState = null;
        await ShowDialogAsync<EquipmentUpsDialog>("Подключение UPS", parameters,
            draftKey: draftKey, openingContext: opening,
            presence: original is not null ? Service.CreateUpsPresence(link.UpsEquipmentId, opening.EquipmentId) : null,
            readFreshCancellationState: ReadResetAsync,
            acceptFreshCancellationState: (component, replacement) =>
            {
                if (component is EquipmentUpsDialog dialog && replacement == resetState && resetOriginal is not null)
                {
                    original = _upsDraftOriginal = resetOriginal;
                    baselineError = dialog.BaselineError = null;
                }
            },
            prepareCancellationEnvelope: (parent, replacement) =>
            {
                if (_activeEditor?.NestedTestId is not null || _upsTestDraft is not null)
                    return false;

                parent.ActiveEditorState = null;
                if (replacement is null)
                {
                    parent.ActiveUpsOriginal = null;
                    parent.ActiveUpsServers = null;
                    parent.ActiveUpsTests = null;
                    return true;
                }
                if (replacement != resetState || resetOriginal is null || resetServers is null || resetTests is null)
                    return false;
                parent.ActiveUpsOriginal = resetOriginal;
                parent.ActiveUpsServers = resetServers;
                parent.ActiveUpsTests = resetTests;
                return true;
            });

        async Task<string?> ReadResetAsync(int equipmentId, int employeeId, CancellationToken token)
        {
            var fresh = await Service.ReadUpsEditorResetAsync(link.UpsEquipmentId, equipmentId, employeeId, token);
            if (fresh is null || !IsCurrent(opening) || version != _upsVersion)
                return null;

            var servers = CloneUpsServers(fresh.Servers);
            var tests = new List<UpsTest>();
            foreach (var test in fresh.Tests)
            {
                var local = await ConvertUtcToLocalAsync(test.TestedAt);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(opening) || version != _upsVersion)
                    return null;
                tests.Add(CloneUpsTest(test, local));
            }
            resetOriginal = new EquipmentUpsOriginal(link.UpsEquipmentId, CloneUpsServers(fresh.Servers),
                fresh.Tests.Select(test => CloneUpsTest(test, test.TestedAt)).ToList());
            resetServers = servers;
            resetTests = tests;
            return resetState = JsonSerializer.Serialize(new { Servers = servers, Tests = tests });
        }
    }

    private async Task<EquipmentEditorSaveResult> SaveUpsDraftAsync(
        UpsServerLink link,
        IReadOnlyCollection<UpsServerLink> originalServers,
        IReadOnlyCollection<UpsTest> originalTests,
        EquipmentUpsDialogResult value)
    {
        if (_model is not { } model || CurrentUser is not { } actor || _activeEditor is not { } editor ||
            link.ServerEquipmentId != model.Id || ActiveDraftScope != UpsDraftScope(link.UpsEquipmentId))
            return EquipmentEditorSaveResult.Reject("Контекст подключения UPS изменился. Откройте редактор заново.");
        if (editor.NestedTestId is not null)
            return EquipmentEditorSaveResult.Reject("Сначала завершите восстановление вложенного теста UPS.");
        var equipmentId = model.Id;
        var upsId = link.UpsEquipmentId;
        var version = _upsVersion;
        var pageVersion = _operationVersion;
        var attemptId = Guid.NewGuid();
        bool Current() => IsCurrent(pageVersion, equipmentId) && version == _upsVersion && _model?.Id == equipmentId &&
            CurrentUser?.Id == actor.Id && ReferenceEquals(_activeEditor, editor);
        var priorServers = CloneUpsServers(originalServers);
        var priorTests = originalTests.Select(item => CloneUpsTest(item, item.TestedAt)).ToList();
        var currentServers = CloneUpsServers(value.Servers);
        var sentTests = value.Tests.Select(item => CloneUpsTest(item, item.TestedAt)).ToList();
        var currentTests = new List<UpsTest>();
        var originalTestsById = priorTests.ToDictionary(item => item.Id);
        try
        {
            foreach (var test in sentTests)
            {
                if (originalTestsById.TryGetValue(test.Id, out var original) &&
                    test.TestedAt == await ConvertUtcToLocalAsync(original.TestedAt))
                {
                    currentTests.Add(CloneUpsTest(test, original.TestedAt));
                    continue;
                }
                currentTests.Add(CloneUpsTest(test, await ConvertLocalToUtcAsync(test.TestedAt)));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return EquipmentEditorSaveResult.Reject($"Не удалось преобразовать время тестов UPS. Запись не отправлена. {ex.Message}");
        }
        if (!Current())
            return EquipmentEditorSaveResult.Reject("Контекст подключения UPS изменился. Запись не отправлена.");
        return await SaveChildDraftAsync(
            "Сохранение подключения UPS", "Подключение UPS сохранено.",
            new { UpsEquipmentId = upsId },
            () => Service.SaveUpsDetailsAsync(
                upsId, equipmentId, priorServers, currentServers, priorTests, currentTests,
                actor.Id, _pageCts.Token),
            RefreshUpsLinksAsync, Current,
            attemptId: attemptId, commandKind: EquipmentFormService.SaveUpsDetailsCommand);
    }

    private async Task RefreshUpsLinksAsync()
    {
        if (_model is not { } model)
            return;
        var version = _upsVersion;
        var pageVersion = _operationVersion;
        var links = await Service.GetUpsLinksAsync(model.Id);
        if (!IsCurrentSave(model, pageVersion) || version != _upsVersion)
            return;
        _upsLinks = links.ToList();
    }

    private static List<UpsServerLink> CloneUpsServers(IEnumerable<UpsServerLink> source) => source.Select(item => new UpsServerLink
    {
        UpsEquipmentId = item.UpsEquipmentId,
        ServerEquipmentId = item.ServerEquipmentId,
        TurnOffConfigured = item.TurnOffConfigured,
        ServerName = item.ServerName,
        EquipmentTypeId = item.EquipmentTypeId,
        EquipmentName = item.EquipmentName,
        ChangedByName = item.ChangedByName,
        ChangedAt = item.ChangedAt,
    }).ToList();

    private static UpsTest CloneUpsTest(UpsTest item, DateTime testedAt) => new()
    {
        Id = item.Id,
        UpsEquipmentId = item.UpsEquipmentId,
        TestedAt = testedAt,
        BatteryTime = item.BatteryTime,
        Servers = item.Servers,
        Issues = item.Issues,
        ChangedByName = item.ChangedByName,
        ChangedAt = item.ChangedAt,
    };

    private async Task RefreshLocationDisplayDatesAsync()
    {
        var operationVersion = _operationVersion;
        var equipmentId = _model?.Id;
        var localDates = await LoadLocalDatesAsync(GetDisplayDates(
            _locations, _responsibles, _simCards, _networkNameHistory, _lastInventory,
            _phones, _computerHistory, _simServices, _licenses, _softwareInstallations, _accounting));
        if (_disposed || operationVersion != _operationVersion ||
            equipmentId is not null && _model?.Id != equipmentId)
            return;
        _localDates.Clear();
        foreach (var item in localDates)
            _localDates[item.Key] = item.Value;
    }

    private static IEnumerable<DateTime> GetDisplayDates(
        IEnumerable<EquipmentLocationPeriod> locations,
        IEnumerable<EquipmentResponsiblePeriod> responsibles,
        IEnumerable<EquipmentSimCard> simCards,
        IEnumerable<EquipmentNetworkNameHistory> networkNameHistory,
        EquipmentActualInventory? lastInventory,
        IEnumerable<EquipmentPhoneLink> phones,
        IEnumerable<EquipmentComputerSnapshot> computerHistory,
        IEnumerable<EquipmentSimService> simServices,
        IEnumerable<EquipmentLicense> licenses,
        IEnumerable<EquipmentSoftwareInstallation> softwareInstallations,
        IEnumerable<EquipmentAccounting> accounting) =>
        locations.SelectMany(item => new[] { item.FromUtc, item.ChangedAtUtc })
            .Concat(responsibles.SelectMany(item => new[] { item.FromUtc, item.ToUtc ?? default, item.ChangedAtUtc }))
            .Concat(simCards.SelectMany(item => new[] { item.ActivatedAtUtc, item.ChangedAt }))
            .Concat(networkNameHistory.Select(item => item.ChangedAtUtc))
            .Concat(lastInventory is null ? Array.Empty<DateTime>() : new[] { lastInventory.CheckedAt, lastInventory.ChangedAt })
            .Concat(phones.Select(item => item.ChangedAt))
            .Concat(computerHistory.Select(item => item.ChangedAt))
            .Concat(simServices.Select(item => item.ChangedAt))
            .Concat(licenses.Select(item => item.ChangedAt))
            .Concat(softwareInstallations.Select(item => item.ChangedAt))
            .Concat(accounting.Select(item => item.ChangedAt))
            .Where(item => item != default)
            .Distinct();

    private async Task<Dictionary<DateTime, DateTime>> LoadLocalDatesAsync(IEnumerable<DateTime> dates)
    {
        var result = new Dictionary<DateTime, DateTime>();
        foreach (var utc in dates)
        {
            var offset = await JS.InvokeAsync<int>(
                "clayGridTimeZone.getOffsetMinutes", DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O"));
            if (_disposed)
                break;
            result[utc] = utc.AddMinutes(offset);
        }
        return result;
    }

    private async Task<DateTime> ConvertUtcToLocalAsync(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
        return utc.AddMinutes(offset);
    }

    private async Task<DateTime> ConvertLocalToUtcAsync(DateTime local)
    {
        var milliseconds = await JS.InvokeAsync<long>(
            "clayGridTimeZone.localDateTimeToUtcMilliseconds", _pageCts.Token,
            local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            local.ToString(@"HH\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture));
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    private string FormatDisplayDate(DateTime utc, bool includeTime)
    {
        if (utc == default)
            return string.Empty;
        return _localDates.TryGetValue(utc, out var local)
            ? local.ToString(includeTime ? "dd.MM.yyyy HH:mm:ss" : "dd.MM.yyyy")
            : string.Empty;
    }

    private void UpdateOptions()
    {
        if (_model is null)
            return;
        _snOptions.Required = HasSelectedModel && _selectedType?.IsSerialNumberRequired > 0;
        foreach (var options in new[] { _typeOptions, _modelOptions, _phonePoolOptions, _phoneOptions, _ownerOptions, _receiptDocumentOptions, _writeOffDocumentOptions, _shipperOptions, _tenantOptions })
            options.ReadOnly = !_canEdit;
        _tenantOptions.ReadOnly = true;
        _phoneOptions.ReadOnly = !CanEditPhones;
        _phonePoolOptions.ReadOnly = !CanEditPhones;
        foreach (var options in new ClayInputOptions[] { _writeOffDateOptions, _writeOffTimeOptions, _snOptions, _networkNameOptions, _macOptions, _descriptionOptions })
            options.ReadOnly = !_canEdit;
        _locationGridOptions.ShowAddButton = _canEdit;
        _responsibleGridOptions.ShowAddButton = _canEdit;
        _accountGridOptions.ShowAddButton = CanEditAccounts;
        _computerGridOptions.ShowAddButton = CanEditComputer;
        _softwareGridOptions.ShowAddButton = CanEditComputer;
        _simGridOptions.ShowAddButton = CanEditSim && _simCards.Count == 0;
        _simServiceGridOptions.ShowAddButton = CanEditSim && _simCards.Count > 0;
        _licenseGridOptions.ShowAddButton = CanEditLicense && _licenses.Count == 0;
        _accountingGridOptions.ShowAddButton = _canUseAccounting;
        UpdatePhoneSourceFilters();
    }

    private bool ValidateBusinessRules()
    {
        if (_model is null)
            return false;
        var errors = new List<string>();
        if (_mainConflictMessage is not null) errors.Add(_mainConflictMessage);
        if (_model.EquipmentTypeId is null || _model.ModelId is null) errors.Add("Выберите тип и модель оборудования.");
        if (_model.OwnerId is null) errors.Add("Укажите владельца оборудования.");
        if (_selectedType?.IsSerialNumberRequired > 0 && string.IsNullOrWhiteSpace(_model.SerialNumber)) errors.Add("Укажите серийный номер.");
        if (_model.DocumentWriteOffId is not null && _writeOffDate is null) errors.Add("Укажите дату списания.");
        if (_model.EquipmentTypeId != (int)ТипыОборудования.SIM_карта && _writeOffDate is not null && _model.DocumentWriteOffId is null) errors.Add("Укажите документ списания.");
        if (_selectedType?.MacAddressCount > 0 &&
            (!global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddress) ||
             !global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddress2) ||
             !global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddressIlo)))
            errors.Add("MAC-адрес должен содержать 12 hex-символов; допустимы разделители пробел, двоеточие, дефис и точка.");
        _validationMessage = errors.Count == 0 ? null : string.Join(" ", errors);
        return errors.Count == 0;
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        if (MainCloseInProgress || _mainDraftCancelled)
        {
            if (closeAfterSave) await FinishMainCloseAndExitAsync(CloseAsync, _pageCts.Token);
            return;
        }
        if (ConfigureNeedsReconciliation)
        {
            if (!CanResumeConfigure) return;
            var openedModel = _model;
            var employeeId = CurrentUser?.Id;
            await ResumeConfigurePhoneAsync();
            if (_disposed || ConfigureNeedsReconciliation || !ReferenceEquals(openedModel, _model) || CurrentUser?.Id != employeeId)
                return;
        }
        if (_model is null || !_loaded || !_canEdit || _loading || _saving || _mainTransition || !MainRawReady ||
            EquipmentRecoveryWriteBlocked || _mainConflictMessage is not null ||
            CurrentUser is not { } actor)
            return;
        if (_pendingPhoneSync is not null)
        {
            if (!await RetryPendingPhoneSyncAsync())
                return;
            if (!IsModified)
            {
                if (closeAfterSave)
                    await CloseAfterMainSaveAsync();
                return;
            }
        }
        if (!IsModified)
        {
            if (closeAfterSave)
                await CloseAfterMainSaveAsync();
            return;
        }
        var model = _model;
        var version = ++_operationVersion;
        var phonesModified = PhonesModified;
        _saving = true;
        _validationMessage = null;
        _warningMessage = null;
        StateHasChanged();
        var writeCommitted = false;
        EquipmentPhoneSyncIntent? phoneIntent = null;
        try
        {
            await Task.Delay(100);
            if (_form is not null)
            {
                await _form.ValidateAsync();
                if (!IsCurrentSave(model, version) || !_form.IsValid)
                    return;
            }
            if (!ValidateBusinessRules())
                return;
            model.WriteOffAtUtc = await ConvertWriteOffToUtcAsync();
            if (!IsCurrentSave(model, version))
                return;
            var submitted = Snapshot();
            var command = JsonSerializer.Deserialize<EquipmentEntity>(JsonSerializer.Serialize(model))!;
            var originalPhones = ClonePhones(_originalPhones);
            var submittedPhones = ClonePhones(_phones);
            await PersistDraftAsync();
            if (_draftRegistration is not { } registration || !await VerifyMainDraftReadyAsync(registration, model.Id))
            {
                _validationMessage = "Сначала полностью восстановите ввод.";
                return;
            }
            if (!IsCurrentSave(model, version) || Snapshot() != submitted)
            {
                if (IsCurrentSave(model, version))
                    _validationMessage = "Ввод изменился перед отправкой. Проверьте данные и сохраните повторно.";
                return;
            }
            if (phonesModified)
                phoneIntent = await Service.SaveWithPhonesAsync(command, originalPhones, submittedPhones,
                    actor.Id, _pageCts.Token);
            else
                await Service.UpdateAsync(command, actor.Id, _pageCts.Token);
            writeCommitted = true;
            if (!IsCurrentSave(model, version))
                return;
            _mainConflictMessage = null;
            _originalSnapshot = submitted;
            SetAccountingBaseline();
            var warnings = new List<string>();
            var unknownSync = false;
            if (phonesModified && phoneIntent is null)
            {
                await PersistDraftAsync();
                _warningMessage = "Сервер подтвердил SQL-запись. Исход АТС требует отдельной сверки.";
                return;
            }
            if (phoneIntent is not null)
            {
                var pending = new PendingPhoneSync(model.Id, originalPhones, submittedPhones)
                {
                    Attempt = new PhoneSyncAttempt(phoneIntent, []),
                };
                try
                {
                    var attempt = await Service.SynchronizePhoneAttemptAsync(phoneIntent, CreatePhoneSyncCheckpointWriter());
                    if (attempt.AttemptId != phoneIntent.AttemptId ||
                        attempt.Intent.Origin != EquipmentPhoneSyncOrigin.EquipmentSave || attempt.Intent.EquipmentId != model.Id)
                        throw new InvalidOperationException("Ответ АТС не соответствует исходной серверной попытке.");
                    var errors = attempt.Operations.Where(result => result.Phase != PhoneSyncPhase.Succeeded).ToList();
                    warnings.AddRange(errors.Select(result => result.Error!));
                    if (attempt.PlanningError is { } planningError)
                        warnings.Add(planningError + " Повторите планирование через «Применить».");
                    unknownSync = errors.Any(result => result.IsUnknown);
                    pending = pending with
                    {
                        Attempt = attempt,
                        Operations = errors.Select(result => result.Operation).ToArray(),
                        Unknown = unknownSync,
                        UnknownMessage = unknownSync
                            ? string.Join(" ", errors.Where(result => result.IsUnknown).Select(result => result.Error))
                            : null,
                    };
                }
                catch (Microsoft.Data.SqlClient.SqlException)
                {
                    warnings.Add("Синхронизация с АТС не выполнена. Подробности — в сообщении базы данных.");
                    unknownSync = true;
                    pending = pending with { Unknown = true };
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
                {
                    warnings.Add($"Синхронизация с АТС не выполнена: {ex.Message}");
                    unknownSync = true;
                    pending = pending with { Unknown = true };
                }
                _pendingPhoneSync = warnings.Count > 0 || unknownSync ? pending : null;
            }
            _originalPhones = submittedPhones;
            if (unknownSync)
                await PersistDraftAsync();
            else
                await PersistDraftAsync();
            if (!IsCurrentSave(model, version))
                return;
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", _pageCts.Token);
            if (!IsCurrentSave(model, version))
                return;
            if (Snapshot() != submitted)
            {
                _mainConflictMessage = "Основные данные сохранены. Более поздний ввод оставлен на форме. Сверьте актуальные данные.";
                return;
            }
            if (closeAfterSave && warnings.Count == 0)
            {
                await CloseAfterMainSaveAsync();
                return;
            }
            await LoadCurrentAsync(Id);
            if (warnings.Count > 0 && !_disposed)
                _warningMessage = "Основные данные и телефонные связи сохранены. " + string.Join(" ", warnings);
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrentSave(model, version))
        {
        }
        catch (EquipmentOperationException ex)
        {
            if (!IsCurrentSave(model, version))
                return;
            if (ex.Phase == EquipmentWritePhase.RefreshFailed)
            {
                writeCommitted = true;
                _validationMessage = "Оборудование сохранено, но последующее чтение не выполнено. Повторяйте только обновление данных.";
            }
            else
            {
                await PersistDraftAsync();
                _validationMessage = ex.Message;
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            if (!IsCurrentSave(model, version))
                return;
            if (writeCommitted)
            {
                _validationMessage = "Оборудование сохранено, но последующее действие не выполнено. Повторяйте только обновление данных.";
            }
            else
            {
                await PersistDraftAsync();
                _validationMessage = "Оборудование не сохранено. Подробности — в сообщении базы данных.";
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            if (!IsCurrentSave(model, version))
                return;
            if (writeCommitted)
            {
                ShowStatus("Оборудование сохранено.");
                _validationMessage = "Запись подтверждена, но экран или родительский грид не обновлён. Повторите только обновление данных.";
            }
            else
            {
                _validationMessage = "Черновик не сохранён либо канал связи потерян до записи. Команда не отправлена.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (IsCurrentSave(model, version))
            {
                if (!writeCommitted)
                    await PersistDraftAsync();
                _validationMessage = writeCommitted
                    ? $"Оборудование сохранено, но экран не обновлён: {ex.Message}"
                    : ex.Message;
            }
        }
        finally
        {
            if (!_disposed)
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private async Task<bool> RetryPendingPhoneSyncAsync()
    {
        var pending = _pendingPhoneSync;
        var model = _model;
        if (pending is null)
            return true;
        if (model is null || pending.EquipmentId != model.Id)
            return false;
        var canRetry = pending.Attempt is null || pending.Attempt.Operations.Count == 0
            ? !pending.Unknown
            : pending.Attempt.Operations.Any(result => result.CanRetry);
        if (!canRetry)
        {
            _warningMessage = "Результат предыдущей синхронизации АТС неизвестен. Автоматический повтор заблокирован; требуется сверка состояния. " + pending.UnknownMessage;
            return false;
        }
        var version = ++_operationVersion;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        var knownCompletion = false;
        try
        {
            await Task.Delay(100);
            PhoneSyncAttempt? attempt = null;
            List<(PhoneSyncOperation Operation, string? Error, bool Unknown)> errors;
            if (pending.Attempt is not null)
            {
                attempt = await Service.RetryPhoneSyncAttemptAsync(pending.Attempt, CreatePhoneSyncCheckpointWriter());
                errors = attempt.Operations.Where(result => result.Phase != PhoneSyncPhase.Succeeded)
                    .Select(result => (result.Operation, result.Error, result.IsUnknown)).ToList();
            }
            else
            {
                var results = pending.Operations is null
                    ? await Service.SynchronizePhoneOperationsAsync(pending.EquipmentId, pending.Original, pending.Current)
                    : await Service.ExecutePhoneSyncOperationsAsync(
                        pending.EquipmentId, pending.Original, pending.Current, pending.Operations);
                errors = results.Where(result => !result.Success)
                    .Select(result => (result.Operation, result.Error, result.IsUnknown)).ToList();
            }
            var unknown = errors.Any(result => result.Unknown);
            var planningError = attempt?.PlanningError;
            _pendingPhoneSync = errors.Count > 0 || planningError is not null
                ? pending with
                {
                    Attempt = attempt ?? pending.Attempt,
                    Operations = errors.Select(result => result.Operation).ToArray(),
                    Unknown = unknown,
                    UnknownMessage = unknown
                        ? string.Join(" ", errors.Where(result => result.Unknown).Select(result => result.Error))
                        : null,
                }
                : null;
            if (unknown)
                await PersistDraftAsync();
            else if (planningError is not null)
                await PersistDraftAsync();
            else
            {
                knownCompletion = true;
                await PersistDraftAsync();
            }
            if (!IsCurrentSave(model, version))
                return false;
            if (planningError is not null)
            {
                _warningMessage = "Телефонные связи сохранены. " + planningError + " Повторите планирование через «Применить».";
                return false;
            }
            if (errors.Count > 0)
            {
                _warningMessage = unknown
                    ? "Результат синхронизации АТС неизвестен. Автоматический повтор заблокирован; требуется сверка состояния. " +
                      string.Join(" ", errors.Where(result => result.Unknown).Select(result => result.Error))
                    : "Телефонные связи сохранены. " + string.Join(" ", errors.Select(result => result.Error)) + " Повторите синхронизацию через «Применить».";
                return false;
            }
            _warningMessage = null;
            return true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            if (!knownCompletion && _pendingPhoneSync is not null)
                _pendingPhoneSync = _pendingPhoneSync with { Unknown = true, UnknownMessage = ex.Message };
            if (IsCurrentSave(model, version))
                _warningMessage = knownCompletion
                    ? "Результат АТС получен. Обновите данные формы."
                    : "Канал хранилища потерян. Результат повторной команды не подтверждён. Новый вызов АТС заблокирован до сверки.";
            return false;
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrentSave(model, version))
        {
            return false;
        }
        catch (EquipmentOperationException ex)
        {
            await PersistDraftAsync();
            if (IsCurrentSave(model, version))
                _warningMessage = $"Телефонные связи сохранены, синхронизация не выполнена: {ex.Message}";
            return false;
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            await PersistDraftAsync();
            if (IsCurrentSave(model, version))
                _warningMessage = "Повторная синхронизация АТС не выполнена. Подробности — в сообщении базы данных.";
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!knownCompletion)
                await PersistDraftAsync();
            if (IsCurrentSave(model, version))
                _warningMessage = knownCompletion
                    ? "Результат АТС получен. Обновите данные формы."
                    : $"Телефонные связи сохранены, синхронизация не выполнена: {ex.Message} Повторите через «Применить».";
            return false;
        }
        finally
        {
            if (!_disposed)
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private async Task ReconcilePendingPhoneSyncAsync()
    {
        if (ConfigureNeedsReconciliation)
        {
            await ReconcileConfigureAsync();
            StateHasChanged();
            return;
        }
        if (_phoneSyncCheckpoint is not null)
        {
            _warningMessage = PhoneSyncCheckpointMessage();
            StateHasChanged();
            return;
        }
        var pending = _pendingPhoneSync;
        if (pending?.Attempt is null)
            return;
        try
        {
            var reconciliation = await Service.ReconcilePhoneSyncAttemptAsync(pending.Attempt);
            var errors = reconciliation.Attempt.Operations.Where(result => result.Phase != PhoneSyncPhase.Succeeded).ToList();
            var unknown = errors.Any(result => result.IsUnknown);
            var canRetry = errors.Any(result => result.CanRetry);
            _pendingPhoneSync = pending with
            {
                Attempt = reconciliation.Attempt,
                Operations = errors.Select(result => result.Operation).ToArray(),
                Unknown = unknown,
                UnknownMessage = unknown
                    ? string.Join(" ", errors.Where(result => result.IsUnknown).Select(result => result.Error))
                    : null,
            };
            if (!reconciliation.PersistedStateMatches)
            {
                _warningMessage = reconciliation.Attempt.PlanningError is null
                    ? "Read-only сверка не подтвердила состояние телефонных связей. Unknown сохранён; повтор запрещён."
                    : "Read-only сверка не подтвердила состояние телефонных связей. Команды АТС не отправлены.";
            }
            else if (unknown)
            {
                _warningMessage = "Состояние связей в БД совпадает, но исход внешней ATS-операции не доказан. Unknown сохранён; blind replay запрещён.";
            }
            else if (reconciliation.Attempt.PlanningError is not null)
            {
                _warningMessage = "Телефонные связи подтверждены. План АТС не определён. Повторите планирование через «Применить».";
            }
            else if (canRetry)
            {
                _warningMessage = "Read-only сверка завершена. Можно повторить только подтверждённые Failed/NotSent операции через «Применить».";
            }
            else
            {
                _pendingPhoneSync = null;
                _warningMessage = null;
                ShowStatus("Read-only сверка завершена. Повторная ATS-команда не выполнялась.");
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            _warningMessage = $"Не удалось выполнить read-only сверку ATS. Unknown сохранён. {ex.Message}";
        }
        StateHasChanged();
    }

    private bool IsCurrentSave(EquipmentEntity model, int version) => !_disposed && version == _operationVersion && ReferenceEquals(model, _model);

    private async Task WatchBusinessDayAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), BusinessClock);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                await InvokeAsync(RefreshBusinessDayAsync).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RefreshBusinessDayAsync()
    {
        if (_disposed || !_loaded || _loading || _saving || _model is not { } model)
            return;

        var version = _operationVersion;
        var simVersion = _simServiceVersion;
        bool Current() => IsCurrent(version, model.Id) && ReferenceEquals(model, _model) && !_loading && !_saving;

        try
        {
            var day = (await GetClientNowAsync()).Date;
            if (!Current() || simVersion != _simServiceVersion)
                return;

            if (_clientToday != day)
            {
                _clientToday = day;
                StateHasChanged();
            }

            if (ShowSim && _simServicesDay != day)
            {
                ++_simServiceVersion;
                try { await RefreshSimServicesForDayAsync(day); }
                finally
                {
                    if (Current())
                        StateHasChanged();
                }
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException or
            Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed)
                Logger.LogDebug("Не удалось обновить дату карточки ({ErrorType}).", ex.GetType().Name);
        }
    }

    private async Task<DateTime> GetClientNowAsync()
    {
        var utcNow = BusinessClock.GetUtcNow().UtcDateTime;
        var offset = await JS.InvokeAsync<int>(
            "clayGridTimeZone.getOffsetMinutes", _pageCts.Token, utcNow.ToString("O"));
        return utcNow.AddMinutes(offset);
    }

    private async Task<DateTime?> ConvertWriteOffToUtcAsync()
    {
        if (_writeOffDate is null)
            return null;
        var original = string.IsNullOrEmpty(_originalSnapshot) ? null : JsonNode.Parse(_originalSnapshot);
        if (original?["WriteOffDate"]?.Deserialize<DateTime>() == _writeOffDate &&
            original?["WriteOffTime"]?.Deserialize<TimeSpan>() == _writeOffTime &&
            original?["Equipment"]?[nameof(EquipmentEntity.WriteOffAtUtc)]?.Deserialize<DateTime>() is { } originalUtc)
            return originalUtc;

        var milliseconds = await JS.InvokeAsync<long>(
            "clayGridTimeZone.localDateTimeToUtcMilliseconds", _pageCts.Token,
            _writeOffDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            (_writeOffTime ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture));
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    private string Snapshot()
    {
        return Snapshot(_model, _writeOffDate, _writeOffTime, _phones);
    }

    private static string Snapshot(
        EquipmentEntity? model,
        DateTime? writeOffDate,
        TimeSpan? writeOffTime,
        IReadOnlyCollection<EquipmentPhoneLink> phones)
    {
        var equipment = JsonSerializer.SerializeToNode(model)!;
        equipment.AsObject().Remove(nameof(EquipmentEntity.LocationId));
        equipment.AsObject().Remove(nameof(EquipmentEntity.EmployeeId));
        return JsonSerializer.Serialize(new { Equipment = equipment, WriteOffDate = writeOffDate, WriteOffTime = writeOffTime, Phones = phones });
    }

    private sealed record EquipmentMainFieldBaseline(
        EquipmentEntity Equipment, DateTime? WriteOffDate, TimeSpan? WriteOffTime);

    private string? _mainFieldBaselineSource;
    private EquipmentMainFieldBaseline? _mainFieldBaseline;

    private EquipmentMainFieldBaseline? MainFieldChangeBaseline
    {
        get
        {
            if (!ReferenceEquals(_mainFieldBaselineSource, _originalSnapshot))
            {
                _mainFieldBaseline = string.IsNullOrEmpty(_originalSnapshot)
                    ? null
                    : JsonSerializer.Deserialize<EquipmentMainFieldBaseline>(_originalSnapshot);
                _mainFieldBaselineSource = _originalSnapshot;
            }

            return _loaded && _canEdit && _model?.Id == Id && _mainFieldBaseline?.Equipment?.Id == Id
                ? _mainFieldBaseline
                : null;
        }
    }

    private EquipmentEntity? OriginalSnapshotModel()
    {
        if (string.IsNullOrWhiteSpace(_originalSnapshot))
            return null;
        return JsonNode.Parse(_originalSnapshot)?["Equipment"]?.Deserialize<EquipmentEntity>();
    }

    private static readonly string[] EditableMainProperties =
    [
        nameof(EquipmentEntity.EquipmentTypeId), nameof(EquipmentEntity.ModelId),
        nameof(EquipmentEntity.CameraIsConnected), nameof(EquipmentEntity.DocumentWriteOffId),
        nameof(EquipmentEntity.PhonePoolId), nameof(EquipmentEntity.SerialNumber),
        nameof(EquipmentEntity.MacAddress), nameof(EquipmentEntity.MacAddress2),
        nameof(EquipmentEntity.MacAddressIlo), nameof(EquipmentEntity.NetworkName),
        nameof(EquipmentEntity.OwnerId), nameof(EquipmentEntity.TenantId),
        nameof(EquipmentEntity.DocumentReceiptOuId), nameof(EquipmentEntity.ShipperId),
        nameof(EquipmentEntity.Description),
    ];

    private static readonly string[] DerivedMainProperties =
    [
        nameof(EquipmentEntity.LocationId), nameof(EquipmentEntity.EmployeeId),
        nameof(EquipmentEntity.LocationDescription), nameof(EquipmentEntity.EmployeeDescription),
    ];

    private EquipmentEntity? _mainConflictFresh;
    private DateTime? _mainConflictFreshWriteOffDate;
    private TimeSpan? _mainConflictFreshWriteOffTime;
    private string[] _mainConflictFields = [];

    private sealed record MainMergeResult(
        EquipmentEntity Model,
        EquipmentEntity BaselineModel,
        EquipmentEntity FreshModel,
        DateTime? WriteOffDate,
        TimeSpan? WriteOffTime,
        DateTime? BaselineWriteOffDate,
        TimeSpan? BaselineWriteOffTime,
        DateTime? FreshWriteOffDate,
        TimeSpan? FreshWriteOffTime,
        string[] Conflicts);

    private async Task<MainMergeResult> MergeMainStateAsync(
        EquipmentEntity original,
        EquipmentEntity local,
        EquipmentEntity fresh,
        DateTime? localWriteOffDate,
        TimeSpan? localWriteOffTime)
    {
        var originalNode = JsonSerializer.SerializeToNode(original)!.AsObject();
        var localNode = JsonSerializer.SerializeToNode(local)!.AsObject();
        var baselineNode = JsonSerializer.SerializeToNode(fresh)!.AsObject();
        var conflicts = new List<string>();
        foreach (var property in typeof(EquipmentEntity).GetProperties().Where(property => property.CanWrite))
        {
            var propertyName = property.Name;
            if (propertyName is nameof(EquipmentEntity.WriteOffAtUtc) or nameof(EquipmentEntity.ChangedAtUtc)
                or nameof(EquipmentEntity.ChangedById) or nameof(EquipmentEntity.ChangedByName))
                continue;
            var localEqualsBase = JsonNode.DeepEquals(originalNode[propertyName], localNode[propertyName]);
            var freshEqualsBase = JsonNode.DeepEquals(originalNode[propertyName], baselineNode[propertyName]);
            var localEqualsFresh = JsonNode.DeepEquals(localNode[propertyName], baselineNode[propertyName]);
            if (localEqualsBase)
                property.SetValue(local, property.GetValue(fresh));
            else if (EditableMainProperties.Contains(propertyName) && !freshEqualsBase && !localEqualsFresh)
            {
                conflicts.Add(propertyName);
                baselineNode[propertyName] = originalNode[propertyName]?.DeepClone();
            }
        }
        foreach (var propertyName in DerivedMainProperties)
        {
            var property = typeof(EquipmentEntity).GetProperty(propertyName)!;
            property.SetValue(local, property.GetValue(fresh));
        }

        var baseWriteOff = original.WriteOffAtUtc is null
            ? (DateTime?)null
            : await ConvertUtcToLocalAsync(original.WriteOffAtUtc.Value);
        var freshWriteOff = fresh.WriteOffAtUtc is null
            ? (DateTime?)null
            : await ConvertUtcToLocalAsync(fresh.WriteOffAtUtc.Value);
        var mergedWriteOffDate = localWriteOffDate;
        var mergedWriteOffTime = localWriteOffTime;
        var baselineWriteOffDate = freshWriteOff?.Date;
        var baselineWriteOffTime = freshWriteOff?.TimeOfDay;
        var localWriteOffEqualsBase = localWriteOffDate == baseWriteOff?.Date && localWriteOffTime == baseWriteOff?.TimeOfDay;
        var freshWriteOffEqualsBase = freshWriteOff?.Date == baseWriteOff?.Date && freshWriteOff?.TimeOfDay == baseWriteOff?.TimeOfDay;
        var localWriteOffEqualsFresh = localWriteOffDate == freshWriteOff?.Date && localWriteOffTime == freshWriteOff?.TimeOfDay;
        if (localWriteOffEqualsBase)
        {
            mergedWriteOffDate = freshWriteOff?.Date;
            mergedWriteOffTime = freshWriteOff?.TimeOfDay;
        }
        if (localWriteOffEqualsBase || localWriteOffEqualsFresh)
            local.WriteOffAtUtc = fresh.WriteOffAtUtc;
        else if (!freshWriteOffEqualsBase)
        {
            conflicts.Add(nameof(EquipmentEntity.WriteOffAtUtc));
            baselineNode[nameof(EquipmentEntity.WriteOffAtUtc)] = originalNode[nameof(EquipmentEntity.WriteOffAtUtc)]?.DeepClone();
            baselineWriteOffDate = baseWriteOff?.Date;
            baselineWriteOffTime = baseWriteOff?.TimeOfDay;
        }

        var tokenOwner = conflicts.Count > 0 ? original : fresh;
        local.ChangedAtUtc = tokenOwner.ChangedAtUtc;
        local.ChangedById = tokenOwner.ChangedById;
        local.ChangedByName = tokenOwner.ChangedByName;
        if (conflicts.Count > 0)
        {
            baselineNode[nameof(EquipmentEntity.ChangedAtUtc)] = originalNode[nameof(EquipmentEntity.ChangedAtUtc)]?.DeepClone();
            baselineNode[nameof(EquipmentEntity.ChangedById)] = originalNode[nameof(EquipmentEntity.ChangedById)]?.DeepClone();
            baselineNode[nameof(EquipmentEntity.ChangedByName)] = originalNode[nameof(EquipmentEntity.ChangedByName)]?.DeepClone();
        }
        return new MainMergeResult(local, baselineNode.Deserialize<EquipmentEntity>()!, fresh,
            mergedWriteOffDate, mergedWriteOffTime, baselineWriteOffDate, baselineWriteOffTime,
            freshWriteOff?.Date, freshWriteOff?.TimeOfDay, conflicts.Distinct().ToArray());
    }

    private void ApplyMainMergeResult(MainMergeResult result)
    {
        var previousConflict = _mainConflictMessage;
        _model = result.Model;
        _writeOffDate = result.WriteOffDate;
        _writeOffTime = result.WriteOffTime;
        _originalSnapshot = Snapshot(result.BaselineModel,
            result.BaselineWriteOffDate, result.BaselineWriteOffTime, _originalPhones);
        if (result.Conflicts.Length > 0)
        {
            _mainConflictFields = result.Conflicts;
            _mainConflictFresh = result.FreshModel;
            _mainConflictFreshWriteOffDate = result.FreshWriteOffDate;
            _mainConflictFreshWriteOffTime = result.FreshWriteOffTime;
            _mainConflictMessage = "Основная карточка изменилась в другой сессии. Конфликт полей: " +
                                   string.Join(", ", result.Conflicts) +
                                   ". Проверьте значения и выберите, какие оставить.";
            _warningMessage = _mainConflictMessage;
            return;
        }
        ClearMainConflict(previousConflict);
    }

    private void ClearMainConflict(string? previousConflict = null)
    {
        _mainConflictMessage = null;
        _mainConflictFields = [];
        _mainConflictFresh = null;
        _mainConflictFreshWriteOffDate = null;
        _mainConflictFreshWriteOffTime = null;
        if (previousConflict is not null && _warningMessage == previousConflict)
            _warningMessage = null;
    }

    private void ResolveMainConflict(bool keepLocal)
    {
        if (_mainConflictFresh is null || _model is null)
            return;
        var previousConflict = _mainConflictMessage;
        if (!keepLocal)
        {
            foreach (var propertyName in _mainConflictFields)
            {
                if (propertyName == nameof(EquipmentEntity.WriteOffAtUtc))
                {
                    _model.WriteOffAtUtc = _mainConflictFresh.WriteOffAtUtc;
                    _writeOffDate = _mainConflictFreshWriteOffDate;
                    _writeOffTime = _mainConflictFreshWriteOffTime;
                    continue;
                }
                var property = typeof(EquipmentEntity).GetProperty(propertyName)!;
                property.SetValue(_model, property.GetValue(_mainConflictFresh));
            }
        }
        _model.ChangedAtUtc = _mainConflictFresh.ChangedAtUtc;
        _model.ChangedById = _mainConflictFresh.ChangedById;
        _model.ChangedByName = _mainConflictFresh.ChangedByName;
        _originalSnapshot = Snapshot(_mainConflictFresh,
            _mainConflictFreshWriteOffDate, _mainConflictFreshWriteOffTime, _originalPhones);
        ClearMainConflict(previousConflict);
        ShowStatus(keepLocal
            ? "Конфликт разрешён: сохранены значения черновика."
            : "Конфликт разрешён: конфликтные поля заменены данными из БД, остальной черновик сохранён.");
        StateHasChanged();
    }

    private async Task RefreshMainAfterChildWriteAsync(EquipmentEntity? original)
    {
        if (original is null || _model is null)
            return;
        var current = _model;
        var fresh = await Service.GetAsync(current.Id) ??
                    throw new InvalidOperationException("Дочерняя запись сохранена, но основная карточка не найдена при обновлении.");
        var merge = await MergeMainStateAsync(original, current, fresh, _writeOffDate, _writeOffTime);
        ApplyMainMergeResult(merge);
    }

    private async Task CopyAsync()
    {
        if (Id > 0)
            await JS.InvokeVoidAsync("clayOpen", $"/Equipment/EquipmentIns?copyId={Id}", "_blank");
    }

    private async Task OpenModelViewAsync(EquipmentModel model) =>
        await JS.InvokeVoidAsync("clayOpen", EquipmentIns.BuildEditUrl(Configuration["URI_model_form"]!, model.Id), "_blank");

    private async Task CloseAsync()
    {
        _allowNavigation = true;
        await JS.InvokeVoidAsync("clayModelEquipmentForm.close");
    }

    private sealed record EquipmentEditorOpening(
        EquipmentEntity Model,
        int EquipmentId,
        int OperationVersion,
        long Generation)
    {
        public EquipmentEditorContext? EditorContext { get; set; }
    }

    private EquipmentEditorOpening CaptureEditorOpening() =>
        new(_model!, _model!.Id, _operationVersion, ++_editorGeneration);

    private bool IsCurrentCore(EquipmentEditorOpening? opening) =>
        opening is not null && !_disposed && opening.OperationVersion == _operationVersion &&
        opening.Generation == _editorGeneration && ReferenceEquals(opening.Model, _model) &&
        _model.Id == opening.EquipmentId && Id == opening.EquipmentId;

    private bool IsCurrent(EquipmentEditorOpening? opening)
    {
        if (!IsCurrentCore(opening))
            return false;
        try
        {
            opening!.EditorContext?.EnsureCurrent();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<DialogResult?> ShowDialogAsync<T>(
        string title,
        DialogParameters<T> parameters,
        DialogOptionsEx? options = null,
        string? draftKey = null,
        EquipmentEditorOpening? openingContext = null,
        EquipmentChildPresence? presence = null,
        Func<int, int, CancellationToken, Task<string?>>? readFreshCancellationState = null,
        bool newCancellationRecord = false,
        Action<ComponentBase?, string>? acceptFreshCancellationState = null,
        Action? discardFreshCancellationState = null,
        Func<EquipmentDraftState, string?, bool>? prepareCancellationEnvelope = null) where T : ComponentBase
    {
        if (_activeEditorContext is null && _restoredEditorState is not null &&
            _activeEditor is { Kind: nameof(EquipmentSoftwareDialog) or nameof(EquipmentComputerDialog)
                or nameof(EquipmentSimServiceDialog) or nameof(EquipmentAccountingDialog)
                or nameof(EquipmentLicenseDialog) or nameof(EquipmentLocationDialog) or nameof(EquipmentResponsibleDialog)
                or nameof(EquipmentAccountDialog) or nameof(EquipmentUpsLinkDialog) or nameof(EquipmentUpsDialog)
                or nameof(EquipmentSimDialog) or nameof(EquipmentPhoneDialog) } retained &&
            (typeof(T).Name != retained.Kind || retained.Key != draftKey))
        {
            _validationMessage = "Сначала откройте дочерний редактор с сохранённым вводом.";
            return null;
        }
        openingContext ??= _model is null ? null : CaptureEditorOpening();
        var generation = openingContext?.Generation ?? ++_editorGeneration;
        var deferredRaw = typeof(IEquipmentDeferredRawRestore).IsAssignableFrom(typeof(T));
        var protectedRaw = typeof(T) == typeof(EquipmentAccountDialog) || typeof(T) == typeof(EquipmentSimDialog);
        var managesRaw = deferredRaw || protectedRaw;
        var draftRegistration = _draftRegistration;
        var expectedScope = $"child:{typeof(T).Name}:{draftKey ?? "new"}:root";
        var rawAttempted = false;
        var rawReady = false;
        var draftStateToRestore = typeof(T) != typeof(EquipmentUpsDialog) &&
            _activeEditor?.Kind == typeof(T).Name && _activeEditor.Key == draftKey
            ? _restoredEditorState
            : null;
        EquipmentEditorContext? editorContext = null;
        _activeEditorContext = null;
        if (openingContext is not null && typeof(T).GetProperty(nameof(EquipmentEditorDialogBase<object>.EditorContext)) is not null)
        {
            bool OwnsRawScope() => ReferenceEquals(_activeEditorContext, editorContext) && IsCurrentCore(openingContext) &&
                ReferenceEquals(_draftRegistration, draftRegistration) && ActiveDraftScope == expectedScope;

            async Task RestoreChildRawAsync(bool retry)
            {
                if (draftRegistration is null || !OwnsRawScope())
                {
                    if (managesRaw)
                        throw new InvalidOperationException("Черновик этого редактора недоступен.");
                    return;
                }

                var pendingOnly = retry || managesRaw && rawAttempted;
                rawAttempted = true;
                rawReady = false;
                var raw = pendingOnly
                    ? await draftRegistration.ApplyPendingRawAsync(expectedScope, _pageCts.Token)
                    : await draftRegistration.ApplyRawWithResultAsync(expectedScope, _pageCts.Token);
                if (managesRaw)
                {
                    if (!OwnsRawScope())
                        throw new InvalidOperationException("Контекст редактора изменился.");
                    var readiness = await draftRegistration.GetRestoreReadinessAsync(_pageCts.Token);
                    if (!OwnsRawScope() || raw is null || raw.Pending.Count > 0 || readiness is null ||
                        readiness.PendingScopes.Contains(expectedScope) || readiness.FailedScopes.Contains(expectedScope))
                        throw new InvalidOperationException("Восстановление полей редактора ещё не завершено.");
                }
                else if (raw?.Pending.Count > 0)
                    throw new InvalidOperationException("Восстановление полей редактора ещё не завершено.");

                rawReady = true;
            }

            var actorId = CurrentUser?.Id;
            bool CanCancelScope() => OwnsRawScope() && CurrentUser?.Id == actorId && !_draftPersisting;
            EquipmentEditorCancellation? cancellation = null;
            if (draftRegistration is not null && readFreshCancellationState is not null && actorId is > 0)
            {
                cancellation = new(
                    async () => CanCancelScope()
                        ? await draftRegistration.ReadEditorScopeAsync(expectedScope, _pageCts.Token) : null,
                    () => readFreshCancellationState(openingContext.EquipmentId, actorId.Value, _pageCts.Token),
                    async (revision, visible, replacement, checkSensitive) =>
                    {
                        if (!await checkSensitive() || !CanCancelScope() || editorContext?.CaptureDraftState() != visible)
                            return false;

                        var parent = CaptureEquipmentDraftState();
                        var close = replacement is null;
                        parent.ActiveEditorState = replacement;
                        if (close) parent.ActiveEditor = null;
                        if (prepareCancellationEnvelope is not null && !prepareCancellationEnvelope(parent, replacement))
                            return false;
                        var state = JsonSerializer.Serialize(parent);
                        if (!await draftRegistration.ReplaceNestedEditorScopeAsync(expectedScope, revision, state, close, _pageCts.Token) ||
                            !OwnsRawScope() || CurrentUser?.Id != actorId || editorContext is null)
                            return false;
                        var sensitiveCurrent = await checkSensitive();
                        if (!OwnsRawScope() || CurrentUser?.Id != actorId)
                            return false;
                        if (!sensitiveCurrent || editorContext.CaptureDraftState() != visible)
                        {
                            if (close) await draftRegistration.OpenScopeAsync(expectedScope, _pageCts.Token);
                            _lastPersistedDraft = null;
                            await PersistDraftAsync();
                            return false;
                        }

                        if (replacement is not null)
                            acceptFreshCancellationState?.Invoke(editorContext.Component, replacement);
                        editorContext.CancellationAccepted = close;
                        rawReady = true;
                        _lastPersistedDraft = state;
                        return true;
                    },
                    newCancellationRecord,
                    discardFreshCancellationState);
            }

            editorContext = new EquipmentEditorContext(openingContext.EquipmentId, generation,
                () => IsCurrentCore(openingContext))
            {
                DraftState = draftStateToRestore,
                Cancellation = cancellation,
                DraftChanged = async _ =>
                {
                    if (ReferenceEquals(_activeEditorContext, editorContext) && IsCurrentCore(openingContext))
                        await PersistDraftAsync();
                },
                RawRestore = () => RestoreChildRawAsync(retry: false),
                PendingRawRestore = managesRaw ? () => RestoreChildRawAsync(retry: true) : null,
                ReadinessChanged = async readiness =>
                {
                    if (readiness == EquipmentEditorReadiness.Failed && draftRegistration is not null &&
                        ReferenceEquals(_draftRegistration, draftRegistration) && ActiveDraftScope == expectedScope &&
                        ReferenceEquals(_activeEditorContext, editorContext) && IsCurrentCore(openingContext))
                        await draftRegistration.ReportRestoreScopeFailureAsync(expectedScope, _pageCts.Token);
                },
            };
            openingContext.EditorContext = editorContext;
            if (draftStateToRestore is not null)
                _restoredEditorState = null;
            _activeEditorContext = editorContext;
            parameters.Add(nameof(EquipmentEditorDialogBase<object>.EditorContext), editorContext);
            if (BindChildPresence(presence, openingContext) is { } presenceTarget)
                parameters.Add(nameof(EquipmentEditorDialogBase<object>.PresenceTarget), presenceTarget);
        }
        options ??= new DialogOptionsEx();
        if (editorContext?.Cancellation is not null)
        {
            options.BackdropClick = false;
            options.CloseButton = false;
            options.CloseOnEscapeKey = false;
        }
        options.DragMode = MudDialogDragMode.Simple;
        options.DialogAppearance ??= new MudBlazor.Extensions.Core.MudExAppearance();
        options.DialogAppearance.Class = $"{options.DialogAppearance.Class} equipment-draft-dialog-{_draftOwner}".Trim();
        var nestedTestId = _draftRestoring ? _activeEditor?.NestedTestId : null;
        _activeEditor = new EquipmentDraftEditor(typeof(T).Name, draftKey, nestedTestId);
        var draftScope = ActiveDraftScope;
        DialogResult? result = null;
        var focusScope = _equipmentFocusScope;
        var focusEquipment = _model?.Id;
        var focusEmployee = CurrentUser?.Id;
        long? focusReturn = null;
        await PersistDraftAsync();
        await OpenDraftScopeAsync(draftScope);
        try
        {
            if (openingContext is not null && !IsCurrent(openingContext))
                return null;
            if (focusScope is not null)
                focusReturn = await focusScope.CaptureDialogReturnAsync();
            if (openingContext is not null && !IsCurrent(openingContext))
                return null;
            var dialog = await DialogService.ShowExAsync<T>(title, parameters, options);
            if (_activeEditor?.NestedTestId is null)
                _restoredEditorOpened.TrySetResult();
            if (draftRegistration is not null && !managesRaw)
                await draftRegistration.ApplyRawWithResultAsync(draftScope, _pageCts.Token);
            result = await dialog.Result;
            if (openingContext is not null && !IsCurrent(openingContext))
                return null;
            return result;
        }
        finally
        {
            editorContext?.Invalidate();
            var ownsChild = !managesRaw || IsCurrentCore(openingContext) &&
                ReferenceEquals(_activeEditorContext, editorContext) &&
                ReferenceEquals(_draftRegistration, draftRegistration);
            if (ownsChild)
            {
                var keepNested = typeof(T) == typeof(EquipmentUpsDialog) && IsCurrentCore(openingContext) &&
                    _upsTestDraft is not null;
                if (!keepNested)
                {
                    var guardedCancel = editorContext?.Cancellation is not null && (result is null || result.Canceled);
                    var keepUnrestored = managesRaw && !rawReady ||
                        guardedCancel && editorContext?.CancellationAccepted != true;
                    if ((result is null || result.Canceled) && !keepUnrestored && !guardedCancel)
                        await DiscardDraftScopeAsync(draftScope);
                    else
                        await OpenDraftScopeAsync(MainDraftScope);
                    if (keepUnrestored)
                        _restoredEditorState = editorContext?.CaptureDraftState() ?? _restoredEditorState;
                    else
                    {
                        _activeEditor = null;
                        _restoredEditorState = null;
                        ClearUpsDraft();
                    }
                }
                _activeEditorContext = null;
                await PersistDraftAsync();
            }
            if (result is not null && focusReturn is { } request && focusScope is not null && !_disposed &&
                ReferenceEquals(_equipmentFocusScope, focusScope) && focusEquipment == Id && focusEmployee == CurrentUser?.Id)
                await focusScope.RestoreDialogReturnAsync(request);
        }
    }

    private static EquipmentResponsiblePeriod CloneResponsible(EquipmentResponsiblePeriod item) => new()
    {
        EquipmentId = item.EquipmentId,
        FromUtc = item.FromUtc,
        ToUtc = item.ToUtc,
        EmployeeId = item.EmployeeId,
        EmployeeName = item.EmployeeName,
        ReturnDate = item.ReturnDate,
        Notes = item.Notes,
        ChangedById = item.ChangedById,
        ChangedByName = item.ChangedByName,
        ChangedAtUtc = item.ChangedAtUtc,
    };

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var equipmentId = _model?.Id;
        var operationVersion = _operationVersion;
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { dialog => dialog.Title, title },
            { dialog => dialog.Message, message },
            { dialog => dialog.ConfirmText, "Да" },
            { dialog => dialog.CancelText, "Нет" },
        };
        var dialog = await DialogService.ShowExAsync<ConfirmDialog>(
            title, parameters, new DialogOptionsEx { DragMode = MudDialogDragMode.Simple });
        var result = await dialog.Result;
        return result is not null && !result.Canceled &&
               (equipmentId is null || IsCurrent(operationVersion, equipmentId.Value) && _model?.Id == equipmentId);
    }

    /// <summary>Совместимый синхронный вызов освобождения формы.</summary>
    public void Dispose() => _ = DisposeAsync();

    /// <summary>Отменяет операции и освобождает live-регистрацию без удаления durable draft.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        ResetEquipmentReachability();
        _pageCts.Cancel();
        if (_businessDayWatch is not null)
            await _businessDayWatch;
        if (_draftRegistration is not null)
            await _draftRegistration.DisposeAsync();
        _draftRegistration = null;
        _pageCts.Dispose();
    }
}
