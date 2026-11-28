using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private bool _remoteQueueManagerOpen;

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
        var prioritize = new Button { Content = ErrorMessages.Text("Поднять в очереди", "Move to front") };
        var pickup = new Button { Content = ErrorMessages.Text("Забрать сейчас", "Get now") };
        var receivedOnly = new CheckBox { Content = ErrorMessages.Text("Уже получено на ПК", "Already received on Windows"),
            Margin = new Thickness(6, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in new[] { refresh, first, next, prioritize, pickup, remove })
        {
            button.Margin = new Thickness(0, 0, 6, 4);
            toolbar.Children.Add(button);
        }
        toolbar.Children.Add(receivedOnly);
        DockPanel.SetDock(toolbar, Dock.Top);
        panel.Children.Add(toolbar);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(status);
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow
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
        DownloadItem? pickupQueueRow = null;

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
            var rows = grid.SelectedItems.OfType<RemoteQueueRow>().ToArray();
            var selected = rows.Length == 1 ? rows[0] : null;
            bool files = rows.Length > 0 && rows.All(row => row.HasFile);
            remove.Content = files && rows.Length > 1 ? ErrorMessages.Text("Удалить выбранные…", "Delete selected…") : files
                ? ErrorMessages.Text("Удалить файл…", "Delete file…")
                : ErrorMessages.Text("Отменить задание…", "Cancel job…");
            remove.IsEnabled = !busy && (files || selected is not null) && rows.All(row => row.CanRemove);
            remove.ToolTip = rows.Any(row => row.HasFile && !row.CanRemove)
                ? ErrorMessages.Text("Первый час хранения файл защищён от удаления.", "The file is protected during its first hour.") : null;
            ToolTipService.SetShowOnDisabled(remove, true);
            prioritize.IsEnabled = !busy && selected?.Item.State == "queued";
            pickup.IsEnabled = !busy && selected?.HasFile == true && SessionState.Coordinator?.IsRunning != true && !_startingSession;
            receivedOnly.IsEnabled = !busy;
            refresh.IsEnabled = !busy;
            first.IsEnabled = !busy && current is not null;
            next.IsEnabled = !busy && following is not null;
        }

        async Task LoadAsync(string? cursor, Func<Task<string>>? action = null)
        {
            if (busy || token.IsCancellationRequested) return;
            busy = true;
            UpdateButtons();
            status.Text = ErrorMessages.Text("Обращаемся к серверу…", "Contacting the server…");
            try
            {
                string message = action is null ? "" : await action();
                if (token.IsCancellationRequested) return;
                var page = await RemoteDownloadClient.ListQueueAsync(connection, cursor, token, receivedOnly.IsChecked == true);
                if (page.Jobs.Length == 0 && cursor is not null)
                {
                    cursor = null;
                    page = await RemoteDownloadClient.ListQueueAsync(connection, null, token, receivedOnly.IsChecked == true);
                }
                if (token.IsCancellationRequested) return;
                string? selectedId = (grid.SelectedItem as RemoteQueueRow)?.Item.Id;
                var rows = page.Jobs.Select(item => new RemoteQueueRow(item)).ToArray();
                grid.ItemsSource = rows;
                grid.SelectedItem = rows.FirstOrDefault(row => row.Item.Id == selectedId);
                current = cursor;
                following = page.Next;
                status.Text = message.Length > 0 ? message : rows.Length == 0
                    ? ErrorMessages.Text("Нет подходящих заданий.", "No matching jobs.") : "";
                if(page.UnclaimedBytes is >= 0 && page.UnclaimedLimitBytes is > 0)
                {
                    if(status.Text.Length>0)status.Text+=Environment.NewLine;
                    string used=(page.UnclaimedBytes.Value/1_000_000_000m).ToString("0.###");
                    status.Text+=ErrorMessages.Text($"Не получено на ПК — {used} из 15 ГБ.", $"Unclaimed on Windows — {used} of 15 GB.");
                }
                if (page.FreeDiskBytes is >= 0)
                {
                    string free = DisplayValueConverter.Bytes(page.FreeDiskBytes.Value);
                    if (status.Text.Length > 0) status.Text += " ";
                    status.Text += ErrorMessages.Text($"Свободно на VPS — {free}.", $"Free space on the VPS — {free}.");
                }
            }
            catch (Exception error) when (RemoteDownloadClient.IsRemoteFailure(error) ||
                error is UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                if (!token.IsCancellationRequested) status.Text = error is HttpRequestException or TimeoutException
                    ? RemoteDownloadClient.DescribeConnectionFailure(error) : ErrorMessages.ForException(error);
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
        receivedOnly.Checked += async (_, _) => await LoadAsync(null);
        receivedOnly.Unchecked += async (_, _) => await LoadAsync(null);
        prioritize.Click += async (_, _) =>
        {
            if (grid.SelectedItems.Count != 1 || grid.SelectedItem is not RemoteQueueRow { Item.State: "queued" } row) return;
            await LoadAsync(current, async () =>
            {
                await RemoteDownloadClient.RaiseQueuePriorityAsync(connection, row.Item, token);
                return ErrorMessages.Text("Задание поднято в очереди.", "The job moved to the front of the queue.");
            });
        };
        pickup.Click += async (_, _) =>
        {
            if (busy || grid.SelectedItems.Count != 1 || grid.SelectedItem is not RemoteQueueRow { HasFile: true } row ||
                SessionState.Coordinator?.IsRunning == true || _startingSession) return;
            await LoadAsync(current, async () =>
            {
                if (row.Item.State == "completed")
                {
                    var journal = new RemoteDownloadJournal(_storage, connection, Log);
                    await Task.Run(() => journal.Load(), token);
                    token.ThrowIfCancellationRequested();
                    var assignment = journal.FindById(row.Item.Id);
                    if (assignment is { ReceivedLocally: false })
                    {
                        pickupQueueRow = SessionState.Queue.FirstOrDefault(item => item.Url == assignment.Request.Url &&
                            item.CanResume && item.ResumeState is null && item.Status != "Completed");
                        if (pickupQueueRow is not null)
                        {
                            dialog.Close();
                            return "";
                        }
                    }
                }
                var options = ReadOptions();
                options.PinOutputDirectory();
                using var client = new RemoteDownloadClient(connection, _storage, Log);
                string path = await Task.Run(() => client.PickupAsync(row.Item, options.OutputDirectory,
                    progress => _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (busy && !token.IsCancellationRequested)
                            status.Text = ErrorMessages.Text($"Получение файла — {progress.Percent:0}%.", $"Receiving the file — {progress.Percent:0}%.");
                    }), token), token);
                _lifecycle.NotifyInformation("Video Downloader", ErrorMessages.Text(
                    $"Сохранено на ПК: {Path.GetFileName(path)}", $"Saved on Windows: {Path.GetFileName(path)}"));
                try
                {
                    using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await Task.Run(() => client.ConfirmPickupAsync(row.Item.Id, path, row.Item.Size!.Value,
                        acknowledgement.Token), acknowledgement.Token);
                }
                catch (Exception error) when (RemoteDownloadClient.IsRemoteFailure(error) || error is UnauthorizedAccessException)
                {
                    return ErrorMessages.Text($"Файл сохранён: {path}. Подтверждение серверу пока не отправлено.",
                        $"File saved: {path}. The receipt has not reached the server yet.");
                }
                return ErrorMessages.Text($"Файл сохранён: {path}", $"File saved: {path}");
            });
        };
        remove.Click += async (_, _) =>
        {
            var rows = grid.SelectedItems.OfType<RemoteQueueRow>().ToArray();
            if (busy || rows.Length == 0 || rows.Any(row => !row.CanRemove) ||
                rows.Length > 1 && rows.Any(row => !row.HasFile)) return;
            string question;
            if (rows[0].HasFile)
            {
                string space = DisplayValueConverter.Bytes(rows.Sum(row => Math.Clamp(row.Item.Size ?? 0, 0, 15_000_000_000L)));
                question = ErrorMessages.Text(
                    $"Удалить выбранные копии с сервера ({rows.Length})? Освободится до {space}.",
                    $"Delete the selected server copies ({rows.Length})? Up to {space} will be freed.");
            }
            else question = ErrorMessages.Text($"Отменить скачивание на сервере?\n\n{rows[0].Name}",
                $"Cancel the download on the server?\n\n{rows[0].Name}");
            if (MessageBox.Show(dialog, question, dialog.Title, MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await LoadAsync(current, async () =>
            {
                int removed = 0;
                foreach (var row in rows)
                {
                    try
                    {
                        await RemoteDownloadClient.RemoveQueueItemAsync(connection, row.Item, token);
                        removed++;
                    }
                    catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict) { }
                }
                return removed == rows.Length ? ErrorMessages.Text($"Обработано: {removed}.", $"Processed: {removed}.") :
                    ErrorMessages.Text($"Обработано: {removed} из {rows.Length}. Остальные файлы недоступны или защищены.",
                        $"Processed: {removed} of {rows.Length}. The other files are unavailable or protected.");
            });
        };
        dialog.Loaded += async (_, _) =>
        {
            refresh.Focus();
            await LoadAsync(null);
        };
        dialog.Closed += (_, _) => lifetime.Cancel();
        UpdateButtons();
        _remoteQueueManagerOpen = true;
        try { dialog.ShowDialog(); }
        finally { _remoteQueueManagerOpen = false; }
        if (pickupQueueRow is not null && !_closing && !_disposed)
            _ = StartWithConfirmationAsync([pickupQueueRow], new HashSet<DownloadItem> { pickupQueueRow },
                serverOnly: true, reconnect: connection);
    }

    private sealed class RemoteQueueRow(RemoteQueueItem item)
    {
        public RemoteQueueItem Item { get; } = item;
        public bool HasFile => Item.State is "completed" or "received";
        public bool CanRemove => !HasFile || Item.DeleteAfter is not double threshold ||
            threshold <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public string Name => Item.Name is { Length: > 0 } name ? name : $"{Item.Source} [{Item.Id[..8]}]";
        public string Size => Item.Size is > 0 ? DisplayValueConverter.Bytes(Item.Size.Value) : "";
        public string Expires => Item.ExpiresAt is >= 0 and < 253402213200
            ? DateTimeOffset.FromUnixTimeSeconds((long)Item.ExpiresAt.Value).ToLocalTime().ToString("g") : "";
        public string Status => Item.State switch
        {
            "completed" => ErrorMessages.Text("Готово к получению", "Ready to receive"),
            "received" => ErrorMessages.Text("Получено на ПК", "Received on Windows"),
            "downloading" when Item.Progress == 100 => ErrorMessages.Text("Ожидание места", "Waiting for space"),
            "downloading" => ErrorMessages.Text("Скачивается", "Downloading") +
                (Item.Progress is >= 0 and <= 100 ? $" {Item.Progress.Value:0}%" : ""),
            _ => RemoteQueueStatus.Waiting(Item.WaitingReason) +
                (Item.Prioritized ? ErrorMessages.Text(" · Приоритет", " · Priority") : "")
        };
    }
}
