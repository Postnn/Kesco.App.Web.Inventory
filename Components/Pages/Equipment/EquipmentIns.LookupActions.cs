using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentIns
{
    private const string ModelSearchAlias = "URI_model_search";
    private const string ModelViewAlias = "URI_model_form";
    private const string LocationSearchAlias = "URI_location_search";
    private int _locationChoiceVersion;

    private bool CanApplyLookup => _loaded && _canEdit && !_loading && !_saving && !_disposed &&
        !_creationUncertain && _createdEquipmentId is null;

    private Func<ClaySelectAdvancedSearchContext, Task<ClaySelectExternalResult<EquipmentModel, int?>?>>? ModelAdvancedSearch =>
        ResolveLookupUri(ModelSearchAlias) is not null ? SearchModelAsync : null;

    private Func<ClaySelectAdvancedSearchContext, Task<ClaySelectExternalResult<LocationLookup, int?>?>>? LocationAdvancedSearch =>
        ResolveLookupUri(LocationSearchAlias) is not null ? SearchLocationAsync : null;

    private Func<EquipmentModel, Task>? ModelViewItem =>
        ResolveLookupUri(ModelViewAlias) is not null ? model => OpenLookupViewAsync(ModelViewAlias, model.Id) : null;

    private Func<LocationLookup, Task>? LocationViewItem =>
        ResolveLookupUri(LocationSearchAlias) is not null ? location => OpenLookupViewAsync(LocationSearchAlias, location.Id) : null;

    private Uri? ResolveLookupUri(string alias)
    {
        var configured = Configuration[alias] ?? WebConfigExtensions.ReadAppSettingFromWebConfig(alias);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (!Uri.TryCreate(Navigation.BaseUri, UriKind.Absolute, out var appBase) ||
            !Uri.TryCreate(appBase, configured.Trim(), out var url) ||
            url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0)
            return null;
        return url;
    }

    private async Task OpenLookupViewAsync(string alias, int id)
    {
        if (id <= 0 || _disposed || ResolveLookupUri(alias) is not { } address) return;
        var url = Navigation.GetUriWithQueryParameters(address.AbsoluteUri, new Dictionary<string, object?> { ["id"] = id });
        await JS.InvokeVoidAsync("clayOpen", _pageCts.Token, url, "_blank");
    }

    private Task<ClaySelectExternalResult<EquipmentModel, int?>?> SearchModelAsync(ClaySelectAdvancedSearchContext search)
    {
        var selection = _selectionVersion;
        var typeId = ModelSource.Filter.EquipmentTypeId;
        return SearchLookupAsync(ModelSearchAlias, search, typeId,
            () => selection == _selectionVersion && typeId == ModelSource.Filter.EquipmentTypeId,
            ModelSource.GetByIdAsync,
            item => typeId is null || item.EquipmentTypeId == typeId);
    }

    private Task<ClaySelectExternalResult<LocationLookup, int?>?> SearchLocationAsync(ClaySelectAdvancedSearchContext search)
    {
        var selection = _locationChoiceVersion;
        return SearchLookupAsync(LocationSearchAlias, search, null, () => selection == _locationChoiceVersion,
            LocationSource.GetByIdAsync, _ => true);
    }

    private async Task<ClaySelectExternalResult<TItem, int?>?> SearchLookupAsync<TItem>(
        string alias, ClaySelectAdvancedSearchContext search, int? typeId, Func<bool> sameSelection,
        Func<int?, CancellationToken, Task<TItem?>> resolve, Func<TItem, bool> inScope)
        where TItem : class, ILookupEntity
    {
        if (!CanApplyLookup || ResolveLookupUri(alias) is not { } address || CurrentUser is not { } openedBy)
            return null;

        var values = new Dictionary<string, object?> { ["search"] = search.SearchText };
        if (alias == ModelSearchAlias) values["codetype"] = typeId;
        var url = Navigation.GetUriWithQueryParameters(address.AbsoluteUri, values);
        var generation = _operationVersion;
        var model = _model;
        var ct = _pageCts.Token;
        bool Current() => IsCurrent(generation) && CanApplyLookup && ReferenceEquals(_model, model) &&
            CurrentUser?.Id == openedBy.Id && sameSelection();

        try
        {
            await Services.GetRequiredService<ClaySelectAdvancedSearchService>()
                .OpenAcknowledgedAsync<TItem, int?>(url, async (result, token) =>
                {
                    if (token.IsCancellationRequested || !Current()) return ClaySelectReturnDecision.Stale;
                    if (result.Value is not > 0) return ClaySelectReturnDecision.Rejected;

                    try
                    {
                        var item = await resolve(result.Value, token);
                        if (token.IsCancellationRequested || !Current()) return ClaySelectReturnDecision.Stale;
                        if (item is null || item.Id != result.Value || !inScope(item))
                            return ClaySelectReturnDecision.Rejected;

                        var employee = await Service.GetCurrentUserAsync(await PrincipalAsync(), token);
                        if (token.IsCancellationRequested || !Current()) return ClaySelectReturnDecision.Stale;
                        if (employee is null || employee.Id != openedBy.Id ||
                            !employee.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
                            return ClaySelectReturnDecision.Rejected;

                        var decision = ClaySelectReturnDecision.Stale;
                        await InvokeAsync(async () =>
                        {
                            if (!token.IsCancellationRequested && Current())
                                decision = await search.ApplyAsync(ClaySelectExternalResult<TItem, int?>.FromItem(item), token);
                        });
                        return decision;
                    }
                    catch (Exception error) when (error is Microsoft.Data.SqlClient.SqlException or TimeoutException)
                    {
                        return token.IsCancellationRequested || !Current()
                            ? ClaySelectReturnDecision.Stale
                            : ClaySelectReturnDecision.Rejected;
                    }
                }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is JSException or InvalidOperationException or ArgumentException)
        {
            if (Current()) _validationMessage = "Не удалось завершить расширенный поиск.";
        }
        return null;
    }
}
