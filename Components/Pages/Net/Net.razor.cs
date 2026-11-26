using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Net;

/// <summary>Форма редактирования существующего порта локальной сети.</summary>
public partial class Net : IDisposable
{
    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "net-form", MaxWidth = "100%" };
    private readonly ClaySelectOptions _selectOptions = new() { AutoSelectSingle = true };
    private readonly ClaySelectOptions _customerOptions = new()
    {
        AutoSelectSingle = true,
        Required = true,
        RequiredError = "Не выбран заказчик.",
    };
    private readonly ClayTextAreaOptions _textAreaOptions = new() { Lines = 4 };
    private readonly ClayDatePickerOptions _dateOptions = new();
    private readonly ClayToolbarButtonOptions _okOptions = new()
    {
        Text = "ОК",
        Icon = Icons.Material.Filled.Check,
        Color = Color.Primary,
        Variant = Variant.Filled,
    };
    private readonly ClayToolbarButtonOptions _applyOptions = new() { Text = "Применить", Icon = Icons.Material.Filled.Save };
    private readonly ClayToolbarButtonOptions _cancelOptions = new() { Text = "Отмена", Icon = Icons.Material.Filled.Close };
    private readonly ClayToolbarButtonOptions _retryOptions = new() { Text = "Повторить", Icon = Icons.Material.Filled.Refresh };
    private readonly ClayToolbarButtonOptions _poeOptions = new()
    {
        Text = "Перезапустить PoE",
        Icon = Icons.Material.Filled.Refresh,
        Title = "Перезапустить PoE",
    };

    private LocalNetworkPort? _model;
    private Employee? CurrentUser;
    private IReadOnlyList<LocalNetworkNeighborPort> _switchPorts = [];
    private IReadOnlyList<LocalNetworkNeighborPort> _exchangePorts = [];
    private IReadOnlyList<NetEquipmentLink> _connectedLinks = [];
    private IReadOnlyList<NetEquipmentLink> _descriptionLinks = [];
    private string? _windowsPrincipal;
    private string? _socketNotes;
    private string? _validationMessage;
    private string? _linkError;
    private string? _loadError;
    private string? _parentRefreshWarning;
    private NetDraft? _originalSnapshot;
    private int? _switchToId = -1;
    private int? _exchangeWithId = -1;
    private int? _employeeOrganizationId;
    private string? _employeeOrganizationName;
    private int _loadVersion;
    private int _employeeVersion;
    private int _socketVersion;
    private int _loadedId = -1;
    private bool _hasReturnDate;
    private bool _loading = true;
    private bool _saving;
    private bool _requiresReconciliation;
    private bool _poeRunning;
    private bool _loaded;
    private bool _notFound;
    private bool _allowNavigation;
    private bool _disposed;

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = null!;
    [Inject] private Clayzor.Lib.Web.DBSelect.Locations.NetSocketSelectDataSource SocketSource { get; set; } = null!;
    [Inject] private Clayzor.Lib.Web.DBSelect.Phones.PhoneEmployeeSelectDataSource EmployeeSource { get; set; } = null!;
    [Inject] private Clayzor.Lib.Web.DBSelect.Phones.PhoneCustomerSelectDataSource CustomerSource { get; set; } = null!;
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IConfiguration Configuration { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private ClayFormWindowService WindowService { get; set; } = null!;

    /// <summary>Идентификатор существующего порта.</summary>
    [SupplyParameterFromQuery(Name = "id")]
    public int Id { get; set; }

    /// <summary>Идентификатор родительской страницы legacy-протокола.</summary>
    [SupplyParameterFromQuery(Name = "parentidp")]
    public string? ParentIdp { get; set; }

    /// <summary>Идентификатор родительского грида legacy-протокола.</summary>
    [SupplyParameterFromQuery(Name = "parentId")]
    public string? ParentId { get; set; }

    /// <summary>Название родительского окна legacy-протокола.</summary>
    [SupplyParameterFromQuery(Name = "parentName")]
    public string? ParentName { get; set; }

    private string PortCaption => $"{_equipmentNetworkName}_{_model?.Port}";
    private string? _equipmentNetworkName;
    private bool IsModified => _loaded && _model is not null && _originalSnapshot != Snapshot(_model);
    private bool CanSave => IsModified && CurrentUser is not null && !_loading && !_saving && !_poeRunning && !_requiresReconciliation;
    private bool ShowSwitch => _exchangeWithId is not > 0;
    private bool ShowExchange => _switchToId is not > 0;
    private string? ChangedByUrl => _model?.ChangedById is > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? $"{Configuration["URI_user_form"]}?id={_model.ChangedById}"
        : null;
    private bool ShowCompanyMismatch => _model?.EmployeeId is > 0 && _model.PersonCustomerId > 0 &&
        _employeeOrganizationId is not null && _model.PersonCustomerId != _employeeOrganizationId;
    private string CompanyMismatchName => !string.IsNullOrWhiteSpace(_employeeOrganizationName)
        ? _employeeOrganizationName
        : $"#{_employeeOrganizationId}";
    private bool ShowExpiredWarning => _hasReturnDate && _model?.ReturnDate?.Date < DateTime.Today;

    /// <summary>Загружает запись после интерактивного рендера и повторно загружает её при смене ID.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loadedId == Id)
            return;
        _loadedId = Id;
        await LoadCurrentAsync(Id);
    }

    private Task LoadCurrentAsync(int id) => LoadAsync(id, ++_loadVersion, null);

    private async Task LoadAsync(int id, int version, NetDraft? submitted)
    {
        _loading = true;
        if (submitted is null)
        {
            _employeeVersion++;
            _socketVersion++;
            _loaded = false;
            _notFound = false;
            _requiresReconciliation = false;
            _loadError = null;
            _validationMessage = null;
            _parentRefreshWarning = null;
            _linkError = null;
            _model = null;
            CurrentUser = null;
            _equipmentNetworkName = null;
            _socketNotes = null;
            _employeeOrganizationId = null;
            _employeeOrganizationName = null;
            _connectedLinks = [];
            _descriptionLinks = [];
        }
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            await LoadDataAsync(id, version, submitted);
        }
        catch (OperationCanceledException) when (_disposed || version != _loadVersion)
        {
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrent(version, id))
            {
                CurrentUser = null;
                if (submitted is null)
                    _loadError = "Не удалось загрузить порт локальной сети. Проверьте соединение и повторите попытку.";
                else
                    AcceptWithoutReload(submitted, "Изменения сохранены, но перечитать порт не удалось. Поздний ввод сохранён в открытой форме.");
            }
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

    private Task RetryLoadAsync() => _loading ? Task.CompletedTask : LoadCurrentAsync(Id);

    private async Task LoadDataAsync(int id, int version, NetDraft? submitted)
    {
        if (!IsCurrent(version, id))
            return;
        if (id <= 0)
        {
            _notFound = true;
            return;
        }

        var authentication = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (!IsCurrent(version, id))
            return;
        var principal = authentication.User.Identity?.Name;
        var currentUser = await Employee.GetCurrentAsync(Db, principal, _pageCts.Token);
        if (!IsCurrent(version, id))
            return;
        var port = await LocalNetworkPort.GetAsync(Db, id, _pageCts.Token);
        if (!IsCurrent(version, id))
            return;
        if (port is null)
        {
            if (submitted is null)
                _notFound = true;
            else
            {
                CurrentUser = null;
                AcceptWithoutReload(submitted, "Изменения сохранены, но порт больше недоступен. Поздний ввод сохранён в открытой форме.");
            }
            return;
        }

        var networkName = await LocalNetworkPort.GetEquipmentNetworkNameAsync(Db, port.EquipmentId, _pageCts.Token);
        if (!IsCurrent(version, id))
            return;
        var officeId = await LocalNetworkPort.GetOfficeLocationIdAsync(Db, port.EquipmentId, _pageCts.Token);
        if (!IsCurrent(version, id))
            return;
        var freePorts = await LocalNetworkPort.GetNeighborsAsync(Db, id, true, _pageCts.Token);
        if (!IsCurrent(version, id))
            return;
        var allPorts = await LocalNetworkPort.GetNeighborsAsync(Db, id, false, _pageCts.Token);
        if (!IsCurrent(version, id))
            return;

        NetSocketLookup? socket = null;
        if (port.SocketId is > 0)
            socket = await SocketSource.GetByIdAsync(port.SocketId, _pageCts.Token);
        if (!IsCurrent(version, id))
            return;
        Employee? employee = null;
        if (port.EmployeeId is > 0)
            employee = await EmployeeSource.GetByIdAsync(port.EmployeeId, _pageCts.Token);
        if (!IsCurrent(version, id))
            return;

        IReadOnlyList<NetEquipmentLink> connectedLinks = [];
        IReadOnlyList<NetEquipmentLink> descriptionLinks = [];
        string? linkError = null;
        try
        {
            var links = await NetEquipmentLinkResolver.ResolveAsync(Db, port.Connected, port.Description, _pageCts.Token);
            connectedLinks = links.Connected;
            descriptionLinks = links.Description;
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            linkError = "Не удалось сформировать ссылки на оборудование. Исходный текст сохранён без изменений.";
        }
        if (!IsCurrent(version, id))
            return;

        var fresh = NetDraft.From(port, port.ReturnDate.HasValue);
        var edited = submitted is not null && _model is not null ? Snapshot(_model) : fresh;
        if (submitted is not null && fresh.ConflictsWithLaterEdits(submitted, edited))
        {
            CurrentUser = currentUser;
            AcceptWithoutReload(submitted, "Отправленные изменения сохранены. Сервер также изменил поля с поздним вводом. Поздний ввод оставлен в форме для сверки.");
            return;
        }
        var visible = submitted is null ? fresh : fresh.WithLaterEdits(submitted, edited);
        var employeeEdited = submitted is not null && edited.EmployeeId != submitted.EmployeeId;
        var socketEdited = submitted is not null && edited.SocketId != submitted.SocketId;
        _windowsPrincipal = principal;
        CurrentUser = currentUser;
        _model = port;
        visible.ApplyTo(port);
        _originalSnapshot = fresh;
        _equipmentNetworkName = networkName ?? string.Empty;
        _switchPorts = WithPortPrompt(freePorts);
        _exchangePorts = WithPortPrompt(allPorts);
        if (!socketEdited)
            _socketNotes = string.IsNullOrWhiteSpace(socket?.Notes) ? null : socket.Notes;
        if (!employeeEdited)
        {
            _employeeOrganizationId = employee?.OrganizationId;
            _employeeOrganizationName = employee?.OrganizationName;
        }
        SocketSource.CurrentSocketId = port.SocketId;
        SocketSource.OfficeLocationId = officeId;
        _switchToId = visible.SwitchToId;
        _exchangeWithId = visible.ExchangeWithId;
        _hasReturnDate = visible.HasReturnDate;
        _connectedLinks = visible.Connected == fresh.Connected ? connectedLinks : [];
        _descriptionLinks = visible.Description == fresh.Description ? descriptionLinks : [];
        _linkError = linkError;
        _validationMessage = null;
        _loaded = true;
    }



    private IReadOnlyList<LocalNetworkNeighborPort> WithPortPrompt(IEnumerable<LocalNetworkNeighborPort> ports) =>
        [new LocalNetworkNeighborPort { Id = -1, Name = "выберите порт" }, .. ports.Where(item => item.Id > 0 && item.Id != Id)];

    private async Task OnSocketChangedAsync(int? id)
    {
        if (_model is null)
            return;
        var model = _model;
        var portId = Id;
        model.SocketId = id;
        var version = ++_socketVersion;
        _socketNotes = null;
        if (id is null)
            return;
        var socket = await SocketSource.GetByIdAsync(id, _pageCts.Token);
        if (_disposed || version != _socketVersion || _model is null ||
            Id != portId || _model.SocketId != id)
            return;
        _socketNotes = string.IsNullOrWhiteSpace(socket?.Notes) ? null : socket.Notes;
    }

    private async Task OnEmployeeChangedAsync(int? id)
    {
        if (_model is null)
            return;
        var model = _model;
        var portId = Id;
        model.EmployeeId = id;
        var version = ++_employeeVersion;
        _employeeOrganizationId = null;
        _employeeOrganizationName = null;
        if (id is null)
            return;
        var employee = await EmployeeSource.GetByIdAsync(id, _pageCts.Token);
        if (_disposed || version != _employeeVersion || _model is null ||
            Id != portId || _model.EmployeeId != id)
            return;
        _employeeOrganizationId = employee?.OrganizationId;
        _employeeOrganizationName = employee?.OrganizationName;
        if (employee?.OrganizationId is > 0)
            _model.PersonCustomerId = employee.OrganizationId.Value;
    }

    private void OnCustomerChanged(int? id)
    {
        if (_model is not null)
        {
            _employeeVersion++;
            _model.PersonCustomerId = id ?? 0;
        }
    }

    private void OnSwitchChanged(int? id)
    {
        _switchToId = id ?? -1;
        if (_switchToId is > 0)
            _exchangeWithId = -1;
    }

    private void OnExchangeChanged(int? id)
    {
        _exchangeWithId = id ?? -1;
        if (_exchangeWithId is > 0)
            _switchToId = -1;
    }

    private void OnReturnDateEnabledChanged(bool? enabled)
    {
        _hasReturnDate = enabled == true;
        if (!_hasReturnDate && _model is not null)
            _model.ReturnDate = null;
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        if (_model is null || _loading || _saving || _poeRunning || CurrentUser is null || _requiresReconciliation)
            return;
        if (!IsModified)
        {
            if (closeAfterSave)
                await CloseAsync();
            return;
        }

        var model = _model;
        var submitted = Snapshot(model);
        var expectedChangedAt = model.ChangedAtUtc;
        var command = new LocalNetworkPort { Id = model.Id };
        submitted.ApplyTo(command);
        var version = ++_loadVersion;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            if (!IsCurrentSave(model, version))
                return;
            var validation = command.Validate();
            if (validation is not null)
            {
                _validationMessage = validation;
                return;
            }
            var currentUser = await Employee.GetCurrentAsync(Db, _windowsPrincipal, _pageCts.Token);
            if (!IsCurrentSave(model, version))
                return;
            CurrentUser = currentUser;
            if (currentUser is null)
            {
                _validationMessage = "Не удалось подтвердить текущего пользователя. Сохранение запрещено.";
                return;
            }

            await LocalNetworkPort.SaveAsync(Db, command, expectedChangedAt,
                submitted.SwitchToId is > 0 ? submitted.SwitchToId : null,
                submitted.ExchangeWithId is > 0 ? submitted.ExchangeWithId : null);
            if (!IsCurrentSave(model, version))
                return;

            if (!closeAfterSave)
                await LoadAsync(model.Id, version, submitted);
            if (!IsCurrent(version, model.Id))
                return;

            try
            {
                await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", _pageCts.Token);
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException)
            {
                if (IsCurrent(version, model.Id))
                    _parentRefreshWarning = "Изменения сохранены, но родительский грид не обновлён. Обновите его при следующем открытии.";
            }

            if (!closeAfterSave || !IsCurrentSave(model, version))
                return;
            if (Snapshot(model) == submitted)
            {
                AcceptWithoutReload(submitted, null);
                await CloseAsync();
            }
            else
            {
                await LoadAsync(model.Id, version, submitted);
            }
        }
        catch (OperationCanceledException) when (_disposed || version != _loadVersion)
        {
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrentSave(model, version))
                _validationMessage = "Результат сохранения локальной сети неизвестен. Обновите запись и проверьте состояние данных.";
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

    private async Task RestartPoeAsync()
    {
        var model = _model;
        var version = _loadVersion;
        if (model?.Poe != 1 || model.Id != Id || CurrentUser is null || _saving || _poeRunning)
            return;
        if (!await ConfirmAsync("Перезапустить PoE?", "Перезапустить PoE для этого порта?"))
            return;
        if (!IsCurrentSave(model, version) || model.Poe != 1)
            return;
        _poeRunning = true;
        _validationMessage = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            CurrentUser = await Employee.GetCurrentAsync(Db, _windowsPrincipal, _pageCts.Token);
            if (!IsCurrentSave(model, version) || model.Poe != 1)
                return;
            if (CurrentUser is null)
                throw new UnauthorizedAccessException("Не удалось подтвердить текущего пользователя. Перезапуск PoE запрещён.");
            await LocalNetworkPort.ResetPoeAsync(Db, model.Id);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            if (IsCurrentSave(model, version))
                _validationMessage = $"Ошибка при перезапуске PoE: {ex.Message}";
        }
        finally
        {
            if (!_disposed)
            {
                _poeRunning = false;
                StateHasChanged();
            }
        }
    }

    private async Task OpenEquipmentAsync(int equipmentId)
    {
        var url = Configuration["URI_equipment_form"];
        if (equipmentId <= 0 || string.IsNullOrWhiteSpace(url))
            return;
        await WindowService.OpenAsync($"{url}?id={equipmentId}", "equipment-form", _pageCts.Token);
    }

    private async Task CancelAsync()
    {
        if (IsModified && !await ConfirmAsync("Закрытие формы", "Закрыть форму без сохранения изменений?"))
            return;
        await CloseAsync();
    }

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
        if (_saving || _poeRunning)
        {
            context.PreventNavigation();
            return;
        }
        if (!IsModified)
            return;
        context.PreventNavigation();
        if (await ConfirmAsync("Закрытие формы", "Перейти без сохранения изменений?"))
        {
            _allowNavigation = true;
            Navigation.NavigateTo(context.TargetLocation);
        }
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

    private bool IsCurrent(int version, int id) => !_disposed && version == _loadVersion && Id == id;
    private bool IsCurrentSave(LocalNetworkPort model, int version) => IsCurrent(version, model.Id) && ReferenceEquals(_model, model);

    private NetDraft Snapshot(LocalNetworkPort model) =>
        NetDraft.From(model, _hasReturnDate, _switchToId, _exchangeWithId);

    private void AcceptWithoutReload(NetDraft submitted, string? message)
    {
        if (_switchToId == submitted.SwitchToId && _exchangeWithId == submitted.ExchangeWithId)
        {
            _switchToId = -1;
            _exchangeWithId = -1;
        }
        _originalSnapshot = submitted with { SwitchToId = -1, ExchangeWithId = -1 };
        _validationMessage = message;
        _requiresReconciliation = message is not null;
    }

    private sealed record NetDraft(
        int CustomerId, int? EmployeeId, int? SocketId, string Connected, string Description,
        DateTime? ReturnDate, bool HasReturnDate, int? SwitchToId, int? ExchangeWithId)
    {
        internal static NetDraft From(LocalNetworkPort port, bool hasReturnDate, int? switchToId = -1, int? exchangeWithId = -1) =>
            new(port.PersonCustomerId, port.EmployeeId, port.SocketId, port.Connected, port.Description,
                port.ReturnDate, hasReturnDate, switchToId, exchangeWithId);

        internal void ApplyTo(LocalNetworkPort port)
        {
            port.PersonCustomerId = CustomerId;
            port.EmployeeId = EmployeeId;
            port.SocketId = SocketId;
            port.Connected = Connected;
            port.Description = Description;
            port.ReturnDate = ReturnDate;
        }

        internal bool ConflictsWithLaterEdits(NetDraft submitted, NetDraft edited) =>
            Conflict(submitted.CustomerId, edited.CustomerId, CustomerId) ||
            Conflict(submitted.EmployeeId, edited.EmployeeId, EmployeeId) ||
            Conflict(submitted.SocketId, edited.SocketId, SocketId) ||
            Conflict(submitted.Connected?.Trim() ?? string.Empty, edited.Connected?.Trim() ?? string.Empty, Connected?.Trim() ?? string.Empty) ||
            Conflict(submitted.Description?.Trim() ?? string.Empty, edited.Description?.Trim() ?? string.Empty, Description?.Trim() ?? string.Empty) ||
            Conflict(submitted.ReturnDate?.Date, edited.ReturnDate?.Date, ReturnDate?.Date);

        private static bool Conflict<T>(T submitted, T edited, T fresh)
        {
            var equality = EqualityComparer<T>.Default;
            return !equality.Equals(edited, submitted) &&
                   !equality.Equals(fresh, submitted) &&
                   !equality.Equals(fresh, edited);
        }

        internal NetDraft WithLaterEdits(NetDraft submitted, NetDraft edited)
        {
            var dateChanged = (edited.ReturnDate, edited.HasReturnDate) != (submitted.ReturnDate, submitted.HasReturnDate);
            var operationChanged = (edited.SwitchToId, edited.ExchangeWithId) != (submitted.SwitchToId, submitted.ExchangeWithId);
            return this with
            {
                CustomerId = edited.CustomerId != submitted.CustomerId ? edited.CustomerId : CustomerId,
                EmployeeId = edited.EmployeeId != submitted.EmployeeId ? edited.EmployeeId : EmployeeId,
                SocketId = edited.SocketId != submitted.SocketId ? edited.SocketId : SocketId,
                Connected = edited.Connected != submitted.Connected ? edited.Connected : Connected,
                Description = edited.Description != submitted.Description ? edited.Description : Description,
                ReturnDate = dateChanged ? edited.ReturnDate : ReturnDate,
                HasReturnDate = dateChanged ? edited.HasReturnDate : HasReturnDate,
                SwitchToId = operationChanged ? edited.SwitchToId : SwitchToId,
                ExchangeWithId = operationChanged ? edited.ExchangeWithId : ExchangeWithId,
            };
        }
    }



    /// <summary>Отменяет загрузки и запрещает поздним ответам изменять закрытую форму.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loadVersion++;
        _employeeVersion++;
        _socketVersion++;
        _pageCts.Cancel();
        _pageCts.Dispose();
    }
}
