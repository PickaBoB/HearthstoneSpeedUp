using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GameSpeed;

/// <summary>
/// The 40-byte control block shared with gsphook.dll inside the game.
/// The GUI creates it before injecting, the DLL opens it afterwards.
/// Field offsets here must match the GspShared struct in gsphook.c.
/// </summary>
public sealed class SharedCtl : IDisposable
{
    public const uint Magic   = 0x44505347; // "GSPD"
    public const uint Version = 2;

    [Flags]
    public enum Flags : uint
    {
        None      = 0,
        WallClock = 1, // also scale GetSystemTimeAsFileTime
        Sleep     = 2, // shorten Sleep() by the same factor
        Selective = 4, // Hearthstone: speed up only while actions play out
    }

    /// <summary>What gsunity reports in the patched field for Hearthstone.</summary>
    [Flags]
    public enum HsState : uint
    {
        None      = 0,
        Active    = 1, // multiplier goes through the game's TimeScaleMgr
        Selective = 2,
        Fast      = 4, // inside a sped-up stretch right now
    }

    /// <summary>Which DLL answered, as reported by the DLL itself.</summary>
    public enum Backend : uint
    {
        None   = 0, // nothing attached, or gsunity found no Unity runtime
        Mono   = 1,
        Il2Cpp = 2,
        Clock  = 3, // gsphook, OS timer hooks
    }

    private const int OffMagic   = 0;
    private const int OffVersion = 4;
    private const int OffMult    = 8;
    private const int OffFlags   = 16;
    private const int OffSeq     = 20;
    private const int OffPatched = 24;
    private const int OffState   = 28;
    private const int OffActual  = 32;
    private const int OffBackend = 40;
    private const int BlockSize  = 4096;

    private IntPtr _mapping;
    private IntPtr _view;
    private uint   _seq;

    public int Pid { get; }

    private SharedCtl(int pid, IntPtr mapping, IntPtr view)
    {
        Pid = pid;
        _mapping = mapping;
        _view = view;
    }

    public static string NameFor(int pid) => $"Local\\GameSpeedCtl_{pid}";

    /// <summary>
    /// Creates the control block for a target process, replacing any block
    /// left behind by an earlier run.
    /// </summary>
    public static SharedCtl Create(int pid)
    {
        // A null DACL would be unreadable from the game when the two sides
        // run at different elevation levels, so the block is opened to
        // Everyone. It holds nothing but a speed figure, but it does mean
        // any local process can retune a hooked game.
        IntPtr sd = IntPtr.Zero;
        var sa = new Native.SECURITY_ATTRIBUTES();
        if (Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                "D:(A;;GA;;;WD)", 1, out sd, IntPtr.Zero))
        {
            sa.nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>();
            sa.lpSecurityDescriptor = sd;
            sa.bInheritHandle = false;
        }

        IntPtr mapping;
        bool existed;
        try
        {
            mapping = Native.CreateFileMappingW(
                Native.INVALID_HANDLE_VALUE,
                sd == IntPtr.Zero ? IntPtr.Zero : StructToPtr(sa),
                Native.PAGE_READWRITE, 0, BlockSize, NameFor(pid));
            existed = Marshal.GetLastWin32Error() == Native.ERROR_ALREADY_EXISTS;
        }
        finally
        {
            if (sd != IntPtr.Zero) Native.LocalFree(sd);
        }

        if (mapping == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "CreateFileMapping failed");

        IntPtr view = Native.MapViewOfFile(mapping,
            Native.FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)BlockSize);
        if (view == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Native.CloseHandle(mapping);
            throw new Win32Exception(err, "MapViewOfFile failed");
        }

        var ctl = new SharedCtl(pid, mapping, view);

        if (existed && ctl.ReadUInt(OffMagic) == Magic)
        {
            // A hook is already live in this process -- a second GUI run, or
            // the CLI after the GUI. Adopt the block rather than reset it:
            // patched/state/actual belong to the DLL, and the sequence has to
            // carry on from where it stands or the DLL, which compares
            // sequence numbers, would ignore writes repeating a value it has
            // already seen.
            ctl._seq = ctl.ReadUInt(OffSeq);
            return ctl;
        }

        ctl.WriteUInt(OffMagic, Magic);
        ctl.WriteUInt(OffVersion, Version);
        ctl.WriteUInt(OffPatched, 0);
        ctl.WriteUInt(OffState, 0);
        ctl.WriteUInt(OffSeq, 0);
        ctl.WriteUInt(OffBackend, 0);
        ctl.WriteDouble(OffActual, 0.0);
        ctl.Write(1.0, Flags.Sleep);
        return ctl;
    }

    private static IntPtr StructToPtr(Native.SECURITY_ATTRIBUTES sa)
    {
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(sa, p, false);
        return p; // process-lifetime leak of 24 bytes, once per injection
    }

    /// <summary>Publishes a new multiplier. The DLL picks it up within 250 ms.</summary>
    public void Write(double multiplier, Flags flags)
    {
        // Clamped to the same range the DLL enforces, so the value in the
        // block always equals the value in force. The DLL also compares the
        // multiplier itself, and a figure it had to clamp would never match.
        multiplier = Math.Clamp(multiplier, 0.01, 1000.0);
        WriteDouble(OffMult, multiplier);
        WriteUInt(OffFlags, (uint)flags);
        WriteUInt(OffSeq, ++_seq); // written last: the DLL watches this
    }

    public double Multiplier => ReadDouble(OffMult);
    public double Actual     => ReadDouble(OffActual);
    public uint   Patched    => ReadUInt(OffPatched);
    public bool   Attached   => ReadUInt(OffState) == 1;
    public Backend Engine    => (Backend)ReadUInt(OffBackend);
    public HsState Hearthstone =>
        Engine is Backend.Mono or Backend.Il2Cpp ? (HsState)Patched : HsState.None;

    /// <summary>
    /// The DLL loaded, looked for what it needed and gave up. Distinct from
    /// a block that is simply still silent, which looks identical otherwise.
    /// </summary>
    public bool GaveUp => ReadUInt(OffState) == 2;

    private void WriteUInt(int off, uint v)
        => Marshal.WriteInt32(_view, off, unchecked((int)v));
    private uint ReadUInt(int off)
        => unchecked((uint)Marshal.ReadInt32(_view, off));
    private void WriteDouble(int off, double v)
        => Marshal.WriteInt64(_view, off, BitConverter.DoubleToInt64Bits(v));
    private double ReadDouble(int off)
        => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_view, off));

    public void Dispose()
    {
        if (_view != IntPtr.Zero) { Native.UnmapViewOfFile(_view); _view = IntPtr.Zero; }
        if (_mapping != IntPtr.Zero) { Native.CloseHandle(_mapping); _mapping = IntPtr.Zero; }
    }
}
