using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Net;

public partial class Net
{
    private const string OperationFormKey = "inventory.net";
    private const string SaveOperationKind = "net.save";
    private const string PoeOperationKind = "net.poe";
    private static readonly ClayToolbarButtonOptions ReadOutcomeOptions = new() { Text = "Прочитать состояние порта" };
    private static readonly ClayToolbarButtonOptions RetryJournalOptions = new() { Text = "Проверить журнал операций" };

    private readonly string _operationRoot = $"net-operation-{Guid.NewGuid():N}";
    private Guid _netDraftInstance = Guid.NewGuid();
    private NetOperationContext? _operationContext;
    private string? _operationNotice;
    private string? _observedOutcome;
    private bool _inspectingOperation;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool OperationBlocked => _inspectingOperation || _pendingNetClose is not null ||
        _operationContext is not
            { Scanned: true, ServerWriteChecked: true, ServerWritePending: false, Attempt: null, NotSentId: null };

    private async Task InitializeOperationContextAsync(LocalNetworkPort port, Employee currentUser, string? principal, int version)
    {
        ClayDraftRegistration? registration = null;
        try
        {
            var entityKey = port.Id.ToString(CultureInfo.InvariantCulture);
            var userKey = currentUser.Id.ToString(CultureInfo.InvariantCulture);
            var token = _pageCts.Token;
            var publishedActor = CurrentUser;
            bool Current() => IsCurrent(version, port.Id) && ReferenceEquals(publishedActor, CurrentUser) && !token.IsCancellationRequested;
            registration = await Drafts.TransferLegacyAndRegisterRamAsync(OperationFormKey, entityKey, userKey,
                async (evidence, ct) =>
                {
                    if (!Current())
                        throw new InvalidOperationException("Контекст сетевого порта изменился.");
                    await LocalNetworkPort.ImportLegacyOperationsAsync(Db, principal, currentUser.Id, port.Id,
                        ParseLegacyNetEvidence(evidence, port.Id), ct);
                    if (!Current())
                        throw new InvalidOperationException("Контекст сетевого порта изменился.");
                    return await Drafts.RegisterRamHandleAsync(OperationFormKey, entityKey, userKey,
                        1, $"[data-net-operation='{_operationRoot}']", _netDraftInstance, ct);
                }, Current, token);
            if (!Current())
                return;

            var context = new NetOperationContext(registration, currentUser.Id, port.Id)
            {
                Attempt = registration.Restore.OperationSnapshot,
            };
            _operationContext = context;
            registration = null;
            await ReadServerWriteStateAsync(context);
            await ScanOperationJournalAsync(context);
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is UnauthorizedAccessException)
        {
            if (IsCurrent(version, port.Id))
                _operationNotice = "Журнал операций недоступен. Сохранение и перезапуск PoE заблокированы.";
        }
        finally
        {
            if (registration is not null)
                await registration.DisposeAsync();
        }
    }

    private async Task ReadServerWriteStateAsync(NetOperationContext context)
    {
        context.ServerWriteChecked = false;
        context.ServerWritePending = await LocalNetworkPort.ReadWritePendingAsync(Db, context.PortId, _pageCts.Token);
        context.ServerWriteChecked = true;
        ShowOperationNotice(context);
    }

    private async Task ScanOperationJournalAsync(NetOperationContext context)
    {
        context.Scanned = false;
        var health = await context.Registration.GetHealthAsync(_pageCts.Token);
        context.Scanned = health is { Healthy: true, PendingWrites: 0 } &&
            (!health.HasOperation || context.Attempt is not null);
        ShowOperationNotice(context);
    }

