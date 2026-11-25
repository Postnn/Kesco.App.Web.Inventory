using System.Globalization;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.PhonePoolAttribute;

/// <summary>Редактор значения по составному ключу телефонного пула.</summary>
public partial class PhonePoolAttributeForm
{
    private const string ValueDraftKey = "pool-attribute.value";
    private static readonly ClayToolbarButtonOptions Ok = new() { Text = "OK", Icon = Icons.Material.Filled.Check, Color = Color.Primary, Variant = Variant.Filled };
    private static readonly ClayToolbarButtonOptions Delete = new()
    {
        Text = "Удалить", Title = "Удалить запись со значением атрибута пула", Icon = Icons.Material.Filled.Delete,
    };
    private static readonly DraftMergeField[] Fields = [new(nameof(FormState.Value), "Значение", ValueDraftKey)];

    private readonly ClayTextBoxOptions _poolOptions = new() { Label = "Пул телефона:", ReadOnly = true };
    private readonly ClayTextBoxOptions _typeOptions = new() { Label = "Тип атрибута:", ReadOnly = true };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { Label = "Описание:", ReadOnly = true };
    private readonly ClayTextBoxOptions _valueOptions = new() { Label = "Значение:", MaxLength = 150, Immediate = true, DraftKey = ValueDraftKey };

    /// <summary>Декодированное ASP.NET значение параметра id.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    [Inject] private IConfiguration Config { get; set; } = null!;

    /// <summary>Единственное редактируемое поле черновика.</summary>
    public sealed record FormState(string Value);

    private PhonePoolAttributeCard? _card;
    private PhonePoolAttributeRights? _rights;
    private bool _deleted;
    private bool _focus;
    private bool _confirmingDelete;

    /// <inheritdoc />
    protected override string FormKey => "phone-pool-attribute";

    /// <inheritdoc />
    protected override string EntityKey => _card?.Key.RouteId ?? RouteKey;

    /// <inheritdoc />
    protected override string RouteKey => PhonePoolAttributeKey.Parse(Id)?.RouteId ?? "invalid:" + Id;

    /// <inheritdoc />
    protected override bool Editable => !_deleted && _card is not null &&
        (_card.Attribute is null ? _rights?.CanInsert == true : _rights?.CanUpdate == true);

    /// <inheritdoc />
    protected override bool CanRegisterDraft => Editable || CanDeleteAttribute;

    /// <inheritdoc />
    protected override IReadOnlyList<DraftMergeField> MergeFields => Fields;

    /// <inheritdoc />
    protected override DraftRowVersion? ReadDraftRowVersion() => _card?.Attribute is { } attribute
        ? new(_card.Key.RouteId, attribute.ChangedBy, null, attribute.ChangedAt) : null;

    /// <inheritdoc />
    protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override bool IsDomainRejection(Exception exception) => exception is PhonePoolAttributeRejectedException;

    private bool CanDeleteAttribute => !_deleted && _card?.Attribute is not null && _rights?.CanDelete == true;
    private bool CanApply => !_confirmingDelete && CanCommit && Editable && (_card?.Attribute is null || Dirty);
    private bool CanCloseOrSave => !Loading && !Busy && !_confirmingDelete && !ConfirmedCleanupPending &&
        Draft is not null && (!Dirty || CanApply);

    /// <inheritdoc />
    protected override async Task<FormState?> LoadCardAsync(CancellationToken routeToken)
    {
        _card = null;
        _rights = null;
        _deleted = false;
        _focus = false;
        if (PhonePoolAttributeKey.Parse(Id) is not { } key)
        {
            Message = "Не удалось установить идентификатор сущности Атрибут пула.";
            return null;
        }

        try
        {
            var result = await PhonePoolAttributeData.ReadAsync(Db, CurrentUser!, key, routeToken);
            routeToken.ThrowIfCancellationRequested();
            _card = result.Card;
            _rights = result.Rights;
            _focus = Editable;
            UpdateOptions();
            return new(_card.Attribute?.Value ?? "");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PhonePoolAttributeRejectedException or SqlException { Number: 229 })
        {
            routeToken.ThrowIfCancellationRequested();
            Message = e is SqlException ? "Нет доступа к данным пары атрибута." : e.Message;
            return null;
        }
    }

