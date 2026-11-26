using Kesco.App.Web.Inventory.Services.PingDiagnostics;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.Data.SqlClient;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private const int EquipmentObservationSamples = 5;
    private const string EquipmentObservationStoppedMessage = "Наблюдение остановлено.";
    private static readonly TimeSpan EquipmentObservationInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EquipmentObservationLimit = TimeSpan.FromSeconds(45);
    private readonly ClayToolbarButtonOptions _observeEquipmentOptions = new() { Text = "Наблюдать доступность" };
    private readonly ClayToolbarButtonOptions _stopEquipmentObservationOptions = new() { Text = "Остановить наблюдение" };
    private bool _equipmentObserving;
    private readonly ClayToolbarButtonOptions _equipmentPingOptions = new() { Text = "Проверить доступность" };
    private CancellationTokenSource? _equipmentPingCancellation;
    private string? _equipmentPingStatus;

    private bool CanCheckEquipmentPing => !_disposed && _loaded && !_loading && !_saving &&
        Id > 0 && _model?.Id == Id && CurrentUser is not null && ShowNetworkName && !HideNetworkAndUps;

    private Task CheckEquipmentReachabilityAsync() => RunEquipmentPingAsync(observe: false);

    private Task ObserveEquipmentReachabilityAsync() => RunEquipmentPingAsync(observe: true);

    private void StopEquipmentObservation()
    {
        if (!_equipmentObserving || _equipmentPingCancellation is not { } request) return;
        _equipmentPingStatus = EquipmentObservationStoppedMessage;
        request.Cancel();
    }

    private async Task RunEquipmentPingAsync(bool observe)
    {
        if (!CanCheckEquipmentPing || _equipmentPingCancellation is not null || CurrentUser is not { } actor)
            return;

        var id = Id;
        var version = _operationVersion;
        var clock = Services.GetService<TimeProvider>() ?? TimeProvider.System;
        using var deadline = observe ? new CancellationTokenSource(EquipmentObservationLimit, clock) : null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token,
            deadline?.Token ?? CancellationToken.None);
        _equipmentPingCancellation = request;
        _equipmentObserving = observe;
        _equipmentPingStatus = null;

        try
        {
            var samples = observe ? EquipmentObservationSamples : 1;
            for (var sample = 0; sample < samples; sample++)
            {
                if (!await CheckAsync()) return;
                if (!observe) return;
                if (sample + 1 == samples)
                {
                    _equipmentPingStatus = $"Наблюдение завершено. {_equipmentPingStatus}";
                    return;
                }

                await InvokeAsync(StateHasChanged);
                await Task.Delay(EquipmentObservationInterval, clock, request.Token);
                if (!Current()) return;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested || !Current())
        {
        }
        catch (PingDiagnosticException error)
        {
            if (Current()) _equipmentPingStatus = error.Message;
        }
        catch (Exception error) when (error is SqlException or TimeoutException or InvalidOperationException)
        {
            if (Current()) _equipmentPingStatus = "Не удалось проверить доступность сохранённого узла.";
        }
        finally
        {
            if (ReferenceEquals(_equipmentPingCancellation, request))
            {
                if (deadline?.IsCancellationRequested == true && IsCurrent(version, id) && CanCheckEquipmentPing &&
                    _equipmentPingStatus != EquipmentObservationStoppedMessage)
                    _equipmentPingStatus = "Время наблюдения истекло.";
                _equipmentPingCancellation = null;
                _equipmentObserving = false;
            }
        }

        async Task<bool> CheckAsync()
        {
            var employee = await Service.GetCurrentUserAsync();
            if (!Current()) return false;
            if (employee?.Id != actor.Id)
            {
                _equipmentPingStatus = "Не удалось подтвердить текущего сотрудника для проверки.";
                return false;
            }

            var saved = await Service.GetAsync(id);
            if (!Current()) return false;
            if (saved?.Id != id)
            {
                _equipmentPingStatus = "Сохранённая запись оборудования недоступна.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(saved.NetworkName))
            {
                _equipmentPingStatus = "Сетевое имя не сохранено.";
                return false;
            }

            var diagnostics = Services.GetRequiredService<PingDiagnosticService>();
            var target = await diagnostics.ResolveAsync(saved.NetworkName, request.Token);
            if (!Current()) return false;
            var result = await diagnostics.AttemptAsync(target, request.Token);
            if (Current())
                _equipmentPingStatus = result.Success
                    ? $"Узел отвечает. Проверено сохранённое имя {target.Host}. {result.Text}"
                    : $"Доступность не подтверждена. Проверено сохранённое имя {target.Host}. {result.Text}";
            return Current();
        }

        bool Current() => !request.IsCancellationRequested && ReferenceEquals(_equipmentPingCancellation, request) &&
            IsCurrent(version, id) && CanCheckEquipmentPing && CurrentUser?.Id == actor.Id;
    }

    private void ResetEquipmentReachability()
    {
        var cancellation = _equipmentPingCancellation;
        _equipmentPingCancellation = null;
        _equipmentObserving = false;
        _equipmentPingStatus = null;
        cancellation?.Cancel();
    }
}