    private async Task<bool> DispatchOperationAsync(
        LocalNetworkPort model, int version, Employee currentUser, string kind, string data, Func<Guid, Task> dispatch, NetDraft? submitted = null)
    {
        if (OperationBlocked || _operationContext is not { } context ||
            context.EmployeeId != currentUser.Id || context.PortId != model.Id)
        {
            _operationNotice = "Операция не отправлена. Журнал не подтверждён для текущего сотрудника и порта.";
            return false;
        }

        context.InUse = true;
        try
        {
            await ReadServerWriteStateAsync(context);
            await ScanOperationJournalAsync(context);
            if (OperationBlocked || !IsCurrentSave(model, version) || !await VerifyNetInputReadyAsync(context))
                return false;

            var scopes = submitted is null ? Array.Empty<string>() : new[] { NetInputScope };
            long inputRevision = 0;
            if (submitted is not null)
            {
                inputRevision = await PersistNetInputAsync(context, submitted);
                if (inputRevision <= 0 || Snapshot(model) != submitted || !IsCurrentSave(model, version))
                    return false;
                try { await context.Registration.EnsureScopesCanBeAcceptedAsync(scopes, _pageCts.Token); }
                catch (InvalidOperationException)
                {
                    if (!await VerifyNetInputReadyAsync(context)) return false;
                    throw;
                }
                if (Snapshot(model) != submitted || !IsCurrentSave(model, version))
                    return false;
            }

            var serverAttemptId = Guid.NewGuid();
            var attemptId = serverAttemptId.ToString("N");
            ClayDraftOperation attempt;
            try
            {
                await context.Registration.EnsureCommandAllowedAsync(_pageCts.Token);
                if (!await VerifyNetInputReadyAsync(context)) return false;
                attempt = await context.Registration.BeginOperationAsync(
                    new ClayDraftOperationIntent(kind, data, scopes, AttemptId: attemptId,
                        BlockOtherDraftOperations: true), _pageCts.Token);
                if (attempt.AttemptId != attemptId || attempt.Kind != kind || attempt.Data != data ||
                    attempt.Status != ClayDraftOperationStatus.Running)
                    throw new InvalidOperationException("Получено неподходящее подтверждение журнала.");
            }
            catch (Exception ex) when (IsJournalError(ex))
            {
                context.NotSentId = attemptId;
                ShowOperationNotice(context);
                return false;
            }
            context.Attempt = attempt;
            _observedOutcome = null;

            var inputReady = await VerifyNetInputReadyAsync(context);
            var acknowledgedInput = submitted is null
                ? attempt.Scopes.Count == 0
                : attempt.Scopes.Count == 1 && Snapshot(model) == submitted &&
                  attempt.Scopes.Any(scope => scope.Name == NetInputScope && scope.TypedRevision == inputRevision);
            if (!IsCurrentSave(model, version) || !acknowledgedInput || !inputReady ||
                !context.ServerWriteChecked || context.ServerWritePending)
            {
                try
                {
                    await context.Registration.RejectOperationAsync(attempt.Revision);
                    context.Attempt = null;
                    if (inputReady && context.ServerWriteChecked && !context.ServerWritePending && IsCurrentSave(model, version))
                        _validationMessage = "Ревизия ввода изменилась до записи. Проверьте данные и примените ещё раз.";
                }
                catch (Exception ex) when (IsJournalError(ex))
                {
                    context.NotSentId = attemptId;
                }
                ShowOperationNotice(context);
                return false;
            }

            try
            {
                await dispatch(serverAttemptId);
            }
            catch (Exception error) when (error is InventoryOperationPendingException or InventoryOperationReservationException
                or LocalNetworkPortRejectedException || kind == PoeOperationKind && error is UnauthorizedAccessException)
            {
                // Конфликт сохранения может относиться ко второму порту. Не выдаём его за открытый слот текущего.
                context.ServerWritePending = kind == PoeOperationKind && error is InventoryOperationPendingException;
                context.ServerWriteChecked = error is not InventoryOperationReservationException and not InventoryOperationPendingException;
                await context.Registration.RejectOperationAsync(attempt.Revision);
                context.Attempt = null;
                if (IsCurrentSave(model, version))
                    _validationMessage = error.Message;
                ShowOperationNotice(context);
                return false;
            }
            catch (Exception)
            {
                context.Attempt = attempt with { Status = ClayDraftOperationStatus.Unknown };
                try { await context.Registration.UnknownOperationAsync(attempt.Revision); }
                catch (Exception ex) when (IsJournalError(ex)) { /* Durable Running по-прежнему блокирует повтор. */ }
                ShowOperationNotice(context);
                return false;
            }

            context.Attempt = attempt with { Status = ClayDraftOperationStatus.Confirmed };
            context.OutcomeVerified = true;
            await FinishConfirmedOperationAsync(context);
            return true;
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            context.Scanned = false;
            ShowOperationNotice(context);
            return false;
        }
        finally
        {
            await LeaveOperationContextAsync(context);
        }
    }

