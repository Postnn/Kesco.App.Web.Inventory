using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Presence;
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
    private static readonly ClayToolbarButtonOptions SubmittedSnapshotButton = new() { Text = "Посмотреть снимок попытки" };
    private static readonly DraftMergeField[] Fields = [new(nameof(FormState.Value), "Значение", ValueDraftKey)];

    private readonly ClayTextBoxOptions _poolOptions = new() { Label = "Пул телефона:", ReadOnly = true };
    private readonly ClayTextBoxOptions _typeOptions = new() { Label = "Тип атрибута:", ReadOnly = true };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { Label = "Описание:", ReadOnly = true };
    private static readonly ClayTextBoxOptions RecoveryValueOptions = new()
    {
        Label = "Восстановленный ввод", ReadOnly = true, DraftKey = ValueDraftKey,
    };
    private readonly ClayTextBoxOptions _valueOptions = new() { Label = "Значение:", MaxLength = 150, Immediate = true, DraftKey = ValueDraftKey };

    /// <summary>Декодированное ASP.NET значение параметра id.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    [Inject] private IConfiguration Config { get; set; } = null!;

    /// <summary>Единственное редактируемое поле черновика.</summary>
    public sealed record FormState(string Value);

    private ClayPresenceTarget? _presenceTarget;
    private PhonePoolAttributeCard? _card;
    private PhonePoolAttributeRights? _rights;
    private bool _deleted;
    private bool _focus;
    private bool _confirmingDelete;
    private string? _freshReadFailure;
    private PhonePoolAttributeCard? _freshRecovery;
    private string? _submittedRecovery;
    private bool _submittedUnavailable;
    private (string Id, long Revision)? _submittedIdentity;
    private bool _retryingRecovery;
    private PhonePoolAttributeKey? RequestedKey => PhonePoolAttributeKey.Parse(Id);

    /// <inheritdoc />
    protected override string FormKey => "phone-pool-attribute";

    /// <inheritdoc />
    protected override string EntityKey => _card?.Key.RouteId ?? RouteKey;

    /// <inheritdoc />
    protected override string RouteKey => RequestedKey?.RouteId ?? "invalid:" + Id;

    /// <inheritdoc />
    protected override bool Editable => !_deleted && _card is not null &&
        (_card.Attribute is null ? _rights?.CanInsert == true : _rights?.CanUpdate == true);

    /// <inheritdoc />
    protected override bool CanRegisterDraft => Editable || CanDeleteAttribute;

    /// <inheritdoc />
    protected override bool SupportsEditorCancellation => Editable;

    /// <inheritdoc />
    protected override bool CanReadMissingDraft => RequestedKey is not null;

    /// <inheritdoc />
    protected override IReadOnlyList<DraftMergeField> MergeFields => Fields;

    /// <inheritdoc />
    protected override DraftRowVersion? ReadDraftRowVersion() => _card?.Attribute is { } attribute
        ? new(_card.Key.RouteId, attribute.ChangedBy, null, attribute.ChangedAt) : null;

    /// <inheritdoc />
    protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override bool IsDomainRejection(Exception exception) => exception is PhonePoolAttributeRejectedException;

    private bool ShowRecovery => !Loading && _card is null && CurrentUser is not null && RequestedKey is not null &&
        (Draft is not null || StorageBlocked);
    private bool CanRetryRecovery => ShowRecovery && !Busy && !_retryingRecovery && !ConfirmedCleanupPending;

    private bool CanDeleteAttribute => !_deleted && _card?.Attribute is not null && _rights?.CanDelete == true;
    private bool CanApply => !_confirmingDelete && CanCommit && Editable && (_card?.Attribute is null || Dirty);
    private bool CanCloseOrSave => !Loading && !Busy && !_confirmingDelete && !ConfirmedCleanupPending &&
        _card is not null && Draft is not null && (!Dirty || CanApply);

    /// <inheritdoc />
    protected override async Task<FormState?> LoadCardAsync(CancellationToken routeToken)
    {
        _presenceTarget = null;
        _card = null;
        _rights = null;
        _deleted = false;
        _focus = false;
        _freshReadFailure = null;
        _freshRecovery = null;
        _submittedRecovery = null;
        _submittedUnavailable = false;
        _retryingRecovery = false;
        if (RequestedKey is not { } key)
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
        catch (Exception e) when (e is UnauthorizedAccessException or PhonePoolAttributeRejectedException or SqlException or TimeoutException)
        {
            routeToken.ThrowIfCancellationRequested();
            _freshReadFailure = DescribeReadFailure(e);
            Message = _freshReadFailure;
            return null;
        }
    }

    /// <inheritdoc />
    protected override async Task<CompareRead> ReadForComparisonAsync(FormState proposed, Employee actor, CancellationToken token)
    {
        if ((_card?.Key ?? RequestedKey) is not { } key) return new(null);
        var current = await PhonePoolAttributeData.ReadAsync(Db, actor, key, token);
        return current.Card.Attribute is { } attribute ? new(new FormState(attribute.Value)) : new(null);
    }

    /// <inheritdoc />
    protected override async Task<FreshEditorState?> ReadFreshEditorAsync(Employee actor, CancellationToken token)
    {
        if (!Editable || _card is not { } opened || RequestedKey != opened.Key)
            return null;

        var generation = EditorGeneration;
        try
        {
            var fresh = await PhonePoolAttributeData.ReadAsync(Db, actor, opened.Key, token);
            if (token.IsCancellationRequested || generation != EditorGeneration || RequestedKey != opened.Key ||
                !ReferenceEquals(_card, opened) || fresh.Card.Key != opened.Key || !fresh.Rights.CanRead)
                return null;

            var attribute = fresh.Card.Attribute;
            if (opened.Attribute is null ? attribute is not null || !fresh.Rights.CanInsert
                : attribute is null || !fresh.Rights.CanUpdate)
                return null;

            var version = attribute is null ? null : new DraftRowVersion(opened.Key.RouteId, attribute.ChangedBy, null, attribute.ChangedAt);
            return new FreshEditorState(new FormState(attribute?.Value ?? ""), Access, version, () =>
            {
                _card = fresh.Card;
                _rights = fresh.Rights;
                _focus = true;
                UpdateOptions();
            });
        }
        catch (Exception error) when (error is UnauthorizedAccessException or PhonePoolAttributeRejectedException or SqlException or TimeoutException)
        {
            return null;
        }
    }

    private Task CancelAttributeAsync() =>
        Loading || Busy || _confirmingDelete || _retryingRecovery ? Task.CompletedTask
            : Editable || ConfirmedCleanupPending ? CancelAsync() : CloseAsync();

    private async Task ShowSubmittedRecoveryAsync()
    {
        if (!ShowRecovery || Busy) return;
        var generation = EditorGeneration;
        var key = RouteKey;
        var identity = SubmittedSnapshotIdentity;
        _submittedRecovery = null;
        _submittedUnavailable = false;
        var snapshot = await ReadOwnSubmittedSnapshotAsync<string>((kind, value) =>
            kind switch { "phone-pool-attribute.update" => "Изменение значения", "phone-pool-attribute.delete" => "Удаление значения", _ => null });
        if (generation != EditorGeneration || RouteKey != key || !ShowRecovery) return;
        _submittedIdentity = identity;
        _submittedRecovery = snapshot;
        _submittedUnavailable = snapshot is null;
    }

    private Task RefreshAttributeAsync() => CanRefresh ? RefreshAsync() : RetryRecoveryReadAsync();

    private async Task RetryRecoveryReadAsync()
    {
        if (!CanRetryRecovery || RequestedKey is not { } key || CurrentUser is not { } openedBy)
            return;

        var generation = EditorGeneration;
        var token = Lifetime;
        _retryingRecovery = true;
        _freshRecovery = null;
        _freshReadFailure = null;
        try
        {
            var principal = await ReadPrincipalAsync();
            if (!IsCurrentRecovery()) return;
            var actor = await Employee.GetCurrentWithRolesAsync(Db, principal, token);
            if (!IsCurrentRecovery()) return;
            if (actor is null || actor.Id != openedBy.Id)
            {
                _freshReadFailure = "Не удалось подтвердить сотрудника для чтения атрибута.";
                return;
            }

            var fresh = await PhonePoolAttributeData.ReadAsync(Db, actor, key, token);
            if (!IsCurrentRecovery()) return;
            if (fresh.Card.Key == key)
                _freshRecovery = fresh.Card;
            else
                _freshReadFailure = "Не удалось подтвердить ключ прочитанной пары атрибута.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is UnauthorizedAccessException or PhonePoolAttributeRejectedException or SqlException or TimeoutException)
        {
            if (IsCurrentRecovery())
                _freshReadFailure = DescribeReadFailure(e);
        }
        finally
        {
            if (generation == EditorGeneration)
                _retryingRecovery = false;
        }

        bool IsCurrentRecovery() => !token.IsCancellationRequested && generation == EditorGeneration && RequestedKey == key;
    }

    private static string DescribeReadFailure(Exception error) => error switch
    {
        SqlException { Number: 229 } => "Нет доступа к данным пары атрибута.",
        SqlException or TimeoutException => "Не удалось прочитать актуальные данные пары атрибута.",
        _ => error.Message,
    };

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

    private void UpdateOptions()
    {
        _valueOptions.ReadOnly = !Editable;
        _presenceTarget = CreatePresenceTarget();
    }

    private ClayPresenceTarget? CreatePresenceTarget()
    {
        if (_deleted || _card is not { Attribute: not null, Key.IsValid: true } card ||
            _rights?.CanRead != true || CurrentUser is not { Id: > 0 } employee)
            return null;

        var connectionString = Db.ConnectionString;
        SqlConnectionStringBuilder connection;
        try { connection = new SqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException) { return null; }
        if (string.IsNullOrWhiteSpace(connection.DataSource) || string.IsNullOrWhiteSpace(connection.InitialCatalog))
            return null;

        var scope = JsonSerializer.Serialize(new[]
        {
            connection.DataSource.ToUpperInvariant(),
            connection.InitialCatalog
        });
        var entityKey = card.Key;
        var key = ClayPresenceKey.Create(scope, "inventory.phone-pool-attribute", entityKey.PoolId, entityKey.Type);
        if (_presenceTarget?.Key == key)
            return _presenceTarget;

        var expectedEmployeeId = employee.Id;
        return new(key, async (principal, token) =>
        {
            // Независимое чтение presence не включает UI-обработчик ошибок бизнес-операций.
            using var db = new DbManager(connectionString);
            var actor = await Employee.GetCurrentWithRolesAsync(db, principal.Identity?.Name, token);
            if (actor is null || actor.Id != expectedEmployeeId)
                return null;

            var current = await PhonePoolAttributeData.ReadAsync(db, actor, entityKey, token);
            if (!current.Rights.CanRead || current.Card.Key != entityKey || current.Card.Attribute is null)
                return null;

            return new(actor.Id, actor.Name, current.Rights.CanUpdate
                ? ClayPresenceActivity.Editing
                : ClayPresenceActivity.Viewing);
        });
    }

    private string? AuthorUrl(int id) => id > 0
        ? InventoryMenuUrlBuilder.BuildCardLink("@URI_user_form", id.ToString(CultureInfo.InvariantCulture), Config, Navigation.Uri).Href
        : null;
}

