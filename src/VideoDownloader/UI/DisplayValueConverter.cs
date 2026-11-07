using System.Globalization;
using System.Windows.Data;

namespace VideoDownloader.Native;

// Presentation only: persisted option identifiers and worker progress strings
// remain compatible with existing checkpoints and parsers.
public sealed class DisplayValueConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture) => Display(value?.ToString()??"");
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture) => System.Windows.Data.Binding.DoNothing;

    internal static string Display(string value)
    {
        if(!ErrorMessages.IsRussian)return value;
        return value switch
        {
            "Best"=>"Лучшее",
            "Auto"=>"Авто",
            "Unlimited"=>"Без ограничений",
            _=>value.Replace("TiB","ТиБ",StringComparison.Ordinal).Replace("GiB","ГиБ",StringComparison.Ordinal)
                .Replace("MiB","МиБ",StringComparison.Ordinal).Replace("KiB","КиБ",StringComparison.Ordinal)
                .Replace(" TB"," ТБ",StringComparison.Ordinal)
                .Replace(" GB"," ГБ",StringComparison.Ordinal).Replace(" MB"," МБ",StringComparison.Ordinal)
                .Replace(" KB"," КБ",StringComparison.Ordinal).Replace(" B"," Б",StringComparison.Ordinal)
                .Replace("/s","/с",StringComparison.Ordinal)
        };
    }

    internal static string Bytes(long value)=>Display(DownloadFormat.Bytes(value));
    internal static string Speed(double value)=>Display(DownloadFormat.Speed(value));

    internal static string OptionId(string value) => value switch
    {
        "Лучшее"=>"Best", "Авто"=>"Auto", "Без ограничений"=>"Unlimited", _=>value
    };
}