    private async Task FinishConfirmedOperationAsync(NetOperationContext context)
    {
        if (!context.OutcomeVerified || context.Attempt is not { } attempt)
            return;
        try
        {
            if (!context.ReceiptRecorded)
            {
                await context.Registration.RecordConfirmedOperationAsync(attempt.Revision);
                context.ReceiptRecorded = true;
            }
            await context.Registration.CommitOperationAsync(attempt.Revision);
            if (attempt.Scopes.Any(scope => scope.Name == NetInputScope && scope.TypedRevision == context.LastInputRevision))
            {
                context.LastSavedInput = null;
                context.LastInputRevision = 0;
            }
            if (attempt.Kind == SaveOperationKind)
            {
                context.InputReady = false;
                context.InputRestoreStarted = false;
            }
            context.Attempt = null;
            context.ReceiptRecorded = false;
            context.OutcomeVerified = false;
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            // Бизнес-операция уже подтверждена. Повтор возможен только для журнала.
        }
        ShowOperationNotice(context);
    }

    private bool CanInspectOperation => !_disposed && !_loading && !_saving && !_poeRunning && !_inspectingOperation && _pendingNetClose is null &&
        _model?.Id == Id;

    private async Task CheckOperationJournalAsync()
    {
        if (!CanInspectOperation || _model is not { } model)
            return;
        _inspectingOperation = true;
        try
        {
            if (_operationContext is not { } context)
            {
                if (CurrentUser is { } user)
                {
                    await InitializeOperationContextAsync(model, user, _windowsPrincipal, _loadVersion);
                    if (_operationContext is { } recovered)
                        ApplyRecoveredNetInput(recovered);
                }
                return;
            }
            context.InUse = true;
            try
            {
                if (context.ServerWritePending || !context.ServerWriteChecked)
                {
                    var user = await Employee.GetCurrentAsync(Db, _windowsPrincipal, _pageCts.Token);
                    if (user?.Id != context.EmployeeId ||
                        await LocalNetworkPort.GetAsync(Db, context.PortId, _pageCts.Token) is null ||
                        !ReferenceEquals(context, _operationContext))
                        return;
                    await ReadServerWriteStateAsync(context);
                }
                if (context.NotSentId is { } attemptId)
                {
                    context.Attempt = await context.Registration.RetryStorageAsync(attemptId, _pageCts.Token);
                    context.NotSentId = null;
                }
                if (context.OutcomeVerified)
                    await FinishConfirmedOperationAsync(context);
                else if (context.Attempt is { Status: ClayDraftOperationStatus.Confirmed } restored)
                    await ObserveOperationAsync(context, restored);
                if (IsCurrentInput(context))
                    await ScanOperationJournalAsync(context);
            }
            finally { await LeaveOperationContextAsync(context); }
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (ReferenceEquals(_model, model))
                _operationNotice = "Не удалось проверить журнал. Команды остаются заблокированы.";
        }
        finally { _inspectingOperation = false; }
    }

    private async Task ReadObservedOutcomeAsync()
    {
        if (!CanInspectOperation || _operationContext is not { Attempt: { } attempt, OutcomeVerified: false } context)
            return;
        _inspectingOperation = true;
        context.InUse = true;
        try { await ObserveOperationAsync(context, attempt); }
        finally
        {
            _inspectingOperation = false;
            await LeaveOperationContextAsync(context);
        }
    }

