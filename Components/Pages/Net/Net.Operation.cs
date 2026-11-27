using System.Globalization;
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
    private static readonly ClayToolbarButtonOptions RetryDraftStorageOptions = new() { Text = "Проверить временный ввод" };

    private readonly string _operationRoot = $"net-operation-{Guid.NewGuid():N}";
    private Guid _netDraftInstance = Guid.NewGuid();
    private NetOperationContext? _operationContext;
    private string? _operationNotice;
    private string? _observedOutcome;
    private bool _inspectingOperation;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool OperationBlocked => _inspectingOperation || _pendingNetClose is not null ||
        _operationContext is not
            { Scanned: true };

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
            registration = await Drafts.RegisterRamHandleAsync(OperationFormKey, entityKey, userKey,
                1, $"[data-net-operation='{_operationRoot}']", _netDraftInstance, token);            if (!Current())
                return;

            var context = new NetOperationContext(registration, currentUser.Id, port.Id)
            {
            };
            _operationContext = context;
            registration = null;
            await CheckDraftStorageAsync(context);
        }
        catch (Exception ex) when (IsOperationStateError(ex) || ex is UnauthorizedAccessException)
        {
            if (IsCurrent(version, port.Id))
                _operationNotice = "Временное состояние редактора недоступно. Сохранение и перезапуск PoE заблокированы.";
        }
        finally
        {
            if (registration is not null)
                await registration.DisposeAsync();
        }
    }

    private async Task CheckDraftStorageAsync(NetOperationContext context)
    {
        context.Scanned = false;
        var health = await context.Registration.GetHealthAsync(_pageCts.Token);
        context.Scanned = health is { Healthy: true, PendingWrites: 0 } &&
            true;
        ShowOperationNotice(context);
    }

    private async Task<bool> DispatchOperationAsync(
        LocalNetworkPort model, int version, Employee currentUser, string kind, Func<Task> dispatch, NetDraft? submitted = null)
    {
        if (OperationBlocked || _operationContext is not { } context ||
            context.EmployeeId != currentUser.Id || context.PortId != model.Id)
        {
            _operationNotice = "Операция не отправлена. Состояние временного ввода не подтверждено для текущего сотрудника и порта.";
            return false;
        }

        context.InUse = true;
        try
        {
            await CheckDraftStorageAsync(context);
            if (OperationBlocked || !IsCurrentSave(model, version) || !await VerifyNetInputReadyAsync(context))
                return false;
            if (submitted is not null)
            {
                var inputRevision = await PersistNetInputAsync(context, submitted);
                if (inputRevision <= 0 || Snapshot(model) != submitted || !IsCurrentSave(model, version))
                    return false;
            }

            try
            {
                await dispatch();
            }
            catch (Exception error) when (error is LocalNetworkPortRejectedException ||
                kind == PoeOperationKind && error is UnauthorizedAccessException)
            {
                if (IsCurrentSave(model, version))
                    _validationMessage = error.Message;
                ShowOperationNotice(context);
                return false;
            }

            context.OutcomeVerified = true;
            await FinishConfirmedOperationAsync(context);
            return true;
        }
        catch (Exception ex) when (IsOperationStateError(ex))
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
    private Task FinishConfirmedOperationAsync(NetOperationContext context)
    {
        if (!context.OutcomeVerified) return Task.CompletedTask;
        context.LastSavedInput = null;
        context.LastInputRevision = 0;
        context.InputReady = false;
        context.InputRestoreStarted = false;
        context.OutcomeVerified = false;
        ShowOperationNotice(context);
        return Task.CompletedTask;
    }
    private bool CanInspectOperation => !_disposed && !_loading && !_saving && !_poeRunning && !_inspectingOperation && _pendingNetClose is null &&
        _model?.Id == Id;

    private async Task RetryDraftStorageAsync()
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
                if (context.OutcomeVerified)
                    await FinishConfirmedOperationAsync(context);
                if (IsCurrentInput(context))
                    await CheckDraftStorageAsync(context);
            }
            finally { await LeaveOperationContextAsync(context); }
        }
        catch (Exception ex) when (IsOperationStateError(ex))
        {
            if (ReferenceEquals(_model, model))
                _operationNotice = "Не удалось проверить временный ввод. Команды остаются заблокированы.";
        }
        finally { _inspectingOperation = false; }
    }

    private void ShowOperationNotice(NetOperationContext context)
    {
        if (!ReferenceEquals(_operationContext, context)) return;
        _operationNotice = context.OutcomeVerified
            ? "Ответ на команду получен. Повторить можно только очистку временного ввода."
            : !context.Scanned
                ? "Состояние временного ввода не подтверждено. Команды заблокированы."
                : null;
    }


    private static bool IsOperationStateError(Exception ex) =>
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

        internal bool Scanned { get; set; }
        internal bool InUse { get; set; }
        internal bool Retired { get; set; }
        internal bool OutcomeVerified { get; set; }

    }
}
