using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Net;

public partial class Net
{
    private static readonly ClayToolbarButtonOptions ReviewStaleInputOptions = new() { Text = "Сверить данные порта" };
    private static readonly ClayToolbarButtonOptions AcceptStaleInputOptions = new() { Text = "Применить результаты сверки" };

    private bool CanReviewStaleInput => !OperationBlocked && !_loading && !_saving && !_poeRunning &&
        _operationContext is { InputReady: true, InputConflict: true, InputFailed: false, LastSavedInput: not null } context &&
        IsCurrentInput(context);

    private async Task ReviewStaleNetInputAsync()
    {
        if (!CanReviewStaleInput || _operationContext is not { } context || _model is not { } model ||
            context.InputBaseline is not { } original)
            return;

        _inspectingOperation = true;
        context.InputReview = null;
        _validationMessage = null;
        try
        {
            var local = Snapshot(model);
            var storage = await context.Registration.ReadRebaseStateAsync(NetInputScope, _pageCts.Token);
            if (!IsCurrentInput(context) || !ReferenceEquals(model, _model))
                return;
            if (storage.RawKeys.Any(key => !IsKnownNetRawKey(key)))
            {
                _validationMessage = "У черновика есть старые поля без стабильного ключа. Ввод сохранён, автоматически принять исходную версию нельзя.";
                return;
            }

            var freshPort = await ReadPortForDraftReviewAsync(context);
            if (freshPort is null || !IsCurrentInput(context) || !ReferenceEquals(model, _model))
                return;
            if (Snapshot(model) != local)
            {
                _validationMessage = "Ввод изменился во время чтения. Откройте сверку заново.";
                return;
            }

            var fresh = NetDraft.From(freshPort, freshPort.ReturnDate.HasValue);
            var rawFields = storage.RawKeys.Select(GetNetRawField).OfType<NetField>().ToHashSet();
            context.InputReview = new NetInputReview(storage, original, local, fresh, freshPort.ChangedAtUtc, rawFields,
                fresh.GetConflicts(original, local, rawFields));
        }
        catch (Exception ex) when (IsOperationStateError(ex) || ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            if (IsCurrentInput(context))
                _validationMessage = "Не удалось открыть сверку. Ввод сохранён.";
        }
        finally { _inspectingOperation = false; }
    }

    private async Task AcceptStaleNetInputAsync()
    {
        if (!CanReviewStaleInput || _operationContext is not { InputReview: { } review } context ||
            _model is not { } model || review.Conflicts.Any(field => field.KeepLocal is null))
            return;

        _inspectingOperation = true;
        _validationMessage = null;
        try
        {
            var freshPort = await ReadPortForDraftReviewAsync(context);
            if (freshPort is null || !IsCurrentInput(context) || !ReferenceEquals(model, _model))
                return;
            var fresh = NetDraft.From(freshPort, freshPort.ReturnDate.HasValue);
            if (Snapshot(model) != review.Local || fresh != review.Fresh || freshPort.ChangedAtUtc != review.ChangedAt)
            {
                context.InputReview = null;
                _validationMessage = "Запись или ввод изменились во время сверки. Сравните значения заново.";
                return;
            }

            var useFresh = review.Conflicts.Where(field => field.KeepLocal == false).Select(field => field.Field).ToHashSet();
            var accepted = fresh.WithLaterEdits(review.Original, review.Local, useFresh, review.RawFields);
            var discardedRaw = review.Storage.RawKeys.Where(key => GetNetRawField(key) is { } field && useFresh.Contains(field)).ToArray();
            var stored = new NetEditorInput(context.PortId, freshPort.ChangedAtUtc, fresh, accepted);
            if (!await context.Registration.RebaseScopeAsync(NetInputScope, review.Storage.Revision,
                    JsonSerializer.Serialize(stored), discardedRaw, _pageCts.Token))
            {
                if (IsCurrentInput(context))
                    _validationMessage = "Сверку не удалось подтвердить. Ввод и исходная версия сохранены.";
                return;
            }
            if (!IsCurrentInput(context) || !ReferenceEquals(model, _model))
                return;

            var visible = accepted.WithLaterEdits(review.Local, Snapshot(model), null);
            ApplyVisibleNetInput(freshPort, visible);
            _model = freshPort;
            _originalSnapshot = fresh;
            context.InputBaseline = fresh;
            context.InputVersion = freshPort.ChangedAtUtc;
            context.LastSavedInput = stored;
            context.LastInputRevision = 0;
            context.InputConflict = false;
            context.InputReady = false;
            context.InputRestoreStarted = false;
            _inputViewRevision++;
            context.InputReview = null;
        }
        catch (Exception ex) when (IsOperationStateError(ex) || ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            if (IsCurrentInput(context))
            {
                context.InputReview = null;
                _validationMessage = "Не удалось подтвердить сверку. Ввод сохранён, откройте сверку заново.";
            }
        }
        finally { _inspectingOperation = false; }
    }

    private async Task<LocalNetworkPort?> ReadPortForDraftReviewAsync(NetOperationContext context)
    {
        var actor = await Employee.GetCurrentAsync(Db, _windowsPrincipal, _pageCts.Token);
        if (actor?.Id != context.EmployeeId || !IsCurrentInput(context))
        {
            if (IsCurrentInput(context))
                _validationMessage = "Не удалось подтвердить доступ сотрудника к черновику.";
            return null;
        }

        var port = await LocalNetworkPort.GetAsync(Db, context.PortId, _pageCts.Token);
        if (port is null && IsCurrentInput(context))
            _validationMessage = "Порт недоступен. Локальный ввод сохранён.";
        return port;
    }

    private static bool IsKnownNetRawKey(string key) => GetNetRawField(key) is not null ||
        key.StartsWith("data:net.switch:", StringComparison.Ordinal) ||
        key.StartsWith("data:net.exchange:", StringComparison.Ordinal);

    private static NetField? GetNetRawField(string key)
    {
        if (key.StartsWith("data:net.socket:", StringComparison.Ordinal)) return NetField.Socket;
        if (key.StartsWith("data:net.employee:", StringComparison.Ordinal)) return NetField.Employee;
        if (key.StartsWith("data:net.customer:", StringComparison.Ordinal)) return NetField.Customer;
        if (key.StartsWith("data:net.connected:", StringComparison.Ordinal)) return NetField.Connected;
        if (key.StartsWith("data:net.description:", StringComparison.Ordinal)) return NetField.Description;
        if (key.StartsWith("data:net.return-date:", StringComparison.Ordinal)) return NetField.ReturnDate;
        return null;
    }

    private sealed record NetInputReview(ClayDraftRebaseState Storage, NetDraft Original, NetDraft Local,
        NetDraft Fresh, DateTime ChangedAt, IReadOnlySet<NetField> RawFields, IReadOnlyList<NetConflict> Conflicts);
}
