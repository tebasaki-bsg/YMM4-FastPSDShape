using System.Reflection;
using System.Windows;
using YukkuriMovieMaker.Commons;

namespace YMM4FastPsdShape.UI;

/// <summary>図形アイテムの「レイヤー」欄（プリセットとツリー）</summary>
internal sealed class LayerTreeEditorAttribute : PropertyEditorAttribute
{
    public LayerTreeEditorAttribute()
    {
        PropertyEditorSize = PropertyEditorSize.FullWidth;
    }

    public override FrameworkElement Create() => new LayerTreeEditor();

    public override void SetBindings(FrameworkElement control, object item, object propertyOwner, PropertyInfo propertyInfo)
    {
        ShapeLog.Info($"SetBindings レイヤー欄 item={ShapeLog.Id(item)} owner={ShapeLog.Id(propertyOwner)}");
        if (control is not LayerTreeEditor editor) return;
        if (propertyOwner is FastPsdShapeParameter p) editor.Attach(p);
        else
        {
            editor.Detach();
            ShapeLog.Error($"レイヤー欄の編集対象が想定外の型です: {propertyOwner?.GetType().FullName}");
        }
    }

    public override void ClearBindings(FrameworkElement control)
    {
        ShapeLog.Info($"ClearBindings レイヤー欄 {ShapeLog.Id(control)}");
        if (control is LayerTreeEditor editor) editor.Detach();
    }
}