    private async Task ObserveOperationAsync(NetOperationContext context, ClayDraftOperation attempt)
    {
        var version = _loadVersion;
        bool Current() => IsCurrentInput(context) && IsCurrent(version, context.PortId);
        if (!Current() || !ReferenceEquals(context.Attempt, attempt))
            return;
        _observedOutcome = null;
        try
        {
            if (Guid.TryParse(attempt.AttemptId, out var attemptId) && attemptId != Guid.Empty)
            {
                var save = ReadSaveIntent(attempt, context.PortId);
                var receipt = save is not null
                    ? await LocalNetworkPort.ReadSaveReceiptAsync(Db, _windowsPrincipal, context.EmployeeId, context.PortId,
                        save.SwitchToId, save.ExchangeWithId, attemptId, _pageCts.Token)
                    : attempt.Kind == PoeOperationKind && attempt.Scopes.Count == 0 &&
                      attempt.Data == context.PortId.ToString(CultureInfo.InvariantCulture)
                        ? await LocalNetworkPort.ReadPoeReceiptAsync(Db, _windowsPrincipal, context.EmployeeId,
                            context.PortId, attemptId, _pageCts.Token)
                        : null;
                if (!Current() || !ReferenceEquals(context.Attempt, attempt))
                    return;
                if (receipt is { State: InventoryOperationState.Confirmed } &&
                    receipt.AttemptId == attemptId && receipt.ResultEntityId == context.PortId)
                {
                    var submitted = save is null ? null : FindSentNetDraft(context, save.Fingerprint);
                    context.Attempt = attempt with { Status = ClayDraftOperationStatus.Confirmed };
                    context.OutcomeVerified = true;
                    await FinishConfirmedOperationAsync(context);
                    if (!Current())
                        return;
                    if (save is null)
                        _observedOutcome = "Сервер подтвердил успешный ответ на команду PoE.";
                    else if (submitted is not null)
                        AcceptWithoutReload(submitted, "Сохранение подтверждено сервером. Сверьте текущие данные порта.");
                    else
                    {
                        context.InputConflict = true;
                        _observedOutcome = "Сохранение подтверждено сервером. Отправленный снимок недоступен. Проверьте текущий ввод перед новой записью.";
                    }
                    await ReadServerWriteStateAsync(context);
                    return;
                }
                if (save is not null && attempt.Status != ClayDraftOperationStatus.Confirmed &&
                    receipt is { State: InventoryOperationState.Rejected } && receipt.AttemptId == attemptId)
                {
                    await context.Registration.RejectOperationAsync(attempt.Revision);
                    if (!Current())
                        return;
                    context.Attempt = null;
                    await ReadServerWriteStateAsync(context);
                    if (Current())
                        _observedOutcome = "Сервер подтвердил отказ сохранения. Ввод сохранён.";
                    return;
                }
            }
            var user = await Employee.GetCurrentAsync(Db, _windowsPrincipal, _pageCts.Token);
            if (!Current())
                return;
            var port = user?.Id == context.EmployeeId
                ? await LocalNetworkPort.GetAsync(Db, context.PortId, _pageCts.Token)
                : null;
            if (!Current())
                return;

            _observedOutcome = port is null
                ? "Порт сейчас недоступен. Исход попытки не установлен, повтор заблокирован."
                : $"Последнее изменение записи (UTC): {port.ChangedAtUtc:dd.MM.yyyy HH:mm:ss}. " +
                  "Чтение записи не подтверждает исход конкретной команды. Повтор остаётся заблокирован.";
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is UnauthorizedAccessException)
        {
            if (Current())
                _observedOutcome = "Не удалось прочитать состояние порта. Повтор остаётся заблокирован.";
        }
    }

    private void ShowOperationNotice(NetOperationContext context)
    {
        if (!ReferenceEquals(_operationContext, context))
            return;
        _operationNotice = context.NotSentId is not null
            ? "Команда не отправлена. Проверьте состояние журнала."
            : context.OutcomeVerified
                ? "Ответ на команду получен. Повторить можно только завершение журнала."
                : context.Attempt?.Status == ClayDraftOperationStatus.Confirmed
                    ? "Требуется сверить сохранённое подтверждение с сервером. Повтор заблокирован."
                : context.Attempt is { } attempt
                    ? $"Результат команды «{(attempt.Kind == PoeOperationKind ? "Перезапустить PoE" : "Сохранить")}» неизвестен. Повтор заблокирован."
                    : context.ServerWritePending
                            ? "В серверном журнале есть незавершённая операция с портом. Команды заблокированы."
                            : !context.Scanned || !context.ServerWriteChecked
                                ? "Журнал операций не подтверждён. Команды заблокированы."
                                : null;
    }

    private static string DescribeSaveIntent(LocalNetworkPort model, DateTime expectedChangedAt, NetDraft submitted) =>
        JsonSerializer.Serialize(new NetSaveIntent(model.Id, expectedChangedAt, PositiveId(submitted.SwitchToId),
            PositiveId(submitted.ExchangeWithId), NetDraftFingerprint(submitted)));

    private static NetSaveIntent? ReadSaveIntent(ClayDraftOperation attempt, int portId)
    {
        if (attempt.Kind != SaveOperationKind || attempt.Scopes is not [{ Name: NetInputScope }])
            return null;
        try
        {
            var intent = JsonSerializer.Deserialize<NetSaveIntent>(attempt.Data);
            if (intent?.PortId != portId || PositiveId(intent.SwitchToId) == portId || PositiveId(intent.ExchangeWithId) == portId ||
                PositiveId(intent.SwitchToId) is not null && PositiveId(intent.ExchangeWithId) is not null)
                return null;
            return intent with { SwitchToId = PositiveId(intent.SwitchToId), ExchangeWithId = PositiveId(intent.ExchangeWithId) };
        }
        catch (JsonException) { return null; }
    }

