using System.Globalization;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
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
    private readonly ClayTextBoxOptions _valueOptions = new() { Label = "Значение:", MaxLength = 150, Immediate = true, DraftKey = ValueDraftKey };

    /// <summary>Декодированное ASP.NET значение параметра id.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    [Inject] private IConfiguration Config { get; set; } = null!;

    /// <summary>Единственное редактируемое поле черновика.</summary>
    public sealed record FormState(string Value);

    private ClayPresenceTarget? _presenceTarget;
    private int _presenceEmployeeId;
    private PhonePoolAttributeCard? _card;
    private PhonePoolAttributeRights? _rights;
    private bool _deleted;
    private bool _confirmingDelete;
    private string? _submittedRecovery;
    private bool _submittedUnavailable;
    private (string Id, long Revision)? _submittedIdentity;
    private bool _retryingRecovery;
    private bool _writePending;
    private const string PendingWriteMessage = "Есть незавершённая попытка изменения атрибута. Повтор записи заблокирован.";
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
    protected override bool UsesRamDraft => true;

    /// <inheritdoc />
    protected override async Task ImportLegacyEvidenceAsync(Employee actor, string entityKey,
        IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence, CancellationToken token)
    {
        if (evidence.Count == 0) return;
        if (PhonePoolAttributeKey.Parse(entityKey) is not { } key || key.RouteId != entityKey)
            throw new InvalidOperationException("Не удалось подтвердить ключ прежней попытки.");
        var attempts = new List<(Guid AttemptId, string Kind)>(evidence.Count);
        foreach (var entry in evidence)
        {
            if (entry.Data != entityKey || !Guid.TryParse(entry.AttemptId, out var id) || id == Guid.Empty)
                throw new InvalidOperationException("Прежняя попытка относится к другому атрибуту.");
            attempts.Add((id, entry.Kind));
        }

        await PhonePoolAttributeData.ImportLegacyOperationsAsync(Db, await ReadPrincipalAsync(), actor.Id, key, attempts, token);
        var fresh = await PhonePoolAttributeData.ReadEditorAsync(Db, actor, key, token);
        token.ThrowIfCancellationRequested();
        _writePending = fresh.WritePending;
    }


    /// <inheritdoc />
    protected override bool SupportsEditorCancellation => Editable;

    /// <inheritdoc />
    protected override IReadOnlyList<DraftMergeField> MergeFields => Fields;

    /// <inheritdoc />
    protected override DraftRowVersion? ReadDraftRowVersion() => _card?.Attribute is { } attribute
        ? new(_card.Key.RouteId, attribute.ChangedBy, null, attribute.ChangedAt) : null;

    /// <inheritdoc />
    protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override bool IsDomainRejection(Exception exception) =>
        exception is PhonePoolAttributeRejectedException or InventoryOperationPendingException;

    private bool CanViewSubmittedAttempt => !Loading && _card is not null && HasUnknownSubmittedSnapshot;

    private bool CanDeleteAttribute => !_writePending && !_deleted && _card?.Attribute is not null && _rights?.CanDelete == true;
    private bool CanApply => !_writePending && !_confirmingDelete && !_retryingRecovery && CanCommit && Editable &&
        (_card?.Attribute is null || Dirty);
    private bool CanCloseOrSave => !Loading && !Busy && !_confirmingDelete && !_retryingRecovery && !ConfirmedCleanupPending &&
        _card is not null && Draft is not null && (!Dirty || CanApply);

    /// <inheritdoc />
    protected override async Task<FormState?> LoadCardAsync(CancellationToken routeToken)
    {
        _presenceTarget = null;
        _card = null;
        _rights = null;
        _deleted = false;
        _submittedRecovery = null;
        _submittedUnavailable = false;
        _retryingRecovery = false;
        _writePending = false;
        if (RequestedKey is not { } key)
        {
            Message = "Не удалось установить идентификатор сущности Атрибут пула.";
            return null;
        }

        try
        {
            var result = await PhonePoolAttributeData.ReadEditorAsync(Db, CurrentUser!, key, routeToken);
            routeToken.ThrowIfCancellationRequested();
            _card = result.Card;
            _rights = result.Rights;
            _writePending = result.WritePending;
            UpdateOptions();
            return new(_card.Attribute?.Value ?? "");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PhonePoolAttributeRejectedException or SqlException or TimeoutException)
        {
            routeToken.ThrowIfCancellationRequested();
            Message = DescribeReadFailure(e);
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
    protected override async Task<InventoryOperationReceipt?> ReadVerifiedReceiptAsync(string kind, Guid attemptId,
        FormState? original, FormState proposed, Employee actor, CancellationToken token)
    {
        if (CurrentUser?.Id != actor.Id || RequestedKey is not { } key || attemptId == Guid.Empty)
            return null;

        bool? delete = kind switch
        {
            "phone-pool-attribute.insert" or "phone-pool-attribute.update" => false,
            "phone-pool-attribute.delete" => true,
            _ => null,
        };
        if (delete is not { } deleting) return null;
        return await PhonePoolAttributeData.ReadWriteReceiptAsync(Db, await ReadPrincipalAsync(), actor.Id, key,
            attemptId, deleting, token);
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
        if (!CanViewSubmittedAttempt || Busy || _retryingRecovery ||
            _card is not { } opened || CurrentUser is not { } openedBy) return;

        var generation = EditorGeneration;
        var identity = SubmittedSnapshotIdentity;
        var token = Lifetime;
        _submittedRecovery = null;
        _submittedUnavailable = false;
        _retryingRecovery = true;
        try
        {
            var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), token);
            if (!Current()) return;
            if (actor?.Id != openedBy.Id)
            {
                _submittedUnavailable = true;
                return;
            }

            var (card, rights) = await PhonePoolAttributeData.ReadAsync(Db, actor, opened.Key, token);
            if (!Current()) return;
            if (card.Key != opened.Key || !(rights.CanInsert || rights.CanUpdate || rights.CanDelete))
            {
                _submittedUnavailable = true;
                return;
            }

            var snapshot = await ReadOwnSubmittedSnapshotAsync<string>((kind, _) => kind switch
            {
                "phone-pool-attribute.update" => "Изменение значения",
                "phone-pool-attribute.delete" => "Удаление значения",
                _ => null,
            });
            if (!Current()) return;
            _submittedIdentity = identity;
            _submittedRecovery = snapshot;
            _submittedUnavailable = snapshot is null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or SqlException or TimeoutException)
        {
            if (Current()) _submittedUnavailable = true;
        }
        finally
        {
            if (generation == EditorGeneration) _retryingRecovery = false;
        }

        bool Current() => !token.IsCancellationRequested && generation == EditorGeneration &&
            ReferenceEquals(_card, opened) && identity == SubmittedSnapshotIdentity && CanViewSubmittedAttempt;
    }

    private async Task RefreshAttributeAsync()
    {
        if (!_writePending)
        {
            await RefreshAsync();
            return;
        }
        if (Loading || Busy || _retryingRecovery || _card is not { } opened || CurrentUser is not { } user)
            return;

        var generation = EditorGeneration;
        var token = Lifetime;
        _retryingRecovery = true;
        try
        {
            var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), token);
            if (actor?.Id != user.Id || !Current()) return;
            var fresh = await PhonePoolAttributeData.ReadEditorAsync(Db, actor, opened.Key, token);
            if (!Current()) return;
            _writePending = fresh.WritePending;
            if (!_writePending && CanRefresh)
                await RefreshAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or SqlException or TimeoutException)
        {
            if (Current()) Message = "Не удалось проверить исход попытки изменения атрибута.";
        }
        finally
        {
            if (generation == EditorGeneration) _retryingRecovery = false;
        }

        bool Current() => !token.IsCancellationRequested && generation == EditorGeneration && ReferenceEquals(_card, opened);
    }

    private static string DescribeReadFailure(Exception error) => error switch
    {
        SqlException { Number: 229 } => "Нет доступа к данным пары атрибута.",
        SqlException or TimeoutException => "Не удалось прочитать актуальные данные пары атрибута.",
        _ => error.Message,
    };

    private Task ChangeValueAsync(string? value) => _confirmingDelete ? Task.CompletedTask : UpdateDraftAsync(new(value ?? ""));

    private async Task SaveAsync(bool close)
    {
        if (_confirmingDelete || Loading || Busy || _retryingRecovery || ConfirmedCleanupPending || Draft is not { } draft || _card is not { } original)
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
        var attemptId = Guid.NewGuid();
        if (!await ExecuteWriteAsync(original.Attribute is null ? "phone-pool-attribute.insert" : "phone-pool-attribute.update",
            async principal => saved = await ObserveWriteAsync(PhonePoolAttributeData.SaveAsync(Db, principal, CurrentUser!.Id,
                original.Key, original.Attribute, draft.Value, Lifetime, attemptId)), serverAttemptId: attemptId)) return;

        _card = saved!;
        AcceptCommitted(draft, new(saved!.Attribute!.Value));
        UpdateOptions();
        await AfterDraftCleanupAsync(async () =>
        {
            await RefreshOpenerAsync();
            if (close) await CloseAsync();
            else if (original.Attribute is null && Id != saved.Key.RouteId)
                NavigateAfterConfirmedWrite(Navigation.GetUriWithQueryParameter("id", saved.Key.RouteId));
        });
    }

    private async Task DeleteAsync()
    {
        if (_confirmingDelete || _retryingRecovery || !CanCommit || !CanDeleteAttribute || _card is not { Attribute: { } attribute } original)
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

            var submitted = Draft;
            PhonePoolAttributeCard? saved = null;
            var attemptId = Guid.NewGuid();
            if (!await ExecuteWriteAsync("phone-pool-attribute.delete", async principal =>
                saved = await ObserveWriteAsync(PhonePoolAttributeData.DeleteAsync(Db, principal, CurrentUser!.Id, original.Key,
                    attribute, Lifetime, attemptId)), serverAttemptId: attemptId))
                return;
            _card = saved!;
            _deleted = true;
            if (submitted is null) Accept(new(""));
            else AcceptCommitted(submitted, new(""));
            UpdateOptions();
            await AfterDraftCleanupAsync(async () =>
            {
                Message = "Значение атрибута удалено.";
                await CloseAsync();
            });
        }
        finally { _confirmingDelete = false; }
    }

    private async Task<PhonePoolAttributeCard> ObserveWriteAsync(Task<PhonePoolAttributeCard> write)
    {
        var generation = EditorGeneration;
        try { return await write; }
        catch (Exception error) when (error is InventoryOperationPendingException or PhonePoolAttributeUnknownException)
        {
            if (generation == EditorGeneration) _writePending = true;
            throw;
        }
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
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var entityKey = card.Key;
        var key = ClayPresenceKey.Create("inventory", "phone-pool-attribute", entityKey.PoolId, entityKey.Type);
        if (_presenceEmployeeId == employee.Id && _presenceTarget?.Key == key)
            return _presenceTarget;

        var expectedEmployeeId = employee.Id;
        _presenceEmployeeId = expectedEmployeeId;
        ClayPresenceTarget? target = null;
        bool Current() => ReferenceEquals(_presenceTarget, target) && !_deleted && CurrentUser?.Id == expectedEmployeeId &&
            _card is { Attribute: not null } currentCard && currentCard.Key == entityKey && _rights?.CanRead == true;
        target = new(key, async (principal, token) =>
        {
            if (!Current() || principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } login })
                return null;
            // Независимое чтение presence не включает UI-обработчик ошибок бизнес-операций.
            using var db = new DbManager(connectionString);
            var actor = await Employee.GetCurrentWithRolesAsync(db, login, token);
            if (!Current() || actor is null || actor.Id != expectedEmployeeId)
                return null;

            var current = await PhonePoolAttributeData.ReadAsync(db, actor, entityKey, token);
            if (!Current() || !current.Rights.CanRead || current.Card.Key != entityKey || current.Card.Attribute is null)
                return null;

            return new(actor.Id, actor.Name, Editable && current.Rights.CanUpdate
                ? ClayPresenceActivity.Editing
                : ClayPresenceActivity.Viewing);
        });
        return target;
    }

    private string? AuthorUrl(int id) => id > 0
        ? InventoryMenuUrlBuilder.BuildCardLink("@URI_user_form", id.ToString(CultureInfo.InvariantCulture), Config, Navigation.Uri).Href
        : null;
}

