using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

internal sealed class DiskSpaceWarningDialog:Window
{
    internal DiskSpaceWarningDialog(Window owner,string description)
    {
        Owner=owner;Name="DiskSpaceWarningDialog";
        Title=ErrorMessages.Text("Проверьте свободное место","Check free disk space");
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Width=600;MinWidth=420;SizeToContent=SizeToContent.Height;
        WindowThemeController.Inherit(this,owner);
        NameScope.SetNameScope(this,new NameScope());
        var root=new Grid{Margin=new Thickness(18)};
        root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var explanation=new StackPanel();
        explanation.Children.Add(new TextBlock{Text=ErrorMessages.Text("На диске мало места для этой загрузки.",
            "The disk may not have enough room for this download."),FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        var detail=new TextBlock{Text=description,TextWrapping=TextWrapping.Wrap};
        detail.Name="DiskSpaceWarningDetails";RegisterName(detail.Name,detail);
        explanation.Children.Add(detail);
        explanation.Children.Add(new TextBlock{Text=ErrorMessages.Text(
            "Оценка не гарантирует итоговый размер. Для временных потоков и обработки учтён запас; неизвестные размеры в сумму не входят. Можно освободить место и отменить запуск либо продолжить явно.",
            "The estimate is not a size guarantee. Temporary streams and processing have an allowance; unknown sizes are not included. Free space and cancel this start, or explicitly continue."),
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)});
        var scroll=new ScrollViewer{Name="DiskSpaceWarningScroll",Content=explanation,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        RegisterName(scroll.Name,scroll);root.Children.Add(scroll);
        var buttons=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right};
        var cancel=new Button{Name="CancelDiskSpaceStartButton",Content=ErrorMessages.Text("Отмена","Cancel"),IsCancel=true,IsDefault=true};
        var proceed=new Button{Name="ContinueDiskSpaceStartButton",Content=ErrorMessages.Text("Продолжить","Continue")};
        RegisterName(cancel.Name,cancel);RegisterName(proceed.Name,proceed);
        cancel.Click+=(_,_)=>{DialogResult=false;};proceed.Click+=(_,_)=>{DialogResult=true;};
        buttons.Children.Add(cancel);buttons.Children.Add(proceed);Grid.SetRow(buttons,1);root.Children.Add(buttons);
        Content=root;
        OwnedWindowLayout.Attach(this,owner);
    }
}