    private NetDraft? FindSentNetDraft(NetOperationContext context, string fingerprint)
    {
        var saved = context.LastSavedInput?.Current;
        if (saved is not null && NetDraftFingerprint(saved) == fingerprint)
            return saved;
        var current = _model is { } model ? Snapshot(model) : null;
        return current is not null && NetDraftFingerprint(current) == fingerprint ? current : null;
    }

    private static string NetDraftFingerprint(NetDraft draft) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(draft)));

    private static int? PositiveId(int? value) => value is > 0 ? value : null;

    private static List<(Guid AttemptId, string Kind, int? SwitchToId, int? ExchangeWithId)> ParseLegacyNetEvidence(
        IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence, int portId)
    {
        var attempts = new List<(Guid, string, int?, int?)>(evidence.Count);
        foreach (var item in evidence)
        {
            if (!Guid.TryParseExact(item.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty)
                throw new InvalidOperationException("Не удалось подтвердить идентификатор операции старого журнала.");
            if (item.Kind == PoeOperationKind && item.Data == portId.ToString(CultureInfo.InvariantCulture))
            {
                attempts.Add((attemptId, item.Kind, null, null));
                continue;
            }
            if (item.Kind != SaveOperationKind ||
                JsonSerializer.Deserialize<NetLegacySaveIntent>(item.Data) is not { } intent || intent.PortId != portId ||
                intent.SwitchToId is <= 0 || intent.ExchangeWithId is <= 0 || intent.SwitchToId == portId ||
                intent.ExchangeWithId == portId || intent.SwitchToId is not null && intent.ExchangeWithId is not null)
                throw new InvalidOperationException("Старая операция не соответствует порту или целевому порту.");
            attempts.Add((attemptId, item.Kind, intent.SwitchToId, intent.ExchangeWithId));
        }
        return attempts;
    }

    private sealed record NetLegacySaveIntent(int PortId, int? SwitchToId, int? ExchangeWithId);

    private sealed record NetSaveIntent(int PortId, DateTime ExpectedChangedAtUtc, int? SwitchToId,
        int? ExchangeWithId, string Fingerprint);

    private static bool IsJournalError(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or JsonException or
            SqlException or TimeoutException;

    private static async Task RetireOperationContextAsync(NetOperationContext? context)
    {
        if (context is null)
            return;
        context.Retired = true;
        await context.InputGate.WaitAsync();
        try
        {
            if (!context.InUse)
                await context.Registration.DisposeAsync();
        }
        finally { context.InputGate.Release(); }
    }

    private static async Task LeaveOperationContextAsync(NetOperationContext context)
    {
        context.InUse = false;
        if (context.Retired)
            await context.Registration.DisposeAsync();
    }

    private sealed class NetOperationContext(ClayDraftRegistration registration, int employeeId, int portId)
    {
        internal ClayDraftRegistration Registration { get; } = registration;
        internal int EmployeeId { get; } = employeeId;
        internal int PortId { get; } = portId;
        internal SemaphoreSlim InputGate { get; } = new(1, 1);
        internal NetDraft? InputBaseline { get; set; }
        internal DateTime InputVersion { get; set; }
        internal NetEditorInput? LastSavedInput { get; set; }
        internal long LastInputRevision { get; set; }
        internal bool InputRestoreStarted { get; set; }
        internal bool InputApplying { get; set; }
        internal bool InputReady { get; set; }
        internal bool InputConflict { get; set; }
        internal NetInputReview? InputReview { get; set; }
        internal bool InputFailed { get; set; }
        internal bool VerifiedAfterWrite { get; set; }
        internal ClayDraftOperation? Attempt { get; set; }
        internal string? NotSentId { get; set; }
        internal bool Scanned { get; set; }
        internal bool ServerWriteChecked { get; set; }
        internal bool ServerWritePending { get; set; }
        internal bool InUse { get; set; }
        internal bool Retired { get; set; }
        internal bool OutcomeVerified { get; set; }
        internal bool ReceiptRecorded { get; set; } = registration.Restore.OperationSnapshot?.Status == ClayDraftOperationStatus.Confirmed;
    }
}
