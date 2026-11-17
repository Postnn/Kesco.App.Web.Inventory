using System.Text.Json;
using System.Web;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Documents;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Создание оборудования с начальным расположением, ответственным и бухгалтерским учётом.</summary>
public partial class EquipmentIns : IDisposable
{
    private const int InventorySpecialistRoleId = 43;
    private const int OnHandsLocationId = 55;
    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new()
    {
        Class = "model-equipment-form equipment-ins-form",
        MaxWidth = "760px",
    };
    private readonly ClayToolbarButtonOptions _okOptions = new()
    {
        Text = "OK", Icon = Icons.Material.Filled.Check, Title = "Сохранить оборудование и закрыть форму",
        Variant = Variant.Filled, Color = Color.Primary,
    };
    private readonly ClayToolbarButtonOptions _applyOptions = new()
    {
        Text = "Применить", Icon = Icons.Material.Filled.Save, Title = "Сохранить и открыть форму редактирования",
    };
    private readonly ClayToolbarButtonOptions _refreshOptions = new()
    {
        Text = "Обновить", Icon = Icons.Material.Filled.Refresh, Title = "Восстановить исходные значения",
    };
    private readonly ClayToolbarButtonOptions _cancelOptions = new()
    {
        Text = "Отмена", Icon = Icons.Material.Filled.Close, Title = "Закрыть форму",
    };
    private readonly ClaySelectOptions _typeOptions = new()
    {
        Required = true, AutoSelectSingle = true, MaxItems = 300, Placeholder = "Введите тип оборудования",
    };
    private readonly ClaySelectOptions _modelOptions = new()
    {
        Required = true, AutoSelectSingle = true, Placeholder = "Введите модель",
    };
    private readonly ClaySelectOptions _ownerOptions = new()
    {
        Required = true, AutoSelectSingle = true, Placeholder = "Введите владельца",
    };
    private readonly ClaySelectOptions _personOptions = new() { AutoSelectSingle = true };
    private readonly ClaySelectOptions _documentOptions = new() { AutoSelectSingle = true };
    private readonly ClaySelectOptions _locationOptions = new()
    {
        Required = true, AutoSelectSingle = true, Placeholder = "Введите расположение",
    };
    private readonly ClaySelectOptions _responsibleOptions = new() { AutoSelectSingle = true };
    private readonly ClaySelectOptions _accountingDocumentOptions = new() { AutoSelectSingle = true };
    private readonly ClaySelectOptions _tenantOptions = new() { AutoSelectSingle = true };
    private readonly ClayDatePickerOptions _dateOptions = new() { Required = true, Clearable = false };
    private readonly ClayTimeOptions _timeOptions = new() { Required = true, Clearable = false };
    private readonly ClayTextBoxOptions _snOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _networkNameOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _macOptions = new() { MaxLength = 12 };
    private readonly ClayTextBoxOptions _locationDescriptionOptions = new() { MaxLength = 300 };
    private readonly ClayTextBoxOptions _responsibleDescriptionOptions = new() { MaxLength = 300 };
    private readonly ClayTextBoxOptions _inventoryNumberOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _accountingDescriptionOptions = new() { MaxLength = 300 };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { MaxLength = 300, Lines = 3 };

    private global::Clayzor.Lib.Entities.Inventory.Equipment _model = new();
    private MudForm? _form;
    private EquipmentType? _selectedType;
    private EquipmentModel? _selectedModel;
    private DateTime? _receiptDate;
    private TimeSpan? _receiptTime = TimeSpan.Zero;
    private bool _sortTypesByName;
    private bool _loading;
    private bool _saving;
    private bool _loaded;
    private bool _canEdit;
    private bool _notFound;
    private bool _accountingAvailable;
    private bool _allowNavigation;
    private bool _disposed;
    private int _operationVersion;
    private int _ownerVersion;
    private string? _validationMessage;
    private string _originalSnapshot = string.Empty;

    private DocumentSelectDataSource ReceiptDocumentSource =>
        Services.GetRequiredKeyedService<DocumentSelectDataSource>("receipt");
    private DocumentSelectDataSource AccountingDocumentSource =>
        Services.GetRequiredKeyedService<DocumentSelectDataSource>("accounting");

