using System.Data.SqlTypes;
using System.Globalization;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon.Beeline;

/// <summary>Карточка двух параметров существующего договора Билайн.</summary>
public partial class BeelineParamsForm
{
    private const string ZoneDraftKey = "beeline.zone";
    private const string EmailDraftKey = "beeline.email";
    private const string NoEquipment = "Невозможно открыть форму оборудования, т.к. не удалось определить код оборудования по номеру телефона мобильного бюджета.";

    private static readonly ClayToolbarButtonOptions Ok = new()
    {
        Text = "OK", Icon = Icons.Material.Filled.Check, Color = Color.Primary, Variant = Variant.Filled,
    };
    private static readonly ClayToolbarButtonOptions SaveButton = new() { Text = "Сохранить", Icon = Icons.Material.Filled.Save };
    private static readonly ClayToolbarButtonOptions SubmittedSnapshotButton = new() { Text = "Посмотреть снимок попытки" };
    private static readonly ClayTextBoxOptions RecoveryZoneOptions = new() { Label = "Time zone, ввод из браузера", ReadOnly = true, DraftKey = ZoneDraftKey };
    private static readonly ClayTextBoxOptions RecoveryEmailOptions = new() { Label = "Email менеджера, ввод из браузера", ReadOnly = true, DraftKey = EmailDraftKey };
    private static readonly ClayTextBoxOptions BaseNumberOptions = new() { Label = "Договор основной:", ReadOnly = true };
    private static readonly ClayTextBoxOptions BudgetNumberOptions = new() { Label = "Договор МБ:", ReadOnly = true };
    private static readonly ClayTextBoxOptions BudgetPhoneOptions = new() { ReadOnly = true };
    private static readonly ClayTextBoxOptions CustomerOptions = new() { Label = "Компания:", ReadOnly = true };
    private static readonly ClayTextBoxOptions UserOptions = new() { Label = "Имя в Билайн:", ReadOnly = true };
    private static readonly ClayDatePickerOptions ActualDateOptions = new() { Label = "Актуальность:", DateFormat = "dd.MM.yyyy", ReadOnly = true };
    private static readonly ClayTextBoxOptions ActualTimeOptions = new() { Label = "Время", ReadOnly = true, Disabled = true };
    private static readonly DraftMergeField[] Fields =
    [
        new(nameof(BeelineDraft.TimeZone), "Time zone", ZoneDraftKey),
        new(nameof(BeelineDraft.EmailManager), "Email менеджера", EmailDraftKey),
    ];
    private readonly ClayDropDownListOptions _zoneOptions = new() { Label = "Time zone:", Required = true, DraftKey = ZoneDraftKey, AdvanceFocusOnSelection = true };
    private readonly ClayTextBoxOptions _emailOptions = new()
    {
        Label = "Email менеджера:", Required = true, MaxLength = 50, Immediate = true, DraftKey = EmailDraftKey,
    };

    /// <summary>Исходное значение строкового номера из URL.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    [Inject] private IConfiguration Config { get; set; } = null!;
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;

    private BeelineContract? _contract;
    private string[] _zones = [];
    private bool _canUpdate;
    private bool _openingSim;
    private long _simRevision;
    private string? _freshReadFailure;
    private BeelineDraft? _freshRecovery;
    private BeelineDraft? _submittedRecovery;
    private bool _submittedUnavailable;
    private (string Id, long Revision)? _submittedIdentity;
    private bool _retryingRecovery;

    private bool ShowRecovery => !Loading && CurrentUser is not null && CanReadMissingDraft &&
        (Draft is not null || StorageBlocked) && (_contract is null || !Editable && StorageBlocked);

    private bool CanRetryRecovery => ShowRecovery && !Busy && !_retryingRecovery && !ConfirmedCleanupPending;

    private (string Route, string Number, int EmployeeId, ClayPresenceTarget Target)? _presence;

