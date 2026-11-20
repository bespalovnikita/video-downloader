using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace VideoDownloader.Native;

// A size change only alters presentation. The existing collection view keeps
// its sort, filters, current row and complete data for copy/export/details.
internal static class ResponsiveDataGridColumns
{
    internal static void Configure(DataGrid grid,double wideWidth,string compactHelp,params DataGridColumn[] detailColumns)
    {
        string originalHelp=AutomationProperties.GetHelpText(grid);
        object? originalTip=grid.ToolTip;
        void Refresh()
        {
            if(!double.IsFinite(grid.ActualWidth) || grid.ActualWidth<=0)return;
            bool wide=grid.ActualWidth>=wideWidth;
            foreach(var column in detailColumns)column.Visibility=wide?Visibility.Visible:Visibility.Collapsed;
            AutomationProperties.SetHelpText(grid,wide?originalHelp:compactHelp);
            grid.ToolTip=wide?originalTip:compactHelp;
        }
        grid.Loaded+=(_,_)=>Refresh();
        grid.SizeChanged+=(_,_)=>Refresh();
        Refresh();
    }
}
