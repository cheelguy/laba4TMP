using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SocketLab1.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int DefaultPort = 5050;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private TcpClient? _client;
    private StreamReader? _clientReader;
    private StreamWriter? _clientWriter;

    [ObservableProperty]
    private string _serverAddress = "127.0.0.1";

    [ObservableProperty]
    private string _serverPort = DefaultPort.ToString();

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _clientServerAddress = "127.0.0.1";

    [ObservableProperty]
    private string _clientServerPort = DefaultPort.ToString();

    [ObservableProperty]
    private string _pathInput = string.Empty;

    [ObservableProperty]
    private string _clientResponse = string.Empty;

    [ObservableProperty]
    private bool _isClientConnected;

    [ObservableProperty]
    private string? _selectedDrive;

    [ObservableProperty]
    private string _currentBrowsePath = string.Empty;

    [ObservableProperty]
    private string? _selectedBrowserItem;

    public ObservableCollection<string> ServerLog { get; } = [];
    public ObservableCollection<string> ClientLog { get; } = [];
    public ObservableCollection<string> LocalDrives { get; } = [];
    public ObservableCollection<string> BrowserEntries { get; } = [];

    public MainWindowViewModel()
    {
        RefreshLocalDrives();
    }

    [RelayCommand]
    private async Task StartServerAsync()
    {
        if (IsServerRunning)
        {
            return;
        }

        if (!int.TryParse(ServerPort, out var port))
        {
            AddServerLog("Ошибка: некорректный порт сервера.");
            return;
        }

        try
        {
            var ip = IPAddress.Parse(ServerAddress);
            _listener = new TcpListener(ip, port);
            _serverCts = new CancellationTokenSource();
            _listener.Start();
            IsServerRunning = true;
            AddServerLog($"Сервер запущен на {ip}:{port}.");
            _ = Task.Run(() => AcceptLoopAsync(_serverCts.Token));
        }
        catch (Exception ex)
        {
            AddServerLog($"Ошибка запуска сервера: {ex.Message}");
            await StopServerAsync();
        }
    }

    [RelayCommand]
    private async Task StopServerAsync()
    {
        if (!IsServerRunning)
        {
            return;
        }

        try
        {
            _serverCts?.Cancel();
            _listener?.Stop();
            AddServerLog("Сервер остановлен.");
        }
        catch (Exception ex)
        {
            AddServerLog($"Ошибка остановки сервера: {ex.Message}");
        }
        finally
        {
            IsServerRunning = false;
            _listener = null;
            _serverCts?.Dispose();
            _serverCts = null;
            await Task.CompletedTask;
        }
    }

    [RelayCommand]
    private async Task ConnectClientAsync()
    {
        if (IsClientConnected)
        {
            return;
        }

        if (!int.TryParse(ClientServerPort, out var port))
        {
            AddClientLog("Ошибка: некорректный порт.");
            return;
        }

        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(ClientServerAddress, port);
            var stream = _client.GetStream();
            _clientReader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            _clientWriter = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true
            };
            IsClientConnected = true;
            AddClientLog($"Подключено к серверу {ClientServerAddress}:{port}.");

            var message = await ReceiveMessageAsync(_clientReader);
            if ((message is not null) && (message.Type == "drives"))
            {
                ClientResponse = "Список логических устройств:\n" + message.Payload;
                AddClientLog($"Получен список логических устройств ({GetByteCount(message.Payload)} байт).");
            }
        }
        catch (Exception ex)
        {
            AddClientLog($"Ошибка подключения: {ex.Message}");
            await DisconnectClientAsync();
        }
    }

    [RelayCommand]
    private async Task DisconnectClientAsync()
    {
        try
        {
            if (IsClientConnected && (_clientWriter is not null))
            {
                await SendMessageAsync(_clientWriter, new NetMessage("close", string.Empty));
                AddClientLog("Отправлено уведомление close серверу.");
            }
        }
        catch (Exception ex)
        {
            AddClientLog($"Ошибка отправки уведомления close: {ex.Message}");
        }
        finally
        {
            _clientReader?.Dispose();
            _clientWriter?.Dispose();
            _client?.Dispose();
            _clientReader = null;
            _clientWriter = null;
            _client = null;
            IsClientConnected = false;
            AddClientLog("Соединение закрыто.");
        }
    }

    [RelayCommand]
    private async Task SendPathRequestAsync()
    {
        if (!IsClientConnected || (_clientReader is null) || (_clientWriter is null))
        {
            AddClientLog("Ошибка: клиент не подключен к серверу.");
            return;
        }

        if (string.IsNullOrWhiteSpace(PathInput))
        {
            AddClientLog("Ошибка: путь не задан.");
            return;
        }

        try
        {
            await SendMessageAsync(_clientWriter, new NetMessage("request", PathInput.Trim()));
            AddClientLog($"Отправлен запрос: {PathInput.Trim()} ({GetByteCount(PathInput.Trim())} байт).");
            var response = await ReceiveMessageAsync(_clientReader);

            if (response is null)
            {
                AddClientLog("Ошибка: сервер не ответил.");
                return;
            }

            if (response.Type == "result")
            {
                ClientResponse = response.Payload;
                AddClientLog($"Получен ответ от сервера ({GetByteCount(response.Payload)} байт).");
                return;
            }

            if (response.Type == "error")
            {
                ClientResponse = "Ошибка сервера:\n" + response.Payload;
                AddClientLog($"Сервер вернул ошибку: {response.Payload}");
                return;
            }

            ClientResponse = "Получен неизвестный ответ сервера.";
            AddClientLog("Неизвестный тип ответа.");
        }
        catch (Exception ex)
        {
            AddClientLog($"Ошибка запроса: {ex.Message}");
        }
        finally
        {
            await DisconnectClientAsync();
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        ServerLog.Clear();
        ClientLog.Clear();
    }

    [RelayCommand]
    private void RefreshBrowser()
    {
        RefreshLocalDrives();
    }

    [RelayCommand]
    private void OpenSelectedPath()
    {
        if (string.IsNullOrWhiteSpace(SelectedBrowserItem))
        {
            AddClientLog("Не выбран элемент в списке каталога.");
            return;
        }

        var selected = SelectedBrowserItem;
        if (selected.StartsWith("[DIR] ", StringComparison.Ordinal))
        {
            var directory = selected.Replace("[DIR] ", string.Empty);
            if (Directory.Exists(directory))
            {
                CurrentBrowsePath = directory;
                PathInput = directory;
                LoadBrowserEntries(directory);
                AddClientLog($"Открыт каталог: {directory} (путь выбран для передачи серверу).");
                return;
            }
        }

        if (selected.StartsWith("[FILE] ", StringComparison.Ordinal))
        {
            PathInput = selected.Replace("[FILE] ", string.Empty);
            AddClientLog($"Выбран файл для передачи: {PathInput}");
        }
    }

    [RelayCommand]
    private void NavigateUp()
    {
        if (string.IsNullOrWhiteSpace(CurrentBrowsePath))
        {
            return;
        }

        var parent = Directory.GetParent(CurrentBrowsePath);
        if (parent is null)
        {
            return;
        }

        CurrentBrowsePath = parent.FullName;
        LoadBrowserEntries(parent.FullName);
        AddClientLog($"Переход на уровень выше: {parent.FullName}");
    }

    partial void OnSelectedDriveChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        CurrentBrowsePath = value;
        LoadBrowserEntries(value);
        AddClientLog($"Выбран диск: {value}");
    }

    public async Task ShutdownAsync()
    {
        await DisconnectClientAsync();
        await StopServerAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? incomingClient = null;

            try
            {
                incomingClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(incomingClient, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AddServerLog($"Ошибка ожидания клиента: {ex.Message}");
                incomingClient?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        using var _ = tcpClient;
        using var stream = tcpClient.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };

        AddServerLog("Клиент подключился.");

        var drives = string.Join(
            Environment.NewLine,
            DriveInfo.GetDrives().Select(x => $"{x.Name} ({x.DriveType})"));
        await SendMessageAsync(writer, new NetMessage("drives", drives));
        AddServerLog($"Отправлен список логических устройств ({GetByteCount(drives)} байт).");

        while (!cancellationToken.IsCancellationRequested)
        {
            NetMessage? message;
            try
            {
                message = await ReceiveMessageAsync(reader);
            }
            catch (Exception ex)
            {
                AddServerLog($"Ошибка чтения от клиента: {ex.Message}");
                break;
            }

            if (message is null)
            {
                AddServerLog("Клиент завершил соединение.");
                break;
            }

            if (message.Type == "close")
            {
                AddServerLog("Получено уведомление close. Соединение закрывается.");
                break;
            }

            if (message.Type != "request")
            {
                await SendMessageAsync(writer, new NetMessage("error", "Неизвестная команда клиента."));
                continue;
            }

            var requestedPath = message.Payload.Trim();
            AddServerLog($"Запрос пути: {requestedPath}");

            try
            {
                if (Directory.Exists(requestedPath))
                {
                    var content = BuildDirectoryStructure(requestedPath);
                    await SendMessageAsync(writer, new NetMessage("result", content));
                    AddServerLog($"Отправлена структура каталога ({GetByteCount(content)} байт).");
                }
                else if (File.Exists(requestedPath))
                {
                    var text = await ReadTextFileSafelyAsync(requestedPath, cancellationToken);
                    await SendMessageAsync(writer, new NetMessage("result", text));
                    AddServerLog($"Отправлено содержимое файла ({GetByteCount(text)} байт).");
                }
                else
                {
                    await SendMessageAsync(writer, new NetMessage("error", "Путь не существует."));
                    AddServerLog("Отправлена ошибка: путь не существует.");
                }
            }
            catch (Exception ex)
            {
                await SendMessageAsync(writer, new NetMessage("error", $"Ошибка обработки пути: {ex.Message}"));
                AddServerLog($"Отправлена ошибка обработки: {ex.Message}");
            }
        }

        AddServerLog("Обработка клиента завершена.");
    }

    private static string BuildDirectoryStructure(string directoryPath)
    {
        var sb = new StringBuilder();
        BuildDirectoryStructureRecursive(directoryPath, 0, sb);
        return sb.ToString();
    }

    private static void BuildDirectoryStructureRecursive(
        string directoryPath,
        int depth,
        StringBuilder sb)
    {
        var indent = new string(' ', depth * 2);
        var dirName = string.IsNullOrWhiteSpace(Path.GetFileName(directoryPath))
            ? directoryPath
            : Path.GetFileName(directoryPath);
        sb.AppendLine($"{indent}[DIR] {dirName}");

        try
        {
            foreach (var dir in Directory.GetDirectories(directoryPath))
            {
                BuildDirectoryStructureRecursive(dir, depth + 1, sb);
            }

            foreach (var file in Directory.GetFiles(directoryPath))
            {
                sb.AppendLine($"{indent}  [FILE] {Path.GetFileName(file)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{indent}  [ERROR] {ex.Message}");
        }
    }

    private static async Task<string> ReadTextFileSafelyAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (LooksBinary(bytes))
        {
            throw new InvalidDataException("Файл не является текстовым.");
        }

        // UTF-8 as default for cross-platform text exchange.
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        int probeLength = Math.Min(bytes.Length, 4096);
        for (int i = 0; i < probeLength; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task SendMessageAsync(StreamWriter writer, NetMessage message)
    {
        var line = JsonSerializer.Serialize(message, JsonOptions);
        await writer.WriteLineAsync(line);
    }

    private static async Task<NetMessage?> ReceiveMessageAsync(StreamReader reader)
    {
        var line = await reader.ReadLineAsync();
        if (line is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<NetMessage>(line, JsonOptions);
    }

    private void AddServerLog(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ServerLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        });
    }

    private void AddClientLog(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ClientLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        });
    }

    private static int GetByteCount(string text)
    {
        return Encoding.UTF8.GetByteCount(text);
    }

    private void RefreshLocalDrives()
    {
        LocalDrives.Clear();
        foreach (var drive in DriveInfo.GetDrives().Select(x => x.Name))
        {
            LocalDrives.Add(drive);
        }

        if (LocalDrives.Count > 0)
        {
            SelectedDrive = LocalDrives[0];
        }
    }

    private void LoadBrowserEntries(string path)
    {
        BrowserEntries.Clear();

        try
        {
            foreach (var directory in Directory.GetDirectories(path))
            {
                BrowserEntries.Add($"[DIR] {directory}");
            }

            foreach (var file in Directory.GetFiles(path))
            {
                BrowserEntries.Add($"[FILE] {file}");
            }
        }
        catch (Exception ex)
        {
            BrowserEntries.Add($"[ERROR] {ex.Message}");
        }
    }

    private sealed record NetMessage(string Type, string Payload);
}