    private bool IsModified => _loaded && Snapshot() != _originalSnapshot;
    private bool CanSave => _loaded && _canEdit && !_loading && !_saving && IsModified;
    private bool ShowNetworkName => _selectedType?.HasNetworkName > 0;
    private int MacCount => Math.Min(3, _selectedModel?.NetworkInterfaces ?? 0);

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var version = ++_operationVersion;
        _loading = true;
        _loaded = false;
        _notFound = false;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            var copyId = QueryInt("copyId");
            var initialTypeId = QueryInt("codetype");
            var model = copyId > 0 ? await Service.GetForCopyAsync(copyId) : new global::Clayzor.Lib.Entities.Inventory.Equipment();
            if (!IsCurrent(version))
                return;
            if (model is null)
            {
                _notFound = true;
                return;
            }
            model.Id = 0;
            if (model.EquipmentTypeId is null && initialTypeId > 0)
                model.EquipmentTypeId = initialTypeId;

            var canEdit = await Service.HasRoleAsync(InventorySpecialistRoleId);
            if (!IsCurrent(version))
                return;
            var localOffset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", _pageCts.Token);
            if (!IsCurrent(version))
                return;

            _model = model;
            ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
            ReceiptDocumentSource.PersonId = model.OwnerId;
            _selectedType = model.EquipmentTypeId is > 0
                ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token)
                : null;
            _selectedModel = model.ModelId is > 0
                ? await ModelSource.GetByIdAsync(model.ModelId, _pageCts.Token)
                : null;
            if (!IsCurrent(version))
                return;
            _receiptDate = DateTime.UtcNow.AddMinutes(localOffset).Date;
            _receiptTime = TimeSpan.Zero;
            _canEdit = canEdit;
            _accountingAvailable = await Service.CanUseAccountingAsync(model.OwnerId);
            if (!IsCurrent(version))
                return;
            UpdateOptions();
            _validationMessage = null;
            _loaded = true;
            _originalSnapshot = Snapshot();
        }
        finally
        {
            if (IsCurrent(version))
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private void OnTypeIdChanged(int? id)
    {
        if (_model.EquipmentTypeId == id)
            return;
        _model.EquipmentTypeId = id;
        _model.ModelId = null;
        _selectedType = null;
        _selectedModel = null;
        ModelSource.Filter.EquipmentTypeId = id;
        ClearHiddenFeatures();
        UpdateOptions();
    }

    private void OnTypeChanged(EquipmentType? type)
    {
        if (type is null)
            return;
        _selectedType = type;
        _model.EquipmentTypeId = type.Id;
        ModelSource.Filter.EquipmentTypeId = type.Id;
        UpdateOptions();
    }

    private void OnModelIdChanged(int? id)
    {
        if (_model.ModelId == id)
            return;
        _model.ModelId = id;
        _selectedModel = null;
        ClearHiddenFeatures();
    }

    private async Task OnModelChangedAsync(EquipmentModel? model)
    {
        if (model is null)
            return;
        _selectedModel = model;
        _model.ModelId = model.Id;
        if (_model.EquipmentTypeId != model.EquipmentTypeId)
        {
            _model.EquipmentTypeId = model.EquipmentTypeId;
            ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
            _selectedType = await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token);
        }
        ClearHiddenFeatures();
        UpdateOptions();
    }

    private async Task OnOwnerChangedAsync(int? id)
    {
        if (_model.OwnerId == id)
            return;
        var version = ++_ownerVersion;
        _model.OwnerId = id;
        _model.DocumentReceiptOuId = null;
        _model.ShipperId = null;
        ReceiptDocumentSource.PersonId = id;
        _accountingAvailable = false;
        _model.IsAccounting = false;
        ClearAccountingValues();
        UpdateOptions();
        if (id is null)
            return;
        var available = await Service.CanUseAccountingAsync(id);
        if (_disposed || version != _ownerVersion || _model.OwnerId != id)
            return;
        _accountingAvailable = available;
        UpdateOptions();
        StateHasChanged();
    }

    private async Task OnReceiptDocumentChangedAsync(int? id)
    {
        _model.DocumentReceiptOuId = id;
        if (id is null || _model.OwnerId is null)
            return;
        var shipperId = await ReceiptDocumentSource.GetSingleShipperIdAsync(id.Value, _model.OwnerId.Value);
        if (!_disposed && _model.DocumentReceiptOuId == id && shipperId is not null)
            _model.ShipperId = shipperId;
    }

    private void OnLocationChanged(int? id)
    {
        _model.LocationId = id;
        UpdateOptions();
    }

    private void OnAccountingChanged(bool? value)
    {
        _model.IsAccounting = value == true;
        if (!_model.IsAccounting)
            ClearAccountingValues();
        UpdateOptions();
    }

    private void OnTypeSortChanged(bool? value) => EquipmentTypeSource.SortByName = _sortTypesByName = value == true;

    private void OnMacChanged(string? value, int index)
    {
        var normalized = global::Clayzor.Lib.Entities.Inventory.Equipment.NormalizeMacAddress(value);
        if (index == 1) _model.MacAddress = normalized;
        if (index == 2) _model.MacAddress2 = normalized;
        if (index == 3) _model.MacAddressIlo = normalized;
    }

    private void ClearHiddenFeatures()
    {
        if (!ShowNetworkName)
            _model.NetworkName = null;
        if (MacCount < 1) _model.MacAddress = null;
        if (MacCount < 2) _model.MacAddress2 = null;
        if (MacCount < 3) _model.MacAddressIlo = null;
    }

    private void ClearAccountingValues()
    {
        _model.InventoryNumber = null;
        _model.AccountingDocumentId = null;
        _model.TenantId = null;
        _model.AccountingDescription = null;
    }

    private void UpdateOptions()
    {
        _snOptions.Required = _selectedType?.IsSerialNumberRequired > 0;
        _responsibleOptions.Required = _model.LocationId == OnHandsLocationId;
        foreach (var options in new[]
                 {
                     _typeOptions, _modelOptions, _ownerOptions, _personOptions, _documentOptions,
                     _locationOptions, _responsibleOptions,
                 })
            options.ReadOnly = !_canEdit;
        foreach (var options in new ClayInputOptions[]
                 {
                     _dateOptions, _timeOptions, _snOptions, _networkNameOptions, _macOptions,
                     _locationDescriptionOptions, _responsibleDescriptionOptions, _descriptionOptions,
                 })
            options.ReadOnly = !_canEdit;
        _tenantOptions.ReadOnly = !_canEdit;
        _tenantOptions.Disabled = !_model.IsAccounting;
        _accountingDocumentOptions.ReadOnly = !_canEdit;
        _accountingDocumentOptions.Disabled = !_model.IsAccounting;
        _inventoryNumberOptions.ReadOnly = !_canEdit;
        _inventoryNumberOptions.Disabled = !_model.IsAccounting;
        _accountingDescriptionOptions.ReadOnly = !_canEdit;
        _accountingDescriptionOptions.Disabled = !_model.IsAccounting;
    }

    private bool ValidateBusinessRules()
    {
        var errors = new List<string>();
        if (_receiptDate is null || _receiptTime is null) errors.Add("Укажите дату и время поступления.");
        if (_model.EquipmentTypeId is null) errors.Add("Укажите тип оборудования.");
        if (_model.ModelId is null) errors.Add("Укажите модель оборудования.");
        if (_selectedType?.IsSerialNumberRequired > 0 && string.IsNullOrWhiteSpace(_model.SerialNumber))
            errors.Add("Укажите серийный номер.");
        if (_model.OwnerId is null) errors.Add("Укажите владельца оборудования.");
        if (_model.LocationId is null) errors.Add("Укажите расположение оборудования.");
        if (_model.LocationId == OnHandsLocationId && _model.EmployeeId is null)
            errors.Add("Для расположения «На руках» укажите ответственного сотрудника.");
        if (!global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddress) ||
            !global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddress2) ||
            !global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddressIlo))
            errors.Add("MAC-адрес должен содержать 12 шестнадцатеричных символов.");
        if (_model.IsAccounting && !_accountingAvailable)
            errors.Add("Бухгалтерский учёт недоступен для выбранного владельца.");
        _validationMessage = errors.Count == 0 ? null : string.Join(" ", errors);
        return errors.Count == 0;
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        if (!_loaded || !_canEdit || _saving)
            return;
        if (!IsModified)
        {
            if (closeAfterSave)
                await CloseAsync();
            return;
        }

        var version = ++_operationVersion;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Task.Delay(100);
            if (_form is not null)
            {
                await _form.ValidateAsync();
                if (!IsCurrent(version) || !_form.IsValid)
                    return;
            }
            if (!ValidateBusinessRules())
                return;
            _model.ReceivedAtUtc = await ConvertReceiptTimeToUtcAsync();
            if (!IsCurrent(version))
                return;
            var id = await Service.CreateAsync(_model);
            if (!IsCurrent(version))
                return;
            if (id <= 0)
                throw new InvalidOperationException("База данных не вернула идентификатор созданного оборудования.");
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", _pageCts.Token);
            if (!IsCurrent(version))
                return;
            if (closeAfterSave)
            {
                await CloseAsync();
                return;
            }
            _allowNavigation = true;
            Navigation.NavigateTo(BuildEditUrl(GetConfiguredUrl("URI_equipment_form"), id));
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrent(version))
                _validationMessage = "Оборудование не создано. Подробности — в сообщении базы данных.";
        }
        catch (InvalidOperationException ex)
        {
            if (IsCurrent(version))
                _validationMessage = ex.Message;
        }
        finally
        {
            if (IsCurrent(version))
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private async Task<DateTime> ConvertReceiptTimeToUtcAsync()
    {
        var local = _receiptDate!.Value.Date.Add(_receiptTime!.Value);
        var wallTimeAsUtc = DateTime.SpecifyKind(local, DateTimeKind.Utc);
        var offset = await JS.InvokeAsync<int>(
            "clayGridTimeZone.getOffsetMinutes", _pageCts.Token, wallTimeAsUtc.ToString("O"));
        return ApplyClientOffset(local, offset);
    }

    internal static DateTime ApplyClientOffset(DateTime local, int offsetMinutes) =>
        DateTime.SpecifyKind(local.AddMinutes(-offsetMinutes), DateTimeKind.Utc);

    internal static string BuildEditUrl(string url, int id) =>
        $"{url}{(url.Contains('?') ? '&' : '?')}id={id}";

    private string GetConfiguredUrl(string key) => Configuration[key] is { Length: > 0 } url
        ? url
        : throw new InvalidOperationException($"Не задан URL внешней формы: {key}");

    private int QueryInt(string name)
    {
        var query = HttpUtility.ParseQueryString(new Uri(Navigation.Uri).Query);
        return int.TryParse(query[name], out var value) ? value : 0;
    }

    private string Snapshot() => JsonSerializer.Serialize(new
    {
        Model = _model,
        Date = _receiptDate,
        Time = _receiptTime,
    });

    private bool IsCurrent(int version) => !_disposed && version == _operationVersion;

    private async Task RefreshAsync()
    {
        if (_saving || IsModified && !await ConfirmAsync("Несохранённые данные будут потеряны. Продолжить?"))
            return;
        await LoadAsync();
    }

    private async Task CancelAsync()
    {
        if (IsModified && !await ConfirmAsync("Закрыть форму без сохранения изменений?"))
            return;
        await CloseAsync();
    }

    private async Task CloseAsync()
    {
        _allowNavigation = true;
        await JS.InvokeVoidAsync("clayModelEquipmentForm.close");
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = await DialogService.ShowAsync<ConfirmDialog>(
            "Подтверждение",
            new DialogParameters<ConfirmDialog> { { component => component.Message, message } },
            new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Small });
        var result = await dialog.Result;
        return result is { Canceled: false };
    }

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_saving || _allowNavigation || !IsModified)
        {
            if (_saving)
                context.PreventNavigation();
            return;
        }
        if (!await ConfirmAsync("Перейти на другую страницу без сохранения изменений?"))
            context.PreventNavigation();
        else
            _allowNavigation = true;
    }

    /// <summary>Освобождает ресурсы страницы и отменяет ожидания.</summary>
    public void Dispose()
    {
        _disposed = true;
        _operationVersion++;
        _ownerVersion++;
        _pageCts.Cancel();
        _pageCts.Dispose();
    }
}
