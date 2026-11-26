using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        Func<T, PhoneRelationsSnapshot?> propose) where T : class
    {
        if (_telChildOpen || !_loaded || !_canEdit || _saving || TelWriteBlocked ||
            _telJournal is not { } registration)
            return null;
        _telChildOpen = true;
        var version = _operationVersion;
        bool Current() => _telChildOpen && IsTelInputCurrent(registration, version);
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
                json => _telChildCache[scope] = json, Accept, stored, state.HasRaw, original, originalChangedAtUtc);
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
