using System.Text.Json;
using System.Text.Json.Nodes;
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

    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "equipment-form", MaxWidth = "100%" };
    private readonly ClaySelectOptions _typeOptions = new() { Required = true, AutoSelectSingle = true, MaxItems = 300, Placeholder = "Выберите тип оборудования" };
    private readonly ClaySelectOptions _modelOptions = new() { Required = true, AutoSelectSingle = true, Placeholder = "Выберите модель" };
    private readonly ClaySelectOptions _phonePoolOptions = new() { AutoSelectSingle = true, Placeholder = "Выберите телефонный пул" };
    private readonly ClaySelectOptions _phoneOptions = new() { AutoSelectSingle = true, AlwaysShowAdvancedSearch = true, Placeholder = "Выберите телефонный номер" };
    private readonly ClayGridOptions _phoneGridOptions = new() { EmptyText = "Нет телефонных номеров", ShowAddButton = false, ShowHeader = false };
    private int _hotelPhoneCount;
    private readonly ClayGridOptions _locationGridOptions = new() { EmptyText = "Нет расположений", AddTooltip = "Добавить расположение" };
    private List<EquipmentLocationPeriod> _locations = [];
    private readonly Dictionary<DateTime, DateTime> _localDates = new();
    private DateTime _clientToday = DateTime.Today;
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
    private readonly ClaySelectOptions _ownerOptions = new() { Required = true, AutoSelectSingle = true, Placeholder = "Выберите владельца" };
    private readonly ClaySelectOptions _receiptDocumentOptions = new() { Placeholder = "Выберите документ поступления" };
    private readonly ClaySelectOptions _writeOffDocumentOptions = new() { Placeholder = "Выберите документ списания" };
    private readonly ClaySelectOptions _shipperOptions = new() { AutoSelectSingle = true, Placeholder = "Выберите поставщика" };
    private readonly ClaySelectOptions _tenantOptions = new() { AutoSelectSingle = true, Placeholder = "Выберите арендатора" };
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
    private readonly ClayToolbarButtonOptions _keepDraftOptions = new() { Text = "Оставить значения черновика", Title = "Разрешить конфликт в пользу локальных значений" };
    private readonly ClayToolbarButtonOptions _takeFreshOptions = new() { Text = "Взять конфликтные поля из БД", Title = "Заменить только конфликтные поля свежими значениями" };
    private readonly ClayToolbarButtonOptions _reconcileOptions = new() { Text = "Сверить без повторной записи", Title = "Проверить результат только чтением" };
    private readonly ClayToolbarButtonOptions _reconcileAtsOptions = new() { Text = "Сверить ATS без повтора", Title = "Проверить результат ATS только чтением" };
    private int _networkAttachmentCount;
    private int _networkVersion;
    private IReadOnlyList<EquipmentNetworkNameHistory> _networkNameHistory = [];
    private IReadOnlyList<EquipmentNetworkPortLink> _networkPorts = [];

    private MudForm? _form;
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
    private string? _statusMessage;
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
    private const string PendingSyncWarning = "Основные данные и телефонные связи уже сохранены в БД, но синхронизация АТС не завершена. При закрытии повтор из этой формы будет потерян; потребуется ручная синхронизация.";
    private bool HasPendingPhoneSync => _pendingPhoneSync is not null;
    private bool HasUnsavedDraft => _loaded && _model is not null && Snapshot() != _originalSnapshot;
    private bool IsModified => HasUnsavedDraft || HasPendingPhoneSync;
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving && _mainConflictMessage is null;
    private string CloseConfirmation(string draftMessage)
    {
        if (!HasPendingPhoneSync) return draftMessage;
        return HasUnsavedDraft ? $"{draftMessage} {PendingSyncWarning}" : PendingSyncWarning;
    }
    private string RefreshConfirmation()
    {
        const string draftMessage = "Отменить несохранённые изменения и загрузить запись повторно?";
        if (!HasPendingPhoneSync) return draftMessage;
        const string pendingMessage = "Основные данные и телефонные связи уже сохранены в БД, но синхронизация АТС не завершена. Обновление записи сохранит ожидающую синхронизацию и возможность повтора через «Применить».";
        return HasUnsavedDraft ? $"{draftMessage} {pendingMessage}" : pendingMessage;
    }
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
        if (_loadedId != Id)
        {
            await ReleaseDraftRegistrationAsync();
            _draftStorageError = null;
            _loadedId = Id;
            await LoadCurrentAsync(Id);
            await InitializeDraftAsync();
            return;
        }
        await PersistDraftAsync();
    }

    private async Task LoadCurrentAsync(int id)
    {
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
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            CurrentUser = await Service.GetCurrentUserWithRolesAsync();
            if (!IsCurrent(version, id))
                return;
            var model = id > 0 ? await Service.GetAsync(id) : null;
            if (!IsCurrent(version, id))
                return;
            if (model is null)
            {
                _model = null;
                _notFound = true;
                return;
            }
            var canEdit = CurrentUser?.HasRole(Роли.Специалист_по_инвентаризации_оборудования) == true;
            if (!IsCurrent(version, id))
                return;
            var selectedType = model.EquipmentTypeId is > 0
                ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, id))
                return;
            var canEditSim = selectedType?.HasSimCharacteristics > 0 &&
                             CurrentUser?.HasRole(Роли.Администратор_тарифов_связи) == true;
            var canEditLicense = selectedType?.HasSoftwareCharacteristics > 0 &&
                                 (CurrentUser?.HasRole(Роли.Специалист_по_инвентаризации_по,
                                      EmployeeRoleScope.PersonOrGlobal, model.OwnerId) == true ||
                                  model.TenantId is not null && model.TenantId != model.OwnerId &&
                                  CurrentUser?.HasRole(Роли.Специалист_по_инвентаризации_по,
                                      EmployeeRoleScope.PersonOrGlobal, model.TenantId) == true);
            var canViewPhoneConfig = selectedType?.Id == (int)ТипыОборудования.IP_телефон &&
                                     CurrentUser?.HasRole(Роли.Дежурный_инженер) == true;
            var canViewAccounts = selectedType?.MacAddressCount > 0 &&
                                  CurrentUser?.HasRole(Роли.Дежурный_инженер, EmployeeRoleScope.Global) == true;
            var canManageUps = model.EquipmentTypeId is < 100 && selectedType?.HasSimCharacteristics == 0 &&
                               CurrentUser?.HasRole(Роли.Специалист_по_инвентаризации_оборудования,
                                   EmployeeRoleScope.PersonOrGlobal, model.OwnerId) == true;
            var canChangeNetwork = selectedType?.HasComputerCharacteristics > 0 &&
                                   CurrentUser?.HasRole(Роли.Исполнитель_заявок_службы_поддержки_пользователей,
                                       EmployeeRoleScope.Global) == true;
            var canUseAccounting = await Service.CanUseAccountingAsync(model.OwnerId);
            if (!IsCurrent(version, id))
                return;
            var selectedModel = model.ModelId is > 0
                ? await ModelSource.GetByIdAsync(model.ModelId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, id))
                return;
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

            _clientToday = clientToday;
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
            _model = null;
            _loadError = "Не удалось загрузить оборудование из-за ошибки соединения или SQL. Повторите загрузку после восстановления соединения.";
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
        if (!_canEdit || _saving)
            return;
        var parameters = new DialogParameters<EquipmentPhoneDialog>
        {
            { dialog => dialog.ReturnDate, item.ReturnDate },
            { dialog => dialog.Notes, item.Notes },
        };
        var result = await ShowDialogAsync<EquipmentPhoneDialog>("Срок возврата", parameters,
            draftKey: item.PhoneId.ToString());
        if (result is null || result.Canceled || result.Data is not EquipmentPhoneReturnResult value)
            return;
        item.ReturnDate = value.ReturnDate;
        item.Notes = value.Notes;
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
        await ShowDialogAsync<EquipmentLocationDialog>("Расположение", parameters,
            draftKey: (existing?.FromUtc ?? original?.FromUtc)?.Ticks.ToString(), openingContext: opening);
    }

    private async Task<EquipmentEditorSaveResult> SaveLocationDraftAsync(
        EquipmentLocationPeriod? existing,
        EquipmentLocationDialogResult value)
    {
        if (_model is null)
            return EquipmentEditorSaveResult.Reject("Оборудование не загружено.");
        var isEdit = existing is not null || value.Original is not null;
        if (!isEdit && _locations.LastOrDefault()?.LocationId == value.LocationId)
            return EquipmentEditorSaveResult.Reject("Оборудование уже находится в выбранном расположении.");
        if (isEdit)
        {
            if (value.Original is null || value.Original.EquipmentId != _model.Id)
                return EquipmentEditorSaveResult.Reject("Исходный период расположения не подтверждён. Запись не отправлена.");
            var fresh = existing ?? _locations.FirstOrDefault(item => item.FromUtc == value.Original.FromUtc);
            var originalError = value.Original.ValidateFresh(fresh,
                fresh is not null && ReferenceEquals(fresh, _locations.LastOrDefault()));
            if (originalError is not null)
                return EquipmentEditorSaveResult.Reject(originalError);
        }
        var version = ++_locationVersion;
        var equipmentId = _model.Id;
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
            LocationId = value.LocationId,
            FromUtc = value.Original?.FromUtc ?? fromUtc,
            ChangedAtUtc = value.Original?.ChangedAtUtc ?? default,
            ReturnDate = value.ReturnDate,
            Notes = value.Notes,
        };
        var responsible = !isEdit && requireResponsible && value.EmployeeId is not null
            ? new EquipmentResponsiblePeriod
            {
                EquipmentId = equipmentId,
                EmployeeId = value.EmployeeId.Value,
                FromUtc = fromUtc,
                Notes = string.Empty,
            }
            : null;
        return await SaveChildDraftAsync(
            isEdit ? "Изменение расположения" : "Добавление расположения", "Расположение сохранено.",
            () => isEdit
                ? Service.UpdateLocationAsync(location)
                : Service.AddLocationAsync(location, responsible, expectedActiveResponsible),
            async () =>
            {
                await RefreshLocationsAsync();
                await RefreshResponsiblesAsync();
            },
            () => !_disposed && version == _locationVersion && _model?.Id == equipmentId);
    }

    private async Task RefreshLocationsAsync()
    {
        if (_model is null)
            return;
        var locations = await Service.GetLocationsAsync(_model.Id);
        if (_disposed)
            return;
        _locations = locations.ToList();
        _model.LocationId = _locations.LastOrDefault()?.LocationId;
        UpdatePhoneSourceFilters();
        await RefreshLocationDisplayDatesAsync();
    }

    private async Task RefreshResponsiblesAsync()
    {
        if (_model is null)
            return;
        var responsibles = await Service.GetResponsiblesAsync(_model.Id);
        if (_disposed)
            return;
        _responsibles = responsibles.ToList();
        _model.EmployeeId = _responsibles.LastOrDefault(item => item.ToUtc is null)?.EmployeeId;
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
        await ShowDialogAsync<EquipmentResponsibleDialog>("Ответственный", parameters,
            draftKey: $"{mode}:{(existing?.FromUtc ?? original?.FromUtc)?.Ticks}", openingContext: opening);
    }

    private async Task<EquipmentEditorSaveResult> SaveResponsibleDraftAsync(
        EquipmentResponsibleDialogMode mode,
        EquipmentResponsiblePeriod? existing,
        EquipmentResponsibleDialogResult value)
    {
        if (_model is null)
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
        var equipmentId = _model.Id;
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

        async Task Write()
        {
            if (mode == EquipmentResponsibleDialogMode.Issue)
            {
                await Service.IssueResponsibleAsync(new EquipmentResponsiblePeriod
                {
                    EquipmentId = equipmentId,
                    EmployeeId = value.EmployeeId!.Value,
                    FromUtc = fromUtc!.Value,
                    ReturnDate = value.ReturnDate,
                    Notes = value.Notes,
                });
            }
            else if (mode == EquipmentResponsibleDialogMode.Edit)
            {
                var current = baseline!;
                current.ReturnDate = value.ReturnDate;
                current.Notes = value.Notes;
                await Service.UpdateResponsibleAsync(current);
            }
            else
            {
                var current = baseline!;
                current.ToUtc = toUtc;
                var next = value.EmployeeId is null ? null : new EquipmentResponsiblePeriod
                {
                    EquipmentId = equipmentId,
                    EmployeeId = value.EmployeeId.Value,
                    FromUtc = fromUtc!.Value,
                    ReturnDate = value.ReturnDate,
                    Notes = value.Notes,
                };
                var location = value.LocationId is null ? null : new EquipmentLocationPeriod
                {
                    EquipmentId = equipmentId,
                    LocationId = value.LocationId.Value,
                    FromUtc = toUtc!.Value,
                    ReturnDate = null,
                    Notes = string.Empty,
                };
                await Service.WithdrawResponsibleAsync(current, next, location, value.FromLocal?.Date);
            }
        }

        return await SaveChildDraftAsync(
            "Сохранение ответственного", "Сведения об ответственном сохранены.", Write,
            async () =>
            {
                await RefreshResponsiblesAsync();
                await RefreshLocationsAsync();
            },
            () => !_disposed && version == _locationVersion && _model?.Id == equipmentId);
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
        await ShowDialogAsync<EquipmentAccountDialog>(
            "Учётная запись", parameters, new DialogOptionsEx { DragMode = MudDialogDragMode.Simple },
            existing?.Login ?? string.Empty);
    }

    private Task<EquipmentEditorSaveResult> SaveAccountDraftAsync(
        EquipmentAccount? existing,
        EquipmentAccountDialogResult value)
    {
        if (_model is null)
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Оборудование не загружено."));
        var equipmentId = _model.Id;
        var version = ++_accountVersion;
        var account = new EquipmentAccount
        {
            EquipmentId = equipmentId,
            Login = value.Login,
            Password = value.Password,
            LimitedRights = value.LimitedRights ? (byte)1 : (byte)0,
            Notes = value.Notes,
        };
        return SaveChildDraftAsync(
            existing is null ? "Добавление учётной записи" : "Изменение учётной записи",
            "Учётная запись сохранена.",
            () => existing is null ? Service.AddAccountAsync(account) : Service.UpdateAccountAsync(account),
            RefreshAccountsAsync,
            () => !_disposed && version == _accountVersion && _model?.Id == equipmentId);
    }

    private async Task DeleteAccountAsync(EquipmentAccount account)
    {
        if (_model is null || !_canEdit || !ShowAccounts || _saving ||
            !await ConfirmAsync("Удаление учётной записи", $"Удалить учётную запись «{account.Login}»?"))
            return;
        var version = ++_accountVersion;
        var equipmentId = _model.Id;
        var result = await SaveChildDraftAsync(
            "Удаление учётной записи", "Учётная запись удалена.",
            () => Service.DeleteAccountAsync(account), RefreshAccountsAsync,
            () => !_disposed && version == _accountVersion && _model?.Id == equipmentId);
        if (!result.CanClose && result.Message is not null)
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
        var latest = _computerHistory.LastOrDefault();
        var memoryBanks = _selectedModel?.MemoryBanks ?? 0;
        var clientNow = await GetClientNowAsync();
        if (!IsCurrent(opening))
            return;
        var parameters = new DialogParameters<EquipmentComputerDialog>
        {
            { dialog => dialog.EquipmentId, opening.EquipmentId },
            { dialog => dialog.Date, clientNow.Date },
            { dialog => dialog.ExpectedExisting, latest?.Date == clientNow.Date },
            { dialog => dialog.ChangedAt, latest?.Date == clientNow.Date ? latest.ChangedAt : default },
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
            { dialog => dialog.Save, (Func<EquipmentComputerSnapshot, Task<EquipmentEditorSaveResult>>)(snapshot => SaveComputerDraftAsync(snapshot, opening)) },
        };
        await ShowDialogAsync<EquipmentComputerDialog>(
            "Компьютер", parameters, new DialogOptionsEx { MaxWidth = MaxWidth.Medium, FullWidth = true },
            openingContext: opening);
    }

    private Task<EquipmentEditorSaveResult> SaveComputerDraftAsync(
        EquipmentComputerSnapshot snapshot,
        EquipmentEditorOpening opening)
    {
        if (!IsCurrent(opening) || snapshot.EquipmentId is not 0 && snapshot.EquipmentId != opening.EquipmentId)
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Запись не отправлена."));
        var version = ++_computerVersion;
        snapshot.EquipmentId = opening.EquipmentId;
        return SaveChildDraftAsync(
            "Сохранение конфигурации компьютера", "Конфигурация компьютера сохранена.",
            async () =>
            {
                var today = (await GetClientNowAsync()).Date;
                if (!IsCurrent(opening) || version != _computerVersion)
                    throw new InvalidOperationException("Контекст оборудования изменился. Запись не отправлена.");
                await Service.SaveComputerPhasedAsync(snapshot, today);
            }, RefreshComputerAsync,
            () => IsCurrent(opening) && version == _computerVersion);
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
        await ShowDialogAsync<EquipmentSimDialog>(
            "SIM-карта", parameters, new DialogOptionsEx { DragMode = MudDialogDragMode.Simple },
            openingContext: opening);
    }

    private async Task<EquipmentEditorSaveResult> SaveSimDraftAsync(
        EquipmentSimDialogResult value,
        EquipmentEditorOpening opening)
    {
        if (!IsCurrent(opening))
            return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Запись не отправлена.");
        var version = ++_simVersion;
        int? duplicateId;
        DateTime activatedUtc;
        try
        {
            duplicateId = await Service.GetDuplicateSimIdAsync(opening.EquipmentId, value.PhoneNumber);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            return EquipmentEditorSaveResult.Reject($"Не удалось проверить дубликат SIM. Запись не отправлена. {ex.Message}");
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
        var sim = new EquipmentSimCard
        {
            EquipmentId = opening.EquipmentId,
            PhoneNumber = value.PhoneNumber,
            ActivatedAtUtc = activatedUtc,
            ContractId = value.ContractId,
            PersonalDataPersonId = value.PersonalDataPersonId,
            Secrets = value.Secrets,
        };
        return await SaveChildDraftAsync(
            "Сохранение SIM", "Сведения SIM сохранены.",
            () => Service.SaveSimAsync(sim, duplicateId), RefreshSimAsync,
            () => IsCurrent(opening) && version == _simVersion);
    }

    private async Task DeleteSimAsync()
    {
        if (_model is null || !CanEditSim || _saving ||
            !await ConfirmAsync("Удаление сведений SIM", "Удалить сведения SIM без удаления самого оборудования?"))
            return;
        var version = ++_simVersion;
        var equipmentId = _model.Id;
        var result = await SaveChildDraftAsync(
            "Удаление сведений SIM", "Сведения SIM удалены.",
            () => Service.DeleteSimAsync(equipmentId), RefreshSimAsync,
            () => !_disposed && version == _simVersion && _model?.Id == equipmentId);
        if (!result.CanClose && result.Message is not null)
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
            { dialog => dialog.Save, (Func<EquipmentSimService, Task<EquipmentEditorSaveResult>>)(service => SaveSimServiceDraftAsync(sim.PhoneNumber, service)) },
        };
        await ShowDialogAsync<EquipmentSimServiceDialog>("Дополнительная услуга SIM", parameters,
            draftKey: existing?.Id.ToString(), openingContext: opening);
    }

    private Task<EquipmentEditorSaveResult> SaveSimServiceDraftAsync(
        string phoneNumber, EquipmentSimService service)
    {
        if (_model is null)
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Оборудование не загружено."));
        var version = ++_simServiceVersion;
        var equipmentId = _model.Id;
        service.EquipmentId = equipmentId;
        service.PhoneNumber = phoneNumber;
        return SaveChildDraftAsync(
            "Сохранение дополнительной услуги SIM", "Дополнительная услуга SIM сохранена.",
            async () =>
            {
                var businessToday = (await GetClientNowAsync()).Date;
                if (_disposed || version != _simServiceVersion || _model?.Id != equipmentId)
                    throw new InvalidOperationException("Контекст оборудования изменился. Запись не отправлена.");
                await Service.SaveSimServiceAsync(service, businessToday);
            }, RefreshSimServicesAsync,
            () => !_disposed && version == _simServiceVersion && _model?.Id == equipmentId);
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

    private async Task RefreshSimServicesAsync()
    {
        if (_model is null)
            return;
        var version = _simServiceVersion;
        var equipmentId = _model.Id;
        var businessToday = (await GetClientNowAsync()).Date;
        if (_disposed || version != _simServiceVersion || _model.Id != equipmentId)
            return;
        var services = await Service.GetSimServicesAsync(
            equipmentId, _showServiceHistory, _showCompanyRequiredServices, businessToday);
        if (_disposed || version != _simServiceVersion || _model.Id != equipmentId)
            return;
        _clientToday = businessToday;
        _simServices = services.ToList();
        await RefreshLocationDisplayDatesAsync();
    }

    private async Task OpenLicenseDialogAsync()
    {
        if (_model is null || !_canEditLicense || _saving)
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
        await ShowDialogAsync<EquipmentLicenseDialog>("Лицензия", parameters);
    }

    private Task<EquipmentEditorSaveResult> SaveLicenseDraftAsync(EquipmentLicense license)
    {
        if (_model is null)
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Оборудование не загружено."));
        var version = ++_licenseVersion;
        var equipmentId = _model.Id;
        license.EquipmentId = equipmentId;
        return SaveChildDraftAsync(
            "Сохранение лицензии", "Лицензия сохранена.",
            () => Service.SaveLicenseAsync(license,
                _licenseTypes.FirstOrDefault(item => item.Id == license.LicenseTypeId)),
            RefreshLicensesAsync,
            () => !_disposed && version == _licenseVersion && _model?.Id == equipmentId);
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
            draftKey: existing?.Id.ToString());
    }

    private Task<EquipmentEditorSaveResult> SaveSoftwareDraftAsync(EquipmentSoftwareInstallation installation)
    {
        if (_model is null)
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Оборудование не загружено."));
        var version = ++_softwareVersion;
        var equipmentId = _model.Id;
        installation.EquipmentId = equipmentId;
        return SaveChildDraftAsync(
            "Сохранение установленного ПО", "Установленное ПО сохранено.",
            () => Service.SaveSoftwareInstallationAsync(installation), RefreshSoftwareAsync,
            () => !_disposed && version == _softwareVersion && _model?.Id == equipmentId);
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
        var parameters = new DialogParameters<EquipmentAccountingDialog>
        {
            { dialog => dialog.Value, value },
            { dialog => dialog.Save, SaveAccountingDraftAsync },
        };
        await ShowDialogAsync<EquipmentAccountingDialog>(
            "Бухгалтерский учёт", parameters,
            new DialogOptionsEx { MaxWidth = MaxWidth.Medium, FullWidth = true },
            existing?.Id.ToString());
    }

    private async Task<EquipmentEditorSaveResult> SaveAccountingDraftAsync(EquipmentAccounting accounting)
    {
        if (_model is null || !_canUseAccounting)
            return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Откройте форму повторно.");
        var version = ++_accountingVersion;
        var equipmentId = _model.Id;
        accounting.EquipmentId = equipmentId;
        return await SaveChildDraftAsync(
            "Сохранение бухгалтерского учёта", "Бухгалтерский учёт сохранён.",
            () => Service.SaveAccountingAsync(accounting), RefreshAccountingAsync,
            () => !_disposed && version == _accountingVersion && _model?.Id == equipmentId);
    }

    private static bool IsUnknownWriteException(Microsoft.Data.SqlClient.SqlException exception) =>
        exception.Number is -2 or -1 or 2 or 53 or 64 or 121 or 1231 or 10054 or 10060 or 40143 or 40501 or 40613;

    private async Task<EquipmentEditorSaveResult> SaveChildDraftAsync(
        string operationName,
        string successMessage,
        Func<Task> write,
        Func<Task> refresh,
        Func<bool> contextIsCurrent)
    {
        _saving = true;
        _validationMessage = null;
        var original = OriginalSnapshotModel();
        StateHasChanged();
        long? draftRevision = null;
        try
        {
            try
            {
                draftRevision = await BeginDraftOperationAsync(operationName);
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
            {
                return EquipmentEditorSaveResult.Reject($"Черновик не сохранён. Команда «{operationName}» не отправлена. {ex.Message}");
            }
            try
            {
                if (!contextIsCurrent())
                {
                    await RejectDraftOperationAsync(draftRevision);
                    return EquipmentEditorSaveResult.Reject("Контекст оборудования изменился. Команда не отправлена.");
                }
                await write();
            }
            catch (EquipmentOperationException ex) when (ex.Phase == EquipmentWritePhase.Unknown)
            {
                await UnknownDraftOperationAsync(draftRevision);
                return EquipmentEditorSaveResult.Unknown(
                    $"Результат команды «{operationName}» неизвестен. Повторная запись заблокирована; обновите форму и сверьте данные.");
            }
            catch (EquipmentOperationException ex) when (ex.Phase == EquipmentWritePhase.RefreshFailed)
            {
                await CommitDraftOperationAsync(draftRevision);
                return EquipmentEditorSaveResult.RefreshFailed(
                    $"Запись «{operationName}» подтверждена, но последующее чтение не выполнено. Повторяйте только обновление данных. {ex.Message}");
            }
            catch (EquipmentOperationException ex)
            {
                await RejectDraftOperationAsync(draftRevision);
                return EquipmentEditorSaveResult.Reject(ex.Message);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (IsUnknownWriteException(ex))
            {
                await UnknownDraftOperationAsync(draftRevision);
                return EquipmentEditorSaveResult.Unknown(
                    $"Результат команды «{operationName}» неизвестен. Повторная запись заблокирована; обновите форму и сверьте данные.");
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                await RejectDraftOperationAsync(draftRevision);
                return EquipmentEditorSaveResult.Reject($"Команда «{operationName}» не выполнена. Подробности — в сообщении базы данных.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                await RejectDraftOperationAsync(draftRevision);
                return EquipmentEditorSaveResult.Reject(ex.Message);
            }

            _statusMessage = successMessage;
            EquipmentEditorSaveResult outcome;
            if (!contextIsCurrent())
            {
                outcome = EquipmentEditorSaveResult.RefreshFailed("Запись подтверждена, но контекст формы изменился. Обновите данные.");
            }
            else
            {
                try
                {
                    await RefreshMainAfterChildWriteAsync(original);
                    await refresh();
                    outcome = EquipmentEditorSaveResult.Commit();
                }
                catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
                {
                    _validationMessage = $"{operationName}: запись подтверждена, но экран не обновлён. Повторите только обновление данных.";
                    outcome = EquipmentEditorSaveResult.RefreshFailed(_validationMessage);
                }
            }
            try
            {
                await CommitDraftOperationAsync(draftRevision);
                return outcome;
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
            {
                return EquipmentEditorSaveResult.RefreshFailed(
                    $"Запись «{operationName}» подтверждена, но локальная фиксация результата не завершена. Повторяйте только чтение/сверку, не бизнес-запись. {ex.Message}");
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
        if (_model is null || !_canUseAccounting || _saving || _accounting.LastOrDefault()?.Id != latest.Id ||
            !await ConfirmAsync("Удаление бухгалтерского учёта", "Удалить последнюю запись бухгалтерского учёта?"))
            return;
        var version = ++_accountingVersion;
        var equipmentId = _model.Id;
        var result = await SaveChildDraftAsync(
            "Удаление бухгалтерского учёта", "Запись бухгалтерского учёта удалена.",
            () => Service.DeleteAccountingAsync(equipmentId, latest.Id), RefreshAccountingAsync,
            () => !_disposed && version == _accountingVersion && _model?.Id == equipmentId);
        if (!result.CanClose && result.Message is not null)
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

    private async Task ConfigurePhoneEquipmentAsync()
    {
        if (!CanConfigurePhone || _model is null || _saving)
            return;
        if (IsModified)
        {
            _validationMessage = "Сохраните оборудование перед настройкой или отмените изменения.";
            StateHasChanged();
            return;
        }
        var model = _model;
        var version = ++_operationVersion;
        _saving = true;
        _validationMessage = null;
        _warningMessage = null;
        _statusMessage = null;
        long? draftRevision = null;
        try
        {
            draftRevision = await BeginDraftOperationAsync("configure-phone-equipment");
            var attempt = await Service.ConfigurePhoneEquipmentAttemptAsync(model.Id);
            var errors = attempt.Operations.Where(result => result.Phase != PhoneSyncPhase.Succeeded).ToList();
            var unknown = errors.Any(result => result.IsUnknown);
            _pendingPhoneSync = errors.Count > 0
                ? new PendingPhoneSync(model.Id, [], ClonePhones(_phones), errors.Select(result => result.Operation).ToArray(),
                    unknown, unknown ? string.Join(" ", errors.Where(result => result.IsUnknown).Select(result => result.Error)) : null)
                {
                    Attempt = attempt,
                }
                : null;
            if (unknown)
                await UnknownDraftOperationAsync(draftRevision);
            else
                await CommitDraftOperationAsync(draftRevision);
            if (!IsCurrentSave(model, version))
                return;
            if (errors.Count > 0)
                _warningMessage = "Оборудование настроено не полностью. " + string.Join(" ", errors.Select(result => result.Error));
            else
                _statusMessage = "Настройка оборудования выполнена.";
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            if (draftRevision is not null)
                _draftOperationUnresolved = true;
            if (_pendingPhoneSync is not null)
                _pendingPhoneSync = _pendingPhoneSync with { Unknown = true, UnknownMessage = ex.Message };
            if (IsCurrentSave(model, version))
                _warningMessage = "Канал хранилища потерян. Результат команды не подтверждён; автоматический повтор заблокирован до сверки.";
        }
        catch (EquipmentOperationException ex) when (ex.Phase == EquipmentWritePhase.Unknown)
        {
            await UnknownDraftOperationAsync(draftRevision);
            if (IsCurrentSave(model, version))
                _warningMessage = "Результат настройки оборудования неизвестен. Автоматический повтор не выполняется; сверьте состояние АТС.";
        }
        catch (EquipmentOperationException ex)
        {
            await RejectDraftOperationAsync(draftRevision);
            if (IsCurrentSave(model, version))
                _warningMessage = $"Настройка оборудования не выполнена: {ex.Message}";
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            await UnknownDraftOperationAsync(draftRevision);
            if (IsCurrentSave(model, version))
                _warningMessage = "Результат настройки оборудования неизвестен. Автоматический повтор не выполняется; сверьте состояние АТС.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            await RejectDraftOperationAsync(draftRevision);
            if (IsCurrentSave(model, version))
                _warningMessage = $"Настройка оборудования не выполнена: {ex.Message}";
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
        if (_model is null || !_canChangeNetwork || _saving)
            return;
        if (!attach && !await ConfirmAsync("Отключение сети", "Отключить оборудование от рабочей сети?"))
            return;
        if (IsModified)
        {
            await SaveAsync(false);
            if (IsModified || _disposed || _model is null)
                return;
        }
        var version = ++_networkVersion;
        var equipmentId = _model.Id;
        var result = await SaveChildDraftAsync(
            attach ? "Подключение к локальной сети" : "Отключение от локальной сети",
            attach ? "Оборудование подключено к рабочей сети." : "Оборудование отключено от рабочей сети.",
            () => attach ? Service.AttachNetworkAsync(equipmentId) : Service.DetachNetworkAsync(equipmentId),
            async () =>
            {
                var count = await Service.GetNetworkAttachmentCountAsync(equipmentId);
                if (!_disposed && version == _networkVersion && _model?.Id == equipmentId)
                    _networkAttachmentCount = count;
            },
            () => !_disposed && version == _networkVersion && _model?.Id == equipmentId);
        if (!result.CanClose && result.Message is not null)
        {
            _validationMessage = result.Message;
            StateHasChanged();
        }
    }

    private UpsEquipmentSelectDataSource UpsSource => Services.GetRequiredKeyedService<UpsEquipmentSelectDataSource>("ups");

    private async Task OpenUpsLinkDialogAsync()
    {
        if (_model is null || !_canManageUps || _saving)
            return;
        var result = await ShowDialogAsync<EquipmentUpsLinkDialog>(
            "Подключение UPS", new DialogParameters<EquipmentUpsLinkDialog>());
        if (result is null || result.Canceled || result.Data is not int upsId)
            return;
        await AddUpsLinkAsync(upsId);
    }

    private async Task AddUpsLinkAsync(int upsId)
    {
        if (_model is null || !_canManageUps || _saving)
            return;
        var version = ++_upsVersion;
        var equipmentId = _model.Id;
        var result = await SaveChildDraftAsync(
            "Подключение UPS", "UPS подключён.",
            () => Service.AddUpsLinkAsync(upsId, equipmentId), RefreshUpsLinksAsync,
            () => !_disposed && version == _upsVersion && _model?.Id == equipmentId);
        if (!result.CanClose && result.Message is not null)
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
        var equipmentId = _model.Id;
        List<UpsServerLink> originalServers;
        List<UpsTest> originalTests;
        List<UpsServerLink> dialogServers;
        var dialogTests = new List<UpsTest>();
        try
        {
            originalServers = (await Service.GetUpsServersAsync(link.UpsEquipmentId)).ToList();
            originalTests = (await Service.GetUpsTestsAsync(link.UpsEquipmentId)).ToList();
            if (_disposed || version != _upsVersion || _model?.Id != equipmentId)
                return;
            dialogServers = _restoredUpsServers ?? CloneUpsServers(originalServers);
            if (_restoredUpsTests is not null)
            {
                dialogTests = _restoredUpsTests;
            }
            else
            {
                foreach (var test in originalTests)
                {
                    var utc = DateTime.SpecifyKind(test.TestedAt, DateTimeKind.Utc);
                    var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
                    dialogTests.Add(CloneUpsTest(test, utc.AddMinutes(offset)));
                }
            }
            if (_disposed || version != _upsVersion || _model?.Id != equipmentId)
                return;
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _upsVersion && _model?.Id == equipmentId)
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
        _restoredUpsServers = null;
        _restoredUpsTests = null;
        var parameters = new DialogParameters<EquipmentUpsDialog>
        {
            { dialog => dialog.UpsEquipmentId, link.UpsEquipmentId },
            { dialog => dialog.Servers, dialogServers },
            { dialog => dialog.Tests, dialogTests },
            { dialog => dialog.GetNow, (Func<Task<DateTime>>)GetClientNowAsync },
            { dialog => dialog.DraftChanged, PersistDraftAsync },
            { dialog => dialog.NestedTestChanged, async id =>
            {
                if (_activeEditor is not null)
                    _activeEditor = _activeEditor with { NestedTestId = id };
                await PersistDraftAsync();
            } },
            { dialog => dialog.RestoreTestId, _draftRestoring ? _activeEditor?.NestedTestId : null },
            { dialog => dialog.NotifyTestOpened, () =>
            {
                _restoredEditorOpened.TrySetResult();
                return Task.CompletedTask;
            } },
            { dialog => dialog.Save, (Func<EquipmentUpsDialogResult, Task<EquipmentEditorSaveResult>>)(value => SaveUpsDraftAsync(link, originalServers, originalTests, value)) },
        };
        await ShowDialogAsync<EquipmentUpsDialog>("Подключение UPS", parameters,
            draftKey: link.UpsEquipmentId.ToString());
    }

    private async Task<EquipmentEditorSaveResult> SaveUpsDraftAsync(
        UpsServerLink link,
        IReadOnlyCollection<UpsServerLink> originalServers,
        IReadOnlyCollection<UpsTest> originalTests,
        EquipmentUpsDialogResult value)
    {
        if (_model is null)
            return EquipmentEditorSaveResult.Reject("Оборудование не загружено.");
        var equipmentId = _model.Id;
        var version = _upsVersion;
        var currentTests = new List<UpsTest>();
        var originalTestsById = originalTests.ToDictionary(item => item.Id);
        try
        {
            foreach (var test in value.Tests)
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
        return await SaveChildDraftAsync(
            "Сохранение подключения UPS", "Подключение UPS сохранено.",
            () => Service.SaveUpsDetailsAsync(
                link.UpsEquipmentId, equipmentId, originalServers, value.Servers, originalTests, currentTests),
            RefreshUpsLinksAsync,
            () => !_disposed && version == _upsVersion && _model?.Id == equipmentId);
    }

    private async Task RefreshUpsLinksAsync()
    {
        if (_model is null)
            return;
        var version = _upsVersion;
        var equipmentId = _model.Id;
        var links = await Service.GetUpsLinksAsync(equipmentId);
        if (_disposed || version != _upsVersion || _model.Id != equipmentId)
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
        if (_model is null || !_loaded || !_canEdit || _loading || _saving)
            return;
        if (_pendingPhoneSync is not null)
        {
            if (!await RetryPendingPhoneSyncAsync())
                return;
            if (!IsModified)
            {
                if (closeAfterSave)
                    await CloseAsync();
                return;
            }
        }
        if (!IsModified)
        {
            if (closeAfterSave)
                await CloseAsync();
            return;
        }
        var model = _model;
        var version = ++_operationVersion;
        var phonesModified = PhonesModified;
        _saving = true;
        _validationMessage = null;
        _warningMessage = null;
        StateHasChanged();
        long? draftRevision = null;
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
            var operationKind = phonesModified ? "equipment-phone-save" : "equipment-save";
            draftRevision = await BeginDraftOperationAsync(operationKind,
                new EquipmentMainDraftIntent(_originalSnapshot, Snapshot()));
            if (phonesModified)
                phoneIntent = await Service.SaveWithPhonesAsync(model, _originalPhones, _phones);
            else
                await Service.UpdateAsync(model);
            writeCommitted = true;
            _mainConflictMessage = null;
            _originalSnapshot = Snapshot();
            if (!IsCurrentSave(model, version))
            {
                await CommitDraftOperationAsync(draftRevision);
                return;
            }
            SetAccountingBaseline();
            var warnings = new List<string>();
            var unknownSync = false;
            if (phoneIntent is not null)
            {
                var pending = new PendingPhoneSync(model.Id, ClonePhones(_originalPhones), ClonePhones(_phones))
                {
                    Attempt = new PhoneSyncAttempt(phoneIntent, []),
                };
                try
                {
                    var attempt = await Service.SynchronizePhoneAttemptAsync(phoneIntent);
                    var errors = attempt.Operations.Where(result => result.Phase != PhoneSyncPhase.Succeeded).ToList();
                    warnings.AddRange(errors.Select(result => result.Error!));
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
                catch (EquipmentOperationException ex) when (ex.Phase == EquipmentWritePhase.Unknown)
                {
                    unknownSync = true;
                    warnings.Add($"Результат синхронизации АТС неизвестен: {ex.Message}");
                    pending = pending with { Unknown = true, UnknownMessage = ex.Message };
                }
                catch (Microsoft.Data.SqlClient.SqlException)
                {
                    warnings.Add("Синхронизация с АТС не выполнена. Подробности — в сообщении базы данных.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
                {
                    warnings.Add($"Синхронизация с АТС не выполнена: {ex.Message}");
                }
                _pendingPhoneSync = warnings.Count > 0 || unknownSync ? pending : null;
            }
            _originalPhones = ClonePhones(_phones);
            if (unknownSync)
                await UnknownDraftOperationAsync(draftRevision);
            else
                await CommitDraftOperationAsync(draftRevision);
            if (!IsCurrentSave(model, version))
                return;
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", _pageCts.Token);
            if (!IsCurrentSave(model, version))
                return;
            if (closeAfterSave && warnings.Count == 0)
            {
                await CloseAsync();
                return;
            }
            await LoadCurrentAsync(Id);
            if (warnings.Count > 0 && !_disposed)
                _warningMessage = "Основные данные и телефонные связи сохранены. " + string.Join(" ", warnings);
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrentSave(model, version))
        {
        }
        catch (EquipmentOperationException ex) when (ex.Phase == EquipmentWritePhase.Unknown)
        {
            if (!IsCurrentSave(model, version))
                return;
            await UnknownDraftOperationAsync(draftRevision);
            _validationMessage = "Результат сохранения оборудования неизвестен. Автоматический повтор не выполняется; обновите форму и сверьте данные.";
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
                await RejectDraftOperationAsync(draftRevision);
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
            else if (IsUnknownWriteException(ex))
            {
                await UnknownDraftOperationAsync(draftRevision);
                _validationMessage = "Результат сохранения оборудования неизвестен. Автоматический повтор не выполняется; обновите форму и сверьте данные.";
            }
            else
            {
                await RejectDraftOperationAsync(draftRevision);
                _validationMessage = "Оборудование не сохранено. Подробности — в сообщении базы данных.";
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            if (!IsCurrentSave(model, version))
                return;
            if (writeCommitted)
            {
                _statusMessage = "Оборудование сохранено.";
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
                    await RejectDraftOperationAsync(draftRevision);
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
        long? draftRevision = null;
        try
        {
            await Task.Delay(100);
            draftRevision = await BeginDraftOperationAsync("retry-phone-sync");
            PhoneSyncAttempt? attempt = null;
            List<(PhoneSyncOperation Operation, string? Error, bool Unknown)> errors;
            if (pending.Attempt is not null)
            {
                attempt = await Service.RetryPhoneSyncAttemptAsync(pending.Attempt);
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
            _pendingPhoneSync = errors.Count > 0
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
                await UnknownDraftOperationAsync(draftRevision);
            else
                await CommitDraftOperationAsync(draftRevision);
            if (!IsCurrentSave(model, version))
                return false;
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
            if (draftRevision is not null)
                _draftOperationUnresolved = true;
            if (_pendingPhoneSync is not null)
                _pendingPhoneSync = _pendingPhoneSync with { Unknown = true, UnknownMessage = ex.Message };
            if (IsCurrentSave(model, version))
                _warningMessage = "Канал хранилища потерян. Результат повторной команды не подтверждён; новый вызов АТС заблокирован до сверки.";
            return false;
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrentSave(model, version))
        {
            return false;
        }
        catch (EquipmentOperationException ex) when (ex.Phase == EquipmentWritePhase.Unknown)
        {
            await UnknownDraftOperationAsync(draftRevision);
            if (IsCurrentSave(model, version))
                _warningMessage = "Результат повторной синхронизации АТС неизвестен. Автоматический повтор не выполняется; сверьте состояние.";
            return false;
        }
        catch (EquipmentOperationException ex)
        {
            await RejectDraftOperationAsync(draftRevision);
            if (IsCurrentSave(model, version))
                _warningMessage = $"Телефонные связи сохранены, синхронизация не выполнена: {ex.Message}";
            return false;
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            await UnknownDraftOperationAsync(draftRevision);
            if (IsCurrentSave(model, version))
                _warningMessage = "Результат повторной синхронизации АТС неизвестен. Автоматический повтор не выполняется; сверьте состояние.";
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            await RejectDraftOperationAsync(draftRevision);
            if (IsCurrentSave(model, version))
                _warningMessage = $"Телефонные связи сохранены, синхронизация не выполнена: {ex.Message} Повторите через «Применить».";
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
                _warningMessage = "Read-only сверка не подтвердила состояние телефонных связей. Unknown сохранён; повтор запрещён.";
            }
            else if (unknown)
            {
                _warningMessage = "Состояние связей в БД совпадает, но исход внешней ATS-операции не доказан. Unknown сохранён; blind replay запрещён.";
            }
            else if (canRetry)
            {
                _warningMessage = "Read-only сверка завершена. Можно повторить только подтверждённые Failed/NotSent операции через «Применить».";
            }
            else
            {
                if (_draftOperationUnresolved && _draftOperation is not null)
                    await CommitDraftOperationAsync(_draftOperation.Revision);
                _pendingPhoneSync = null;
                _warningMessage = null;
                _statusMessage = "Read-only сверка завершена. Повторная ATS-команда не выполнялась.";
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            _warningMessage = $"Не удалось выполнить read-only сверку ATS. Unknown сохранён. {ex.Message}";
        }
        StateHasChanged();
    }

    private bool IsCurrentSave(EquipmentEntity model, int version) => !_disposed && version == _operationVersion && ReferenceEquals(model, _model);

    private async Task<DateTime> GetClientNowAsync()
    {
        var utcNow = DateTime.UtcNow;
        var offset = await JS.InvokeAsync<int>(
            "clayGridTimeZone.getOffsetMinutes", _pageCts.Token, utcNow.ToString("O"));
        return utcNow.AddMinutes(offset);
    }

    private async Task<DateTime?> ConvertWriteOffToUtcAsync()
    {
        if (_writeOffDate is null)
            return null;
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
        else if (!freshWriteOffEqualsBase && !localWriteOffEqualsFresh)
        {
            conflicts.Add(nameof(EquipmentEntity.WriteOffAtUtc));
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
        _statusMessage = keepLocal
            ? "Конфликт разрешён: сохранены значения черновика."
            : "Конфликт разрешён: конфликтные поля заменены данными из БД, остальной черновик сохранён.";
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

    private async Task RefreshAsync()
    {
        if (_model is null || _loading || _saving)
            return;
        if (IsModified && !await ConfirmAsync("Обновление", RefreshConfirmation()))
            return;
        _warningMessage = null;
        if (_pendingPhoneSync?.Unknown != true && !_draftOperationUnresolved)
            await DiscardDraftAsync();
        await LoadCurrentAsync(Id);
        await InitializeDraftAsync();
    }

    private async Task CancelAsync()
    {
        if (IsModified && !await ConfirmAsync("Закрытие формы", CloseConfirmation("Закрыть форму без сохранения изменений?")))
            return;
        await DiscardDraftAsync();
        await CloseAsync();
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

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_allowNavigation)
        {
            _allowNavigation = false;
            return;
        }
        if (_saving)
        {
            context.PreventNavigation();
            return;
        }
        if (!IsModified)
            return;
        context.PreventNavigation();
        if (!await ConfirmAsync("Переход на другую страницу", CloseConfirmation("Покинуть форму без сохранения изменений?")))
            return;
        await DiscardDraftAsync();
        _allowNavigation = true;
        Navigation.NavigateTo(context.TargetLocation);
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
        EquipmentEditorOpening? openingContext = null) where T : ComponentBase
    {
        openingContext ??= _model is null ? null : CaptureEditorOpening();
        var generation = openingContext?.Generation ?? ++_editorGeneration;
        var draftStateToRestore = _activeEditor?.Kind == typeof(T).Name && _activeEditor.Key == draftKey
            ? _restoredEditorState
            : null;
        EquipmentEditorContext? editorContext = null;
        _activeEditorContext = null;
        if (openingContext is not null && typeof(T).GetProperty(nameof(EquipmentEditorDialogBase<object>.EditorContext)) is not null)
        {
            editorContext = new EquipmentEditorContext(openingContext.EquipmentId, generation,
                () => IsCurrentCore(openingContext))
            {
                DraftState = draftStateToRestore,
                DraftChanged = async _ =>
                {
                    if (ReferenceEquals(_activeEditorContext, editorContext) && IsCurrentCore(openingContext))
                        await PersistDraftAsync();
                },
                RawRestore = async () =>
                {
                    if (_draftRegistration is not null && ReferenceEquals(_activeEditorContext, editorContext) && IsCurrentCore(openingContext))
                    {
                        var result = await _draftRegistration.ApplyRawWithResultAsync(ActiveDraftScope ?? "main", _pageCts.Token);
                        if (result?.Pending?.Count > 0)
                            throw new InvalidOperationException("Восстановление полей редактора ещё не завершено. Повторите чтение прав доступа.");
                    }
                },
                ReadinessChanged = async readiness =>
                {
                    if (readiness == EquipmentEditorReadiness.Failed && _draftRegistration is not null &&
                        ReferenceEquals(_activeEditorContext, editorContext) && IsCurrentCore(openingContext))
                        await _draftRegistration.ReportRestoreScopeFailureAsync(ActiveDraftScope ?? "main", _pageCts.Token);
                },
            };
            openingContext.EditorContext = editorContext;
            if (draftStateToRestore is not null)
                _restoredEditorState = null;
            _activeEditorContext = editorContext;
            parameters.Add(nameof(EquipmentEditorDialogBase<object>.EditorContext), editorContext);
        }
        options ??= new DialogOptionsEx();
        options.DragMode = MudDialogDragMode.Simple;
        options.DialogAppearance ??= new MudBlazor.Extensions.Core.MudExAppearance();
        options.DialogAppearance.Class = $"{options.DialogAppearance.Class} equipment-draft-dialog-{_draftOwner}".Trim();
        var nestedTestId = _draftRestoring ? _activeEditor?.NestedTestId : null;
        _activeEditor = new EquipmentDraftEditor(typeof(T).Name, draftKey, nestedTestId);
        var draftScope = ActiveDraftScope;
        DialogResult? result = null;
        await PersistDraftAsync();
        await OpenDraftScopeAsync(draftScope);
        try
        {
            if (openingContext is not null && !IsCurrent(openingContext))
                return null;
            var dialog = await DialogService.ShowExAsync<T>(title, parameters, options);
            if (_activeEditor?.NestedTestId is null)
                _restoredEditorOpened.TrySetResult();
            if (_draftRegistration is not null && typeof(T) != typeof(EquipmentAccountDialog) && typeof(T) != typeof(EquipmentSimDialog))
                await _draftRegistration.ApplyRawWithResultAsync(draftScope, _pageCts.Token);
            result = await dialog.Result;
            if (openingContext is not null && !IsCurrent(openingContext))
                return null;
            return result;
        }
        finally
        {
            editorContext?.Invalidate();
            if (result is null || result.Canceled)
                await DiscardDraftScopeAsync(draftScope);
            else
                await OpenDraftScopeAsync("main");
            if (_draftOperationUnresolved && draftScope == _draftOperationScope)
                _restoredEditorState = editorContext?.CaptureDraftState() ?? _restoredEditorState;
            else
                _activeEditor = null;
            _activeEditorContext = null;
            _activeUpsServers = null;
            _activeUpsTests = null;
            await PersistDraftAsync();
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
        _pageCts.Cancel();
        if (_draftRegistration is not null)
            await _draftRegistration.DisposeAsync();
        _draftRegistration = null;
        _pageCts.Dispose();
    }
}
