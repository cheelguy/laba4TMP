using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal enum PlantState
{
    Working = 0,
    Accident = 1,
    Repair = 2
}

internal sealed record ControllerConfig(int InstallationCount);
internal sealed record InitMessage(string Type, int InstallationCount);
internal sealed record AckMessage(string Type, string Status);
internal sealed record StateMessage(string Type, DateTime TimestampUtc, int[] States);

internal static class Program
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 6100;
    private const string InitType = "init";
    private const string StateType = "state";
    private const string AckType = "ack";

    private static async Task<int> Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : DefaultHost;
        int port = (args.Length > 1 && int.TryParse(args[1], out int parsedPort)) ? parsedPort : DefaultPort;

        ControllerConfig config = await LoadConfigAsync();
        if (config.InstallationCount <= 0)
        {
            Console.WriteLine("Ошибка: InstallationCount должен быть > 0.");
            return 1;
        }

        Console.WriteLine($"Контроллер запущен. Количество установок: {config.InstallationCount}");
        Console.WriteLine($"Подключение к пульту: {host}:{port}");
        Console.WriteLine("Нажмите Ctrl+C для остановки.");

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        PlantState[] states = Enumerable.Repeat(PlantState.Working, config.InstallationCount).ToArray();

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(host, port, cts.Token);
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
                using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                await SendJsonLineAsync(writer, new InitMessage(InitType, config.InstallationCount));
                Console.WriteLine("Отправлено количество установок, ожидание подтверждения диспетчера...");

                string? ackLine = await reader.ReadLineAsync(cts.Token);
                AckMessage? ack = ackLine is null ? null : JsonSerializer.Deserialize<AckMessage>(ackLine);
                if ((ack is null) || (ack.Type != AckType) || !string.Equals(ack.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Некорректный ACK, переподключение.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                    continue;
                }

                Console.WriteLine("ACK получен. Начинается передача состояний (каждые 2 секунды).");
                Random random = new();

                while (!cts.Token.IsCancellationRequested)
                {
                    StepStates(states, random);
                    int[] payloadStates = states.Select(static s => (int)s).ToArray();
                    await SendJsonLineAsync(writer, new StateMessage(StateType, DateTime.UtcNow, payloadStates));

                    Console.WriteLine($"Отправлено состояние: {string.Join(",", payloadStates)}");
                    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка соединения: {ex.Message}");
                Console.WriteLine("Повтор через 2 секунды...");
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            }
        }

        Console.WriteLine("Контроллер остановлен.");
        return 0;
    }

    private static void StepStates(PlantState[] states, Random random)
    {
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = states[i] switch
            {
                PlantState.Working => random.NextDouble() < 0.2 ? PlantState.Accident : PlantState.Working,
                PlantState.Accident => PlantState.Repair,
                PlantState.Repair => random.NextDouble() < 0.5 ? PlantState.Working : PlantState.Repair,
                _ => PlantState.Working
            };
        }
    }

    private static async Task SendJsonLineAsync<T>(StreamWriter writer, T payload)
    {
        string line = JsonSerializer.Serialize(payload);
        await writer.WriteLineAsync(line);
    }

    private static async Task<ControllerConfig> LoadConfigAsync()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "controller-config.json");
        JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        if (!File.Exists(configPath))
        {
            ControllerConfig defaultConfig = new(10);
            string defaultJson = JsonSerializer.Serialize(defaultConfig, jsonOptions);
            await File.WriteAllTextAsync(configPath, defaultJson);
            return defaultConfig;
        }

        string json = await File.ReadAllTextAsync(configPath);
        return JsonSerializer.Deserialize<ControllerConfig>(json, jsonOptions) ?? new ControllerConfig(10);
    }
}
