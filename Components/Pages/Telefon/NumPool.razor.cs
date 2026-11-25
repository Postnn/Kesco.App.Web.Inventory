using System.Globalization;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Phones;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon;

/// <summary>Карточка одного номера ТфОП и четырёх наборов его использования.</summary>
public partial class NumPool
{
    public sealed record FormState(NumPoolSnapshot Data, bool EmployeePrivate);

    private static readonly ClayToolbarButtonOptions SaveButton = new() { Text = "Сохранить", Icon = Icons.Material.Filled.Save };
    private readonly ClayTextBoxOptions _number = new() { Label = "Номер ТфОП", Required = true, MaxLength = 20, DraftKey = "numpool.number" };
    private readonly ClaySelectOptions _capacity = new() { Label = "Номерная ёмкость", Required = true, AutoSelectSingle = true, MaxItems = 100, DraftKey = "numpool.capacity" };
    private readonly ClayTextBoxOptions _incomingFor = new() { Label = "Входящий для", MaxLength = 300, DraftKey = "numpool.for" };
    private readonly ClayTextBoxOptions _incomingTo = new() { Label = "Входящий на", MaxLength = 50, DraftKey = "numpool.to" };
    private readonly ClayNumberOptions _control = new() { Label = "Контроль", Required = true, Min = 0, Max = 255, DraftKey = "numpool.control" };
    private readonly ClayTextAreaOptions _notes = new() { Label = "Примечания", Lines = 2, MaxLength = 300, DraftKey = "numpool.notes" };
    private NumPoolSelectDataSource _capacities = null!;
    private NumPoolSnapshot? _saved;
    private NumPoolAccess _access = new(false, false, false, false);
    private bool _focusPending;
    private bool _deleted;

    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;
    [Inject] private IConfiguration Configuration { get; set; } = null!;

    protected override string FormKey => "num-pool";
    protected override string RouteKey => Id is null ? "new" : "id:" + Id;
    protected override string EntityKey => RouteKey;
    protected override bool Editable => !_deleted && (Id is null && _saved is null ? _access.CanInsert : _saved is not null && _access.CanUpdate);
    protected override bool CanRegisterDraft => Editable || CanDelete;
    private bool CanDelete => !_deleted && _saved is not null && _access.CanDelete;
    private bool CanSave => Editable && CanCommit && Draft is { } draft && Clean is { } clean && !NumPoolData.SameValues(draft.Data, clean.Data);
    private string CardTitle => $"Номер ТфОП: {_saved?.Header.Number ?? Id}";

    protected override void OnInitialized() => _capacities = new(Db, NumPoolKind.Capacity);
    protected override Task LoadAccessAsync(Employee user) => SetAccessAsync(user);
    private async Task SetAccessAsync(Employee user) => _access = await NumPoolData.ReadAccessAsync(Db, user, Lifetime);
    protected override bool IsDomainRejection(Exception exception) => exception is NumPoolConflict;
    protected override bool StatesEqual(FormState? left, FormState? right) => left is null || right is null
        ? left == right : left.EmployeePrivate == right.EmployeePrivate && NumPoolData.SameVersion(left.Data, right.Data);

    protected override async Task<FormState?> LoadCardAsync()
    {
        _saved = null;
        _deleted = false;
        if (!_access.CanRead) { Message = "Нет доступа к справочнику номеров ТфОП."; return null; }
        if (Id is { } number)
        {
            if (!NumPoolData.ValidNumber(number)) { Message = "Некорректный номер ТфОП в ссылке."; return null; }
            _saved = await NumPoolData.ReadAsync(Db, number, Lifetime);
            if (_saved is null) { Message = "Номер ТфОП не найден или недоступен."; return null; }
        }
        UpdateOptions();
        _focusPending = Editable;
        return new(_saved ?? NumPoolSnapshot.Empty, false);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var focus = _focusPending && !Loading;
        await base.OnAfterRenderAsync(firstRender);
        if (!focus || Draft is null) return;
        _focusPending = false;
        await JS.InvokeVoidAsync("clayNumPool.focus", Lifetime, DraftRootSelector, _saved is null ? _number.DraftKey : _incomingFor.DraftKey);
    }

