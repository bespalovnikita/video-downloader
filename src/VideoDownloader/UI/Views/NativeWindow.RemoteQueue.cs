using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private void OpenRemoteQueueManager()
    {
        if (_disposed || _closing || _appUpdateHandoff || RejectProfileBackupMutation()) return;
        RemoteDownloadConnection connection;
        try { connection = CaptureRemoteDownloadConnection(F<PasswordBox>("RemoteDownloadPasswordBox").Password); }
        catch (ArgumentException error)
        {
            T("RemoteDownloadSettingsStatus").Text = ErrorMessages.ForException(error);
            return;
        }

        var dialog = new Window
        {
            Title = ErrorMessages.Text("Файловый менеджер серверной очереди", "Server queue file manager"),
            Owner = _window, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 820, Height = 460, MinWidth = 480, MinHeight = 300, ShowInTaskbar = false
        };
        WindowThemeController.Inherit(dialog, _window);
        OwnedWindowLayout.Attach(dialog, _window);

        var panel = new DockPanel { Margin = new Thickness(16) };
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var refresh = new Button { Content = ErrorMessages.Text("Обновить", "Refresh") };
        var first = new Button { Content = ErrorMessages.Text("К началу", "First page"), IsEnabled = false };
        var next = new Button { Content = ErrorMessages.Text("Далее", "Next page"), IsEnabled = false };
        var remove = new Button { IsEnabled = false };
        foreach (var button in new[] { refresh, first, next, remove })
        {
            button.Margin = new Thickness(0, 0, 6, 4);
            toolbar.Children.Add(button);
        }
        DockPanel.SetDock(toolbar, Dock.Top);
        panel.Children.Add(toolbar);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(status);
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow
        };
        AutomationProperties.SetName(grid, dialog.Title);
        AddColumn("Файл или источник", "File or source", nameof(RemoteQueueRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn("Состояние", "Status", nameof(RemoteQueueRow.Status), DataGridLength.SizeToCells);
        AddColumn("Размер", "Size", nameof(RemoteQueueRow.Size), DataGridLength.SizeToCells);
        AddColumn("Доступен до", "Available until", nameof(RemoteQueueRow.Expires), DataGridLength.SizeToCells);
        panel.Children.Add(grid);
        dialog.Content = panel;

        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        string? current = null;
        string? following = null;
        bool busy = false;

        void AddColumn(string russian, string english, string property, DataGridLength width)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = ErrorMessages.Text(russian, english), Binding = new System.Windows.Data.Binding(property),
                Width = width, MinWidth = 90
            });
        }

        void UpdateButtons()
        {
            var selected = grid.SelectedItem as RemoteQueueRow;
            remove.Content = selected?.Item.State == "completed"
                ? ErrorMessages.Text("Удалить файл…", "Delete file…")
                : ErrorMessages.Text("Отменить задание…", "Cancel job…");
            remove.IsEnabled = !busy && selected is not null;
            refresh.IsEnabled = !busy;
            first.IsEnabled = !busy && current is not null;
            next.IsEnabled = !busy && following is not null;
        }

        async Task LoadAsync(string? cursor, RemoteQueueItem? toRemove = null)
        {
            if (busy || token.IsCancellationRequested) return;
            busy = true;
            UpdateButtons();
            status.Text = ErrorMessages.Text("Обращаемся к серверу…", "Contacting the server…");
            try
            {
                bool changed = false;
                if (toRemove is not null)
                {
                    try { await RemoteDownloadClient.RemoveQueueItemAsync(connection, toRemove, token); }
                    catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict) { changed = true; }
                }
                var page = await RemoteDownloadClient.ListQueueAsync(connection, cursor, token);
                if (page.Jobs.Length == 0 && cursor is not null)
                {
                    cursor = null;
                    page = await RemoteDownloadClient.ListQueueAsync(connection, null, token);
                }
                if (token.IsCancellationRequested) return;
                string? selectedId = (grid.SelectedItem as RemoteQueueRow)?.Item.Id;
                var rows = page.Jobs.Select(item => new RemoteQueueRow(item)).ToArray();
                grid.ItemsSource = rows;
                grid.SelectedItem = rows.FirstOrDefault(row => row.Item.Id == selectedId);
                current = cursor;
                following = page.Next;
                status.Text = changed ? ErrorMessages.Text("Файл больше недоступен.", "The file is no longer available.") :
                    rows.Length == 0 ? ErrorMessages.Text("Серверная очередь пуста.", "The server queue is empty.") : "";
                if (page.FreeDiskBytes is >= 0)
                {
                    string free = DisplayValueConverter.Bytes(page.FreeDiskBytes.Value);
                    if (status.Text.Length > 0) status.Text += " ";
                    status.Text += ErrorMessages.Text($"Свободно на VPS — {free}.", $"Free space on the VPS — {free}.");
                }
            }
            catch (Exception error) when (RemoteDownloadClient.IsRemoteFailure(error))
            {
                if (!token.IsCancellationRequested) status.Text = RemoteDownloadClient.DescribeConnectionFailure(error);
            }
            finally
            {
                busy = false;
                if (!token.IsCancellationRequested) UpdateButtons();
            }
        }

        grid.SelectionChanged += (_, _) => UpdateButtons();
        refresh.Click += async (_, _) => await LoadAsync(current);
        first.Click += async (_, _) => await LoadAsync(null);
        next.Click += async (_, _) => await LoadAsync(following);
        remove.Click += async (_, _) =>
        {
            if (busy || grid.SelectedItem is not RemoteQueueRow row) return;
            string question = row.Item.State == "completed"
                ? ErrorMessages.Text("Удалить готовую копию с сервера?", "Delete the completed copy from the server?")
                : ErrorMessages.Text("Отменить скачивание на сервере?", "Cancel the download on the server?");
            if (MessageBox.Show(dialog, $"{question}\n\n{row.Name}", dialog.Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                await LoadAsync(current, row.Item);
        };
        dialog.Loaded += async (_, _) =>
        {
            refresh.Focus();
            await LoadAsync(null);
        };
        dialog.Closed += (_, _) => lifetime.Cancel();
        UpdateButtons();
        dialog.ShowDialog();
    }

    private sealed class RemoteQueueRow(RemoteQueueItem item)
    {
        public RemoteQueueItem Item { get; } = item;
        public string Name => Item.Name is { Length: > 0 } name ? name : $"{Item.Source} [{Item.Id[..8]}]";
        public string Size => Item.Size is > 0 ? DisplayValueConverter.Bytes(Item.Size.Value) : "";
        public string Expires => Item.ExpiresAt is >= 0 and < 253402213200
            ? DateTimeOffset.FromUnixTimeSeconds((long)Item.ExpiresAt.Value).ToLocalTime().ToString("g") : "";
        public string Status => Item.State switch
        {
            "completed" => ErrorMessages.Text("Готово", "Ready"),
            "downloading" => ErrorMessages.Text("Скачивается", "Downloading") +
                (Item.Progress is >= 0 and <= 100 ? $" {Item.Progress.Value:0}%" : ""),
            _ => ErrorMessages.Text("В очереди", "Queued")
        };
    }
}
