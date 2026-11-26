using System.Windows.Markup;
using FanMaxxxing.Core;

namespace FanMaxxxing.UI;

/// <summary>Texto traducido en XAML: <c>Text="{loc:T main.duration}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
