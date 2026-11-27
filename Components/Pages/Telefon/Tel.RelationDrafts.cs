using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon;

public partial class Tel
{
    private const string NewTelParallelScope = "tel-parallel-new";
    private bool _telChildOpen;
    private ClayDraftRestore? _telChildCacheRestore;
    private readonly Dictionary<string, string?> _telChildCache = new(StringComparer.Ordinal);

    private static string TelParallelScope(PhoneParallelCall? value) => value is null ? NewTelParallelScope :
        "tel-parallel-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.CallNumber)));

    private static string TelEquipmentScope(PhoneEquipmentKey key) =>
        FormattableString.Invariant($"tel-equipment-{key.EquipmentId}-{key.ConnectionEquipmentId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");

    private async Task<TelRelationDraftSession<T>?> OpenTelRelationDraftAsync<T>(string scope, T original, DateTime originalChangedAtUtc,
        Func<T, PhoneRelationsSnapshot?> propose, Func<PhoneRelationsSnapshot?, (T Value, DateTime ChangedAtUtc)?> projectFresh,
        bool isNew) where T : class
    {
        if (_telChildOpen || !_loaded || !_canEdit || _saving || TelWriteBlocked ||
            CurrentUser is not { Id: > 0 } employee || _telJournal is not { } registration)
            return null;
        _telChildOpen = true;
        var version = _operationVersion;
        var route = (Id, CopyId);
        bool Current() => _telChildOpen && IsTelInputCurrent(registration, version) &&
            (Id, CopyId) == route && CurrentUser?.Id == employee.Id && _canEdit && !TelWriteBlockedOutsideChild;

        async Task<(T Value, DateTime ChangedAtUtc)?> ReadFresh()
        {
            if (!Current() || HasPendingSync) return null;
            var principal = await ReadPrincipalAsync();
            var actor = await Service.GetCurrentUserAsync(principal, _pageCts.Token);
            if (!Current() || actor?.Id != employee.Id || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
                return null;
            if (await Service.ReadSqlPendingAsync(principal, actor.Id, route.Id, _pageCts.Token) || !Current())
                return null;
            if (route.Id > 0)
            {
                var fresh = await Service.GetAsync(route.Id, currentUser: actor);
                return Current() && fresh?.Phone.Id == route.Id ? projectFresh(fresh.Relations) : null;
            }
            if (route.CopyId > 0)
            {
                var source = await Service.GetAsync(route.CopyId, currentUser: actor);
                if (!Current() || source?.Phone.Id != route.CopyId) return null;
                // The copy flow inherits parallel calls, but starts with no equipment links.
                return projectFresh(new PhoneRelationsSnapshot { ParallelCalls = source.Relations.ParallelCalls });
            }
            return Current() ? projectFresh(null) : null;
        }
        try
        {
            if (!ReferenceEquals(_telChildCacheRestore, registration.Restore))
            {
                _telChildCache.Clear();
                _telChildCacheRestore = registration.Restore;
            }
            if (!_telChildCache.TryGetValue(scope, out var stored))
                registration.Restore.TypedScopes?.TryGetValue(scope, out stored);
            await registration.OpenScopeAsync(scope, _pageCts.Token);
            var state = await registration.ReadEditorScopeAsync(scope, _pageCts.Token);
            if (!Current() || state.HasOperation)
            {
                _telChildOpen = false;
                return null;
            }
            async Task<long> Accept(T value)
            {
                var relations = Current() ? propose(value) : null;
                if (relations is null)
                    throw new InvalidOperationException("Связь изменена в основной форме.");
                var baseline = CurrentTelInputFingerprint();
                var revision = await PersistTelInputScopeAsync(registration, SerializeTelInput(relations));
                if (!Current() || baseline != CurrentTelInputFingerprint())
                    throw new InvalidOperationException("Ввод основной формы изменился во время принятия связи.");
                ApplyAcceptedTelRelation(relations);
                return revision;
            }
            var session = new TelRelationDraftSession<T>(registration, scope, _telJournalRoot, _pageCts.Token, Current,
                json => _telChildCache[scope] = json, Accept, stored, state.HasRaw, original, originalChangedAtUtc,
                DialogService, ReadFresh, isNew);
            await session.InitializeAsync();
            if (Current())
                return session;
        }
        catch (Exception ex) when (IsTelJournalError(ex) || ex is JsonException)
        {
            if (Current())
                _validationMessage = "Не удалось открыть черновик связи.";
        }
        _telChildOpen = false;
        return null;
    }

    private void ApplyAcceptedTelRelation(PhoneRelationsSnapshot relations)
    {
        _relations = relations;
        UpdateDependentState();
    }

    private static bool SameParallelValues(PhoneParallelCall left, PhoneParallelCall right) =>
        left.CallNumber == right.CallNumber && left.DelayBeforeCall == right.DelayBeforeCall &&
        left.DelayTooQuickResponse == right.DelayTooQuickResponse && left.NotUsed == right.NotUsed;

    private static PhoneParallelCall TelParallelValues(PhoneParallelCall value) => new()
    {
        CallNumber = value.CallNumber,
        DelayBeforeCall = value.DelayBeforeCall,
        DelayTooQuickResponse = value.DelayTooQuickResponse,
        NotUsed = value.NotUsed,
    };
}
