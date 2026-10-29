using System.Text;
using System.Windows;
using Forms=System.Windows.Forms;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private readonly SemaphoreSlim _portableIngressGate=new(1,1);

    private void AddInputText(string text)
    {
        if(PortableQueueCodec.IsPortable(text))_ = ImportPortableTextAsync(text);
        else AddUrls(ParseUrls(text));
    }

    private void OpenPortableQueueMenu()
    {
        var menu=new ContextMenu();
        void Add(string label,Action action)
        {
            var item=new MenuItem{Header=label};item.Click+=(_,_)=>action();menu.Items.Add(item);
        }
        Add("Копировать очередь",()=>_ = ExportPortableQueueAsync(false));
        Add("Вставить очередь",()=>Paste(true));
        Add("Сохранить очередь в файл…",()=>_ = ExportPortableQueueAsync(true));
        Add("Открыть файл очереди…",()=>
        {
            using var picker=new Forms.OpenFileDialog{Title="Открыть очередь",Filter="Переносимая очередь|*.vdqueue|Все файлы|*.*"};
            if(picker.ShowDialog()==Forms.DialogResult.OK)_ = ImportPortableFileAsync(picker.FileName);
        });
        var button=F<Button>("PortableQueueButton");button.ContextMenu=menu;
        menu.PlacementTarget=button;menu.IsOpen=true;
    }

    private async Task ExportPortableQueueAsync(bool toFile)
    {
        if(_closing || _disposed)return;
        var entries=SessionState.Queue.Select(row=>new PortableQueueEntry(row.Url,row.Title)).ToArray();
        if(entries.Length==0)
        {
            T("ClipboardHint").Text="Очередь пуста.";return;
        }
        string? file=null;
        if(toFile)
        {
            using var picker=new Forms.SaveFileDialog{Title="Сохранить очередь",Filter="Переносимая очередь|*.vdqueue",DefaultExt="vdqueue",AddExtension=true,FileName="video-downloader.vdqueue",OverwritePrompt=true};
            if(picker.ShowDialog()!=Forms.DialogResult.OK)return;
            file=picker.FileName;
        }
        try
        {
            string text=await Task.Run(()=>PortableQueueCodec.Encode(entries));
            if(_closing || _disposed)return;
            if(file!=null)
            {
                await Task.Run(()=>WritePortableQueueFile(file,text));
                if(!_closing && !_disposed)T("ClipboardHint").Text=$"Очередь сохранена: {entries.Length} ссылок. На другом клиенте открой файл .vdqueue.";
            }
            else
            {
                if(text.Length>PortableQueueCodec.ClipboardTextLimit)
                {
                    MessageBox.Show(_window,"Очередь слишком велика для надёжного переноса через буфер обмена Android. Выбери «Сохранить очередь в файл…» и открой файл на другом клиенте.","Перенос очереди",MessageBoxButton.OK,MessageBoxImage.Information);
                    return;
                }
                Clipboard.SetText(text);
                T("ClipboardHint").Text=$"Скопировано {entries.Length} ссылок. На другом клиенте нажми «Вставить очередь» или Paste, затем Start.";
            }
        }
        catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            PortableQueueError(error);
        }
    }

    internal Task ImportPortableTextAsync(string text)=>ImportPortableAsync(()=>PortableQueueCodec.Decode(text));
    internal Task ImportPortableFileAsync(string file)=>ImportPortableAsync(()=>PortableQueueCodec.Decode(ReadPortableQueueFile(file)));

    private async Task ImportPortableAsync(Func<IReadOnlyList<PortableQueueEntry>> decode)
    {
        await _portableIngressGate.WaitAsync();
        try
        {
            if(_closing || _disposed)return;
            if(_checkpointReadOnly || _legacyEngineBlocked)
                throw new InvalidOperationException("Очередь недоступна для сохранения. Сначала устрани ошибку восстановления или заверши прежний движок.");
            var entries=await Task.Run(decode);
            if(_closing || _disposed)return;
            if(_checkpointReadOnly || _legacyEngineBlocked)
                throw new InvalidOperationException("Очередь недоступна для сохранения.");
            var added=SessionState.ImportPortable(entries,ReadOptions());
            Reindex();
            if(_checkpointReadOnly)throw new IOException("Не удалось сохранить импортированную очередь.");
            foreach(var row in added)RequestRowPreview(row);
            foreach(var row in SessionState.Queue)_preview.RefreshKnownTitle(row);
            T("ClipboardHint").Text=$"Добавлено {added.Count} из {entries.Count} ссылок; существующие строки сохранены. Загрузка начнётся по Start.";
        }
        catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            PortableQueueError(error);
        }
        finally { _portableIngressGate.Release(); }
    }

    private void PortableQueueError(Exception error)
    {
        if(_closing || _disposed)return;
        Log("Перенос очереди: "+error.Message);
        MessageBox.Show(_window,error.Message,"Перенос очереди",MessageBoxButton.OK,MessageBoxImage.Warning);
    }

    private static string ReadPortableQueueFile(string file)
    {
        if(new FileInfo(file).Length>PortableQueueCodec.MaxTextChars)
            throw new InvalidDataException("Файл очереди слишком велик.");
        using var reader=new StreamReader(file,new UTF8Encoding(false,true),false);
        var text=new StringBuilder();var buffer=new char[8192];
        while(true)
        {
            int count=reader.Read(buffer,0,buffer.Length);if(count==0)break;
            if(text.Length+count>PortableQueueCodec.MaxTextChars)throw new InvalidDataException("Файл очереди слишком велик.");
            text.Append(buffer,0,count);
        }
        return text.ToString();
    }

    private static void WritePortableQueueFile(string file,string text)
    {
        string full=Path.GetFullPath(file),parent=Path.GetDirectoryName(full)!;
        string temporary=Path.Combine(parent,".vdqueue-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            File.WriteAllText(temporary,text,new UTF8Encoding(false));
            File.Move(temporary,full,true);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
}
