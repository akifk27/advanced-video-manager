using System;
using System.Windows.Markup;

namespace avmsoftware;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocalizedExtension : MarkupExtension
{
    public string Tr { get; set; } = "";
    public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => AvmLanguage.Text(Tr, En);
}
