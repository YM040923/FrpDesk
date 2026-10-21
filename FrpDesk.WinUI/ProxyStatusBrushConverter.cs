using FrpDesk.Models;
using Microsoft.UI.Xaml.Data;

namespace FrpDesk;

/// <summary>隧道运行态到颜色令牌。返回的是 App.xaml 里那一份共享笔刷，所以主题和高对比度切换会跟着变。</summary>
public sealed class ProxyStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is TunnelProxyStatus status
            ? status switch
            {
                TunnelProxyStatus.Running => "SuccessBrush",
                TunnelProxyStatus.Failed => "AlertBrush",
                _ => "MutedBrush"
            }
            : "MutedBrush";
        return (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