    private void UpdateOptions()
    {
        _number.ReadOnly = _capacity.ReadOnly = _saved is not null || !Editable;
        _incomingFor.ReadOnly = _incomingTo.ReadOnly = _control.ReadOnly = _notes.ReadOnly = !Editable;
    }

    private Task ChangeHeaderAsync(NumPoolHeader header) => Draft is { } draft
        ? UpdateDraftAsync(draft with { Data = draft.Data with { Header = header } }) : Task.CompletedTask;

    private Task ChangePrivateAsync(bool value) => Draft is { } draft
        ? UpdateDraftAsync(draft with { EmployeePrivate = value }) : Task.CompletedTask;

    private Task AddLinkAsync(NumPoolKind kind, NumPoolOption option)
    {
        if (Draft is not { } draft || CurrentUser is not { } user || draft.Data.Links(kind).Any(link => link.Id == option.Id))
            return Task.CompletedTask;
        var privateValue = kind == NumPoolKind.Employee && draft.EmployeePrivate ? (byte)1 : (byte)0;
        var link = new NumPoolLink(option.Id, option.Name, privateValue, user.Id, user.Name, DateTime.UtcNow);
        var links = draft.Data.Links(kind).Append(link).OrderBy(item => item.Name, StringComparer.CurrentCulture).ThenBy(item => item.Id).ToArray();
        return UpdateDraftAsync(draft with { Data = draft.Data.SetLinks(kind, links), EmployeePrivate = kind != NumPoolKind.Employee && draft.EmployeePrivate });
    }

    private Task RemoveLinkAsync(NumPoolKind kind, int id) => Draft is { } draft
        ? UpdateDraftAsync(draft with { Data = draft.Data.SetLinks(kind, draft.Data.Links(kind).Where(link => link.Id != id).ToArray()) })
        : Task.CompletedTask;

    private async Task SaveAsync(bool close)
    {
        if (!CanSave || Draft is not { } draft) return;
        Validation = NumPoolData.Validate(draft.Data.Header);
        if (Validation.Count != 0) return;
        if (!await ExecuteWriteAsync("numpool.save", async principal =>
            _saved = await NumPoolData.SaveAsync(Db, principal, _saved, draft.Data, Lifetime))) return;

        Accept(new(_saved!, false));
        UpdateOptions();
        await RefreshOpenerAsync();
        if (close) await FinishAsync();
        else if (Id != _saved!.Header.Number && !StorageBlocked)
            Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("id", _saved.Header.Number), replace: true);
    }

    private async Task DeleteAsync()
    {
        if (!CanCommit || !CanDelete || _saved is not { } original ||
            !await ConfirmAsync("Удаление", $"Удалить номер ТфОП: {original.Header.Number}?")) return;
        if (!await ExecuteWriteAsync("numpool.delete", principal => NumPoolData.DeleteAsync(Db, principal, original, Lifetime))) return;
        _deleted = true;
        _saved = null;
        Accept(new(NumPoolSnapshot.Empty, false));
        UpdateOptions();
        Message = "Номер ТфОП удалён.";
        await RefreshOpenerAsync();
        await FinishAsync();
    }

    private async Task FinishAsync()
    {
        await JS.InvokeVoidAsync("clayNumPool.setSuccess", Lifetime);
        await CloseAsync();
    }

    private string? EmployeeUrl(int id) => ConfigUrl("@URI_user_form", id);
    private string? CustomerUrl(int id) => ConfigUrl("@URI_person_form", id);
    private string PhoneUrl(int id) => Navigation.ToAbsoluteUri($"Telefon/Tel?id={id}").AbsoluteUri;
    private string? ConfigUrl(string key, int id) => InventoryMenuUrlBuilder.BuildCardLink(key,
        id.ToString(CultureInfo.InvariantCulture), Configuration, Navigation.Uri).Href;

    private async Task OpenLinkAsync(string url)
    {
        if (!await Windows.OpenAsync(url, "numpool-link:" + url, Lifetime)) Message = "Браузер заблокировал открытие карточки.";
    }
}
