using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Net;

public partial class Net
{
    private const string OperationFormKey = "inventory.net";
    private const string SaveOperationKind = "net.save";
    private const string PoeOperationKind = "net.poe";
    private static readonly ClayToolbarButtonOptions ReadOutcomeOptions = new() { Text = "Прочитать состояние порта" };
    private static readonly ClayToolbarButtonOptions RetryJournalOptions = new() { Text = "Проверить журнал операций" };
    private static readonly ClayToolbarButtonOptions RecoverOperationOptions = new() { Text = "Открыть сохранённую попытку" };

    private readonly string _operationRoot = $"net-operation-{Guid.NewGuid():N}";
    private NetOperationContext? _operationContext;
    private string? _operationNotice;
    private string? _observedOutcome;
    private bool _inspectingOperation;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool OperationBlocked => _inspectingOperation ||
        _operationContext is not { Scanned: true, Attempt: null, NotSentId: null, OtherAttempts.Count: 0 };

    private async Task InitializeOperationContextAsync(LocalNetworkPort port, Employee currentUser, int version)
    {
        ClayDraftRegistration? registration = null;
        try
        {
            registration = await Drafts.RegisterHandleAsync(OperationFormKey,
                port.Id.ToString(CultureInfo.InvariantCulture), currentUser.Id.ToString(CultureInfo.InvariantCulture),
                1, $"[data-net-operation='{_operationRoot}']", _pageCts.Token);
            if (!IsCurrent(version, port.Id))
                return;

            var context = new NetOperationContext(registration, currentUser.Id, port.Id)
            {
                Attempt = registration.Restore.OperationSnapshot,
            };
            _operationContext = context;
            registration = null;
            await ScanOperationJournalAsync(context);
        }
        catch (Exception ex) when (IsJournalError(ex))
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

    private async Task ScanOperationJournalAsync(NetOperationContext context)
    {
        context.Scanned = false;
        var candidates = await context.Registration.ListRecoveryCandidatesAsync(_pageCts.Token)
            ?? throw new InvalidOperationException("Журнал не подтвердил поиск попыток.");
        var health = await context.Registration.GetHealthAsync(_pageCts.Token);
        context.OtherAttempts = candidates.Where(candidate => candidate.HasOperation).ToArray();
        context.Scanned = health is { Healthy: true, PendingWrites: 0 } &&
            (!health.HasOperation || context.Attempt is not null);
        ShowOperationNotice(context);
    }

    private async Task<bool> DispatchOperationAsync(
        LocalNetworkPort model, int version, Employee currentUser, string kind, string data, Func<Task> dispatch)
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
            await ScanOperationJournalAsync(context);
            if (OperationBlocked || !IsCurrentSave(model, version))
                return false;

            var attemptId = Guid.NewGuid().ToString("N");
            ClayDraftOperation attempt;
            try
            {
                await context.Registration.EnsureCommandAllowedAsync(_pageCts.Token);
                attempt = await context.Registration.BeginOperationAsync(
                    new ClayDraftOperationIntent(kind, data, AttemptId: attemptId), _pageCts.Token);
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

            if (!IsCurrentSave(model, version))
            {
                try
                {
                    await context.Registration.RejectOperationAsync(attempt.Revision);
                    context.Attempt = null;
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
                await dispatch();
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
        var attempt = context.Attempt!;
        try
        {
            if (!context.ReceiptRecorded)
            {
                await context.Registration.RecordConfirmedOperationAsync(attempt.Revision);
                context.ReceiptRecorded = true;
            }
            await context.Registration.CommitOperationAsync(attempt.Revision);
            context.Attempt = null;
            context.ReceiptRecorded = false;
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            // Бизнес-операция уже подтверждена. Повтор возможен только для журнала.
        }
        ShowOperationNotice(context);
    }

    private bool CanInspectOperation => !_disposed && !_loading && !_saving && !_poeRunning && !_inspectingOperation &&
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
                    await InitializeOperationContextAsync(model, user, _loadVersion);
                return;
            }
            context.InUse = true;
            try
            {
                if (context.NotSentId is { } attemptId)
                {
                    context.Attempt = await context.Registration.RetryStorageAsync(attemptId, _pageCts.Token);
                    context.NotSentId = null;
                }
                if (context.Attempt?.Status == ClayDraftOperationStatus.Confirmed)
                    await FinishConfirmedOperationAsync(context);
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

    private async Task RecoverOperationAsync(ClayDraftRecoveryCandidate candidate)
    {
        if (!CanInspectOperation || _operationContext is not { Attempt: null, NotSentId: null } context ||
            !candidate.CanRecover || !context.OtherAttempts.Contains(candidate))
            return;
        _inspectingOperation = context.InUse = true;
        try
        {
            if (!await context.Registration.RecoverListedDraftAsync(candidate.DraftId, _pageCts.Token))
            {
                if (ReferenceEquals(_operationContext, context))
                    _operationNotice = "Сохранённая попытка сейчас недоступна. Команды остаются заблокированы.";
                return;
            }

            context.Attempt = context.Registration.Restore.OperationSnapshot;
            if (ReferenceEquals(_operationContext, context))
                _observedOutcome = null;
            context.ReceiptRecorded = context.Attempt?.Status == ClayDraftOperationStatus.Confirmed;
            await ScanOperationJournalAsync(context);
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            context.Scanned = false;
            ShowOperationNotice(context);
        }
        finally
        {
            _inspectingOperation = false;
            await LeaveOperationContextAsync(context);
        }
    }

    private async Task ReadObservedOutcomeAsync()
    {
        if (!CanInspectOperation || _operationContext is not { Attempt: { } attempt } context ||
            attempt.Status == ClayDraftOperationStatus.Confirmed)
            return;
        var version = _loadVersion;
        _inspectingOperation = true;
        _observedOutcome = null;
        try
        {
            var user = await Employee.GetCurrentAsync(Db, _windowsPrincipal, _pageCts.Token);
            if (!IsCurrent(version, context.PortId))
                return;
            var port = user?.Id == context.EmployeeId
                ? await LocalNetworkPort.GetAsync(Db, context.PortId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, context.PortId))
                return;

            _observedOutcome = port is null
                ? "Порт сейчас недоступен. Исход попытки не установлен, повтор заблокирован."
                : $"Последнее изменение записи (UTC): {port.ChangedAtUtc:dd.MM.yyyy HH:mm:ss}. " +
                  "Чтение записи не подтверждает исход конкретной команды. Повтор остаётся заблокирован.";
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or OperationCanceledException)
        {
            if (IsCurrent(version, context.PortId))
                _observedOutcome = "Не удалось прочитать состояние порта. Повтор остаётся заблокирован.";
        }
        finally { _inspectingOperation = false; }
    }

    private void ShowOperationNotice(NetOperationContext context)
    {
        if (!ReferenceEquals(_operationContext, context))
            return;
        _operationNotice = context.NotSentId is not null
            ? "Команда не отправлена. Проверьте состояние журнала."
            : context.Attempt?.Status == ClayDraftOperationStatus.Confirmed
                ? "Ответ на команду получен. Повторить можно только завершение журнала."
                : context.Attempt is { } attempt
                    ? $"Результат команды «{(attempt.Kind == PoeOperationKind ? "Перезапустить PoE" : "Сохранить")}» неизвестен. Повтор заблокирован."
                    : context.OtherAttempts.Count > 0
                        ? "Для порта найдена сохранённая попытка. Команды заблокированы."
                        : !context.Scanned
                            ? "Журнал операций не подтверждён. Команды заблокированы."
                            : null;
    }

    private static string DescribeSaveIntent(LocalNetworkPort model, DateTime expectedChangedAt, NetDraft submitted) =>
        JsonSerializer.Serialize(new
        {
            PortId = model.Id,
            ExpectedChangedAtUtc = expectedChangedAt,
            submitted.SwitchToId,
            submitted.ExchangeWithId,
            Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(submitted))),
        });

    private static bool IsJournalError(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or JsonException;

    private static async Task RetireOperationContextAsync(NetOperationContext? context)
    {
        if (context is null)
            return;
        context.Retired = true;
        if (!context.InUse)
            await context.Registration.DisposeAsync();
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
        internal ClayDraftOperation? Attempt { get; set; }
        internal IReadOnlyList<ClayDraftRecoveryCandidate> OtherAttempts { get; set; } = [];
        internal string? NotSentId { get; set; }
        internal bool Scanned { get; set; }
        internal bool InUse { get; set; }
        internal bool Retired { get; set; }
        internal bool ReceiptRecorded { get; set; } = registration.Restore.OperationSnapshot?.Status == ClayDraftOperationStatus.Confirmed;
    }
}
