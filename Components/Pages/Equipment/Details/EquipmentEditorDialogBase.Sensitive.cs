using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Проверяет только метаданные нативного ввода секретов при отмене редактора.</summary>
public abstract partial class EquipmentEditorDialogBase<TDraft>
{
    [Inject] private IJSRuntime SensitiveJs { get; set; } = null!;

    private IJSObjectReference? _sensitiveModule;
    private ElementReference _watchedSensitiveRoot;
    private string? _sensitiveGeneration;
    private long? _releaseSensitiveAfterRender;

    /// <summary>Стабильный контейнер секретных полей текущего редактора.</summary>
    protected virtual ElementReference? SensitiveCancellationRoot => null;

    private sealed record SensitiveInputState(long Revision, bool Dirty, bool Composing);

    private async Task UpdateSensitiveWatchAsync()
    {
        if (_disposed || EditorContext?.Cancellation is null || SensitiveCancellationRoot is not { } root)
            return;
        if (_releaseSensitiveAfterRender is not null)
        {
            _releaseSensitiveAfterRender = null;
            await ResetSensitiveWatchAsync();
        }
        if (_sensitiveGeneration is not null)
            return;

        try
        {
            var module = await SensitiveJs.InvokeAsync<IJSObjectReference>("import",
                "./_content/Clayzor.Lib.Web.Controls/js/clayTransientSensitiveInputs.js");
            if (module is null)
                return;
            var generation = Guid.NewGuid().ToString("N");
            if (_disposed || !await module.InvokeAsync<bool>("watchTransientSensitiveInputs", root, generation))
            {
                await module.DisposeAsync();
                return;
            }
            if (_disposed)
            {
                await module.InvokeVoidAsync("unwatchTransientSensitiveInputs", root, generation);
                await module.DisposeAsync();
                return;
            }
            _sensitiveModule = module;
            _watchedSensitiveRoot = root;
            _sensitiveGeneration = generation;
        }
        catch (Exception error) when (IsRawRestoreFailure(error)) { }
    }

    private async Task<SensitiveInputState?> ReadSensitiveInputStateAsync() =>
        _sensitiveModule is { } module
            ? await module.InvokeAsync<SensitiveInputState?>("readTransientSensitiveInputs", _watchedSensitiveRoot, _sensitiveGeneration)
            : null;

    private async Task<long?> FreezeSensitiveInputAsync(long revision) =>
        _sensitiveModule is { } module
            ? await module.InvokeAsync<long?>("freezeTransientSensitiveInputs", _watchedSensitiveRoot, _sensitiveGeneration, revision)
            : null;

    private async Task<bool> CheckSensitiveLeaseAsync(long? lease)
    {
        if (lease is null) return !SensitiveCancellationRoot.HasValue;
        try
        {
            return _sensitiveModule is { } module && await module.InvokeAsync<bool>(
                "transientSensitiveFreezeIsCurrent", _watchedSensitiveRoot, _sensitiveGeneration, lease.Value);
        }
        catch (Exception error) when (IsRawRestoreFailure(error)) { return false; }
    }

    private async Task ReleaseSensitiveInputAsync(long lease)
    {
        try
        {
            if (_sensitiveModule is { } module)
                await module.InvokeVoidAsync("releaseTransientSensitiveInputs", _watchedSensitiveRoot, _sensitiveGeneration, lease);
        }
        catch (Exception error) when (IsRawRestoreFailure(error)) { }
    }

    private async Task ResetSensitiveWatchAsync()
    {
        var module = _sensitiveModule;
        var generation = _sensitiveGeneration;
        _sensitiveModule = null;
        _sensitiveGeneration = null;
        if (module is null) return;
        try
        {
            await module.InvokeVoidAsync("unwatchTransientSensitiveInputs", _watchedSensitiveRoot, generation);
            await module.DisposeAsync();
        }
        catch (Exception error) when (IsRawRestoreFailure(error)) { }
    }
}
