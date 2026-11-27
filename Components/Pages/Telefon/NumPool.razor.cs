using Clayzor.Lib.Web.Controls.Components.Presence;
using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Phones;
using Kesco.App.Web.Inventory.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
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
    private readonly ClaySelectOptions _capacity = new() { Label = "Номерная ёмкость", Required = true, AutoSelectSingle = true, MaxItems = 100, DraftKey = "numpool.capacity", AdvanceFocusOnSelection = true };
    private readonly ClayTextBoxOptions _incomingFor = new() { Label = "Входящий для", MaxLength = 300, DraftKey = "numpool.for" };
    private readonly ClayTextBoxOptions _incomingTo = new() { Label = "Входящий на", MaxLength = 50, DraftKey = "numpool.to" };
    private readonly ClayNumberOptions _control = new() { Label = "Контроль", Required = true, Min = 0, Max = 255, DraftKey = "numpool.control" };
    private readonly ClayTextAreaOptions _notes = new() { Label = "Примечания", Lines = 2, MaxLength = 300, DraftKey = "numpool.notes" };
    private NumPoolSelectDataSource _capacities = null!;
    private NumPoolSnapshot? _saved;
    private NumPoolAccess _access = new(false, false, false, false);
    private bool _deleted;

    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    [Inject] private ClayFormWindowService Windows { get; set; } = null!;
    [Inject] private IConfiguration Configuration { get; set; } = null!;

    private (int Generation, int EmployeeId, string Key, bool Editing)? _presenceContext;
    private ClayPresenceTarget? _presenceTarget;

    private ClayPresenceTarget? GetPresenceTarget()
    {
        if (Loading || _deleted || Id is null || _saved?.Header.Number is not { Length: > 0 } key ||
            !_access.CanRead || CurrentUser is not { Id: > 0 } actor)
            return null;
        var context = (Generation: EditorGeneration, EmployeeId: actor.Id, Key: key, Editing: Editable);
        if (_presenceContext != context)
        {
            _presenceContext = context;
            var lifetime = Lifetime;
            _presenceTarget = PhoneNumberCardPresence.NumPool(Db, context.EmployeeId, context.Key, context.Editing,
                () => !lifetime.IsCancellationRequested && !Loading && !_deleted && Id is not null &&
                    EditorGeneration == context.Generation && CurrentUser?.Id == context.EmployeeId &&
                    _access.CanRead && _saved?.Header.Number == context.Key && Editable == context.Editing);
        }
        return _presenceTarget;
    }

    protected override string FormKey => "num-pool";
    protected override string RouteKey => Id is null ? "new" : "id:" + Id;
    protected override string EntityKey => RouteKey;
    protected override bool Editable => !_deleted && (Id is null && _saved is null ? _access.CanInsert : _saved is not null && _access.CanUpdate);
    protected override bool CanRegisterDraft => Editable || CanDelete ||
        !_deleted && _saved is null && CanReadMissingDraft && (_access.CanUpdate || _access.CanDelete);
    protected override bool UsesRamDraft => true;
    protected override bool SupportsEditorCancellation => CanRegisterDraft;

    protected override IReadOnlyList<DraftMergeField> MergeFields =>
    [
        .. (Id is null ? new DraftMergeField[]
        {
            new("Data.Header.Number", "Номер ТфОП", "numpool.number"),
            new("Data.Header.CapacityId", "Номерная ёмкость", "numpool.capacity"),
        } : []),
        new("Data.Header.IncomingFor", "Входящий для", "numpool.for"),
        new("Data.Header.IncomingTo", "Входящий на", "numpool.to"),
        new("Data.Header.Control", "Контроль", "numpool.control"),
        new("Data.Header.Notes", "Примечания", "numpool.notes"),
        new("Data.Connections", "Подключения телефонии"),
        new("Data.Customers", "Лица заказчики"),
        new("Data.Employees", "Сотрудники"),
        new("Data.Phones", "Телефонные номера"),
        new("EmployeePrivate", "Признак личного номера при добавлении сотрудника"),
    ];
    protected override bool CanReadMissingDraft => _access.CanRead && NumPoolData.ValidNumber(Id);

    protected override DraftRowVersion? ReadDraftRowVersion() => Version(_saved);

    private static DraftRowVersion? Version(NumPoolSnapshot? snapshot) =>
        snapshot?.Header is { ChangedAt: { } changedAt } header
            ? new("id:" + header.Number, header.ChangedBy, header.ChangedByName, changedAt) : null;

    protected override async Task<FreshEditorState?> ReadFreshEditorAsync(Employee actor, CancellationToken token)
    {
        var generation = EditorGeneration;
        var route = RouteKey;
        var original = _saved;
        var number = original?.Header.Number ?? Id;
        var access = await NumPoolData.ReadAccessAsync(Db, actor, token);
        if (!access.CanRead || !(number is null ? access.CanInsert : access.CanUpdate || access.CanDelete))
            return null;

        var latest = number is null ? NumPoolSnapshot.Empty : await NumPoolData.ReadAsync(Db, number, token);
        if (token.IsCancellationRequested || generation != EditorGeneration || route != RouteKey ||
            CurrentUser?.Id != actor.Id || !ReferenceEquals(original, _saved) || latest is null ||
            original is not null && latest.Header.Number != original.Header.Number)
            return null;

        return new(new FormState(latest, false), Access, Version(number is null ? null : latest), () =>
        {
            _saved = number is null ? null : latest;
            _access = access;
            UpdateOptions();
        });
    }

    protected override async Task<CompareRead> ReadForComparisonAsync(FormState proposed, Employee actor, CancellationToken token)
    {
        var number = Id ?? proposed.Data.Header.Number;
        if (!NumPoolData.ValidNumber(number)) return new(null);
        var access = await NumPoolData.ReadAccessAsync(Db, actor, token);
        if (!access.CanRead) throw new UnauthorizedAccessException();
        token.ThrowIfCancellationRequested();
        var actual = await NumPoolData.ReadAsync(Db, number, token);
        return new(actual is null ? null : new FormState(actual, false));
    }

    protected override bool SameComparisonValues(FormState actual, FormState proposed) =>
        NumPoolData.SameValues(actual.Data, proposed.Data);

    private bool CanDelete => !_deleted && _saved is not null && _access.CanDelete;
    private bool CanSave => Editable && CanCommit && Draft is { } draft && Clean is { } clean &&
        !NumPoolData.SameValues(draft.Data, clean.Data);
    private string CardTitle => $"Номер ТфОП: {_saved?.Header.Number ?? Id}";

    protected override void OnInitialized() => _capacities = new(Db, NumPoolKind.Capacity);
    protected override async Task LoadAccessAsync(Employee user, CancellationToken routeToken)
    {
        var access = await NumPoolData.ReadAccessAsync(Db, user, routeToken);
        routeToken.ThrowIfCancellationRequested();
        _access = access;
    }
    protected override bool IsDomainRejection(Exception exception) => exception is NumPoolConflict;
    protected override bool StatesEqual(FormState? left, FormState? right) => left is null || right is null
        ? left == right : left.EmployeePrivate == right.EmployeePrivate && NumPoolData.SameVersion(left.Data, right.Data);

    protected override async Task<FormState?> LoadCardAsync(CancellationToken routeToken)
    {
        _saved = null;
        _deleted = false;
        if (!_access.CanRead) { Message = "Нет доступа к справочнику номеров ТфОП."; return null; }
        routeToken.ThrowIfCancellationRequested();
        if (Id is { } number)
        {
            if (!NumPoolData.ValidNumber(number)) { Message = "Некорректный номер ТфОП в ссылке."; return null; }
            var saved = await NumPoolData.ReadAsync(Db, number, routeToken);
            routeToken.ThrowIfCancellationRequested();
            _saved = saved;
            if (_saved is null)
            {
                UpdateOptions();
                Message = "Номер ТфОП не найден или недоступен.";
                return null;
            }
        }
        UpdateOptions();
        return new(_saved ?? NumPoolSnapshot.Empty, false);
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
        if (Draft is not { } draft || CurrentUser is not { } user)
            return Task.CompletedTask;
        if (draft.Data.Links(kind).Any(link => link.Id == option.Id))
            return kind == NumPoolKind.Employee && draft.EmployeePrivate ? ChangePrivateAsync(false) : Task.CompletedTask;
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
        if (!CanSave || Draft is not { } draft || CurrentUser is null) return;
        Validation = NumPoolData.Validate(draft.Data.Header);
        if (Validation.Count != 0) return;
        var generation = EditorGeneration;
        var original = _saved;
        NumPoolSnapshot? saved = null;
        if (!await ExecuteWriteAsync("numpool.save", async principal =>
            saved = await NumPoolData.SaveAsync(Db, principal, original, draft.Data, Lifetime)) ||
            generation != EditorGeneration || saved is null) return;

        _saved = saved;
        var accepted = new FormState(saved, false);
        AcceptCommitted(draft, accepted);
        UpdateOptions();
        await AfterDraftCleanupAsync(async () =>
        {
            if (generation != EditorGeneration || await HasLaterNumPoolInputAsync(accepted))
                return;
            await RefreshOpenerAsync();
            if (generation != EditorGeneration || await HasLaterNumPoolInputAsync(accepted))
                return;
            if (close) await FinishAsync();
            else if (Id != _saved!.Header.Number)
                Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("id", _saved.Header.Number), replace: true);
        });
    }

    private async Task DeleteAsync()
    {
        if (!CanCommit || !CanDelete || _saved is not { } original ||
            Draft is not { } submitted ||
            !await ConfirmAsync("Удаление", $"Удалить номер ТфОП: {original.Header.Number}?")) return;
        if (!ReferenceEquals(Draft, submitted) || !ReferenceEquals(_saved, original)) return;
        var generation = EditorGeneration;
        if (!await ExecuteWriteAsync("numpool.delete", principal =>
            NumPoolData.DeleteAsync(Db, principal, original, Lifetime)) || generation != EditorGeneration) return;
        _deleted = true;
        _saved = null;
        var accepted = new FormState(NumPoolSnapshot.Empty, false);
        AcceptCommitted(submitted, accepted);
        UpdateOptions();
        await AfterDraftCleanupAsync(async () =>
        {
            Message = "Номер ТфОП удалён.";
            if (generation != EditorGeneration || await HasLaterNumPoolInputAsync(accepted))
                return;
            await RefreshOpenerAsync();
            if (generation == EditorGeneration && !await HasLaterNumPoolInputAsync(accepted))
                await FinishAsync();
        });
    }

    private Task RefreshNumPoolAsync() => RefreshAsync();

    private async Task<bool> HasLaterNumPoolInputAsync(FormState accepted)
    {
        var generation = EditorGeneration;
        var registration = EditorDraftRegistration;
        try
        {
            var scope = registration is not null
                ? await registration.ReadEditorScopeAsync("main", Lifetime) : null;
            if (generation != EditorGeneration || !ReferenceEquals(registration, EditorDraftRegistration))
                return true;
            if (scope is { HasRaw: false } && StatesEqual(Draft, accepted))
                return false;
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException) { }

        if (generation == EditorGeneration && ReferenceEquals(registration, EditorDraftRegistration))
            Message = "Результат подтверждён. Форма оставлена открытой для проверки более позднего ввода.";
        return true;
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
