using System.Globalization;
using System.Runtime.InteropServices;

namespace GameSpeed;

/// <summary>
/// Headless mode, for scripting and for testing the hook without the window.
///
///   GameSpeed.exe --pid 1234 --inject --speed 8 --hold 20
///
/// The shared block outlives this process: the DLL keeps its own handle to
/// the section, so the game stays at the last multiplier set here.
/// </summary>
internal static class Cli
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);

    public static int Run(string[] args)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);   // so output lands in the caller's console

        int    pid    = 0;
        double speed  = double.NaN;
        bool   inject = false;
        bool   force  = false;
        double hold   = 0;
        var    flags  = SharedCtl.Flags.Sleep;
        var    method = Method.Auto;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a)
            {
                case "--pid":    pid = int.Parse(Need(next, a)); i++; break;
                case "--speed":  speed = double.Parse(Need(next, a), CultureInfo.InvariantCulture); i++; break;
                case "--hold":   hold = double.Parse(Need(next, a), CultureInfo.InvariantCulture); i++; break;
                case "--inject": inject = true; break;
                case "--force":  force = true; break;
                case "--method":
                    method = Need(next, a).ToLowerInvariant() switch
                    {
                        "unity" => Method.Unity,
                        "clock" => Method.Clock,
                        "auto"  => Method.Auto,
                        _ => throw new ArgumentException(
                                 "--method принимает auto, unity или clock."),
                    };
                    i++;
                    break;
                case "--wall":   flags |= SharedCtl.Flags.WallClock; break;
                case "--selective": flags |= SharedCtl.Flags.Selective; break;
                case "--no-sleep-scale": flags &= ~SharedCtl.Flags.Sleep; break;
                case "--help" or "-h" or "/?": Usage(); return 0;
                default:
                    Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                    Usage();
                    return 2;
            }
        }

        if (pid <= 0) { Console.Error.WriteLine("Нужен --pid."); Usage(); return 2; }

        try
        {
            using SharedCtl ctl = SharedCtl.Create(pid);

            if (inject && ctl.Attached && !force)
            {
                Console.WriteLine($"PID {pid} уже под хуком, внедрение пропущено.");
            }
            else if (inject)
            {
                Method chosen = Injector.Resolve(method, pid);
                if (method == Method.Auto)
                    Console.WriteLine($"Движок: {Injector.DetectUnity(pid)} -> механизм {chosen}.");
                string dll = Injector.DllPath(chosen);
                Injector.Inject(pid, dll);
                Console.WriteLine($"Внедрено в PID {pid} ({Path.GetFileName(dll)}).");
            }

            if (!double.IsNaN(speed))
            {
                ctl.Write(speed, flags);
                Console.WriteLine($"Запрошено x{speed.ToString("0.##", CultureInfo.InvariantCulture)}, флаги: {flags}.");
            }

            // The DLL needs up to 250 ms to attach and another tick to report.
            DateTime deadline = DateTime.UtcNow.AddSeconds(hold);
            bool reported = false;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
                if (!reported && ctl.Attached)
                {
                    Console.WriteLine($"Отозвалась: {ctl.Engine}" +
                        (ctl.Engine == SharedCtl.Backend.Clock
                            ? $", пропатчено импортов: {ctl.Patched}." : "."));
                    reported = true;
                }
                if (ctl.GaveUp)
                {
                    Console.Error.WriteLine(
                        $"Рантайм Unity не найден. Список загруженных модулей — " +
                        $"в %TEMP%, файл gsunity_{pid}.log — он показывает, что это за движок.");
                    return 1;
                }
                var hs = ctl.Hearthstone;
                string phase = hs.HasFlag(SharedCtl.HsState.Selective)
                    ? (hs.HasFlag(SharedCtl.HsState.Fast) ? "  [действие]" : "  [простой]") : "";
                Console.WriteLine($"  фактически x{ctl.Actual.ToString("0.00", CultureInfo.InvariantCulture)}{phase}");
            }

            if (hold > 0 && !ctl.Attached)
            {
                Console.Error.WriteLine("DLL так и не отозвалась через общую память.");
                return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Need(string? v, string opt)
        => v ?? throw new ArgumentException($"У {opt} пропущено значение.");

    private static void Usage()
    {
        Console.WriteLine("""
            GameSpeed, консольный режим

              --pid <n>          целевой процесс (обязательно)
              --inject           внедрить модуль
              --force            внедрить, даже если процесс уже под хуком
                                 (догрузить пересобранную DLL)
              --method <m>       auto (по умолчанию), unity или clock
              --speed <x>        множитель, например 8, 16, 30
              --hold <сек>       держать и печатать фактическое ускорение
              --wall             ускорять и системные часы
              --selective        Hearthstone: ускорять только бой и розыгрыш
              --no-sleep-scale   не сокращать Sleep()

            Без аргументов запускается обычное окно.
            """);
    }
}
