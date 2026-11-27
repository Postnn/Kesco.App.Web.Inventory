using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class PhoneTemplateForm
{
    private bool _templateConflictNeedsReview;
    private CancellationTokenSource? _templateConflictReview;
    private bool _templateTransitionApplying;
    private bool _templateInputDiscarded;
    private (long DraftRevision, long? EditorRevision)? _pendingTemplateClose;
    private TemplateTransition? _releaseTransitionAfterRender;

    private Task CancelAsync() => ChangeTemplateEditorAsync(refresh: false);

    private async Task RefreshAsync()
    {
        if (_disposed || _loading || _saving || _journalBusy || _pendingTemplateClose is not null)
            return;
        if (_rawPending && !_editorWatchFailed && !_knownWrite)
        {
            await RetryTemplateRawAsync();
            return;
        }
        if ((_templateAttempt is not null || HasUnprovedTemplateSubmission) && !_knownWrite && _notSentAttempt is null)
        {
            await ObserveTemplateAsync();
            return;
        }
        if (_journalNotice is not null)
        {
            await RetryTemplateJournalAsync();
            return;
        }
        if (_inputSaveFailed || _editorWatchFailed)
        {
            await RetryTemplateInputAsync();
            return;
        }
        if (_templateConflictNeedsReview)
        {
            await ReviewTemplateConflictAsync();
            return;
        }
        await ChangeTemplateEditorAsync(refresh: true);
    }

    private async Task ReviewTemplateConflictAsync()
    {
        if (!_canEdit || _model is not { } model || _writeBaseline is not { } original ||
            _templateJournal is not { } journal || _templateTextModule is not { } module ||
            _editorWatchGeneration is not { } generation || CurrentUser is not { Id: > 0 } employee ||
            TemplateInputBlocked || !_journalReady || _serverWritePending || _templateAttempt is not null ||
            _protectedSubmission is not null || _knownWrite || _notSentAttempt is not null)
        {
            _inputNotice = "Не удалось начать сверку шаблона. Ввод сохранён.";
            return;
        }

        var version = _loadVersion;
        using var review = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        var token = review.Token;
        var bound = new TemplateTransition(module, _editorRoot, generation);
        bool Current() => !token.IsCancellationRequested && IsCurrentSave(model, version) &&
            CurrentUser?.Id == employee.Id && _canEdit && ReferenceEquals(_templateJournal, journal) &&
            ReferenceEquals(_templateConflictReview, review) && generation == _editorWatchGeneration && _writeBaseline == original;
        var frozen = false;
        _templateConflictReview = review;
        _journalBusy = true;
        try
        {
            var snapshot = await ReadCurrentTemplateAsync(token);
            var scope = await journal.ReadEditorScopeAsync(TemplateInputScope, token);
            var captured = await journal.ReadRebaseStateAsync(TemplateInputScope, token);
            var readiness = await journal.GetRestoreReadinessAsync(token);
            if (!Current())
                return;
            if (scope.HasOperation || scope.Revision != captured.Revision || !readiness.Complete)
            {
                _inputNotice = "Ввод изменился или не восстановлен полностью. Повторите сверку.";
                return;
            }

            var shown = await ReadCurrentTemplateForResetAsync(model, requireEdit: true, token);
            if (!Current())
                return;
            if (shown is null || await EquipmentModel.HasPendingPhoneTemplateWriteAsync(Db, employee, model.Id, token))
            {
                if (Current())
                    _inputNotice = "Не удалось подтвердить доступ к актуальному шаблону. Ввод сохранён.";
                return;
            }
            if (!Current())
                return;
            var freshBaseline = shown.CaptureWriteBaseline();
            if (SameTemplateVersion(freshBaseline, original))
            {
                _templateConflictNeedsReview = false;
                _inputNotice = "Исходная версия шаблона не изменилась. Ввод сохранён.";
                return;
            }

            if (!await AskKeepTemplateInputAsync(token) || !Current())
                return;

            frozen = await module.InvokeAsync<bool>("freezeTemplateInput", token, bound.Root, generation, snapshot.Revision);
            if (!Current() || !frozen)
            {
                if (Current())
                    _inputNotice = "Ввод изменился. Повторите сверку шаблона.";
                return;
            }

            var fresh = await ReadCurrentTemplateForResetAsync(model, requireEdit: true, token);
            if (!Current())
                return;
            if (fresh is null || !SameTemplateVersion(fresh.CaptureWriteBaseline(), freshBaseline) ||
                await EquipmentModel.HasPendingPhoneTemplateWriteAsync(Db, employee, model.Id, token))
            {
                if (Current())
                    _inputNotice = "Актуальная версия или доступ изменились. Повторите сверку. Ввод сохранён.";
                return;
            }
            if (!Current())
                return;

            // Сохраняем исходные окончания строк при эквивалентном тексте браузера.
            var local = MatchesBrowserTemplateText(model.PhoneTemplate, snapshot.Text) ? model.PhoneTemplate : snapshot.Text;
            var replacement = SerializeTemplateDraft(freshBaseline, local);
            _templateTransitionApplying = true;
            ++_templateInputEpoch;
            if (!await journal.RebaseScopeAsync(TemplateInputScope, captured.Revision, replacement, [], token))
            {
                if (Current())
                    _inputNotice = "Ввод изменился или хранилище недоступно. Повторите сверку.";
                return;
            }
            if (!Current())
                return;

            _writeBaseline = freshBaseline;
            model.PhoneTemplate = local;
            model.ChangedAt = fresh.ChangedAt;
            model.ChangedById = fresh.ChangedById;
            model.ChangedByName = fresh.ChangedByName;
            _scheduledInput = replacement;
            _templateConflictNeedsReview = false;
            _inputNotice = "Исходная версия обновлена. Ваш текст сохранён в редакторе.";
            _errorMessage = null;
        }
        catch (Exception error) when (IsJournalError(error) || error is IOException)
        {
            if (Current())
                _inputNotice = "Не удалось завершить сверку шаблона. Ввод сохранён.";
        }
        finally
        {
            if (ReferenceEquals(_templateConflictReview, review))
            {
                _templateConflictReview = null;
                _templateTransitionApplying = false;
                _journalBusy = false;
            }
            if (frozen)
                await ReleaseTemplateTransitionAsync(bound);
            if (!_disposed)
                StateHasChanged();
        }
    }

    private static bool SameTemplateVersion(EquipmentModelWriteBaseline a, EquipmentModelWriteBaseline b) =>
        a.Id == b.Id && a.EquipmentTypeId == b.EquipmentTypeId && a.ChangedAt == b.ChangedAt &&
        string.Equals(a.PhoneTemplate ?? string.Empty, b.PhoneTemplate ?? string.Empty, StringComparison.Ordinal);

    private async Task<bool> AskKeepTemplateInputAsync(CancellationToken token)
    {
        const string title = "Сверка версии шаблона";
        var dialog = await DialogService.ShowExAsync<ConfirmDialog>(title, new DialogParameters
        {
            { nameof(ConfirmDialog.Title), title },
            { nameof(ConfirmDialog.Message), "В базе сохранена другая версия шаблона. Принять её как исходную версию и оставить ваш текст в редакторе?" },
            { nameof(ConfirmDialog.ConfirmText), "Оставить мой текст" },
            { nameof(ConfirmDialog.CancelText), "Продолжить без изменений" },
        }, new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, CloseOnEscapeKey = true, CloseButton = true });
        try { return await dialog.Result.WaitAsync(token) is { Canceled: false }; }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            dialog.Close(DialogResult.Cancel());
            return false;
        }
    }

    private void CancelTemplateConflictReview()
    {
        if (_templateConflictReview is not { } review)
            return;

        _templateConflictReview = null;
        _templateTransitionApplying = false;
        _journalBusy = false;
        review.Cancel();
    }

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_templateConflictReview is { } review)
        {
            context.PreventNavigation();
            review.Cancel();
            return;
        }
        if (_allowNavigation)
        {
            _allowNavigation = false;
            if (_templateAttempt is null && !_knownWrite)
                return;
        }
        if (_model is null && !_loading && !_saving)
            return;
        context.PreventNavigation();
        await ChangeTemplateEditorAsync(refresh: false, context.TargetLocation, context.CancellationToken);
    }

    private async Task ChangeTemplateEditorAsync(bool refresh, string? target = null, CancellationToken navigationToken = default)
    {
        if (_disposed || _loading || _saving || _journalBusy || _releaseTransitionAfterRender is not null)
            return;
        if (_pendingTemplateClose is { } pending && _templateJournal is { } closing)
        {
            if (refresh) return;
            _journalBusy = true;
            try { await CloseApprovedTemplateAsync(closing, pending.DraftRevision, pending.EditorRevision, target); }
            finally { _journalBusy = false; }
            return;
        }

        var unresolved = _templateAttempt is not null || _protectedSubmission is not null || _knownWrite ||
            _notSentAttempt is not null;
        if (refresh && unresolved)
        {
            _inputNotice = "Сначала завершите сверку предыдущей операции. Ввод и попытка сохранены.";
            return;
        }


        var model = _model;
        if (model is null || _templateInputDiscarded || !_canEdit)
        {
            if (!refresh)
            {
                try
                {
                    if (_templateJournal is { } retained)
                        await CloseReadOnlyTemplateAsync(retained, unresolved, target, navigationToken);
                    else
                        await LeaveTemplateAsync(target);
                }
                catch (Exception ex) when (IsJournalError(ex))
                {
                    _inputNotice = "Не удалось закрыть редактор.";
                }
            }
            else if (model is not null && !_templateInputDiscarded)
                await RefreshReadOnlyTemplateAsync(model);
            return;
        }
        if (_templateJournal is not { } journal || _templateTextModule is not { } module ||
            _editorWatchGeneration is not { } generation || _restoreTemplateRawRequested || _restoringTemplateRaw)
        {
            _inputNotice = "Не удалось проверить защищённый ввод. Изменения оставлены в редакторе.";
            return;
        }

        var version = _loadVersion;
        var bound = new TemplateTransition(module, _editorRoot, generation);
        bool Current() => !navigationToken.IsCancellationRequested && IsCurrentSave(model, version) &&
            ReferenceEquals(_templateJournal, journal) && generation == _editorWatchGeneration;
        var frozen = false;
        _journalBusy = true;
        try
        {
            var token = _pageCts.Token;
            var snapshot = await ReadCurrentTemplateAsync(token);
            var scope = await journal.ReadEditorScopeAsync(TemplateInputScope, token);
            if (!Current())
                return;
            var dirty = unresolved || scope.HasOperation || IsModified || scope.HasRaw || _inputMissingOriginal ||
                !MatchesBrowserTemplateText(_writeBaseline?.PhoneTemplate, snapshot.Text);
            var choice = dirty ? await AskTemplateCancelChoiceAsync() : refresh
                ? ClayEditorCancelChoice.ResetAndStay : ClayEditorCancelChoice.CloseWithoutSaving;
            if (!Current() || choice == ClayEditorCancelChoice.Continue)
                return;

            EquipmentModel? fresh = null;
            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                if (unresolved || scope.HasOperation)
                {
                    _inputNotice = "Сначала завершите сверку предыдущей операции. Ввод и попытка сохранены.";
                    return;
                }
                fresh = await ReadCurrentTemplateForResetAsync(model, requireEdit: true, token);
                if (fresh is null && Current())
                    _inputNotice = "Не удалось получить актуальный шаблон и подтвердить доступ. Ввод сохранён.";
                if (fresh is null || !Current())
                    return;
            }

            frozen = await module.InvokeAsync<bool>("freezeTemplateInput", token, bound.Root, generation, snapshot.Revision);
            if (!Current())
                return;
            if (!frozen)
            {
                _inputNotice = "Ввод изменился. Подтвердите отмену ещё раз.";
                return;
            }

            _templateTransitionApplying = true;
            ++_templateInputEpoch;
            if (fresh is null)
            {
                await CloseApprovedTemplateAsync(journal, scope.Revision, snapshot.Revision, target);
                return;
            }

            var replacement = SerializeTemplateDraft(fresh.CaptureWriteBaseline(), fresh.PhoneTemplate);
            if (!await journal.ReplaceEditorScopeAsync(TemplateInputScope, scope.Revision, replacement, token))
            {
                if (Current())
                    _inputNotice = "Ввод изменился или хранилище недоступно. Подтвердите отмену ещё раз.";
                return;
            }
            // Once the replacement is durable, the current route must show the same state even
            // if a second navigation request canceled the first one.
            if (!IsCurrentSave(model, version) || !ReferenceEquals(journal, _templateJournal))
                return;
            _scheduledInput = replacement;
            _inputSaveFailed = _inputMissingOriginal = false;
            _inputNotice = null;
            ++_templateEditorRenderKey;
            _model = fresh;
            _templateConflictNeedsReview = false;
            _writeBaseline = fresh.CaptureWriteBaseline();
            _releaseTransitionAfterRender = bound;
            frozen = false;
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is IOException)
        {
            if (Current())
                _inputNotice = "Не удалось завершить отмену или обновление. Непринятый ввод оставлен в редакторе.";
        }
        finally
        {
            _templateTransitionApplying = false;
            _journalBusy = false;
            if (frozen)
                await ReleaseTemplateTransitionAsync(bound);
            if (!_disposed)
                StateHasChanged();
        }
    }

    private async Task<ClayEditorCancelChoice> AskTemplateCancelChoiceAsync()
    {
        var dialog = await DialogService.ShowExAsync<ClayEditorCancelDialog>("Отмена изменений", new DialogParameters(),
            new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, CloseOnEscapeKey = true, CloseButton = true });
        return await dialog.Result is { Canceled: false, Data: ClayEditorCancelChoice choice }
            ? choice : ClayEditorCancelChoice.Continue;
    }

    private async Task<EquipmentModel?> ReadCurrentTemplateForResetAsync(EquipmentModel model, bool requireEdit, CancellationToken token)
    {
        var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), token);
        if (actor is null || actor.Id != CurrentUser?.Id || (requireEdit && !actor.HasRole(Роли.Дежурный_инженер)))
            return null;
        var fresh = await EquipmentModel.GetForEditAsync(Db, model.Id, token);
        return fresh?.Id == model.Id && fresh.EquipmentTypeId == (int)ТипыОборудования.IP_телефон ? fresh : null;
    }

    private async Task RefreshReadOnlyTemplateAsync(EquipmentModel model)
    {
        var version = _loadVersion;
        _journalBusy = true;
        try
        {
            var fresh = await ReadCurrentTemplateForResetAsync(model, requireEdit: false, _pageCts.Token);
            if (!IsCurrentSave(model, version)) return;
            if (fresh is null)
            {
                _inputNotice = "Не удалось получить актуальный шаблон и подтвердить доступ.";
                return;
            }
            _model = fresh;
            _templateConflictNeedsReview = false;
            _writeBaseline = fresh.CaptureWriteBaseline();
            ++_templateEditorRenderKey;
            _inputNotice = null;
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (IsCurrentSave(model, version))
                _inputNotice = "Не удалось получить актуальный шаблон и подтвердить доступ.";
        }
        finally { _journalBusy = false; }
    }

    private async Task CloseAtEditorRevisionAsync(long revision)
    {
        if (_templateJournal is not { } journal || _editorWatchGeneration is not { } generation ||
            _templateTextModule is not { } module || _model is not { } model)
            return;
        var version = _loadVersion;
        var token = _pageCts.Token;
        var bound = new TemplateTransition(module, _editorRoot, generation);
        if (!await module.InvokeAsync<bool>("freezeTemplateInput", token, bound.Root, generation, revision))
            return;
        try
        {
            if (!IsCurrentSave(model, version) || !ReferenceEquals(journal, _templateJournal) ||
                generation != _editorWatchGeneration)
                return;
            var scope = await journal.ReadEditorScopeAsync(TemplateInputScope, token);
            if (!IsCurrentSave(model, version) || !ReferenceEquals(journal, _templateJournal) ||
                generation != _editorWatchGeneration || scope.HasOperation || scope.HasRaw || IsModified)
                return;
            await CloseApprovedTemplateAsync(journal, scope.Revision, revision);
        }
        finally { await ReleaseTemplateTransitionAsync(bound); }
    }

    private async Task CloseReadOnlyTemplateAsync(ClayDraftRegistration journal, bool unresolved, string? target,
        CancellationToken navigationToken)
    {
        var version = _loadVersion;
        bool Current() => !_disposed && !navigationToken.IsCancellationRequested && version == _loadVersion &&
            ReferenceEquals(journal, _templateJournal);
        _journalBusy = true;
        try
        {
            var scope = await journal.ReadEditorScopeAsync(TemplateInputScope, _pageCts.Token);
            if (!Current()) return;
            if ((unresolved || scope.HasRaw || scope.HasOperation || IsModified) &&
                await AskTemplateCancelChoiceAsync() != ClayEditorCancelChoice.CloseWithoutSaving)
                return;
            if (Current())
                await CloseApprovedTemplateAsync(journal, scope.Revision, null, target);
        }
        finally { _journalBusy = false; }
    }


    private async Task CloseApprovedTemplateAsync(ClayDraftRegistration journal, long draftRevision,
        long? editorRevision, string? target = null)
    {
        var version = _loadVersion;
        _pendingTemplateClose = (draftRevision, editorRevision);
        try
        {
            if (!await journal.CloseRamIfRevisionAsync(draftRevision, _pageCts.Token))
            {
                if (version == _loadVersion && ReferenceEquals(journal, _templateJournal))
                {
                    _pendingTemplateClose = null;
                    _templateOptions.ReadOnly = !_canEdit || _editorWatchGeneration is null;
                    _inputNotice = "Ввод изменился. Подтвердите закрытие ещё раз.";
                }
                return;
            }
            if (_disposed || version != _loadVersion || !ReferenceEquals(journal, _templateJournal))
                return;
            _pendingTemplateClose = null;
            _templateJournal = null;
            _templateRamOwner = null;
            await ReleaseTemplateJournalAsync();
            _templateInputDiscarded = true;
            _templateOptions.ReadOnly = true;
            if (_model is { } model) model.PhoneTemplate = _writeBaseline?.PhoneTemplate ?? string.Empty;
            await LeaveTemplateAsync(target, editorRevision);
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (!_disposed && version == _loadVersion)
            {
                _templateOptions.ReadOnly = true;
                _inputNotice = ReferenceEquals(journal, _templateJournal)
                    ? "Не удалось удалить временные данные. Повторите закрытие."
                    : "Временные данные удалены. Если окно осталось открытым, закройте его.";
            }
        }
    }

    private async Task LeaveTemplateAsync(string? target, long? revision = null)
    {
        _allowNavigation = true;
        try
        {
            if (target is not null)
                Navigation.NavigateTo(target);
            else if (revision is { } expected && _templateTextModule is { } module && _editorWatchGeneration is { } generation)
            {
                if (!await module.InvokeAsync<bool>("closeTemplateIfCurrent", _pageCts.Token, _editorRoot, generation, expected))
                    _allowNavigation = false;
            }
            else
                await JS.InvokeVoidAsync("clayModelEquipmentForm.close");
        }
        catch
        {
            _allowNavigation = false;
            throw;
        }
    }


    private async Task ReleaseTemplateTransitionAfterRenderAsync()
    {
        if (_releaseTransitionAfterRender is not { } bound)
            return;
        _releaseTransitionAfterRender = null;
        await ReleaseTemplateTransitionAsync(bound);
    }

    private static async Task ReleaseTemplateTransitionAsync(TemplateTransition bound)
    {
        try { await bound.Module.InvokeVoidAsync("releaseTemplateInput", bound.Root, bound.Generation); }
        catch (Exception ex) when (IsJournalError(ex)) { }
    }

    private sealed record TemplateTransition(IJSObjectReference Module, ElementReference Root, string Generation);
}
