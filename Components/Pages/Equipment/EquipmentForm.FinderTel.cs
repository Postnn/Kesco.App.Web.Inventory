using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Default;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private FinderTelContext PhoneFinderContext() => new(_model?.LocationId, _model?.EmployeeId, _model?.EquipmentTypeId,
        _phones.FirstOrDefault(item => item.IsDefault)?.PhoneId, _model?.Id is > 0 ? _model.Id : null);

    private async Task<ClaySelectExternalResult<PhoneNumberLookup, int?>?> OpenPhoneFinderAsync(ClaySelectAdvancedSearchContext search)
    {
        if (!CanEditPhones || _saving || _model is null) return null;
        var model = _model;
        var snapshot = PhoneFinderContext();
        var excluded = _phones.Select(item => item.PhoneId).Distinct().Order().ToArray();
        var address = PhoneFinderRoutes.Build(Configuration["URI_tel_finder"] ?? "Default/FinderTel", new Dictionary<string, string?>
        {
            ["fLocation"] = PhoneFinderRoutes.Id(snapshot.LocationId), ["fEmployee"] = PhoneFinderRoutes.Id(snapshot.EmployeeId),
            ["fTypeEquipment"] = PhoneFinderRoutes.Id(snapshot.EquipmentTypeId), ["fMainNumber"] = PhoneFinderRoutes.Id(snapshot.PhoneIdBase),
            ["fEquipment"] = PhoneFinderRoutes.Id(snapshot.EquipmentId),
        }, excluded, search.SearchText ?? "");
        var ct = _pageCts.Token;
        bool Current() => !_disposed && !_saving && CanEditPhones && ReferenceEquals(model, _model) && snapshot == PhoneFinderContext() &&
            excluded.SequenceEqual(_phones.Select(item => item.PhoneId).Distinct().Order());
        try
        {
            var window = Services.GetRequiredService<ClaySelectAdvancedSearchService>();
            var db = Services.GetRequiredService<DbManager>();
            var result = await window.OpenAsync<PhoneNumberLookup, int?>(Navigation.ToAbsoluteUri(address).AbsoluteUri, ct);
            if (result?.Value is not int id) return null;
            if (Current() && await PhoneFinderData.ResolveTelAsync(db, snapshot, excluded, id, ct) is { } phone && Current())
                return ClaySelectExternalResult<PhoneNumberLookup, int?>.FromItem(phone);
            if (!_disposed) _validationMessage = "Контекст оборудования или доступность номера изменились. Откройте поиск ещё раз.";
        }
        catch (Exception error) when (error is JSException or System.Text.Json.JsonException or ArgumentException or
            InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed) _validationMessage = "Не удалось завершить расширенный выбор телефонного номера.";
        }
        return null;
    }
}
