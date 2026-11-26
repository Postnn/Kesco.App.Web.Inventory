using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class PhoneTemplateForm
{
    private static readonly ClaySelectOptions TemplateAttributeOptions = new()
    {
        Label = "Типы аттрибутов",
        Clearable = false,
    };
    private static readonly ClayToolbarButtonOptions FindAttributeOptions = new() { Text = "Найти тип аттрибута в шаблоне" };
    private static readonly ClayToolbarButtonOptions InsertAttributeOptions = new() { Text = "Вставить тип аттрибута в шаблон" };
    private static readonly ClayToolbarButtonOptions ClearAttributeOptions = new() { Text = "Очистить значение поле Типы аттрибутов" };

    private PhoneTemplateAttributeType[] _templateAttributeTypes = [];
    private string? _templateAttributeType;
    private string? _templateToolsNotice;

    private bool CanUseTemplateAttribute => !_disposed && !_loading && !_saving && _canEdit &&
        _editorWatchGeneration is not null && _templateAttributeType is not null;

    private async Task LoadTemplateAttributeTypesAsync(EquipmentModel model, int version)
    {
        try
        {
            var types = await PhoneTemplateAttributeType.ReadAsync(Db, _pageCts.Token);
            if (IsCurrentSave(model, version))
                _templateAttributeTypes = types;
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrentSave(model, version))
                _templateToolsNotice = "Не удалось загрузить типы аттрибутов.";
        }
    }

    private Task SelectTemplateAttributeAsync(string? type)
    {
        _templateAttributeType = _templateAttributeTypes.Any(item => item.Type == type) ? type : null;
        _templateToolsNotice = null;
        return FindTemplateAttributeAsync();
    }

    private void ClearTemplateAttribute()
    {
        _templateAttributeType = null;
        _templateToolsNotice = null;
    }

    private Task FindTemplateAttributeAsync() => UseTemplateAttributeAsync(insert: false);

    private Task InsertTemplateAttributeAsync() => UseTemplateAttributeAsync(insert: true);

    private async Task UseTemplateAttributeAsync(bool insert)
    {
        if (!CanUseTemplateAttribute || (insert && TemplateWriteBlocked) || _templateTextModule is not { } module ||
            _editorWatchGeneration is not { } generation || _templateAttributeType is not { } type)
            return;

        try
        {
            var changed = await module.InvokeAsync<bool>(insert ? "insertTemplateAttribute" : "findTemplateAttribute",
                _pageCts.Token, _editorRoot, generation, type);
            if (generation == _editorWatchGeneration && type == _templateAttributeType)
                _templateToolsNotice = changed ? null : insert
                    ? "Не удалось вставить тип аттрибута в шаблон."
                    : $"Тип аттрибута '{type}' не найден в шаблоне!";
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (generation == _editorWatchGeneration)
                _templateToolsNotice = "Редактор шаблона недоступен.";
        }
    }
}
