using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GameSpeed;

public enum Bitness { Unknown, X86, X64 }

/// <summary>Which mechanism to use for scaling.</summary>
public enum Method
{
    Auto,   // Unity timeScale if a Unity runtime is in the process, else clock
    Clock,  // gsphook: OS timer hooks, works on any engine
    Unity,  // gsunity: UnityEngine.Time.timeScale, past Unity's delta clamp
}

/// <summary>What the injector could tell about a target before injecting.</summary>
public enum UnityKind { None, Mono, Il2Cpp, Unreadable }

public sealed record TargetProcess(int Pid, string Name, Bitness Bits, string Window)
{
    public override string ToString() => $"{Name} ({Pid})";
}

public static class Injector
{
    public static Bitness SelfBitness => Environment.Is64BitProcess ? Bitness.X64 : Bitness.X86;

    /// <summary>
    /// The DLL for a mechanism, matching this injector's bitness, from the
    /// exe folder.
    /// </summary>
    public static string DllPath(Method method)
    {
        string dir  = AppContext.BaseDirectory;
        string stem = method == Method.Unity ? "gsunity" : "gsphook";
        string name = stem + (SelfBitness == Bitness.X64 ? "64" : "32") + ".dll";
        string p = Path.Combine(dir, name);
        if (File.Exists(p)) return p;
        // Fall back to a plain name, for a hand-built DLL dropped alongside.
        string alt = Path.Combine(dir, stem + ".dll");
        return File.Exists(alt) ? alt : p;
    }

    /// <summary>
    /// Looks for a Unity scripting runtime among the target's loaded modules,
    /// which is what decides the mechanism under <see cref="Method.Auto"/>.
    /// </summary>
    public static UnityKind DetectUnity(int pid)
    {
        var me = new Native.MODULEENTRY32W
        {
            dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>(),
        };

        // A process loading modules right now answers ERROR_BAD_LENGTH; the
        // documented response is to retry.
        IntPtr snap = Native.INVALID_HANDLE_VALUE;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            snap = Native.CreateToolhelp32Snapshot(
                Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, pid);
            if (snap != Native.INVALID_HANDLE_VALUE) break;
            if (Marshal.GetLastWin32Error() != Native.ERROR_BAD_LENGTH) break;
            Thread.Sleep(50);
        }
        if (snap == Native.INVALID_HANDLE_VALUE) return UnityKind.Unreadable;

        try
        {
            if (!Native.Module32FirstW(snap, ref me)) return UnityKind.Unreadable;
            bool il2cpp = false;
            do
            {
                string m = me.szModule ?? "";
                if (m.StartsWith("mono", StringComparison.OrdinalIgnoreCase)
                    && m.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    return UnityKind.Mono;           // Mono is the stronger signal
                if (m.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                    il2cpp = true;
            }
            while (Native.Module32NextW(snap, ref me));

            return il2cpp ? UnityKind.Il2Cpp : UnityKind.None;
        }
        finally { Native.CloseHandle(snap); }
    }

    /// <summary>Resolves <see cref="Method.Auto"/> for a given target.</summary>
    public static Method Resolve(Method method, int pid)
    {
        if (method != Method.Auto) return method;
        return DetectUnity(pid) is UnityKind.Mono or UnityKind.Il2Cpp
            ? Method.Unity
            : Method.Clock;
    }

    public static Bitness GetBitness(int pid)
    {
        if (!Environment.Is64BitOperatingSystem) return Bitness.X86;
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return Bitness.Unknown;
        try
        {
            // On 64-bit Windows a process is 32-bit exactly when it runs under WOW64.
            return Native.IsWow64Process(h, out bool wow) ? (wow ? Bitness.X86 : Bitness.X64)
                                                          : Bitness.Unknown;
        }
        finally { Native.CloseHandle(h); }
    }

    public static List<TargetProcess> ListProcesses()
    {
        var list = new List<TargetProcess>();
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                string title = "";
                try { title = p.MainWindowTitle ?? ""; } catch { }
                list.Add(new TargetProcess(p.Id, p.ProcessName, GetBitness(p.Id), title));
            }
            catch { /* processes come and go while we enumerate */ }
            finally { p.Dispose(); }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>
    /// A copy of the DLL named after its contents. LoadLibraryW on a path that
    /// is already loaded returns the old module without running anything, so
    /// a rebuilt DLL injected under its usual name into a game that still
    /// holds the previous build would silently never start. The copy also
    /// keeps bin\ free of locks, so a rebuild never trips over a running game.
    /// </summary>
    private static string StageCopy(string dllPath)
    {
        byte[] bytes = File.ReadAllBytes(dllPath);
        string hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        string dir = Path.Combine(Path.GetTempPath(), "GameSpeed");
        Directory.CreateDirectory(dir);
        string staged = Path.Combine(dir,
            $"{Path.GetFileNameWithoutExtension(dllPath)}_{hash}.dll");
        // An existing copy has the same contents by construction and may be
        // locked by a game that loaded it.
        if (!File.Exists(staged))
        {
            string tmp = staged + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, staged, overwrite: true);
        }
        return staged;
    }

    /// <summary>
    /// Loads the hook DLL into the target with LoadLibraryW on a remote thread.
    /// </summary>
    public static void Inject(int pid, string dllPath)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException(
                $"Не найдена хук-библиотека:\n{dllPath}\n\n" +
                "Соберите нативную часть (см. README) и положите её рядом с GameSpeed.exe.",
                dllPath);

        dllPath = StageCopy(dllPath);

        Bitness target = GetBitness(pid);
        if (target != Bitness.Unknown && target != SelfBitness)
            throw new InvalidOperationException(
                $"Разрядность не совпадает: процесс {target}, инжектор {SelfBitness}.\n" +
                $"Запустите {(target == Bitness.X86 ? "GameSpeed32.exe" : "GameSpeed.exe")}.");

        IntPtr hProc = Native.OpenProcess(Native.INJECT_ACCESS, false, pid);
        if (hProc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "OpenProcess не удался. Если игра запущена от администратора, " +
                "запустите и GameSpeed от администратора.");

        IntPtr remote = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        try
        {
            byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remote = Native.VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)pathBytes.Length,
                Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remote == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx не удался");

            if (!Native.WriteProcessMemory(hProc, remote, pathBytes,
                    (UIntPtr)pathBytes.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory не удался");

            // kernel32 sits at the same base in every process of a given
            // bitness, so our own LoadLibraryW address is valid over there.
            IntPtr k32 = Native.GetModuleHandleA("kernel32.dll");
            IntPtr loadLibrary = Native.GetProcAddress(k32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LoadLibraryW не найдена");

            thread = Native.CreateRemoteThread(hProc, IntPtr.Zero, UIntPtr.Zero,
                loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread не удался");

            if (Native.WaitForSingleObject(thread, 10000) == Native.WAIT_TIMEOUT)
                throw new TimeoutException("LoadLibrary в целевом процессе не ответила за 10 с.");

            if (Native.GetExitCodeThread(thread, out uint exit) && exit == 0)
                throw new InvalidOperationException(
                    "LoadLibraryW вернула NULL — DLL не загрузилась. " +
                    "Чаще всего это несовпадение разрядности или отсутствующая зависимость.");
        }
        finally
        {
            if (thread != IntPtr.Zero) Native.CloseHandle(thread);
            if (remote != IntPtr.Zero)
                Native.VirtualFreeEx(hProc, remote, UIntPtr.Zero, Native.MEM_RELEASE);
            Native.CloseHandle(hProc);
        }
    }
}
