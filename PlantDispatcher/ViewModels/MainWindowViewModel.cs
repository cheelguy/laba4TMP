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
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PlantDispatcher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const string InitType = "init";
    private const string StateType = "state";
    private const string AckType = "ack";
    private const int DefaultPort = 6100;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _listenIp = "127.0.0.1";

    [ObservableProperty]
    private string _listenPort = DefaultPort.ToString();

    [ObservableProperty]
    private string _statusText = "Сервер диспетчера не запущен.";

    [ObservableProperty]
    private bool _isRunning;

    public ObservableCollection<PlantItemViewModel> Plants { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    [RelayCommand]
    private async Task StartServerAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (!IPAddress.TryParse(ListenIp, out IPAddress? ip))
        {
            AddLog("Ошибка: некорректный IP.");
            return;
        }

        if (!int.TryParse(ListenPort, out int port))
        {
            AddLog("Ошибка: некорректный порт.");
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(ip, port);
            _listener.Start();
            IsRunning = true;
            StatusText = $"Сервер запущен на {ip}:{port}";
            AddLog(StatusText);
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            AddLog($"Ошибка запуска: {ex.Message}");
            await StopServerAsync();
        }
    }

    [RelayCommand]
    private async Task StopServerAsync()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            AddLog($"Ошибка остановки: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            StatusText = "Сервер диспетчера остановлен.";
            AddLog(StatusText);
            await Task.CompletedTask;
        }
    }

    public async Task ShutdownAsync()
    {
        await StopServerAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        if (_listener is null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка ожидания подключения: {ex.Message}");
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        AddLog("Контроллер подключен.");
        StatusText = "Получение данных от контроллера.";

        while (!token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка чтения: {ex.Message}");
                break;
            }

            if (line is null)
            {
                break;
            }

            JsonDocument doc = JsonDocument.Parse(line);
            string? type = doc.RootElement.TryGetProperty("Type", out JsonElement typeElement)
                ? typeElement.GetString()
                : doc.RootElement.TryGetProperty("type", out JsonElement lowerTypeElement)
                    ? lowerTypeElement.GetString()
                    : null;

            if (string.Equals(type, InitType, StringComparison.OrdinalIgnoreCase))
            {
                int count = doc.RootElement.TryGetProperty("InstallationCount", out JsonElement countElement)
                    ? countElement.GetInt32()
                    : doc.RootElement.GetProperty("installationCount").GetInt32();

                InitializePlants(count);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { Type = AckType, Status = "ok" }));
                AddLog($"Получен init. Количество установок: {count}. Отправлен ACK.");
                continue;
            }

            if (string.Equals(type, StateType, StringComparison.OrdinalIgnoreCase))
            {
                JsonElement statesElement = doc.RootElement.TryGetProperty("States", out JsonElement upperStates)
                    ? upperStates
                    : doc.RootElement.GetProperty("states");

                int[] states = statesElement.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                ApplyStates(states);
                AddLog($"Получено обновление состояний: {string.Join(",", states)}");
            }
        }

        AddLog("Контроллер отключился.");
        StatusText = "Ожидание подключения контроллера.";
    }

    private void InitializePlants(int count)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Plants.Clear();
            for (int i = 0; i < count; i++)
            {
                Plants.Add(new PlantItemViewModel(i + 1));
            }
        });
    }

    private void ApplyStates(int[] states)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int count = Math.Min(states.Length, Plants.Count);
            for (int i = 0; i < count; i++)
            {
                Plants[i].SetState(states[i]);
            }
        });
    }

    private void AddLog(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (Logs.Count > 250)
            {
                Logs.RemoveAt(0);
            }
        });
    }
}

public partial class PlantItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _stateText;

    [ObservableProperty]
    private IBrush _stateBrush;

    public PlantItemViewModel(int index)
    {
        Title = $"Установка {index}";
        StateText = "Работает";
        StateBrush = Brushes.Green;
    }

    public void SetState(int stateCode)
    {
        if (stateCode == 1)
        {
            StateText = "Авария";
            StateBrush = Brushes.Red;
            return;
        }

        if (stateCode == 2)
        {
            StateText = "Ремонт";
            StateBrush = Brushes.Gray;
            return;
        }

        StateText = "Работает";
        StateBrush = Brushes.Green;
    }
}
