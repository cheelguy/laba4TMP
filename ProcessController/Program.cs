using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal sealed record MeasurementMessage(
    DateTime TimestampUtc,
    double TemperatureC,
    double PressureAtm);

internal static class Program
{
    private const int DefaultPort = 6000;
    private const string DefaultHost = "127.0.0.1";

    private static async Task<int> Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : DefaultHost;
        int port = (args.Length > 1 && int.TryParse(args[1], out int parsedPort))
            ? parsedPort
            : DefaultPort;

        Console.WriteLine("Контроллер технологического процесса запущен.");
        Console.WriteLine($"Подключение к диспетчеру: {host}:{port}");
        Console.WriteLine("Для остановки нажмите Ctrl+C.");

        using CancellationTokenSource cancellationTokenSource = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                using TcpClient tcpClient = new();
                await tcpClient.ConnectAsync(host, port, cancellationTokenSource.Token);
                using NetworkStream stream = tcpClient.GetStream();
                using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true)
                {
                    AutoFlush = true
                };

                Console.WriteLine("Соединение с диспетчером установлено.");
                Random random = new();

                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    MeasurementMessage measurement = new(
                        DateTime.UtcNow,
                        TemperatureC: GenerateUniform(random, 0.0, 100.0),
                        PressureAtm: GenerateUniform(random, 0.0, 6.0));

                    string payload = JsonSerializer.Serialize(measurement);
                    await writer.WriteLineAsync(payload);

                    Console.WriteLine(
                        $"Отправлено: T={measurement.TemperatureC:F2} C, P={measurement.PressureAtm:F2} атм, " +
                        $"UTC={measurement.TimestampUtc:O}");

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Ошибка соединения/передачи: {exception.Message}");
                Console.WriteLine("Повторная попытка через 2 секунды...");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationTokenSource.Token);
            }
        }

        Console.WriteLine("Контроллер остановлен.");
        return 0;
    }

    private static double GenerateUniform(Random random, double minInclusive, double maxInclusive)
    {
        // NextDouble() дает равномерное распределение на [0;1),
        // значит масштабирование дает равномерное распределение на [min;max).
        return minInclusive + (random.NextDouble() * (maxInclusive - minInclusive));
    }
}