    /// <inheritdoc />
    protected override async Task<CompareRead> ReadForComparisonAsync(FormState proposed, Employee actor, CancellationToken token)
    {
        if (_card is null) return new(null);
        var current = await PhonePoolAttributeData.ReadAsync(Db, actor, _card.Key, token);
        return current.Card.Attribute is { } attribute ? new(new FormState(attribute.Value)) : new(null);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var root = DraftRootSelector;
        var focus = _focus && !Loading && !DraftInputSuppressed;
        await base.OnAfterRenderAsync(firstRender);
        if (!focus || root != DraftRootSelector || Loading || !Editable || StorageBlocked) return;
        _focus = false;
        try { await JS.InvokeVoidAsync("clayNumPool.focus", Lifetime, root, ValueDraftKey); }
        catch (JSException) { }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
    }

    private Task ChangeValueAsync(string? value) => _confirmingDelete ? Task.CompletedTask : UpdateDraftAsync(new(value ?? ""));

    private async Task SaveAsync(bool close)
    {
        if (_confirmingDelete || Loading || Busy || ConfirmedCleanupPending || Draft is not { } draft || _card is not { } original)
            return;
        if (close && !Dirty)
        {
            await CloseAsync();
            return;
        }
        if (!CanApply) return;
        if (PhonePoolAttributeKey.ValidateValue(draft.Value) is { } error)
        {
            Validation = [error];
            return;
        }
        Validation = [];
        PhonePoolAttributeCard? saved = null;
        if (!await ExecuteWriteAsync(original.Attribute is null ? "phone-pool-attribute.insert" : "phone-pool-attribute.update",
            async principal => saved = await PhonePoolAttributeData.SaveAsync(Db, principal, CurrentUser!.Id,
                original.Key, original.Attribute, draft.Value, Lifetime))) return;

        _card = saved!;
        Accept(new(saved!.Attribute!.Value));
        UpdateOptions();
        await AfterDraftCleanupAsync(async () =>
        {
            await RefreshOpenerAsync();
            if (close) await CloseAsync();
            else if (original.Attribute is null && Id != saved.Key.RouteId)
                Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("id", saved.Key.RouteId), replace: true);
        });
    }

    private async Task DeleteAsync()
    {
        if (_confirmingDelete || !CanCommit || !CanDeleteAttribute || _card is not { Attribute: { } attribute } original)
            return;
        _confirmingDelete = true;
        try
        {
            var parameters = new DialogParameters<ConfirmDialog>
            {
                { dialog => dialog.Title, "Удаление" },
                { dialog => dialog.CancelText, "Нет" },
                { dialog => dialog.Message, $"Вы уверены, что хотите удалить значение атрибута {original.Key.Type}?" },
            };
            var dialog = await Dialogs.ShowExAsync<ConfirmDialog>("Удаление", parameters,
                new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, CloseOnEscapeKey = true, CloseButton = true });
            if (await dialog.Result is not { Canceled: false, Data: true } || !ReferenceEquals(original, _card) || !CanCommit) return;

            PhonePoolAttributeCard? saved = null;
            if (!await ExecuteWriteAsync("phone-pool-attribute.delete", async principal =>
                saved = await PhonePoolAttributeData.DeleteAsync(Db, principal, CurrentUser!.Id, original.Key, attribute, Lifetime)))
                return;
            _card = saved!;
            _deleted = true;
            Accept(new(""));
            UpdateOptions();
            await AfterDraftCleanupAsync(async () =>
            {
                Message = "Значение атрибута удалено.";
                await CloseAsync();
            });
        }
        finally { _confirmingDelete = false; }
    }

    private Task BeforeLocationAsync(LocationChangingContext context)
    {
        if (!_confirmingDelete) return BeforeNavigationAsync(context);
        context.PreventNavigation();
        return Task.CompletedTask;
    }

    private void UpdateOptions() => _valueOptions.ReadOnly = !Editable;

    private string? AuthorUrl(int id) => id > 0
        ? InventoryMenuUrlBuilder.BuildCardLink("@URI_user_form", id.ToString(CultureInfo.InvariantCulture), Config, Navigation.Uri).Href
        : null;
}

