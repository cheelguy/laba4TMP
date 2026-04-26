using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DispatcherPanel.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int DefaultPort = 6000;
    private const int MaxPoints = 120;
    private const double ChartWidth = 760;
    private const double ChartHeight = 180;
    private const double TempMax = 100.0;
    private const double PressureMax = 6.0;

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private DateTime? _startTimestamp;

    [ObservableProperty]
    private string _listenIp = "127.0.0.1";

    [ObservableProperty]
    private string _listenPort = DefaultPort.ToString();

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private string _lastTemperature = "-";

    [ObservableProperty]
    private string _lastPressure = "-";

    [ObservableProperty]
    private string _status = "Ожидание запуска сервера диспетчера.";

    public ObservableCollection<Point> TemperaturePoints { get; } = [];
    public ObservableCollection<Point> PressurePoints { get; } = [];
    public ObservableCollection<string> LogItems { get; } = [];

    [RelayCommand]
    private async Task StartListeningAsync()
    {
        if (IsListening)
        {
            return;
        }

        if (!IPAddress.TryParse(ListenIp, out IPAddress? ipAddress))
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
            _serverCts = new CancellationTokenSource();
            _listener = new TcpListener(ipAddress, port);
            _listener.Start();
            IsListening = true;
            Status = $"Сервер диспетчера запущен на {ipAddress}:{port}.";
            AddLog(Status);
            _ = Task.Run(() => AcceptLoopAsync(_serverCts.Token));
        }
        catch (Exception exception)
        {
            AddLog($"Ошибка запуска сервера: {exception.Message}");
            await StopListeningAsync();
        }
    }

    [RelayCommand]
    private async Task StopListeningAsync()
    {
        try
        {
            _serverCts?.Cancel();
            _listener?.Stop();
            AddLog("Сервер диспетчера остановлен.");
        }
        catch (Exception exception)
        {
            AddLog($"Ошибка остановки: {exception.Message}");
        }
        finally
        {
            IsListening = false;
            _listener = null;
            _serverCts?.Dispose();
            _serverCts = null;
            await Task.CompletedTask;
        }
    }

    [RelayCommand]
    private void ClearCharts()
    {
        TemperaturePoints.Clear();
        PressurePoints.Clear();
        _startTimestamp = null;
        LastTemperature = "-";
        LastPressure = "-";
        AddLog("Графики очищены.");
    }

    public async Task ShutdownAsync()
    {
        await StopListeningAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? tcpClient = null;
            try
            {
                tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(tcpClient, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                AddLog($"Ошибка ожидания подключения: {exception.Message}");
                tcpClient?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        AddLog("Контроллер подключился.");
        Status = "Получение телеметрии...";

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                AddLog($"Ошибка чтения данных: {exception.Message}");
                break;
            }

            if (line is null)
            {
                AddLog("Контроллер отключился.");
                break;
            }

            try
            {
                MeasurementMessage? message = JsonSerializer.Deserialize<MeasurementMessage>(line);
                if (message is null)
                {
                    continue;
                }

                UpdateCharts(message);
            }
            catch (Exception exception)
            {
                AddLog($"Ошибка разбора пакета: {exception.Message}");
            }
        }

        Status = "Ожидание подключения контроллера.";
    }

    private void UpdateCharts(MeasurementMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _startTimestamp ??= message.TimestampUtc;
            double secondsFromStart = (message.TimestampUtc - _startTimestamp.Value).TotalSeconds;

            double x = TemperaturePoints.Count >= MaxPoints
                ? ChartWidth
                : (TemperaturePoints.Count * (ChartWidth / (MaxPoints - 1)));

            if (TemperaturePoints.Count >= MaxPoints)
            {
                ShiftPointsLeft(TemperaturePoints);
                ShiftPointsLeft(PressurePoints);
                x = ChartWidth;
            }

            double yTemp = ScaleY(message.TemperatureC, TempMax);
            double yPressure = ScaleY(message.PressureAtm, PressureMax);

            TemperaturePoints.Add(new Point(x, yTemp));
            PressurePoints.Add(new Point(x, yPressure));

            LastTemperature = $"{message.TemperatureC:F2} C";
            LastPressure = $"{message.PressureAtm:F2} атм";
            AddLog(
                $"t={secondsFromStart:F0}s | T={message.TemperatureC:F2} C | " +
                $"P={message.PressureAtm:F2} атм");
        });
    }

    private static void ShiftPointsLeft(ObservableCollection<Point> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        points.RemoveAt(0);
        double dx = ChartWidth / (MaxPoints - 1);
        for (int i = 0; i < points.Count; i++)
        {
            Point point = points[i];
            points[i] = new Point(Math.Max(0, point.X - dx), point.Y);
        }
    }

    private static double ScaleY(double value, double maxValue)
    {
        double normalized = Math.Clamp(value / maxValue, 0.0, 1.0);
        return ChartHeight - (normalized * ChartHeight);
    }

    private void AddLog(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogItems.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (LogItems.Count > 300)
            {
                LogItems.RemoveAt(0);
            }
        });
    }

    private sealed record MeasurementMessage(
        DateTime TimestampUtc,
        double TemperatureC,
        double PressureAtm);
}
