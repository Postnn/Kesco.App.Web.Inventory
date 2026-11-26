using System.Globalization;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
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
    private readonly ClayDropDownListOptions _zoneOptions = new() { Label = "Time zone:", Required = true, DraftKey = ZoneDraftKey };
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

    private bool CanSave => !_openingSim && CanCommit && Editable && Dirty;
    private IEnumerable<string> ZoneChoices => Draft?.TimeZone is { Length: > 0 } zone && !_zones.Contains(zone, StringComparer.Ordinal)
        ? _zones.Append(zone) : _zones;

    /// <inheritdoc />
    protected override string FormKey => "beeline-parameters";

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
    protected override DraftRowVersion? ReadDraftRowVersion() => _contract is { } contract
        ? new(contract.Number, contract.ChangedBy, null, contract.ChangedAt) : null;

    /// <inheritdoc />
    protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override bool IsDomainRejection(Exception exception) => exception is BeelineRejectedException;

    /// <inheritdoc />
    protected override async Task<BeelineDraft?> LoadCardAsync(CancellationToken routeToken)
    {
        _contract = null;
        _freshReadFailure = null;
        _canUpdate = false;
        ++_simRevision;
        _openingSim = false;
        _zones = Config.GetSection("BeelineTimeZones").GetChildren()
            .Select(entry => entry.Value).OfType<string>().Where(zone => !string.IsNullOrWhiteSpace(zone))
            .Distinct(StringComparer.Ordinal).OrderByDescending(zone => zone, StringComparer.Ordinal).ToArray();

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
            if (!_canUpdate)
                Message = "Договор открыт для просмотра. Изменение параметров недоступно.";
            return DraftFrom(contract);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SqlException or TimeoutException)
        {
            routeToken.ThrowIfCancellationRequested();
            _freshReadFailure = e switch
            {
                SqlException { Number: 229 } => "Нет доступа к данным договора Билайн.",
                SqlException or TimeoutException => "Не удалось прочитать актуальные параметры договора Билайн.",
                _ => e.Message,
            };
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

        BeelineContract? saved = null;
        if (!await ExecuteWriteAsync("beeline.update", async principal =>
                saved = await BeelineData.SaveAsync(Db, principal, CurrentUser!.Id, original, draft, _zones, Lifetime))) return;

        _contract = saved!;
        Accept(DraftFrom(saved!));
        await AfterDraftCleanupAsync(async () =>
        {
            Message = "Параметры договора сохранены.";
            if (close) await CloseAsync();
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

