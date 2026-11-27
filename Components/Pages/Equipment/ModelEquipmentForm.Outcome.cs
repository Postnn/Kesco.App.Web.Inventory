using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class ModelEquipmentForm
{
    private const string DraftForm = "inventory.model-equipment";
    private readonly string _journalRoot = $"model-write-{Guid.NewGuid():N}";
    private ClayDraftRegistration? _journal;
    private Guid _draftInstance = Guid.NewGuid();
    private bool _journalBusy;
    private bool _lateInput;
    private int _journalEmployeeId;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private async Task InitializeJournalAsync(int id, int initialType, int version)
    {
        if (CurrentUser is not { } actor || !_canEdit)
            return;
        var entityKey = id > 0 ? id.ToString(CultureInfo.InvariantCulture) : $"new:{initialType.ToString(CultureInfo.InvariantCulture)}";
        ClayDraftRegistration? registration = null;
        try
        {
            var token = _pageCts.Token;
            bool Current() => !_disposed && !token.IsCancellationRequested &&
                _loadVersion == version && CurrentUser?.Id == actor.Id;
            registration = await Drafts.RegisterRamHandleAsync(DraftForm, entityKey,
                actor.Id.ToString(CultureInfo.InvariantCulture), 1, $".{_journalRoot}", _draftInstance, token);
            if (!Current())
            {
                await registration.DisposeAsync();
                return;
            }
            _journal = registration;
            _journalEmployeeId = actor.Id;
            _modelDraftClosed = false;
            await RestoreEditorInputAsync(registration, version);
        }
        finally
        {
            if (registration is not null && !ReferenceEquals(_journal, registration))
                await registration.DisposeAsync();
        }
    }

    private async Task ResetJournalAsync()
    {
        var registration = _journal;
        _journal = null;
        ResetEditorInput();
        _lateInput = false;
        if (registration is not null)
            await registration.DisposeAsync();
    }

    private static string EditableFingerprint(EquipmentModel model) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            model.EquipmentTypeId, model.ResourceId, model.Name, model.PartNumber, model.Cpu,
            model.MonitorSize, model.MemoryTypeId, model.MemoryBanks, model.IsMultimedia,
            model.NetworkInterfaces, model.NetworkName, model.Driver, model.LifeTime, model.Description,
        })));

    private static bool IsJournalError(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or JsonException or
            Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException or TimeoutException;
}
