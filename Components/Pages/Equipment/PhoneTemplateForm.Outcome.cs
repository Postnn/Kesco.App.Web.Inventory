using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class PhoneTemplateForm
{
    private const string TemplateDraftForm = "inventory.phone-template";
    private readonly string _journalRoot = $"template-write-{Guid.NewGuid():N}";
    private ClayDraftRegistration? _templateJournal;
    private (int ModelId, int EmployeeId, Guid Instance)? _templateRamOwner;
    private bool _journalReady;
    private bool _journalBusy;
    private int _journalEmployeeId;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool TemplateWriteBlocked => !_journalReady || _journalBusy || _pendingTemplateClose is not null ||
        _templateConflictNeedsReview || TemplateInputBlocked;

    private async Task InitializeTemplateJournalAsync(int id, int employeeId, int version)
    {
        ClayDraftRegistration? registration = null;
        try
        {
            var token = _pageCts.Token;
            var entityKey = id.ToString(CultureInfo.InvariantCulture);
            var userKey = employeeId.ToString(CultureInfo.InvariantCulture);
            bool Current() => !_disposed && !token.IsCancellationRequested && version == _loadVersion &&
                id == Id && CurrentUser?.Id == employeeId;
            if (_templateRamOwner is not { } owner || owner.ModelId != id || owner.EmployeeId != employeeId)
            {
                owner = (id, employeeId, Guid.NewGuid());
                _templateRamOwner = owner;
            }
            registration = await Drafts.RegisterRamHandleAsync(TemplateDraftForm, entityKey, userKey,
                1, $".{_journalRoot}", owner.Instance, token, MaximumTemplateRestoreBytes);
            if (!Current())
            {
                await registration.DisposeAsync();
                return;
            }
            _templateJournal = registration;
            _journalEmployeeId = employeeId;
            if (_model is { } model)
                await RestoreTemplateInputAsync(registration, model, version);
            _journalReady = Current();
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            _journalReady = false;
            if (registration is not null)
                await registration.DisposeAsync();
            if (ReferenceEquals(_templateJournal, registration))
                _templateJournal = null;
        }
    }

    private async Task ReloadSavedTemplateAsync(EquipmentModel model, string submitted, int version)
    {
        if (TemplateInputBlocked || _templateJournal is not { } journal)
            throw new InvalidOperationException("Ввод требует завершения восстановления.");
        var observed = await EquipmentModel.GetForEditAsync(Db, model.Id, _pageCts.Token);
        if (!IsCurrentSave(model, version)) return;
        if (observed?.EquipmentTypeId != (int)ТипыОборудования.IP_телефон)
            throw new InvalidOperationException("Сохранённый шаблон больше недоступен.");

        var protectedState = await journal.ReadRebaseStateAsync(TemplateInputScope, _pageCts.Token);
        var local = await ReadLiveTemplateAsync(_pageCts.Token);
        if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal)) return;

        var keepLocal = !MatchesBrowserTemplateText(submitted, local) || protectedState.RawKeys.Count > 0;
        var exactLocal = MatchesBrowserTemplateText(model.PhoneTemplate, local) ? model.PhoneTemplate : local;
        if (keepLocal && observed.PhoneTemplate != submitted)
            throw new InvalidOperationException("Актуальный шаблон изменился. Введённый текст сохранён для сверки.");

        var baseline = observed.CaptureWriteBaseline();
        var rebased = keepLocal ? SerializeTemplateDraft(baseline, exactLocal) : null;
        var boundBeforeRebase = model.PhoneTemplate;
        if (rebased is not null && !await journal.RebaseScopeAsync(
            TemplateInputScope, protectedState.Revision, rebased, [], _pageCts.Token))
            throw new InvalidOperationException("Поздний ввод не подтверждён в защищённом черновике.");
        if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal)) return;

        _writeBaseline = baseline;
        _scheduledInput = rebased;
        model.ChangedAt = observed.ChangedAt;
        model.ChangedById = observed.ChangedById;
        model.ChangedByName = observed.ChangedByName;
        if (model.PhoneTemplate == boundBeforeRebase)
        {
            ++_templateInputEpoch;
            var nextText = keepLocal ? exactLocal : observed.PhoneTemplate;
            if (!MatchesBrowserTemplateText(nextText, local))
                ++_templateEditorRenderKey;
            model.PhoneTemplate = nextText;
        }
        await PersistTemplateInputAsync();
        if (_inputSaveFailed)
            throw new InvalidOperationException("Не удалось подтвердить поздний ввод.");
    }

    private async ValueTask ReleaseTemplateJournalAsync()
    {
        CancelTemplateConflictReview();
        var journal = _templateJournal;
        _templateJournal = null;
        _pendingTemplateClose = null;
        _journalEmployeeId = 0;
        _protectedSubmission = null;
        _scheduledInput = null;
        _inputNotice = null;
        _restoreTemplateRawRequested = _rawPending = _inputSaveFailed = _inputMissingOriginal = false;
        _rawRestoreFlight = null;
        _journalReady = false;
        if (journal is not null)
            await journal.DisposeAsync();
    }

    private static bool IsJournalError(Exception error) => error is JSException or JSDisconnectedException or
        Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException or
        JsonException or OperationCanceledException or TimeoutException;
}
