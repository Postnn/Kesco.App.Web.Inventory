using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class PhoneTemplateForm
{
    private bool _templateTransitionApplying;
    private bool _templateInputDiscarded;
    private TemplateTransition? _releaseTransitionAfterRender;

    private Task CancelAsync() => ChangeTemplateEditorAsync(refresh: false);

    private Task RefreshAsync() => ChangeTemplateEditorAsync(refresh: true);

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
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
        if (_templateAttempt is not null || _protectedSubmission is not null || _knownWrite ||
            _notSentAttempt is not null || _journalCandidates.Count > 0)
        {
            _inputNotice = "Сначала завершите сверку предыдущей операции. Ввод и попытка сохранены.";
            return;
        }

        var model = _model;
        if (model is null || _templateInputDiscarded || !_canEdit)
        {
            if (!refresh)
            {
                try { await LeaveTemplateAsync(target); }
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
            _editorWatchGeneration is not { } generation || _restoreTemplateRawRequested || _restoringTemplateRaw || _rawPending)
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
            if (scope.HasOperation)
            {
                _inputNotice = "Сначала завершите сверку предыдущей операции. Ввод и попытка сохранены.";
                return;
            }
            var dirty = IsModified || scope.HasRaw || _inputMissingOriginal ||
                !MatchesBrowserTemplateText(_writeBaseline?.PhoneTemplate, snapshot.Text);
            var choice = dirty ? await AskTemplateCancelChoiceAsync() : refresh
                ? ClayEditorCancelChoice.ResetAndStay : ClayEditorCancelChoice.CloseWithoutSaving;
            if (!Current() || choice == ClayEditorCancelChoice.Continue)
                return;

            EquipmentModel? fresh = null;
            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                fresh = await ReadCurrentTemplateForResetAsync(model, requireEdit: true);
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
            if (dirty || fresh is not null)
            {
                var replacement = fresh is null ? null : SerializeTemplateDraft(fresh.CaptureWriteBaseline(), fresh.PhoneTemplate);
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
            }

            if (fresh is not null)
            {
                _model = fresh;
                _writeBaseline = fresh.CaptureWriteBaseline();
                _releaseTransitionAfterRender = bound;
                frozen = false;
                return;
            }
            if (dirty)
            {
                _templateInputDiscarded = true;
                _templateOptions.ReadOnly = true;
                model.PhoneTemplate = _writeBaseline?.PhoneTemplate ?? string.Empty;
                _inputNotice = "Ввод отменён. Если окно осталось открытым, закройте или обновите его.";
                _releaseTransitionAfterRender = bound;
                frozen = false;
            }
            if (Current())
                await LeaveTemplateAsync(target, snapshot.Revision);
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

    private async Task<EquipmentModel?> ReadCurrentTemplateForResetAsync(EquipmentModel model, bool requireEdit)
    {
        var token = _pageCts.Token;
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
            var fresh = await ReadCurrentTemplateForResetAsync(model, requireEdit: false);
            if (!IsCurrentSave(model, version)) return;
            if (fresh is null)
            {
                _inputNotice = "Не удалось получить актуальный шаблон и подтвердить доступ.";
                return;
            }
            _model = fresh;
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

    private async Task LeaveTemplateAsync(string? target, long? revision = null)
    {
        if (target is null && revision is { } current)
        {
            await CloseAtEditorRevisionAsync(current);
            return;
        }
        _allowNavigation = true;
        try
        {
            if (target is not null)
                Navigation.NavigateTo(target);
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
