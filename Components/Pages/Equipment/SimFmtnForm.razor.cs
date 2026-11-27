using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Карточка строкового префикса FMTN.</summary>
public partial class SimFmtnForm
{
    private readonly ClayTextBoxOptions _prefix = new() { Label = "Префикс FMTN", Required = true, MaxLength = 11, DraftKey = "fmt.prefix" };
    private readonly ClayTextBoxOptions _shortNumber = new() { Label = "Короткий номер", MaxLength = 20, DraftKey = "fmt.short" };
    private readonly ClayTextBoxOptions _description = new() { Label = "Описание", MaxLength = 50, DraftKey = "fmt.description" };
    private SimFmtn? _original;
    private SimFmtnAccess _access = new();
    private bool _deleted;
    private string? _savedKeyForNavigation;
    private bool ChangingIdentity => _savedKeyForNavigation is { } key && Id != key;

    /// <summary>Необязательный исходный ключ из ссылки.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    /// <summary>Legacy PageId родительского окна.</summary>
    [SupplyParameterFromQuery(Name = "parentidp")] public string? ParentPage { get; set; }
    /// <summary>Legacy ItemId родительского окна.</summary>
    [SupplyParameterFromQuery(Name = "parentId")] public string? ParentId { get; set; }
    /// <summary>Legacy ItemName родительского окна.</summary>
    [SupplyParameterFromQuery(Name = "parentName")] public string? ParentName { get; set; }

    private (int Generation, int EmployeeId, string Key, bool Editing)? _presenceContext;
    private ClayPresenceTarget? _presenceTarget;

    private ClayPresenceTarget? GetPresenceTarget()
    {
        if (Loading || _deleted || Id is null || _original?.Prefix is not { Length: > 0 } key ||
            !_access.CanRead || CurrentUser is not { Id: > 0 } actor || ChangingIdentity || Id != key)
            return null;
        var context = (Generation: EditorGeneration, EmployeeId: actor.Id, Key: key, Editing: Editable);
        if (_presenceContext != context)
        {
            _presenceContext = context;
            var lifetime = Lifetime;
            _presenceTarget = PhoneNumberCardPresence.SimFmtn(Db, context.EmployeeId, context.Key, context.Editing,
                () => !lifetime.IsCancellationRequested && !Loading && !_deleted && Id is not null && !ChangingIdentity && Id == context.Key &&
                    EditorGeneration == context.Generation && CurrentUser?.Id == context.EmployeeId &&
                    _access.CanRead && _original?.Prefix == context.Key && Editable == context.Editing);
        }
        return _presenceTarget;
    }

    protected override string FormKey => "sim-fmtn";
    protected override string EntityKey => Id ?? "new";
    protected override string RouteKey => EntityKey;
    protected override bool Editable => !_deleted && (Id is null ? _access.CanInsert : _original is not null && _access.CanUpdate);
    private bool CanDelete => _original?.Fields.FitsWritePolicy == true && _access.CanDelete;
    protected override bool CanRegisterDraft => Editable || CanDelete;
    protected override bool UsesRamDraft => true;
    protected override bool SupportsEditorCancellation => true;
    protected override DraftRowVersion? ReadDraftRowVersion() => _original is { } row
        ? new(row.Prefix, row.ChangedBy, row.ChangedByName, row.ChangedAt) : null;

    protected override IReadOnlyList<DraftMergeField> MergeFields =>
    [
        new("Prefix", "Префикс FMTN", "fmt.prefix"),
        new("ShortNumber", "Короткий номер", "fmt.short"),
        new("Description", "Описание", "fmt.description"),
    ];
    protected override bool CanReadMissingDraft => _access.CanRead && (_access.CanUpdate || _access.CanDelete) &&
        Id is { Length: > 0 and <= 20 } key && key.All(c => char.IsAsciiDigit(c) || c is '(' or ')' or '_');

    protected override async Task<CompareRead> ReadForComparisonAsync(SimFmtnFields proposed, Employee actor, CancellationToken token)
    {
        var access = await SimFmtnData.ReadAccessAsync(Db, actor, token);
        if (!access.CanRead) throw new UnauthorizedAccessException();
        var target = proposed.Prefix;
        if (target is not { Length: > 0 and <= 20 } ||
            target.Any(c => !char.IsAsciiDigit(c) && c is not ('(' or ')' or '_'))) return new(null);
        var found = await SimFmtnData.ReadAsync(Db, target, token);
        if (Id is not { } original || original == target) return new(found?.Fields);
        token.ThrowIfCancellationRequested();
        var source = await SimFmtnData.ReadAsync(Db, original, token);
        return new(found?.Fields, source?.Fields, ChangedKey: true);
    }
    protected override bool IsDomainRejection(Exception exception) => exception is SimFmtnConflict;

    protected override async Task LoadAccessAsync(Employee user, CancellationToken routeToken)
    {
        _access = await SimFmtnData.ReadAccessAsync(Db, user, routeToken);
        routeToken.ThrowIfCancellationRequested();
    }

