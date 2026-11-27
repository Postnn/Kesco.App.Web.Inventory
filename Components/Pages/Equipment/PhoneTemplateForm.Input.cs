using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class PhoneTemplateForm
{
    private const string TemplateInputScope = "template-input";
    private const string TemplateSubmissionScope = "template-submission";
    private const long MaximumTemplateTextBytes = 32L * 1024 * 1024;
    private const long MaximumTemplateRestoreBytes = 256L * 1024 * 1024;
    private ElementReference _editorRoot;
    private IJSObjectReference? _templateTextModule;
    private DotNetObjectReference<PhoneTemplateForm>? _editorObserver;
    private string? _editorWatchGeneration;
    private bool _editorWatchStarting;
    private bool _editorWatchFailed;
    private long _templateInputEpoch;
    private long _templateEditorRenderKey;
    private bool _restoreTemplateRawRequested;
    private object? _rawRestoreFlight;
    private bool _restoringTemplateRaw => _rawRestoreFlight is not null;
    private bool _rawPending;
    private bool _inputSaveFailed;
    private bool _inputMissingOriginal;
    private string? _inputNotice;
    private string? _scheduledInput;
    private TemplateSubmission? _protectedSubmission;

    private bool TemplateInputBlocked =>
        _restoreTemplateRawRequested || _restoringTemplateRaw || _rawPending || _inputSaveFailed || _inputMissingOriginal ||
        _editorWatchGeneration is null || _templateInputDiscarded || _pendingTemplateClose is not null || _releaseTransitionAfterRender is not null;

    private async Task RestoreTemplateInputAsync(ClayDraftRegistration journal, EquipmentModel model, int version)
    {
        if (journal.Restore.CancelledScopes?.Contains(TemplateInputScope) == true)
            await journal.OpenScopeAsync(TemplateInputScope, _pageCts.Token);
        var initialText = model.PhoneTemplate;
        var raw = await journal.ReadEditorScopeAsync(TemplateInputScope, _pageCts.Token);
        if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal))
            return;
        if (model.PhoneTemplate != initialText)
            throw new InvalidOperationException("Ввод изменился при открытии черновика.");

        if (journal.Restore.TypedScopes?.TryGetValue(TemplateInputScope, out var json) == true)
        {
            var draft = JsonSerializer.Deserialize<TemplateDraft>(json);
            if (draft is not { Original: not null, Current: not null } ||
                draft.Original.Id != model.Id || draft.Original.EquipmentTypeId != model.EquipmentTypeId)
                throw new InvalidOperationException("Исходная версия черновика не относится к открытой модели.");
            _writeBaseline = draft.Original;
            if (model.PhoneTemplate != draft.Current)
                ++_templateEditorRenderKey;
            model.PhoneTemplate = draft.Current;
            _scheduledInput = json;
        }
        else if (raw.HasRaw)
        {
            _inputMissingOriginal = true;
            _inputNotice = "Ввод восстановлен без исходной версии. Перед записью требуется сверка.";
        }
        _restoreTemplateRawRequested = raw.HasRaw;
    }

    private async Task RestoreTemplateRawAsync()
    {
        if (_loading || _saving || _journalBusy || _disposed || !_canEdit || _restoringTemplateRaw ||
            _templateJournal is not { } journal || _model is not { } model || _editorWatchGeneration is not { } generation)
            return;

        var version = _loadVersion;
        var employeeId = _journalEmployeeId;
        var epoch = _templateInputEpoch;
        var retry = _rawPending;
        var flight = new object();
        var token = _pageCts.Token;
        bool Current() => IsCurrentSave(model, version) && ReferenceEquals(_templateJournal, journal) &&
            employeeId == CurrentUser?.Id && generation == _editorWatchGeneration &&
            ReferenceEquals(_rawRestoreFlight, flight);

        _restoreTemplateRawRequested = false;
        _rawRestoreFlight = flight;
        StateHasChanged();
        try
        {
            if (retry)
            {
                var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), token);
                if (!Current()) return;
                if (actor is null || actor.Id != employeeId || !actor.HasRole(Роли.Дежурный_инженер))
                {
                    TemplateRawPending("Не удалось подтвердить сотрудника для восстановления. Ввод сохранён.");
                    return;
                }
            }
            if (epoch != _templateInputEpoch)
            {
                TemplateRawPending("Ввод изменился. Нажмите «Обновить», чтобы перечитать состояние формы.");
                return;
            }
            var result = retry
                ? await journal.ApplyPendingRawSilentlyAsync(TemplateInputScope, token)
                : await journal.ApplyRawSilentlyAsync(TemplateInputScope, token);
            if (!Current()) return;

            var readiness = await journal.GetRestoreReadinessAsync(token);
            if (!Current()) return;
            if (result is null || result.Pending.Count != 0 || !TemplateRawReady(readiness))
            {
                TemplateRawPending("Часть ввода ещё не восстановлена. Запись заблокирована.");
                return;
            }
            var restored = await ReadLiveTemplateAsync(token);
            if (!Current()) return;
            if (epoch != _templateInputEpoch)
            {
                TemplateRawPending("Ввод изменился. Нажмите «Обновить», чтобы перечитать состояние формы.");
                return;
            }
            _rawPending = false;
            ++_templateInputEpoch;
            if (!MatchesBrowserTemplateText(model.PhoneTemplate, restored))
                model.PhoneTemplate = restored;
            _inputNotice = _inputSaveFailed ? "Не удалось сохранить защищённый ввод. Для проверки состояния нажмите «Обновить»."
                : _inputMissingOriginal ? "Ввод восстановлен без исходной версии. Перед записью требуется сверка." : null;
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is IOException or Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            if (Current())
                TemplateRawPending("Не удалось восстановить защищённый ввод. Запись заблокирована.");
        }
        finally
        {
            if (ReferenceEquals(_rawRestoreFlight, flight))
            {
                _rawRestoreFlight = null;
                if (!_disposed) StateHasChanged();
            }
        }
    }

    private void TemplateRawPending(string message)
    {
        _rawPending = true;
        _inputNotice = message;
    }

    private static bool TemplateRawReady(ClayDraftRestoreReadiness readiness) =>
        !readiness.PendingScopes.Contains(TemplateInputScope) && !readiness.FailedScopes.Contains(TemplateInputScope);

    private async Task EnsureTemplateRawReadyAsync(ClayDraftRegistration journal, EquipmentModel model, int version, CancellationToken token)
    {
        bool ready;
        try { ready = TemplateRawReady(await journal.GetRestoreReadinessAsync(token)); }
        catch (Exception ex) when (IsJournalError(ex)) { ready = false; }
        if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal))
            throw new InvalidOperationException("Контекст шаблона изменился. Запись не отправлена.");
        if (ready) return;

        TemplateRawPending("Ввод шаблона ещё не восстановлен. Запись не отправлена.");
        throw new InvalidOperationException(_inputNotice);
    }

    private async Task EnsureTemplateEditorWatcherAsync()
    {
        if (!_canEdit || _loading || _disposed || _templateInputDiscarded || _pendingTemplateClose is not null || _model is not { } model ||
            _editorWatchGeneration is not null || _editorWatchStarting || _editorWatchFailed)
            return;
        var version = _loadVersion;
        _editorWatchStarting = true;
        var generation = Guid.NewGuid().ToString("N");
        DotNetObjectReference<PhoneTemplateForm>? observer = DotNetObjectReference.Create(this);
        try
        {
            var module = await GetTemplateTextModuleAsync(_pageCts.Token);
            await module.InvokeVoidAsync("watchTemplateText", _pageCts.Token, _editorRoot, observer, generation);
            if (!IsCurrentSave(model, version))
            {
                await StopTemplateEditorWatcherAsync(generation);
                return;
            }

            _editorObserver = observer;
            observer = null;
            _editorWatchGeneration = generation;
            _templateOptions.ReadOnly = false;
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is IOException)
        {
            await StopTemplateEditorWatcherAsync(generation);
            if (IsCurrentSave(model, version))
            {
                _editorWatchFailed = true;
                _inputNotice = "Не удалось подключить защищённый ввод шаблона. Запись заблокирована.";
            }
        }
        finally
        {
            observer?.Dispose();
            _editorWatchStarting = false;
            if (!_disposed)
                StateHasChanged();
        }
    }

    private async ValueTask ReleaseTemplateEditorWatcherAsync()
    {
        var generation = _editorWatchGeneration;
        var observer = _editorObserver;
        _editorWatchGeneration = null;
        _editorObserver = null;
        _editorWatchFailed = false;
        ++_templateInputEpoch;
        _templateOptions.ReadOnly = true;
        try
        {
            if (generation is not null)
                await StopTemplateEditorWatcherAsync(generation);
        }
        finally { observer?.Dispose(); }
    }

    private async ValueTask StopTemplateEditorWatcherAsync(string generation)
    {
        if (_templateTextModule is not { } module)
            return;
        try { await module.InvokeVoidAsync("unwatchTemplateText", _editorRoot, generation); }
        catch (Exception ex) when (IsJournalError(ex)) { }
    }

    /// <summary>Получает только поколение и редакторскую ревизию, перечитывая текст отдельным потоком.</summary>
    [JSInvokable]
    public async Task OnTemplateEditorChangedAsync(string generation, long revision)
    {
        if (_disposed || !_canEdit || _templateTransitionApplying || _releaseTransitionAfterRender is not null || _templateInputDiscarded || _pendingTemplateClose is not null ||
            generation != _editorWatchGeneration || _model is not { } model)
            return;
        var epoch = _templateInputEpoch;
        bool Current() => !_disposed && _model?.Id == Id && ReferenceEquals(model, _model) &&
            generation == _editorWatchGeneration && epoch == _templateInputEpoch && _pendingTemplateClose is null;
        try
        {
            var snapshot = await TryReadTemplateSnapshotAsync(generation, _pageCts.Token);
            if (Current() && snapshot is not null && snapshot.Revision >= revision &&
                !MatchesBrowserTemplateText(model.PhoneTemplate, snapshot.Text))
            {
                await OnTemplateInputAsync(snapshot.Text);
                if (!_disposed && model.Id == Id && ReferenceEquals(model, _model) && generation == _editorWatchGeneration)
                    StateHasChanged();
            }
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is IOException)
        {
            if (Current())
            {
                _inputSaveFailed = true;
                _inputNotice = "Новый ввод остаётся в редакторе. Не удалось подтвердить чтение потока.";
                StateHasChanged();
            }
        }
    }

    private static bool MatchesBrowserTemplateText(string? source, string live) =>
        (source ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') == live;

    private async ValueTask<IJSObjectReference> GetTemplateTextModuleAsync(CancellationToken token) =>
        _templateTextModule ??= await JS.InvokeAsync<IJSObjectReference>("import", token, "./js/phoneTemplateDraftText.js");

    private async Task<TemplateTextSnapshot?> TryReadTemplateSnapshotAsync(string generation, CancellationToken token)
    {
        var module = await GetTemplateTextModuleAsync(token);
        var snapshot = await module.InvokeAsync<TemplateTextWire?>("readTemplateSnapshot", token, _editorRoot, generation);
        if (snapshot?.Payload is null)
            return null;
        await using var reference = snapshot.Payload;
        await using var stream = await reference.OpenReadStreamAsync(MaximumTemplateTextBytes, token);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(token);
        var current = await module.InvokeAsync<long?>("currentTemplateRevision", token, _editorRoot, generation);
        return current == snapshot.Revision && generation == _editorWatchGeneration
            ? new TemplateTextSnapshot(text, snapshot.Revision) : null;
    }

    private async Task<TemplateTextSnapshot> ReadCurrentTemplateAsync(CancellationToken token)
    {
        if (_editorWatchGeneration is not { } generation)
            throw new InvalidOperationException("Поле шаблона недоступно.");
        return await TryReadTemplateSnapshotAsync(generation, token)
            ?? throw new InvalidOperationException("Ввод изменился во время чтения.");
    }

    private async Task<string> ReadLiveTemplateAsync(CancellationToken token) =>
        (await ReadCurrentTemplateAsync(token)).Text;

    internal sealed record TemplateTextWire(
        [property: JsonPropertyName("revision")] long Revision,
        [property: JsonPropertyName("payload")] IJSStreamReference Payload);

    private sealed record TemplateTextSnapshot(string Text, long Revision)
    {
        public override string ToString() => nameof(TemplateTextSnapshot);
    }

    private async Task OnTemplateInputAsync(string? text)
    {
        if (_model is not { } model || !_canEdit || _disposed || _pendingTemplateClose is not null || _templateInputDiscarded)
            return;
        _allowNavigation = false;
        ++_templateInputEpoch;
        model.PhoneTemplate = text ?? string.Empty;
        await PersistTemplateInputAsync();
    }

    private async Task PersistTemplateInputAsync()
    {
        if (_loading || _disposed || !_canEdit || TemplateInputBlocked || _templateJournal is not { } journal ||
            _model is not { } model || _writeBaseline is not { } baseline ||
            (!IsModified && _scheduledInput is null))
            return;

        var snapshot = SerializeTemplateDraft(baseline, model.PhoneTemplate);
        if (_scheduledInput == snapshot)
            return;
        _scheduledInput = snapshot;
        var version = _loadVersion;
        try { await SaveProtectedScopeAsync(journal, TemplateInputScope, snapshot, _pageCts.Token); }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (IsCurrentSave(model, version) && _scheduledInput == snapshot)
            {
                _inputSaveFailed = true;
                _inputNotice = "Не удалось сохранить защищённый ввод. Запись заблокирована.";
            }
        }
    }

    private Task RetryTemplateRawAsync() => _rawPending ? RestoreTemplateRawAsync() : Task.CompletedTask;

    private async Task RetryTemplateInputAsync()
    {
        if (_saving || _loading || _journalBusy || _disposed || _restoringTemplateRaw || (_rawPending && !_editorWatchFailed))
            return;
        if (_editorWatchFailed)
        {
            _editorWatchFailed = false;
            _inputNotice = _rawPending ? "Часть ввода ещё не восстановлена. Запись заблокирована." : null;
            await EnsureTemplateEditorWatcherAsync();
            return;
        }
        if (_templateJournal is not { } journal || _model is not { } model)
            return;
        if (!_inputSaveFailed)
            return;
        try
        {
            var version = _loadVersion;
            var snapshot = await ReadCurrentTemplateAsync(_pageCts.Token);
            if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal))
                return;
            ++_templateInputEpoch;
            if (!MatchesBrowserTemplateText(model.PhoneTemplate, snapshot.Text))
                model.PhoneTemplate = snapshot.Text;
            _inputSaveFailed = false;
            _scheduledInput = null;
            _inputNotice = null;
            await PersistTemplateInputAsync();
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            _inputSaveFailed = true;
            _inputNotice = "Защищённый ввод пока не подтверждён. Запись не отправлена.";
        }
    }

    private async Task ImportTemplateFileAsync(InputFileChangeEventArgs args)
    {
        if (_model is not { } model || _writeBaseline is not { } baseline || !_canEdit ||
            _saving || _loading || TemplateWriteBlocked || _templateJournal is not { } journal)
            return;
        var version = _loadVersion;
        var previous = model.PhoneTemplate;
        _journalBusy = true;
        try
        {
            var state = await journal.ReadEditorScopeAsync(TemplateInputScope, _pageCts.Token);
            await using var stream = args.File.OpenReadStream(10_000_000, _pageCts.Token);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(_pageCts.Token);
            if (!IsCurrentSave(model, version) || model.PhoneTemplate != previous || !ReferenceEquals(_templateJournal, journal))
                return;
            var snapshot = SerializeTemplateDraft(baseline, text);
            if (!await journal.ReplaceEditorScopeAsync(
                TemplateInputScope, state.Revision, snapshot, _pageCts.Token))
                throw new InvalidOperationException("Старый защищённый ввод не был заменён.");
            if (IsCurrentSave(model, version) && model.PhoneTemplate == previous && ReferenceEquals(_templateJournal, journal))
            {
                _scheduledInput = snapshot;
                ++_templateInputEpoch;
                if (model.PhoneTemplate != text)
                    ++_templateEditorRenderKey;
                model.PhoneTemplate = text;
                _inputNotice = null;
            }
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is IOException)
        {
            if (IsCurrentSave(model, version))
                _inputNotice = "Не удалось загрузить файл в защищённый черновик. Предыдущий ввод сохранён.";
        }
        finally { _journalBusy = false; }
    }

    private static string SerializeTemplateDraft(EquipmentModelWriteBaseline original, string? current) =>
        JsonSerializer.Serialize(new TemplateDraft(original, current ?? string.Empty));

    private static async Task<long> SaveProtectedScopeAsync(ClayDraftRegistration journal, string scope, string snapshot, CancellationToken token)
    {
        var revision = await journal.SaveTypedScopeAsync(scope, snapshot, token);
        return revision > 0 ? revision : throw new InvalidOperationException("Защищённый черновик не подтверждён.");
    }

    private sealed record TemplateDraft(EquipmentModelWriteBaseline Original, string Current)
    {
        public override string ToString() => nameof(TemplateDraft);
    }

    private sealed record TemplateSubmission(int ModelId, string AttemptId, long Revision, long InputRevision, string Text, bool Confirmed)
    {
        public override string ToString() => nameof(TemplateSubmission);
    }
}
