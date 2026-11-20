using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.Native;

// A size change only alters presentation. The existing collection view keeps
// its sort, filters, current row and complete data for copy/export/details.
internal static class ResponsiveDataGridColumns
{
    internal static void Configure(DataGrid grid,double wideWidth,string compactHelp,params DataGridColumn[] detailColumns)
    {
        string originalHelp=AutomationProperties.GetHelpText(grid);
        object? originalTip=grid.ToolTip;
        bool refreshPending=false;
        void Refresh()
        {
            if(!double.IsFinite(grid.ActualWidth) || grid.ActualWidth<=0)return;
            bool wide=grid.ActualWidth>=wideWidth;
            foreach(var column in detailColumns)column.Visibility=wide?Visibility.Visible:Visibility.Collapsed;
            AutomationProperties.SetHelpText(grid,wide?originalHelp:compactHelp);
            grid.ToolTip=wide?originalTip:compactHelp;
        }
        void ScheduleRefresh()
        {
            if(refreshPending || !grid.IsLoaded || grid.Dispatcher.HasShutdownStarted)return;
            refreshPending=true;
            // DataGrid accounts for a viewport resize at Loaded priority, while
            // changing column visibility schedules fresh widths at Render. Doing
            // that inside SizeChanged lets the later viewport callback subtract
            // the same shrink again. Finish that accounting first; visibility
            // then invalidates and reallocates the actual column widths normally.
            _=grid.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>
            {
                refreshPending=false;
                if(grid.IsLoaded)Refresh();
            }));
        }
        grid.Loaded+=(_,_)=>ScheduleRefresh();
        grid.SizeChanged+=(_,_)=>ScheduleRefresh();
        ScheduleRefresh();
    }
}