    protected override async Task<FreshEditorState?> ReadFreshEditorAsync(Employee actor, CancellationToken token)
    {
        var key = Id;
        var access = await SimFmtnData.ReadAccessAsync(Db, actor, token);
        if (!access.CanRead || key is null && !access.CanInsert) return null;
        var original = key is null ? null : await SimFmtnData.ReadAsync(Db, key, token);
        if (key is not null && original is null) return null;

        var fields = original?.Fields ?? new SimFmtnFields("", "", "");
        var version = original is null ? null :
            new DraftRowVersion(original.Prefix, original.ChangedBy, original.ChangedByName, original.ChangedAt);
        return new FreshEditorState(fields, Access, version, () =>
        {
            _original = original;
            _access = access;
            _prefix.ReadOnly = _shortNumber.ReadOnly = _description.ReadOnly = !Editable;
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var root = DraftRootSelector;
        await base.OnAfterRenderAsync(firstRender);
        if (root != DraftRootSelector || Loading) return;
        if (_savedKeyForNavigation is { } target && Id == target && !Loading)
        {
            _savedKeyForNavigation = null;
            if (_original?.Prefix == target && !StorageBlocked && !Dirty) await CloseAsync();
        }
    }

    protected override async Task<SimFmtnFields?> LoadCardAsync(CancellationToken routeToken)
    {
        _original = null;
        _deleted = false;
        if (!_access.CanRead) { Message = "Нет доступа к справочнику FMTN."; return null; }
        if (Id is { } key)
        {
            if (key.Length == 0 || key.Length > 20 || key.Any(c => !char.IsAsciiDigit(c) && c is not ('(' or ')' or '_')))
            {
                Message = "Некорректный префикс FMTN в ссылке.";
                return null;
            }
            var original = await SimFmtnData.ReadAsync(Db, key, routeToken);
            routeToken.ThrowIfCancellationRequested();
            _original = original;
            if (_original is null)
            {
                _prefix.ReadOnly = _shortNumber.ReadOnly = _description.ReadOnly = true;
                Message = "Запись FMTN не найдена.";
                return null;
            }
        }
        _prefix.ReadOnly = _shortNumber.ReadOnly = _description.ReadOnly = !Editable;
        return _original?.Fields ?? new("", "", "");
    }

    private async Task SaveAsync()
    {
        if (Busy || Loading || StorageBlocked || ChangingIdentity || _deleted ||
            Draft is not { } fields || CurrentUser is null) return;
        Validation = fields.Validate();
        if (Validation.Count > 0) return;
        if (!Dirty) { await CloseAsync(); return; }
        if (!Editable) return;

        if (!await ExecuteWriteAsync("save", async principal =>
        {
            _original = await SimFmtnData.SaveAsync(Db, principal, _original, fields, Lifetime);
            AcceptCommitted(fields, _original.Fields);
        })) return;

        await AfterDraftCleanupAsync(CompleteSaveAsync);
    }

    private async Task CompleteSaveAsync()
    {
        if (await HasLaterInputAsync())
        {
            Message = "База подтвердила запись. Более поздний ввод сохранён.";
            return;
        }
        if (Id != _original!.Prefix)
        {
            _savedKeyForNavigation = _original.Prefix;
            if (ParentPage is null && ParentId is null && ParentName is null) await RefreshOpenerAsync();
            NavigateAfterConfirmedWrite(Navigation.GetUriWithQueryParameter("id", _original.Prefix));
            return;
        }
        await CompleteAsync();
    }

    private async Task DeleteAsync()
    {
        if (ChangingIdentity || !CanCommit || !CanDelete || _original is not { } original ||
            !await ConfirmAsync("Удаление", "Вы уверены, что хотите удалить информацию о FMTN?")) return;
        var submitted = Draft ?? original.Fields;
        if (!await ExecuteWriteAsync("delete", async principal =>
        {
            await SimFmtnData.DeleteAsync(Db, principal, original, Lifetime);
            AcceptCommitted(submitted, original.Fields);
        })) return;
        _original = null;
        _deleted = true;
        _prefix.ReadOnly = _shortNumber.ReadOnly = _description.ReadOnly = true;
        await AfterDraftCleanupAsync(async () =>
        {
            if (await HasLaterInputAsync())
            {
                Message = "Запись FMTN удалена. Более поздний ввод сохранён.";
                return;
            }
            Message = "Запись FMTN удалена.";
            await CompleteAsync();
        });
    }

    private async Task<bool> HasLaterInputAsync() =>
        Dirty || EditorDraftRegistration is { } registration &&
        (await registration.ReadEditorScopeAsync("main", Lifetime)).HasRaw;

    private async Task CompleteAsync()
    {
        if (ParentPage is null && ParentId is null && ParentName is null) await RefreshOpenerAsync();
        await CloseAsync();
    }
}
