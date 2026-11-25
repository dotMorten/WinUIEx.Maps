using WorkingWithMaps.Models;

namespace WorkingWithMaps.Templates;

public class MapItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; }
    public DataTemplate? SanFranTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        return (((Position)item).Address.Contains("San Francisco") ? SanFranTemplate : DefaultTemplate)
            ?? throw new InvalidOperationException("Configure both map item templates before using the selector.");
    }
}

