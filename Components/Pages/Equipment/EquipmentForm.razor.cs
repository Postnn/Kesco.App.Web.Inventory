using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Detail;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Documents;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
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
    private const int SimEquipmentTypeId = 27;
    private const int OnHandsLocationId = 55;

    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "equipment-form" };
    private readonly ClaySelectOptions _typeOptions = new() { Required = true, AutoSelectSingle = true, MaxItems = 300, Placeholder = "Введите тип оборудования" };
    private readonly ClaySelectOptions _modelOptions = new() { Required = true, AutoSelectSingle = true, Placeholder = "Введите модель" };
    private readonly ClaySelectOptions _phonePoolOptions = new() { AutoSelectSingle = true, Placeholder = "Введите телефонный пул" };
    private readonly ClaySelectOptions _phoneOptions = new() { AutoSelectSingle = true, Placeholder = "Введите телефонный номер" };
    private readonly ClayDetailGridOptions _phoneGridOptions = new() { EmptyText = "Нет телефонных номеров", AddTooltip = "Связать выбранный номер" };
    private readonly IReadOnlyList<ClayDetailColumn<EquipmentPhoneLink>> _phoneColumns =
    [
        new() { Title = "Номер", TextSelector = item => item.Description + (item.IsDefault ? " (основной)" : string.Empty) },
        new() { Title = "Подключение", TextSelector = item => item.ConnectionEquipmentId is > 0 ? $"#{item.ConnectionEquipmentId}" : string.Empty },
        new() { Title = "Срок возврата", TextSelector = item => (item.ReturnDate?.ToString("dd.MM.yyyy") ?? string.Empty) + (string.IsNullOrWhiteSpace(item.Notes) ? string.Empty : $" ({item.Notes})") },
    ];
    private readonly ClayDetailGridOptions _locationGridOptions = new() { EmptyText = "Нет расположений", AddTooltip = "Добавить расположение" };
    private IReadOnlyList<ClayDetailColumn<EquipmentLocationPeriod>> _locationColumns = null!;
    private List<EquipmentLocationPeriod> _locations = [];
    private readonly Dictionary<DateTime, DateTime> _localDates = new();
    private readonly ClaySelectOptions _ownerOptions = new() { Required = true, AutoSelectSingle = true, Placeholder = "Введите владельца" };
    private readonly ClaySelectOptions _receiptDocumentOptions = new() { Placeholder = "Введите документ поступления" };
    private readonly ClaySelectOptions _writeOffDocumentOptions = new() { Placeholder = "Введите документ списания" };
    private readonly ClaySelectOptions _shipperOptions = new() { AutoSelectSingle = true, Placeholder = "Введите поставщика" };
    private readonly ClaySelectOptions _tenantOptions = new() { AutoSelectSingle = true, Placeholder = "Введите арендатора" };
    private readonly ClayTextBoxOptions _snOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _networkNameOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _macOptions = new() { MaxLength = 17 };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { MaxLength = 300, Lines = 3 };
    private readonly ClayDatePickerOptions _writeOffDateOptions = new();
    private readonly ClayTimeOptions _writeOffTimeOptions = new();
    private readonly ClayToolbarButtonOptions _okOptions = new() { Text = "OK", Icon = Icons.Material.Filled.Check, Title = "Сохранить и закрыть", Color = Color.Primary, Variant = Variant.Filled };
    private readonly ClayToolbarButtonOptions _applyOptions = new() { Text = "Применить", Icon = Icons.Material.Filled.Save, Title = "Сохранить без закрытия" };
    private readonly ClayToolbarButtonOptions _refreshOptions = new() { Text = "Обновить", Icon = Icons.Material.Filled.Refresh, Title = "Загрузить запись повторно" };
    private readonly ClayToolbarButtonOptions _cancelOptions = new() { Text = "Отмена", Icon = Icons.Material.Filled.Close, Title = "Закрыть форму" };
    private readonly ClayToolbarButtonOptions _copyOptions = new() { Text = "Копировать", Icon = Icons.Material.Filled.ContentCopy, Title = "Создать оборудование копированием" };

    private MudForm? _form;
    private EquipmentEntity? _model;
    private EquipmentType? _selectedType;
    private EquipmentModel? _selectedModel;
    private List<EquipmentPhoneLink> _phones = [];
    private List<EquipmentPhoneLink> _originalPhones = [];
    private int? _phoneToAdd;
    private int _phoneVersion;
    private int _locationVersion;
    private string? _warningMessage;
    private DateTime? _writeOffDate;
    private TimeSpan? _writeOffTime;
    private bool _sortTypesByName;
    private bool _canEdit;
    private bool _loading = true;
    private bool _saving;
    private bool _loaded;
    private bool _notFound;
    private bool _allowNavigation;
    private bool _disposed;
    private int _loadedId = -1;
    private int _operationVersion;
    private int _modelVersion;
    private int _ownerVersion;
    private int _shipperVersion;
    private int? _confirmedOwnerId;
    private string _originalSnapshot = string.Empty;
    private string? _validationMessage;

    /// <summary>Идентификатор редактируемого оборудования.</summary>
    [SupplyParameterFromQuery(Name = "id")]
    public int Id { get; set; }

    private DocumentSelectDataSource ReceiptDocumentSource => Services.GetRequiredKeyedService<DocumentSelectDataSource>("receipt");
    private DocumentSelectDataSource WriteOffDocumentSource => Services.GetRequiredKeyedService<DocumentSelectDataSource>("writeoff");
    private string Title => _model is null ? $"Оборудование #{Id}" : $"[{Id}] {_model.EquipmentTypeName} {_model.ModelName}";
    private bool IsModified => _loaded && _model is not null && Snapshot() != _originalSnapshot;
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving;
    private bool ShowNetworkName => _selectedType?.HasNetworkName > 0;
    private bool ShowPhonePool => _selectedType?.HasPhoneNumber > 0;
    private bool ShowPhones => _selectedType?.HasPhoneNumber > 0;
    private bool PhonesModified => JsonSerializer.Serialize(_phones) != JsonSerializer.Serialize(_originalPhones);
    private bool ShowCamera => _selectedType?.HasMonitorCharacteristics > 0;
    private int MacCount => Math.Min(3, _selectedModel?.NetworkInterfaces ?? 0);
    private string? ChangedByUrl => _model?.ChangedById > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? EquipmentIns.BuildEditUrl(Configuration["URI_user_form"]!, _model.ChangedById)
        : null;
    private Func<EquipmentModel, Task>? ModelViewItem => !string.IsNullOrWhiteSpace(Configuration["URI_model_form"]) ? OpenModelViewAsync : null;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _locationColumns =
        [
            new() { Title = "С", TextSelector = item => FormatDisplayDate(item.FromUtc, true) },
            new() { Title = "Расположение", TextSelector = item => item.LocationName },
            new() { Title = "Срок возврата", TextSelector = item => item.ReturnDate?.ToString("dd.MM.yyyy") ?? string.Empty },
            new() { Title = "Примечания", TextSelector = item => item.Notes },
            new() { Title = "Изменил", TextSelector = item => item.ChangedByName },
            new() { Title = "Изменено", TextSelector = item => FormatDisplayDate(item.ChangedAtUtc, true) },
        ];
    }

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
        var version = ++_operationVersion;
        _modelVersion++;
        _ownerVersion++;
        _shipperVersion++;
        _loading = true;
        _loaded = false;
        _notFound = false;
        _validationMessage = null;
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
            var selectedModel = model.ModelId is > 0
                ? await ModelSource.GetByIdAsync(model.ModelId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, id))
                return;
            var phones = selectedType?.HasPhoneNumber > 0
                ? await Service.GetPhonesAsync(id)
                : (IReadOnlyList<EquipmentPhoneLink>)[];
            if (!IsCurrent(version, id))
                return;
            var locations = await Service.GetLocationsAsync(id);
            if (!IsCurrent(version, id))
                return;
            DateTime? localWriteOff = null;
            if (model.WriteOffAtUtc is not null)
            {
                var utc = DateTime.SpecifyKind(model.WriteOffAtUtc.Value, DateTimeKind.Utc);
                var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
                localWriteOff = utc.AddMinutes(offset);
                if (!IsCurrent(version, id))
                    return;
            }

            _model = model;
            _selectedType = selectedType;
            _selectedModel = selectedModel;
            _phones = phones.ToList();
            _originalPhones = ClonePhones(phones);
            _locations = locations.ToList();
            await RefreshLocationDisplayDatesAsync();
            _phoneToAdd = null;
            _phoneVersion++;
            _confirmedOwnerId = model.OwnerId;
            ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
            ReceiptDocumentSource.PersonId = model.OwnerId;
            WriteOffDocumentSource.PersonId = model.OwnerId;
            _writeOffDate = localWriteOff?.Date;
            _writeOffTime = localWriteOff?.TimeOfDay;
            _canEdit = canEdit;
            _loaded = true;
            UpdateOptions();
            _originalSnapshot = Snapshot();
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrent(version, id))
        {
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

    private bool IsCurrent(int version, int id) => !_disposed && version == _operationVersion && Id == id;

    private void OnTypeIdChanged(int? id)
    {
        if (_model is null)
            return;
        _modelVersion++;
        if (id is null)
        {
            _model.EquipmentTypeId = null;
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
        ClearHiddenFeatures();
        UpdateOptions();
    }

    private async Task OnTypeChanged(EquipmentType? type)
    {
        if (_model is null || type is null)
            return;
        _selectedType = type;
        _model.EquipmentTypeId = type.Id;
        ModelSource.Filter.EquipmentTypeId = type.Id;
        ClearHiddenFeatures();
        UpdateOptions();
        await LoadPhonesForCurrentTypeAsync();
    }

    private void OnModelIdChanged(int? id)
    {
        if (_model is null)
            return;
        _modelVersion++;
        _model.ModelId = id;
        if (id is null)
            return;
        if (_selectedModel?.Id != id)
            _selectedModel = null;
    }

    private async Task OnModelChangedAsync(EquipmentModel? model)
    {
        if (_model is null || model is null)
            return;
        var version = ++_modelVersion;
        _selectedModel = model;
        _model.ModelId = model.Id;
        if (_model.EquipmentTypeId != model.EquipmentTypeId)
        {
            _model.EquipmentTypeId = model.EquipmentTypeId;
            ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
            var selectedType = await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token);
            if (_disposed || version != _modelVersion || _model.ModelId != model.Id)
                return;
            _selectedType = selectedType;
        }
        ClearHiddenFeatures();
        UpdateOptions();
    }

    private async Task OnOwnerChangedAsync(int? id)
    {
        if (_model is null)
            return;
        if (id is null)
        {
            _ownerVersion++;
            _shipperVersion++;
            _model.OwnerId = null;
            return;
        }
        if (_confirmedOwnerId == id)
        {
            _model.OwnerId = id;
            SetDocumentOwner(id);
            return;
        }
        _ownerVersion++;
        _shipperVersion++;
        _confirmedOwnerId = id;
        _model.OwnerId = id;
        _model.DocumentReceiptOuId = null;
        _model.DocumentWriteOffId = null;
        _model.ShipperId = null;
        SetDocumentOwner(id);
        await Task.CompletedTask;
    }

    private void SetDocumentOwner(int? id) => ReceiptDocumentSource.PersonId = WriteOffDocumentSource.PersonId = id;

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

    private async Task AddPhoneAsync()
    {
        if (_model is null || _phoneToAdd is null || !_canEdit)
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
        var dialog = await DialogService.ShowAsync<EquipmentPhoneDialog>("Срок возврата", parameters);
        var result = await dialog.Result;
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
    }

    private async Task LoadPhonesForCurrentTypeAsync()
    {
        if (_model is null || !ShowPhones)
            return;
        var version = ++_phoneVersion;
        var id = _model.Id;
        var phones = await Service.GetPhonesAsync(id);
        if (_disposed || version != _phoneVersion || _model.Id != id)
            return;
        _phones = phones.ToList();
        _originalPhones = ClonePhones(phones);
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
        var isEdit = existing is not null;
        var parameters = new DialogParameters<EquipmentLocationDialog>
        {
            { dialog => dialog.IsEdit, isEdit },
            { dialog => dialog.LocationId, existing?.LocationId },
            { dialog => dialog.FromDate, existing?.FromUtc.Date },
            { dialog => dialog.FromTime, existing?.FromUtc.TimeOfDay },
            { dialog => dialog.ReturnDate, existing?.ReturnDate },
            { dialog => dialog.Notes, existing?.Notes ?? string.Empty },
        };
        var dialog = await DialogService.ShowAsync<EquipmentLocationDialog>("Расположение", parameters);
        var result = await dialog.Result;
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
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _locationVersion)
                _validationMessage = "Расположение не сохранено. Подробности — в сообщении базы данных.";
        }
        catch (InvalidOperationException ex)
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
        await RefreshLocationDisplayDatesAsync();
    }

    private async Task RefreshLocationDisplayDatesAsync()
    {
        _localDates.Clear();
        foreach (var utc in _locations.SelectMany(item => new[] { item.FromUtc, item.ChangedAtUtc }).Distinct())
        {
            var offset = await JS.InvokeAsync<int>(
                "clayGridTimeZone.getOffsetMinutes", DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O"));
            if (_disposed)
                return;
            _localDates[utc] = utc.AddMinutes(offset);
        }
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

    private void ClearHiddenFeatures()
    {
        if (_model is null)
            return;
        if (!ShowNetworkName) _model.NetworkName = string.Empty;
        if (MacCount < 1) _model.MacAddress = string.Empty;
        if (MacCount < 2) _model.MacAddress2 = string.Empty;
        if (MacCount < 3) _model.MacAddressIlo = string.Empty;
        if (!ShowPhonePool) _model.PhonePoolId = null;
        if (!ShowCamera) _model.CameraIsConnected = 0;
    }

    private void UpdateOptions()
    {
        if (_model is null)
            return;
        _snOptions.Required = _selectedType?.IsSerialNumberRequired > 0;
        foreach (var options in new[] { _typeOptions, _modelOptions, _phonePoolOptions, _phoneOptions, _ownerOptions, _receiptDocumentOptions, _writeOffDocumentOptions, _shipperOptions, _tenantOptions })
            options.ReadOnly = !_canEdit;
        foreach (var options in new ClayInputOptions[] { _writeOffDateOptions, _writeOffTimeOptions, _snOptions, _networkNameOptions, _macOptions, _descriptionOptions })
            options.ReadOnly = !_canEdit;
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
            if (ShowPhones && phonesModified)
                await Service.UpdateWithPhonesAsync(model, _originalPhones, _phones);
            else
                await Service.UpdateAsync(model);
            if (!IsCurrentSave(model, version))
                return;
            _originalSnapshot = Snapshot();
            var warnings = new List<string>();
            if (ShowPhones && phonesModified)
            {
                try
                {
                    warnings.AddRange(await Service.SynchronizePhonesAsync(model.Id, _originalPhones, _phones));
                }
                catch (Microsoft.Data.SqlClient.SqlException)
                {
                    warnings.Add("Синхронизация с АТС не выполнена. Подробности — в сообщении базы данных.");
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add($"Синхронизация с АТС не выполнена: {ex.Message}");
                }
            }
            _originalPhones = ClonePhones(_phones);
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
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrentSave(model, version))
                _validationMessage = "Оборудование не сохранено. Подробности — в сообщении базы данных.";
        }
        catch (InvalidOperationException ex)
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
        if (IsModified && !await ConfirmAsync("Обновление", "Отменить несохранённые изменения и загрузить запись повторно?"))
            return;
        _warningMessage = null;
        await LoadCurrentAsync(Id);
    }

    private async Task CancelAsync()
    {
        if (IsModified && !await ConfirmAsync("Закрытие формы", "Закрыть форму без сохранения изменений?"))
            return;
        await CloseAsync();
    }

    private async Task CopyAsync()
    {
        if (Id > 0)
            await JS.InvokeVoidAsync("open", $"/Equipment/EquipmentIns?copyId={Id}", "_blank");
    }

    private async Task OpenModelViewAsync(EquipmentModel model) =>
        await JS.InvokeVoidAsync("open", EquipmentIns.BuildEditUrl(Configuration["URI_model_form"]!, model.Id), "_blank");

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
        if (!await ConfirmAsync("Переход на другую страницу", "Покинуть форму без сохранения изменений?"))
            return;
        _allowNavigation = true;
        Navigation.NavigateTo(context.TargetLocation);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { dialog => dialog.Title, title },
            { dialog => dialog.Message, message },
            { dialog => dialog.ConfirmText, "Да" },
            { dialog => dialog.CancelText, "Нет" },
        };
        var dialog = await DialogService.ShowAsync<ConfirmDialog>(title, parameters);
        var result = await dialog.Result;
        return result is not null && !result.Canceled;
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