    private ClayPresenceTarget? GetPresenceTarget()
    {
        if (Loading || ShowRecovery || _contract is not { } contract || !BeelineRules.ValidNumber(contract.Number) ||
            CurrentUser is not { Id: > 0 } employee)
            return null;

        var route = RouteKey;
        var number = contract.Number;
        var employeeId = employee.Id;
        if (_presence is { } previous && previous.Route == route && previous.Number == number &&
            previous.EmployeeId == employeeId)
            return previous.Target;

        var connectionString = Db.ConnectionString;
        var target = new ClayPresenceTarget(ClayPresenceKey.Create("inventory", "beeline-contract", number),
            async (principal, token) =>
            {
                var generation = EditorGeneration;
                bool Current() => !Loading && !Lifetime.IsCancellationRequested && EditorGeneration == generation &&
                    RouteKey == route && _contract?.Number == number && CurrentUser?.Id == employeeId && !ShowRecovery;
                if (!Current() || principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } login })
                    return null;

                using var db = new DbManager(connectionString);
                var actor = await Employee.GetCurrentWithRolesAsync(db, login, token);
                if (!Current() || actor?.Id != employeeId)
                    return null;

                try
                {
                    var fresh = await BeelineData.ReadAsync(db, actor, number, token);
                    if (!Current() || fresh.Contract?.Number != number)
                        return null;

                    return new(actor.Id, actor.Name,
                        fresh.CanUpdate && Editable
                            ? ClayPresenceActivity.Editing
                            : ClayPresenceActivity.Viewing);
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            });
        _presence = (route, number, employeeId, target);
        return target;
    }

    private bool CanSave => !_openingSim && CanCommit && Editable && Dirty;
    private IEnumerable<string> ZoneChoices => Draft?.TimeZone is { Length: > 0 } zone && !_zones.Contains(zone, StringComparer.Ordinal)
        ? _zones.Append(zone) : _zones;

    /// <inheritdoc />
    protected override string FormKey => "beeline-parameters";

    /// <inheritdoc />
    protected override bool UsesRamDraft => true;

    /// <inheritdoc />
    protected override string EntityKey => _contract?.Number ?? RouteKey;

    /// <inheritdoc />
    protected override string RouteKey => BeelineRules.ValidNumber(Id) ? Id! : "invalid:" + Id;

    /// <inheritdoc />
    protected override bool Editable => _contract is not null && _canUpdate && _zones.Length > 0;

    /// <inheritdoc />
    protected override bool CanReadMissingDraft => BeelineRules.ValidNumber(Id);

    /// <inheritdoc />
    protected override IReadOnlyList<DraftMergeField> MergeFields => Fields;

    /// <inheritdoc />
    protected override bool SupportsEditorCancellation => true;

    /// <inheritdoc />
    protected override DraftRowVersion? ReadDraftRowVersion() => _contract is { } contract ? VersionOf(contract) : null;

    private static DraftRowVersion VersionOf(BeelineContract contract) =>
        new(contract.Number, contract.ChangedBy, null, contract.ChangedAt);

    /// <inheritdoc />
    protected override async Task<FreshEditorState?> ReadFreshEditorAsync(Employee actor, CancellationToken token)
    {
        var number = Id;
        var zones = ReadConfiguredZones();
        if (!BeelineRules.ValidNumber(number) || zones.Length == 0 || _contract?.Number != number)
            return null;

        try
        {
            var result = await BeelineData.ReadAsync(Db, actor, number!, token);
            token.ThrowIfCancellationRequested();
            if (!result.CanUpdate || result.Contract is not { } contract || contract.Number != number ||
                contract.ChangedAt < SqlDateTime.MinValue.Value || contract.ChangedAt > SqlDateTime.MaxValue.Value)
                return null;

            return new FreshEditorState(DraftFrom(contract), Access, VersionOf(contract), () =>
            {
                _contract = contract;
                _canUpdate = true;
                _zones = zones;
                _freshRecovery = null;
                _freshReadFailure = null;
                UpdateOptions();
            });
        }
        catch (Exception error) when (error is UnauthorizedAccessException or TimeoutException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override bool IsDomainRejection(Exception exception) => exception is BeelineRejectedException;

    /// <inheritdoc />
    protected override async Task<BeelineDraft?> LoadCardAsync(CancellationToken routeToken)
    {
        _contract = null;
        _freshReadFailure = null;
        _freshRecovery = null;
        _submittedRecovery = null;
        _submittedUnavailable = false;
        _retryingRecovery = false;
        _canUpdate = false;
        ++_simRevision;
        _openingSim = false;
        _zones = ReadConfiguredZones();

        if (!BeelineRules.ValidNumber(Id))
        {
            Message = Id is null or "" ? "Не указан номер договора Билайн." : "Некорректный номер договора Билайн.";
            return null;
        }

        try
        {
            var result = await BeelineData.ReadAsync(Db, CurrentUser!, Id!, routeToken);
            routeToken.ThrowIfCancellationRequested();
            if (result.Contract is not { } contract)
            {
                Message = _freshReadFailure = "Договор Билайн не найден или недоступен.";
                return null;
            }

            _contract = contract;
            _canUpdate = result.CanUpdate;
            UpdateOptions();
            routeToken.ThrowIfCancellationRequested();
            if (!_canUpdate)
                Message = "Договор открыт для просмотра. Изменение параметров недоступно.";
            return DraftFrom(contract);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SqlException or TimeoutException)
        {
            routeToken.ThrowIfCancellationRequested();
            _freshReadFailure = DescribeReadFailure(e);
            Message = _freshReadFailure;
            return null;
        }
    }

    /// <inheritdoc />
    protected override async Task<CompareRead> ReadForComparisonAsync(BeelineDraft proposed, Employee actor, CancellationToken token)
    {
        var number = _contract?.Number ?? Id;
        if (!BeelineRules.ValidNumber(number)) return new(null);
        var result = await BeelineData.ReadAsync(Db, actor, number!, token);
        return new(result.Contract is { } contract ? DraftFrom(contract) : null);
    }

    private string[] ReadConfiguredZones() => Config.GetSection("BeelineTimeZones").GetChildren()
        .Select(entry => entry.Value).OfType<string>().Where(zone => !string.IsNullOrWhiteSpace(zone))
        .Distinct(StringComparer.Ordinal).OrderByDescending(zone => zone, StringComparer.Ordinal).ToArray();

    private Task CancelBeelineAsync() => Busy || Loading || _openingSim || _retryingRecovery
        ? Task.CompletedTask
        : CancelAsync();
    private async Task RefreshBeelineAsync()
    {
        if (CanRefresh)
            await RefreshAsync();
        else
            await RetryRecoveryReadAsync();
    }

    private async Task RetryRecoveryReadAsync()
    {
        if (!CanRetryRecovery || CurrentUser is not { } openedBy)
            return;

        var generation = EditorGeneration;
        var number = Id!;
        var token = Lifetime;
        _retryingRecovery = true;
        _freshRecovery = null;
        _freshReadFailure = null;
        try
        {
            var principal = await ReadPrincipalAsync();
            if (generation != EditorGeneration || token.IsCancellationRequested) return;
            var actor = await Employee.GetCurrentWithRolesAsync(Db, principal, token);
            if (generation != EditorGeneration || token.IsCancellationRequested) return;
            if (actor is null || actor.Id != openedBy.Id)
            {
                _freshReadFailure = "Не удалось подтвердить сотрудника для чтения договора Билайн.";
                return;
            }

            var result = await BeelineData.ReadAsync(Db, actor, number, token);
            if (generation != EditorGeneration || token.IsCancellationRequested) return;
            if (result.Contract is { } contract)
                _freshRecovery = DraftFrom(contract);
            else
                _freshReadFailure = "Договор Билайн не найден или недоступен.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is UnauthorizedAccessException or SqlException or TimeoutException)
        {
            if (generation == EditorGeneration && !token.IsCancellationRequested)
                _freshReadFailure = DescribeReadFailure(e);
        }
        finally
        {
            if (generation == EditorGeneration)
                _retryingRecovery = false;
        }
    }

    private static string DescribeReadFailure(Exception error) => error switch
    {
        SqlException { Number: 229 } => "Нет доступа к данным договора Билайн.",
        SqlException or TimeoutException => "Не удалось прочитать актуальные параметры договора Билайн.",
        _ => error.Message,
    };

    private Task ChangeZoneAsync(string? zone) => Draft is { } draft && !_openingSim
        ? UpdateDraftAsync(draft with { TimeZone = zone ?? "" }) : Task.CompletedTask;

    private Task ChangeEmailAsync(string? email) => Draft is { } draft && !_openingSim
        ? UpdateDraftAsync(draft with { EmailManager = email ?? "" }) : Task.CompletedTask;

    private async Task SaveAsync(bool close)
    {
        if (Busy || Loading || _openingSim || ConfirmedCleanupPending || _contract is not { } original || Draft is not { } draft)
            return;
        if (close && !Dirty)
        {
            await CloseAsync();
            return;
        }
        if (!CanSave) return;

        Validation = BeelineRules.Validate(draft, _zones);
        if (Validation.Count != 0)
        {
            var key = string.IsNullOrEmpty(draft.TimeZone) || !_zones.Contains(draft.TimeZone, StringComparer.Ordinal)
                ? ZoneDraftKey : EmailDraftKey;
            try { await JS.InvokeVoidAsync("clayNumPool.focus", Lifetime, DraftRootSelector, key); }
            catch (JSException) { }
            return;
        }

        var employeeId = CurrentUser!.Id;
        BeelineContract? saved = null;
        if (!await ExecuteWriteAsync("beeline.update", async principal =>
            saved = await BeelineData.SaveAsync(Db, principal, employeeId, original, draft, _zones, Lifetime)))
            return;

        if (saved is null)
        {
            Message = "Запись подтверждена. Не удалось перечитать договор. Нажмите «Обновить».";
            return;
        }

        _contract = saved;
        AcceptCommitted(draft, DraftFrom(saved));
        await AfterDraftCleanupAsync(async () =>
        {
            Message = "Параметры договора сохранены.";
            if (close && !Dirty) await CloseAsync();
        });
    }

    private async Task OpenSimAsync()
    {
        if (_openingSim || Busy || Loading || _contract is not { MobileBudget: true } original) return;
        if (original.EquipmentId is not > 0)
        {
            Message = NoEquipment;
            return;
        }

        var revision = ++_simRevision;
        var route = RouteKey;
        var ct = Lifetime;
        _openingSim = true;
        try
        {
            var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), ct);
            if (revision != _simRevision) return;
            if (actor is null || actor.Id != CurrentUser?.Id)
            {
                Message = "Нет доступа к переходу из договора.";
                return;
            }
            var fresh = await BeelineData.ReadAsync(Db, actor, original.Number, ct);
            if (revision != _simRevision || !ReferenceEquals(_contract, original) || RouteKey != route) return;
            if (fresh.Contract is not { MobileBudget: true, EquipmentId: > 0 } current)
            {
                Message = NoEquipment;
                return;
            }

            if (current.BudgetPhone != original.BudgetPhone || current.EquipmentId != original.EquipmentId)
            {
                Message = "Связь с оборудованием изменилась. Обновите карточку договора.";
                return;
            }

            var id = original.EquipmentId.Value;
            if (!await Windows.OpenAsync(Navigation.ToAbsoluteUri($"/Equipment/EquipmentForm?id={id}").AbsoluteUri,
                    $"beeline-sim-{id}", ct) && revision == _simRevision)
                Message = "Браузер не открыл отдельное окно оборудования.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (revision == _simRevision) Message = "Не удалось открыть оборудование мобильного бюджета.";
        }
        finally { if (revision == _simRevision) _openingSim = false; }
    }

    private void UpdateOptions() => _emailOptions.ReadOnly = _zoneOptions.ReadOnly = !Editable;
    private static BeelineDraft DraftFrom(BeelineContract row) => new(row.TimeZone, row.EmailManager);
    private static string Identity(string value) => value;
    private string? AuthorUrl(int id) => id > 0
        ? InventoryMenuUrlBuilder.BuildCardLink("@URI_user_form", id.ToString(CultureInfo.InvariantCulture), Config, Navigation.Uri).Href
        : null;
}
