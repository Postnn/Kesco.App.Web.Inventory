using Clayzor.Lib.Web.Controls.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Операции точной области отмены вложенного теста.</summary>
public sealed record EquipmentUpsTestCancellation(
    Func<Task<ClayDraftEditorScopeState?>> ReadScope,
    Func<Task<EquipmentUpsTestDraft?>> ReadFresh,
    Func<long, EquipmentUpsTestDraft?, Func<bool>, Task<bool>> Replace);
