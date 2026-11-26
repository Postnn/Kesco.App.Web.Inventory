using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Default;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon;

public partial class Tel
{
    private FinderEquipmentContext EquipmentFinderContext() => new(_model?.PhoneTypeId, _model?.StationId,
        _model?.SocketId, _model?.EmployeeId, null);

    private async Task<ClaySelectExternalResult<PhoneEquipmentLookup, PhoneEquipmentKey>?> OpenEquipmentFinderAsync(ClaySelectAdvancedSearchContext search)
    {
        if (!_canEdit || !_availability.Equipment.IsEditable || _saving || _model is null) return null;
        var model = _model;
        var snapshot = EquipmentFinderContext();
        var excluded = _relations.Equipment.Select(item => item.EquipmentId).Distinct().Order().ToArray();
        var address = PhoneFinderRoutes.Build(Configuration["URI_equipment_finder"] ?? "Default/Finder", new Dictionary<string, string?>
        {
            ["teltype"] = PhoneFinderRoutes.Id(snapshot.PhoneTypeId), ["station"] = PhoneFinderRoutes.Id(snapshot.StationId),
            ["socket"] = PhoneFinderRoutes.Id(snapshot.SocketId), ["employee"] = PhoneFinderRoutes.Id(snapshot.EmployeeId),
            ["telid"] = PhoneFinderRoutes.Id(snapshot.PhoneId),
        }, excluded, search.SearchText ?? "");
        var ct = _pageCts.Token;
        bool Current() => !ct.IsCancellationRequested && !_disposed && !_saving && _canEdit &&
            _availability.Equipment.IsEditable && ReferenceEquals(model, _model) && snapshot == EquipmentFinderContext() &&
            excluded.SequenceEqual(_relations.Equipment.Select(item => item.EquipmentId).Distinct().Order());
        try
        {
            await Services.GetRequiredService<ClaySelectAdvancedSearchService>()
                .OpenAcknowledgedAsync<PhoneEquipmentLookup, PhoneEquipmentKey>(
                    Navigation.ToAbsoluteUri(address).AbsoluteUri, async (result, token) =>
                    {
                        if (!Current() || token.IsCancellationRequested) return ClaySelectReturnDecision.Stale;
                        PhoneEquipmentLookup? item;
                        try
                        {
                            item = result.Value is { } key
                                ? await PhoneFinderData.ResolveEquipmentAsync(
                                    Services.GetRequiredService<DbManager>(), snapshot, excluded, key, token)
                                : null;
                        }
                        catch (Exception error) when (error is Microsoft.Data.SqlClient.SqlException or TimeoutException)
                        {
                            if (!Current() || token.IsCancellationRequested) return ClaySelectReturnDecision.Stale;
                            _warningMessage = "Не удалось проверить оборудование. Повторите выбор в окне поиска.";
                            StateHasChanged();
                            return ClaySelectReturnDecision.Rejected;
                        }

                        var decision = ClaySelectReturnDecision.Stale;
                        await InvokeAsync(() =>
                        {
                            if (!Current() || token.IsCancellationRequested) return;
                            if (item is null)
                            {
                                _warningMessage = "Выбранное оборудование больше недоступно. Повторите поиск.";
                                decision = ClaySelectReturnDecision.Rejected;
                            }
                            else
                            {
                                // Выбор добавляет строку и пересоздаёт ClaySelect. Подтверждаем добавленную связь.
                                OnEquipmentSelected(item);
                                decision = _relations.Equipment.Any(link => link.EquipmentId == item.EquipmentId &&
                                    link.ConnectionEquipmentId == item.ConnectionEquipmentId)
                                    ? ClaySelectReturnDecision.Accepted : ClaySelectReturnDecision.Indeterminate;
                            }
                            StateHasChanged();
                        });
                        return decision;
                    }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is JSException or System.Text.Json.JsonException or ArgumentException or
            InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed) _warningMessage = "Не удалось завершить расширенный выбор оборудования.";
        }
        return null;
    }
}
