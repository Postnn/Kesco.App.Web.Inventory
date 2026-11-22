using System.Text.Json;
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
using Microsoft.JSInterop;
using MudBlazor;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Редактирование основной записи оборудования.</summary>
public partial class EquipmentForm : IDisposable
{
    private const int InventorySpecialistRoleId = 43;
    private const int SimSpecialistRoleId = 42;
    private const int LicenseSpecialistRoleId = 44;
    private const int DutyEngineerRoleId = 102;
    private const int SupportSpecialistRoleId = 103;
    private const int SimEquipmentTypeId = 27;
    private const int OnHandsLocationId = 55;

    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "equipment-form", MaxWidth = "100%" };
    private readonly ClaySelectOptions _typeOptions = new() { Required = true, AutoSelectSingle = true, MaxItems = 300, Placeholder = "Выберите тип оборудования" };
    private readonly ClaySelectOptions _modelOptions = new() { Required = true, AutoSelectSingle = true, Placeholder = "Выберите модель" };
    private readonly ClaySelectOptions _phonePoolOptions = new() { AutoSelectSingle = true, Placeholder = "Выберите телефонный пул" };
    private readonly ClaySelectOptions _phoneOptions = new() { AutoSelectSingle = true, Placeholder = "Выберите телефонный номер" };
    private readonly ClayGridOptions _phoneGridOptions = new() { EmptyText = "Нет телефонных номеров", ShowAddButton = false, ShowHeader = false };
    private int _hotelPhoneCount;
    private readonly ClayGridOptions _locationGridOptions = new() { EmptyText = "Нет расположений", AddTooltip = "Добавить расположение" };
    private List<EquipmentLocationPeriod> _locations = [];
    private readonly Dictionary<DateTime, DateTime> _localDates = new();
    private readonly ClayGridOptions _responsibleGridOptions = new() { EmptyText = "Нет ответственных", AddTooltip = "Выдать оборудование" };
    private List<EquipmentResponsiblePeriod> _responsibles = [];
    private readonly ClayGridOptions _accountGridOptions = new() { EmptyText = "Нет учётных записей", AddTooltip = "Добавить учётную запись" };
    private List<EquipmentAccount> _accounts = [];
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
    private readonly ClayToolbarButtonOptions _networkHistoryOptions = new() { Text = "История имён", Icon = Icons.Material.Filled.History, Title = "Показать историю сетевых имён" };
    private int _networkAttachmentCount;
    private int _networkVersion;
    private IReadOnlyList<EquipmentNetworkNameHistory> _networkNameHistory = [];
    private IReadOnlyList<EquipmentNetworkPortLink> _networkPorts = [];

    private MudForm? _form;
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
    private int _modelVersion;
    private int _ownerVersion;
    private int _shipperVersion;
    private int? _confirmedOwnerId;
    private int? _originalOwnerId;
    private int? _originalTenantId;
    private int? _originalReceiptDocumentId;
    private int? _originalWriteOffDocumentId;
    private string _originalSnapshot = string.Empty;
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
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving;
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
    private bool ShowOperations => _canManageUps || _canChangeNetwork;
    private bool ShowPhones => HasSelectedModel && _selectedType?.HasPhoneNumber > 0 ||
                               _phones.Count > 0 && _persistedType?.HasPhoneNumber > 0;
    private bool ShowHotelPhoneWarning => _hotelPhoneCount > 1;
    private bool ShowPhonePool => HasSelectedModel && _selectedType?.HasPhoneNumber > 0 && _selectedType.Id == 30;
    private bool ShowPhoneConfig => _selectedType?.Id == 30;
    private bool ShowComputer => _selectedType?.HasComputerCharacteristics > 0 ||
                                 _persistedType?.HasComputerCharacteristics > 0 &&
                                 (_computerHistory.Count > 0 || _softwareInstallations.Count > 0);
    private bool ShowAccounts => _canViewAccounts && (_selectedType?.MacAddressCount > 0 ||
                                 _persistedType?.MacAddressCount > 0 && _accounts.Count > 0);
    private bool ShowSim => _selectedType?.HasSimCharacteristics > 0 ||
                            _persistedType?.HasSimCharacteristics > 0 && _simCards.Count > 0;
    private bool ShowLicense => _selectedType?.HasSoftwareCharacteristics > 0 ||
                                _persistedType?.HasSoftwareCharacteristics > 0 && _licenses.Count > 0;
    private bool ShowRouter => _routerInfo is not null || _routerInventory.Count > 0;
    private bool PhonesModified => JsonSerializer.Serialize(_phones) != JsonSerializer.Serialize(_originalPhones);
    private bool ShowCamera => HasSelectedModel && _selectedType?.HasPhoneNumber > 0 && _selectedType.Id == 30;
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

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loadedId == Id)
            return;
        _loadedId = Id;
        await LoadCurrentAsync(Id);
    }

    private async Task LoadCurrentAsync(int id)
    {
        if (_pendingPhoneSync?.EquipmentId != id)
            _pendingPhoneSync = null;
        var version = ++_operationVersion;
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
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            var model = id > 0 ? await Service.GetAsync(id) : null;
            if (!IsCurrent(version, id))
                return;
            if (model is null)
            {
                _model = null;
                _notFound = true;
                return;
            }
            var canEdit = await Service.HasRoleAsync(InventorySpecialistRoleId);
            if (!IsCurrent(version, id))
                return;
            var selectedType = model.EquipmentTypeId is > 0
                ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, id))
                return;
            var canEditSim = selectedType?.HasSimCharacteristics > 0 &&
                             await Service.HasRoleAsync(SimSpecialistRoleId);
            var canEditLicense = selectedType?.HasSoftwareCharacteristics > 0 &&
                                 (await Service.HasRoleForPersonAsync(LicenseSpecialistRoleId, model.OwnerId) ||
                                  model.TenantId is not null && model.TenantId != model.OwnerId &&
                                  await Service.HasRoleForPersonAsync(LicenseSpecialistRoleId, model.TenantId));
            var canViewPhoneConfig = selectedType?.Id == 30 && await Service.HasRoleAsync(DutyEngineerRoleId);
            var canViewAccounts = selectedType?.MacAddressCount > 0 && await Service.HasRoleAsync(DutyEngineerRoleId);
            var canManageUps = model.EquipmentTypeId is < 100 && selectedType?.HasSimCharacteristics == 0 &&
                               await Service.HasRoleForPersonAsync(InventorySpecialistRoleId, model.OwnerId);
            var canChangeNetwork = selectedType?.HasComputerCharacteristics > 0 &&
                                   await Service.HasRoleAsync(SupportSpecialistRoleId);
            var canUseAccounting = await Service.CanUseAccountingAsync(model.OwnerId);
            if (!IsCurrent(version, id))
                return;
            var selectedModel = model.ModelId is > 0
                ? await ModelSource.GetByIdAsync(model.ModelId, _pageCts.Token)
                : null;
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
            });
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
        if (!_loading)
            await LoadCurrentAsync(Id);
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

    private async Task OnOwnerChangedAsync(int? id)
    {
        if (_model is null)
            return;
        if (id is null)
        {
            _ownerVersion++;
            _shipperVersion++;
            _accountingVersion++;
            _model.OwnerId = null;
            _canUseAccounting = false;
            _canEditLicense = false;
            SetDocumentOwner(null);
            return;
        }
        if (_confirmedOwnerId == id)
        {
            _model.OwnerId = id;
            SetDocumentOwner(id);
            await RefreshAccountingAccessAsync(id);
            await RefreshLicenseAccessAsync();
            return;
        }
        _ownerVersion++;
        _shipperVersion++;
        _confirmedOwnerId = id;
        _model.OwnerId = id;
        SetDocumentOwner(id);
        await RefreshAccountingAccessAsync(id);
        await RefreshLicenseAccessAsync();
    }

    private async Task RefreshAccountingAccessAsync(int? personId)
    {
        var version = ++_accountingVersion;
        var available = await Service.CanUseAccountingAsync(personId);
        if (_disposed || version != _accountingVersion || _model?.OwnerId != personId)
            return;
        _canUseAccounting = available;
    }

    private async Task RefreshLicenseAccessAsync()
    {
        if (_model is null)
            return;
        var model = _model;
        var version = _ownerVersion;
        var ownerId = model.OwnerId;
        var tenantId = model.TenantId;
        var available = _selectedType?.HasSoftwareCharacteristics > 0 &&
                        (await Service.HasRoleForPersonAsync(LicenseSpecialistRoleId, ownerId) ||
                         tenantId is not null && tenantId != ownerId &&
                         await Service.HasRoleForPersonAsync(LicenseSpecialistRoleId, tenantId));
        if (!_disposed && version == _ownerVersion && ReferenceEquals(model, _model) &&
            model.OwnerId == ownerId && model.TenantId == tenantId)
            _canEditLicense = available;
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
        var shipperId = await ReceiptDocumentSource.GetSingleShipperIdAsync(id.Value, ownerId);
        if (!_disposed && version == _shipperVersion && _model.DocumentReceiptOuId == id &&
            _model.OwnerId == ownerId && shipperId is not null)
            _model.ShipperId = shipperId;
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
        if (_model is null || _phoneToAdd is null || !_canEdit || _saving)
            return;
        var version = ++_phoneVersion;
        var phoneId = _phoneToAdd.Value;
        if (_phones.Any(item => item.PhoneId == phoneId))
        {
            _phoneToAdd = null;
            return;
        }
        var phone = await PhoneNumberSource.GetByIdAsync(phoneId, _pageCts.Token);
        if (_disposed || version != _phoneVersion || phone is null)
            return;
        var connectionId = _model.LocationId is > 0
            ? await Service.GetConnectionEquipmentIdAsync(phoneId, _model.EmployeeId, _model.LocationId.Value)
            : null;
        if (_disposed || version != _phoneVersion)
            return;
        _phones.Add(new EquipmentPhoneLink
        {
            PhoneId = phone.Id,
            EquipmentId = _model.Id,
            Description = phone.Name,
            ConnectionEquipmentId = connectionId,
            AutoDialNumber = string.Empty,
            Notes = string.Empty,
        });
        _phoneToAdd = null;
        UpdatePhoneSourceFilters();
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
        var result = await ShowDialogAsync<EquipmentPhoneDialog>("Срок возврата", parameters);
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
        var phones = await Service.GetPhonesAsync(id);
        if (_disposed || version != _phoneVersion || _model.Id != id)
            return;
        _phones = phones.ToList();
        _originalPhones = ClonePhones(phones);
        UpdatePhoneSourceFilters();
    }

    private sealed record PendingPhoneSync(
        int EquipmentId,
        List<EquipmentPhoneLink> Original,
        List<EquipmentPhoneLink> Current,
        IReadOnlyList<PhoneSyncOperation>? Operations = null);

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
        var isEdit = existing is not null;
        var parameters = new DialogParameters<EquipmentLocationDialog>
        {
            { dialog => dialog.IsEdit, isEdit },
            { dialog => dialog.RequireResponsible, !isEdit && !_responsibles.Any(item => item.ToUtc is null) },
            { dialog => dialog.LocationId, existing?.LocationId },
            { dialog => dialog.FromDate, existing?.FromUtc.Date },
            { dialog => dialog.FromTime, existing?.FromUtc.TimeOfDay },
            { dialog => dialog.ReturnDate, existing?.ReturnDate },
            { dialog => dialog.Notes, existing?.Notes ?? string.Empty },
        };
        var result = await ShowDialogAsync<EquipmentLocationDialog>("Расположение", parameters);
        if (result is null || result.Canceled || result.Data is not EquipmentLocationDialogResult value)
            return;
        if (!isEdit && _locations.LastOrDefault()?.LocationId == value.LocationId)
        {
            _validationMessage = "Оборудование уже находится в выбранном расположении.";
            return;
        }
        var version = ++_locationVersion;
        var equipmentId = _model.Id;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            var requireResponsible = value.LocationId == OnHandsLocationId &&
                                     !await Service.HasCurrentResponsibleAsync(equipmentId);
            if (!isEdit && requireResponsible && value.EmployeeId is null)
            {
                _validationMessage = "Для расположения «На руках» укажите ответственного сотрудника.";
                return;
            }
            var fromUtc = await ConvertLocalToUtcAsync(value.FromLocal);
            if (_disposed || version != _locationVersion || _model.Id != equipmentId)
                return;
            var location = new EquipmentLocationPeriod
            {
                EquipmentId = equipmentId,
                LocationId = value.LocationId,
                FromUtc = existing?.FromUtc ?? fromUtc,
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
            if (isEdit)
                await Service.UpdateLocationAsync(location);
            else
                await Service.AddLocationAsync(location, responsible);
            if (_disposed || version != _locationVersion || _model.Id != equipmentId)
                return;
            await RefreshLocationsAsync();
            await RefreshResponsiblesAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _locationVersion)
                _validationMessage = "Расположение не сохранено. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _locationVersion)
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
        var requireLocation = mode == EquipmentResponsibleDialogMode.Withdraw && _model.LocationId == OnHandsLocationId;
        var parameters = new DialogParameters<EquipmentResponsibleDialog>
        {
            { dialog => dialog.Mode, mode },
            { dialog => dialog.RequireLocation, requireLocation },
            { dialog => dialog.EmployeeId, mode == EquipmentResponsibleDialogMode.Withdraw ? null : existing?.EmployeeId },
            { dialog => dialog.FromDate, existing?.FromUtc.Date },
            { dialog => dialog.FromTime, existing?.FromUtc.TimeOfDay },
            { dialog => dialog.ToDate, existing?.ToUtc?.Date },
            { dialog => dialog.ToTime, existing?.ToUtc?.TimeOfDay },
            { dialog => dialog.ReturnDate, existing?.ReturnDate },
            { dialog => dialog.Notes, existing?.Notes ?? string.Empty },
        };
        if (mode == EquipmentResponsibleDialogMode.Withdraw)
        {
            parameters.Add(dialog => dialog.FromDate, existing?.ToUtc?.Date);
            parameters.Add(dialog => dialog.FromTime, existing?.ToUtc?.TimeOfDay);
        }
        var result = await ShowDialogAsync<EquipmentResponsibleDialog>("Ответственный", parameters);
        if (result is null || result.Canceled || result.Data is not EquipmentResponsibleDialogResult value)
            return;
        var version = ++_locationVersion;
        var equipmentId = _model.Id;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            var fromUtc = value.FromLocal is null ? (DateTime?)null : await ConvertLocalToUtcAsync(value.FromLocal.Value);
            var toUtc = value.ToLocal is null ? (DateTime?)null : await ConvertLocalToUtcAsync(value.ToLocal.Value);
            if (_disposed || version != _locationVersion || _model.Id != equipmentId)
                return;
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
                var current = CloneResponsible(existing!);
                current.ReturnDate = value.ReturnDate;
                current.Notes = value.Notes;
                await Service.UpdateResponsibleAsync(current);
            }
            else
            {
                var current = CloneResponsible(existing!);
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
                await Service.WithdrawResponsibleAsync(current, next, location);
            }
            if (_disposed || version != _locationVersion || _model.Id != equipmentId)
                return;
            await RefreshResponsiblesAsync();
            await RefreshLocationsAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _locationVersion)
                _validationMessage = "Ответственный не сохранён. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _locationVersion)
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

    private async Task OpenAccountDialogAsync(EquipmentAccount? existing)
    {
        if (_model is null || !_canEdit || !ShowAccounts || _saving)
            return;
        var equipmentId = _model.Id;
        var operationVersion = _operationVersion;
        var parameters = new DialogParameters<EquipmentAccountDialog>
        {
            { dialog => dialog.EquipmentId, equipmentId },
            { dialog => dialog.OriginalLogin, existing?.Login },
            { dialog => dialog.Login, existing?.Login ?? string.Empty },
            { dialog => dialog.LimitedRights, existing?.LimitedRights > 0 },
            { dialog => dialog.Notes, existing?.Notes ?? string.Empty },
        };
        var dialog = await DialogService.ShowAsync<EquipmentAccountDialog>("Учётная запись", parameters);
        var result = await dialog.Result;
        if (!IsCurrent(operationVersion, equipmentId) || _model?.Id != equipmentId ||
            result is null || result.Canceled || result.Data is not EquipmentAccountDialogResult value)
            return;
        var version = ++_accountVersion;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            var account = new EquipmentAccount
            {
                EquipmentId = equipmentId,
                Login = value.Login,
                Password = value.Password,
                LimitedRights = value.LimitedRights ? (byte)1 : (byte)0,
                Notes = value.Notes,
            };
            if (existing is null)
                await Service.AddAccountAsync(account);
            else
                await Service.UpdateAccountAsync(account);
            if (_disposed || version != _accountVersion || _model.Id != equipmentId)
                return;
            await RefreshAccountsAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _accountVersion)
                _validationMessage = "Учётная запись не сохранена. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _accountVersion)
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

    private async Task DeleteAccountAsync(EquipmentAccount account)
    {
        if (_model is null || !_canEdit || !ShowAccounts || _saving ||
            !await ConfirmAsync("Удаление учётной записи", $"Удалить учётную запись «{account.Login}»?"))
            return;
        var version = ++_accountVersion;
        var equipmentId = _model.Id;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.DeleteAccountAsync(account);
            if (_disposed || version != _accountVersion || _model.Id != equipmentId)
                return;
            await RefreshAccountsAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _accountVersion)
                _validationMessage = "Учётная запись не удалена. Подробности — в сообщении базы данных.";
        }
        catch (UnauthorizedAccessException ex)
        {
            if (!_disposed && version == _accountVersion)
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
    }

    private async Task OpenComputerDialogAsync()
    {
        if (_model is null || !_canEdit || _saving)
            return;
        var latest = _computerHistory.LastOrDefault();
        var parameters = new DialogParameters<EquipmentComputerDialog>
        {
            { dialog => dialog.EquipmentId, _model.Id },
            { dialog => dialog.Date, DateTime.Today },
            { dialog => dialog.MemoryBanks, _selectedModel?.MemoryBanks ?? 0 },
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
        };
        var result = await ShowDialogAsync<EquipmentComputerDialog>("Компьютер", parameters);
        if (result is null || result.Canceled || result.Data is not EquipmentComputerSnapshot snapshot)
            return;
        var version = ++_computerVersion;
        var equipmentId = _model.Id;
        snapshot.EquipmentId = equipmentId;
        snapshot.Date = DateTime.Today;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.SaveComputerAsync(snapshot);
            if (_disposed || version != _computerVersion || _model.Id != equipmentId)
                return;
            await RefreshComputerAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _computerVersion)
                _validationMessage = "Конфигурация компьютера не сохранена. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _computerVersion)
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
    }

    private async Task OpenSimDialogAsync()
    {
        if (_model is null || !CanEditSim || _saving)
            return;
        var equipmentId = _model.Id;
        var operationVersion = _operationVersion;
        var existing = _simCards.SingleOrDefault();
        DateTime? localActivation = null;
        if (existing is not null)
        {
            var utc = DateTime.SpecifyKind(existing.ActivatedAtUtc, DateTimeKind.Utc);
            var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
            localActivation = utc.AddMinutes(offset);
        }
        if (!IsCurrent(operationVersion, equipmentId))
            return;
        var parameters = new DialogParameters<EquipmentSimDialog>
        {
            { dialog => dialog.EquipmentId, equipmentId },
            { dialog => dialog.HasExisting, existing is not null },
            { dialog => dialog.PhoneNumber, existing?.PhoneNumber ?? string.Empty },
            { dialog => dialog.ActivatedDate, localActivation?.Date },
            { dialog => dialog.ActivatedTime, localActivation?.TimeOfDay },
            { dialog => dialog.ContractId, existing?.ContractId },
            { dialog => dialog.PersonalDataPersonId, existing?.PersonalDataPersonId },
            { dialog => dialog.CurrentEmployeeId, _responsibles.LastOrDefault(item => item.ToUtc is null)?.EmployeeId },
        };
        var dialog = await DialogService.ShowAsync<EquipmentSimDialog>("SIM-карта", parameters);
        var result = await dialog.Result;
        if (!IsCurrent(operationVersion, equipmentId) || _model?.Id != equipmentId ||
            result is null || result.Canceled || result.Data is not EquipmentSimDialogResult value)
            return;
        var version = ++_simVersion;
        var duplicateId = await Service.GetDuplicateSimIdAsync(equipmentId, value.PhoneNumber);
        if (_disposed || version != _simVersion || _model.Id != equipmentId)
            return;
        if (duplicateId is not null &&
            !await ConfirmAsync("Другая SIM с тем же номером",
                $"Списать SIM #{duplicateId} с тем же номером на момент активации текущей?"))
            return;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            var activatedUtc = await ConvertLocalToUtcAsync(value.ActivatedLocal);
            if (_disposed || version != _simVersion || _model.Id != equipmentId)
                return;
            await Service.SaveSimAsync(new EquipmentSimCard
            {
                EquipmentId = equipmentId,
                PhoneNumber = value.PhoneNumber,
                ActivatedAtUtc = activatedUtc,
                ContractId = value.ContractId,
                PersonalDataPersonId = value.PersonalDataPersonId,
                Secrets = value.Secrets,
            }, duplicateId);
            if (_disposed || version != _simVersion || _model.Id != equipmentId)
                return;
            await RefreshSimAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _simVersion)
                _validationMessage = "Сведения SIM не сохранены. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _simVersion)
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

    private async Task DeleteSimAsync()
    {
        if (_model is null || !CanEditSim || _saving ||
            !await ConfirmAsync("Удаление сведений SIM", "Удалить сведения SIM без удаления самого оборудования?"))
            return;
        var version = ++_simVersion;
        var equipmentId = _model.Id;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.DeleteSimAsync(equipmentId);
            if (_disposed || version != _simVersion || _model.Id != equipmentId)
                return;
            await RefreshSimAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _simVersion)
                _validationMessage = "Сведения SIM не удалены. Подробности — в сообщении базы данных.";
        }
        catch (UnauthorizedAccessException ex)
        {
            if (!_disposed && version == _simVersion)
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
        var value = existing is null
            ? new EquipmentSimService { EquipmentId = _model.Id, PhoneNumber = sim.PhoneNumber }
            : new EquipmentSimService
            {
                Id = existing.Id,
                EquipmentId = _model.Id,
                PhoneNumber = sim.PhoneNumber,
                ServiceTypeId = existing.ServiceTypeId,
                ServiceName = existing.ServiceName,
                DateFrom = existing.DateFrom,
                DateTo = existing.DateTo,
                ActivationCost = existing.ActivationCost,
                DailyCost = existing.DailyCost,
                MonthlyCost = existing.MonthlyCost,
            };
        var parameters = new DialogParameters<EquipmentSimServiceDialog>
        {
            { dialog => dialog.Value, value },
            { dialog => dialog.Types, _simServiceTypes },
        };
        var result = await ShowDialogAsync<EquipmentSimServiceDialog>("Дополнительная услуга SIM", parameters);
        if (result is null || result.Canceled || result.Data is not EquipmentSimService service)
            return;
        var version = ++_simServiceVersion;
        var equipmentId = _model.Id;
        service.EquipmentId = equipmentId;
        service.PhoneNumber = sim.PhoneNumber;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.SaveSimServiceAsync(service);
            if (_disposed || version != _simServiceVersion || _model.Id != equipmentId)
                return;
            await RefreshSimServicesAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _simServiceVersion)
                _validationMessage = "Дополнительная услуга не сохранена. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _simServiceVersion)
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

    private async Task OnSimServiceFiltersChangedAsync()
    {
        _simServiceVersion++;
        await RefreshSimServicesAsync();
        StateHasChanged();
    }

    private async Task RefreshSimServicesAsync()
    {
        if (_model is null)
            return;
        var version = _simServiceVersion;
        var equipmentId = _model.Id;
        var services = await Service.GetSimServicesAsync(
            equipmentId, _showServiceHistory, _showCompanyRequiredServices);
        if (_disposed || version != _simServiceVersion || _model.Id != equipmentId)
            return;
        _simServices = services.ToList();
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
        };
        var result = await ShowDialogAsync<EquipmentLicenseDialog>("Лицензия", parameters);
        if (result is null || result.Canceled || result.Data is not EquipmentLicense license)
            return;
        var version = ++_licenseVersion;
        var equipmentId = _model.Id;
        license.EquipmentId = equipmentId;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.SaveLicenseAsync(license,
                _licenseTypes.FirstOrDefault(item => item.Id == license.LicenseTypeId));
            if (_disposed || version != _licenseVersion || _model.Id != equipmentId)
                return;
            await RefreshLicensesAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _licenseVersion)
                _validationMessage = "Лицензия не сохранена. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _licenseVersion)
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
                SoftwareId = existing.SoftwareId,
                SoftwareName = existing.SoftwareName,
                InstalledAt = existing.InstalledAt,
                UninstalledAt = existing.UninstalledAt,
                Installed = existing.Installed,
                Distribution = existing.Distribution,
                Notes = existing.Notes,
            };
        var parameters = new DialogParameters<EquipmentSoftwareDialog> { { dialog => dialog.Value, value } };
        var result = await ShowDialogAsync<EquipmentSoftwareDialog>("Установленное ПО", parameters);
        if (result is null || result.Canceled || result.Data is not EquipmentSoftwareInstallation installation)
            return;
        var version = ++_softwareVersion;
        var equipmentId = _model.Id;
        installation.EquipmentId = equipmentId;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.SaveSoftwareInstallationAsync(installation);
            if (_disposed || version != _softwareVersion || _model.Id != equipmentId)
                return;
            await RefreshSoftwareAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _softwareVersion)
                _validationMessage = "Установленное ПО не сохранено. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _softwareVersion)
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
            };
        var parameters = new DialogParameters<EquipmentAccountingDialog> { { dialog => dialog.Value, value } };
        var result = await ShowDialogAsync<EquipmentAccountingDialog>(
            "Бухгалтерский учёт", parameters,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        if (result is null || result.Canceled || result.Data is not EquipmentAccounting accounting)
            return;
        var version = ++_accountingVersion;
        var equipmentId = _model.Id;
        accounting.EquipmentId = equipmentId;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.SaveAccountingAsync(accounting);
            if (_disposed || version != _accountingVersion || _model.Id != equipmentId)
                return;
            await RefreshAccountingAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _accountingVersion)
                _validationMessage = "Бухгалтерский учёт не сохранён. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _accountingVersion)
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

    private async Task DeleteAccountingAsync(EquipmentAccounting latest)
    {
        if (_model is null || !_canUseAccounting || _saving || _accounting.LastOrDefault()?.Id != latest.Id ||
            !await ConfirmAsync("Удаление бухгалтерского учёта", "Удалить последнюю запись бухгалтерского учёта?"))
            return;
        var version = ++_accountingVersion;
        var equipmentId = _model.Id;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.DeleteAccountingAsync(equipmentId, latest.Id);
            if (_disposed || version != _accountingVersion || _model.Id != equipmentId)
                return;
            await RefreshAccountingAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _accountingVersion)
                _validationMessage = "Результат удаления бухгалтерского учёта неизвестен. Обновите запись и проверьте состояние данных.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            if (!_disposed && version == _accountingVersion)
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
        await DialogService.ShowAsync<EquipmentNetworkNameHistoryDialog>(
            "История сетевых имён", parameters, new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
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
            if (IsModified)
                return;
        }
        var version = ++_networkVersion;
        var equipmentId = _model.Id;
        _saving = true;
        _validationMessage = null;
        _warningMessage = null;
        StateHasChanged();
        try
        {
            if (attach)
                await Service.AttachNetworkAsync(equipmentId);
            else
                await Service.DetachNetworkAsync(equipmentId);
            var count = await Service.GetNetworkAttachmentCountAsync(equipmentId);
            if (_disposed || version != _networkVersion || _model.Id != equipmentId)
                return;
            _networkAttachmentCount = count;
            _warningMessage = attach ? "Оборудование подключено к рабочей сети." : "Оборудование отключено от рабочей сети.";
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _networkVersion)
                _validationMessage = "Состояние сети не изменено. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _networkVersion)
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
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Service.AddUpsLinkAsync(upsId, equipmentId);
            if (_disposed || version != _upsVersion || _model.Id != equipmentId)
                return;
            await RefreshUpsLinksAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _upsVersion)
                _validationMessage = "UPS не подключён. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _upsVersion)
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
            dialogServers = CloneUpsServers(originalServers);
            foreach (var test in originalTests)
            {
                var utc = DateTime.SpecifyKind(test.TestedAt, DateTimeKind.Utc);
                var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
                dialogTests.Add(CloneUpsTest(test, utc.AddMinutes(offset)));
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
        var parameters = new DialogParameters<EquipmentUpsDialog>
        {
            { dialog => dialog.UpsEquipmentId, link.UpsEquipmentId },
            { dialog => dialog.Servers, dialogServers },
            { dialog => dialog.Tests, dialogTests },
        };
        var result = await ShowDialogAsync<EquipmentUpsDialog>("Подключение UPS", parameters);
        if (result is null || result.Canceled || result.Data is not EquipmentUpsDialogResult value)
            return;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            var currentTests = new List<UpsTest>();
            foreach (var test in value.Tests)
                currentTests.Add(CloneUpsTest(test, await ConvertLocalToUtcAsync(test.TestedAt)));
            if (_disposed || version != _upsVersion || _model.Id != equipmentId)
                return;
            await Service.SaveUpsDetailsAsync(
                link.UpsEquipmentId, equipmentId, originalServers, value.Servers, originalTests, currentTests);
            if (_disposed || version != _upsVersion || _model.Id != equipmentId)
                return;
            await RefreshUpsLinksAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _upsVersion)
                _validationMessage = "Результат сохранения подключения UPS неизвестен. Обновите запись и проверьте состояние данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && version == _upsVersion)
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
        var local = _localDates.TryGetValue(utc, out var value) ? value : utc;
        return local.ToString(includeTime ? "dd.MM.yyyy HH:mm:ss" : "dd.MM.yyyy");
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
        if (_model.EquipmentTypeId is null || _model.ModelId is null) errors.Add("Выберите тип и модель оборудования.");
        if (_model.OwnerId is null) errors.Add("Укажите владельца оборудования.");
        if (_selectedType?.IsSerialNumberRequired > 0 && string.IsNullOrWhiteSpace(_model.SerialNumber)) errors.Add("Укажите серийный номер.");
        if (_model.DocumentWriteOffId is not null && _writeOffDate is null) errors.Add("Укажите дату списания.");
        if (_model.EquipmentTypeId != SimEquipmentTypeId && _writeOffDate is not null && _model.DocumentWriteOffId is null) errors.Add("Укажите документ списания.");
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
            if (phonesModified)
                await Service.UpdateWithPhonesAsync(model, _originalPhones, _phones);
            else
                await Service.UpdateAsync(model);
            if (!IsCurrentSave(model, version))
                return;
            _originalSnapshot = Snapshot();
            SetAccountingBaseline();
            var warnings = new List<string>();
            if (phonesModified)
            {
                var pending = new PendingPhoneSync(model.Id, ClonePhones(_originalPhones), ClonePhones(_phones));
                try
                {
                    var results = await Service.SynchronizePhoneOperationsAsync(model.Id, pending.Original, pending.Current);
                    warnings.AddRange(results.Where(result => !result.Success).Select(result => result.Error!));
                    pending = pending with { Operations = results.Where(result => !result.Success).Select(result => result.Operation).ToArray() };
                }
                catch (Microsoft.Data.SqlClient.SqlException)
                {
                    warnings.Add("Синхронизация с АТС не выполнена. Подробности — в сообщении базы данных.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
                {
                    warnings.Add($"Синхронизация с АТС не выполнена: {ex.Message}");
                }
                _pendingPhoneSync = warnings.Count > 0 ? pending : null;
            }
            _originalPhones = ClonePhones(_phones);
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
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrentSave(model, version))
                _validationMessage = "Оборудование не сохранено. Подробности — в сообщении базы данных.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            if (IsCurrentSave(model, version))
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

    private async Task<bool> RetryPendingPhoneSyncAsync()
    {
        var pending = _pendingPhoneSync;
        var model = _model;
        if (pending is null)
            return true;
        if (model is null || pending.EquipmentId != model.Id)
            return false;
        var version = ++_operationVersion;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Task.Delay(100);
            var results = pending.Operations is null
                ? await Service.SynchronizePhoneOperationsAsync(pending.EquipmentId, pending.Original, pending.Current)
                : await Service.ExecutePhoneSyncOperationsAsync(pending.Operations);
            if (!IsCurrentSave(model, version))
                return false;
            var errors = results.Where(result => !result.Success).ToList();
            _pendingPhoneSync = errors.Count > 0
                ? pending with { Operations = errors.Select(result => result.Operation).ToArray() }
                : null;
            if (errors.Count > 0)
            {
                _warningMessage = "Телефонные связи сохранены. " + string.Join(" ", errors.Select(result => result.Error)) + " Повторите синхронизацию через «Применить».";
                return false;
            }
            _warningMessage = null;
            return true;
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrentSave(model, version))
        {
            return false;
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrentSave(model, version))
                _warningMessage = "Телефонные связи сохранены, синхронизация с АТС не выполнена. Повторите через «Применить».";
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
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

    private bool IsCurrentSave(EquipmentEntity model, int version) => !_disposed && version == _operationVersion && ReferenceEquals(model, _model);

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
        var equipment = JsonSerializer.SerializeToNode(_model)!;
        equipment.AsObject().Remove(nameof(EquipmentEntity.LocationId));
        equipment.AsObject().Remove(nameof(EquipmentEntity.EmployeeId));
        return JsonSerializer.Serialize(new { Equipment = equipment, WriteOffDate = _writeOffDate, WriteOffTime = _writeOffTime, Phones = _phones });
    }

    private async Task RefreshAsync()
    {
        if (_model is null || _loading || _saving)
            return;
        if (IsModified && !await ConfirmAsync("Обновление", RefreshConfirmation()))
            return;
        _warningMessage = null;
        await LoadCurrentAsync(Id);
    }

    private async Task CancelAsync()
    {
        if (IsModified && !await ConfirmAsync("Закрытие формы", CloseConfirmation("Закрыть форму без сохранения изменений?")))
            return;
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
        _allowNavigation = true;
        Navigation.NavigateTo(context.TargetLocation);
    }

    private async Task<DialogResult?> ShowDialogAsync<T>(
        string title,
        DialogParameters<T> parameters,
        DialogOptions? options = null) where T : IComponent
    {
        var equipmentId = _model?.Id;
        var operationVersion = _operationVersion;
        var dialog = await DialogService.ShowAsync<T>(title, parameters, options);
        var result = await dialog.Result;
        if (equipmentId is not null &&
            (!IsCurrent(operationVersion, equipmentId.Value) || _model?.Id != equipmentId))
            return null;
        return result;
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
        var dialog = await DialogService.ShowAsync<ConfirmDialog>(title, parameters);
        var result = await dialog.Result;
        return result is not null && !result.Canceled &&
               (equipmentId is null || IsCurrent(operationVersion, equipmentId.Value) && _model?.Id == equipmentId);
    }

    /// <summary>Отменяет незавершённые операции формы.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pageCts.Cancel();
        _pageCts.Dispose();
    }
}
