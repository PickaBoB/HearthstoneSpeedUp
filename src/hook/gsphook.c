/*
 * gsphook.dll -- client-side time scaling hook.
 *
 * The game asks Windows "how much time has passed?" and drives its frame
 * delta, animation playback and local timers from the answer. This DLL
 * answers with a virtual clock that advances `mult` times faster than the
 * real one, so animations and local logic play back at that rate.
 *
 * Nothing here hides itself: no unhooking of monitors, no signature
 * scrambling, no thread hiding. It is a plain, visible IAT patch.
 *
 * Hooking method: import-table patching of every loaded module, plus a
 * GetProcAddress hook for dynamically resolved names. No prologue patching,
 * so no length-disassembler and no risk of corrupting a hot code path.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdarg.h>

/* ------------------------------------------------------------------ */
/* Diagnostics: append-only text log next to the temp directory.       */
/* Build with -DGSP_NO_LOG to compile it out entirely.                 */
/* ------------------------------------------------------------------ */

#ifdef GSP_NO_LOG
#define gsp_log(fmt, ...) ((void)0)
#else
static void gsp_log(const char *fmt, ...)
{
    char path[MAX_PATH], line[1024], tmp[MAX_PATH];
    HANDLE h;
    DWORD written, n;
    va_list ap;

    if (!GetTempPathA(sizeof(tmp), tmp)) return;
    wsprintfA(path, "%sgsphook_%lu.log", tmp, GetCurrentProcessId());

    va_start(ap, fmt);
    n = (DWORD)wvsprintfA(line, fmt, ap);
    va_end(ap);
    if (n + 2 >= sizeof(line)) return;
    line[n++] = '\r';
    line[n++] = '\n';

    h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    WriteFile(h, line, n, &written, NULL);
    CloseHandle(h);
}
#endif

/* ------------------------------------------------------------------ */
/* Shared control block -- layout must match SharedCtl.cs exactly.     */
/* ------------------------------------------------------------------ */

#define GSP_MAGIC   0x44505347u   /* "GSPD" */
#define GSP_VERSION 2u

#define BACKEND_CLOCK 3u          /* this DLL; see gsunity.c for the others */

#define FLAG_WALLCLOCK 0x1u   /* also scale GetSystemTimeAsFileTime */
#define FLAG_SLEEP     0x2u   /* shorten Sleep() by the same factor */

#pragma pack(push, 1)
typedef struct {
    UINT32 magic;
    UINT32 version;
    double mult;        /* requested multiplier, written by the GUI   */
    UINT32 flags;
    UINT32 seq;
    UINT32 patched;     /* thunks patched so far, written by the DLL  */
    UINT32 state;       /* 1 = attached and running                   */
    double actual;      /* measured virtual/real ratio, by the DLL    */
    UINT32 backend;     /* which DLL answered; written by the DLL     */
    UINT32 reserved;
} GspShared;
#pragma pack(pop)

/* ------------------------------------------------------------------ */
/* Real function pointers, captured before any patching happens.       */
/* ------------------------------------------------------------------ */

typedef BOOL      (WINAPI *fn_QPC)(LARGE_INTEGER*);
typedef BOOL      (WINAPI *fn_QPF)(LARGE_INTEGER*);
typedef DWORD     (WINAPI *fn_GetTickCount)(void);
typedef ULONGLONG (WINAPI *fn_GetTickCount64)(void);
typedef DWORD     (WINAPI *fn_timeGetTime)(void);
typedef void      (WINAPI *fn_GetSysTimeAsFileTime)(FILETIME*);
typedef void      (WINAPI *fn_Sleep)(DWORD);
typedef DWORD     (WINAPI *fn_SleepEx)(DWORD, BOOL);
typedef FARPROC   (WINAPI *fn_GetProcAddress)(HMODULE, LPCSTR);

static fn_QPC                  real_QPC;
static fn_QPF                  real_QPF;
static fn_GetTickCount         real_GetTickCount;
static fn_GetTickCount64       real_GetTickCount64;
static fn_timeGetTime          real_timeGetTime;
static fn_GetSysTimeAsFileTime real_GetSystemTimeAsFileTime;
static fn_GetSysTimeAsFileTime real_GetSystemTimePreciseAsFileTime;
static fn_Sleep                real_Sleep;
static fn_SleepEx              real_SleepEx;
static fn_GetProcAddress       real_GetProcAddress;

/* ------------------------------------------------------------------ */
/* Virtual clock                                                       */
/* ------------------------------------------------------------------ */

static struct {
    CRITICAL_SECTION cs;
    BOOL      ready;

    LONGLONG  qpf;            /* real performance frequency           */
    LONGLONG  real_anchor;    /* real QPC at the last accumulate      */
    LONGLONG  virt_qpc;       /* virtual QPC to report right now      */
    LONGLONG  virt_origin;    /* virtual QPC at init, for ms deltas   */
    double    frac;           /* sub-tick remainder, avoids drift     */

    double    mult;           /* live multiplier                      */
    UINT32    flags;

    DWORD     tick_base;      /* real GetTickCount() at init          */
    ULONGLONG tick64_base;
    DWORD     mm_base;        /* real timeGetTime() at init           */
    ULONGLONG ft_base;        /* real FILETIME at init                */
} g;

static GspShared    *g_shm;
static HANDLE        g_shm_handle;
static HMODULE       g_self;
static volatile LONG g_patched;

/* Accumulate elapsed real time into the virtual timeline and return the
 * virtual counter. Accumulating, instead of recomputing from the origin,
 * means a multiplier change never makes the clock jump. */
static LONGLONG virt_qpc_now(void)
{
    LARGE_INTEGER now;
    LONGLONG out, d;
    double acc;

    real_QPC(&now);
    if (!g.ready)                      /* not initialised yet: pass through */
        return now.QuadPart;

    EnterCriticalSection(&g.cs);
    d = now.QuadPart - g.real_anchor;
    if (d < 0) d = 0;                  /* never run backwards */
    acc = (double)d * g.mult + g.frac;
    {
        LONGLONG whole = (LONGLONG)acc;
        g.frac = acc - (double)whole;
        g.virt_qpc += whole;
    }
    g.real_anchor = now.QuadPart;
    out = g.virt_qpc;
    LeaveCriticalSection(&g.cs);
    return out;
}

/* Virtual milliseconds elapsed since init. */
static ULONGLONG virt_elapsed_ms(void)
{
    LONGLONG d = virt_qpc_now() - g.virt_origin;
    if (d < 0) d = 0;
    return (ULONGLONG)(d / g.qpf) * 1000ull
         + (ULONGLONG)((d % g.qpf) * 1000 / g.qpf);
}

/* Virtual 100-ns units since init, split to avoid 64-bit overflow. */
static ULONGLONG virt_elapsed_100ns(void)
{
    LONGLONG d = virt_qpc_now() - g.virt_origin;
    if (d < 0) d = 0;
    return (ULONGLONG)(d / g.qpf) * 10000000ull
         + (ULONGLONG)((d % g.qpf) * 10000000 / g.qpf);
}

static double cur_mult(void)
{
    double m;
    if (!g.ready) return 1.0;
    EnterCriticalSection(&g.cs);
    m = g.mult;
    LeaveCriticalSection(&g.cs);
    return m;
}

/* ------------------------------------------------------------------ */
/* Hooks                                                               */
/* ------------------------------------------------------------------ */

static BOOL WINAPI h_QueryPerformanceCounter(LARGE_INTEGER *p)
{
    if (!p) return FALSE;
    p->QuadPart = virt_qpc_now();
    return TRUE;
}

static DWORD WINAPI h_GetTickCount(void)
{
    if (!g.ready) return real_GetTickCount();
    return (DWORD)(g.tick_base + (DWORD)virt_elapsed_ms());
}

static ULONGLONG WINAPI h_GetTickCount64(void)
{
    if (!g.ready) return real_GetTickCount64();
    return g.tick64_base + virt_elapsed_ms();
}

static DWORD WINAPI h_timeGetTime(void)
{
    if (!g.ready) return real_timeGetTime();
    return (DWORD)(g.mm_base + (DWORD)virt_elapsed_ms());
}

static void WINAPI h_GetSystemTimeAsFileTime(FILETIME *ft)
{
    ULONGLONG v;
    if (!ft) return;
    if (!g.ready || !(g.flags & FLAG_WALLCLOCK)) {
        real_GetSystemTimeAsFileTime(ft);
        return;
    }
    v = g.ft_base + virt_elapsed_100ns();
    ft->dwLowDateTime  = (DWORD)(v & 0xFFFFFFFFull);
    ft->dwHighDateTime = (DWORD)(v >> 32);
}

static void WINAPI h_GetSystemTimePreciseAsFileTime(FILETIME *ft)
{
    h_GetSystemTimeAsFileTime(ft);
}

/* A game that sleeps between frames must sleep proportionally less, or the
 * frame rate caps the speed-up no matter what the clock says. */
static void WINAPI h_Sleep(DWORD ms)
{
    double m = cur_mult();
    if ((g.flags & FLAG_SLEEP) && m > 1.0 && ms > 0 && ms != INFINITE)
        real_Sleep((DWORD)((double)ms / m));
    else
        real_Sleep(ms);
}

static DWORD WINAPI h_SleepEx(DWORD ms, BOOL alertable)
{
    double m = cur_mult();
    if ((g.flags & FLAG_SLEEP) && m > 1.0 && ms > 0 && ms != INFINITE)
        return real_SleepEx((DWORD)((double)ms / m), alertable);
    return real_SleepEx(ms, alertable);
}

/* Forward declaration: the hook table and this hook refer to each other. */
static FARPROC WINAPI h_GetProcAddress(HMODULE mod, LPCSTR name);

/* ------------------------------------------------------------------ */
/* Hook table                                                          */
/* ------------------------------------------------------------------ */

typedef struct {
    const char *name;
    void      **real;   /* where the real pointer is stored */
    void       *hook;
} HookEntry;

static HookEntry g_hooks[] = {
    { "QueryPerformanceCounter",        (void**)&real_QPC,
      (void*)h_QueryPerformanceCounter },
    { "GetTickCount",                   (void**)&real_GetTickCount,
      (void*)h_GetTickCount },
    { "GetTickCount64",                 (void**)&real_GetTickCount64,
      (void*)h_GetTickCount64 },
    { "timeGetTime",                    (void**)&real_timeGetTime,
      (void*)h_timeGetTime },
    { "GetSystemTimeAsFileTime",        (void**)&real_GetSystemTimeAsFileTime,
      (void*)h_GetSystemTimeAsFileTime },
    { "GetSystemTimePreciseAsFileTime", (void**)&real_GetSystemTimePreciseAsFileTime,
      (void*)h_GetSystemTimePreciseAsFileTime },
    { "Sleep",                          (void**)&real_Sleep,
      (void*)h_Sleep },
    { "SleepEx",                        (void**)&real_SleepEx,
      (void*)h_SleepEx },
    { "GetProcAddress",                 (void**)&real_GetProcAddress,
      (void*)h_GetProcAddress },
};

#define HOOK_COUNT (sizeof(g_hooks) / sizeof(g_hooks[0]))

/* Catch code that resolves these names at run time instead of importing
 * them. Comparing against the real addresses makes this independent of
 * which module the caller asked -- kernel32, kernelbase or an api-set. */
static FARPROC WINAPI h_GetProcAddress(HMODULE mod, LPCSTR name)
{
    FARPROC p = real_GetProcAddress(mod, name);
    size_t i;
    if (!p) return p;
    for (i = 0; i < HOOK_COUNT; i++)
        if (*g_hooks[i].real == (void*)p)
            return (FARPROC)g_hooks[i].hook;
    return p;
}

/* ------------------------------------------------------------------ */
/* IAT patching                                                        */
/* ------------------------------------------------------------------ */

static void *resolve(const char *mod, const char *fn)
{
    HMODULE h = GetModuleHandleA(mod);
    if (!h) h = LoadLibraryA(mod);
    return h ? (void*)GetProcAddress(h, fn) : NULL;
}

static BOOL write_thunk(void **slot, void *value)
{
    DWORD old;
    if (*slot == value) return FALSE;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) return FALSE;
    *slot = value;
    VirtualProtect(slot, sizeof(void*), old, &old);
    return TRUE;
}

static int patch_module(HMODULE mod)
{
    BYTE *base = (BYTE*)mod;
    IMAGE_DOS_HEADER *dos;
    IMAGE_NT_HEADERS *nt;
    IMAGE_IMPORT_DESCRIPTOR *imp;
    DWORD rva;
    int n = 0;

    if (!mod || mod == g_self) return 0;

    dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;

    rva = nt->OptionalHeader
            .DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!rva) return 0;

    for (imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + rva);
         imp->Name && imp->FirstThunk;
         imp++)
    {
        IMAGE_THUNK_DATA *names, *addrs;
        if (!imp->OriginalFirstThunk) continue;   /* bound without names */

        names = (IMAGE_THUNK_DATA*)(base + imp->OriginalFirstThunk);
        addrs = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk);

        for (; names->u1.AddressOfData; names++, addrs++) {
            IMAGE_IMPORT_BY_NAME *nm;
            size_t i;
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            nm = (IMAGE_IMPORT_BY_NAME*)(base + names->u1.AddressOfData);
            /* Match on the function name only: the same functions are
             * imported through kernel32, kernelbase and several
             * api-ms-win-core-* stubs depending on the toolchain. */
            for (i = 0; i < HOOK_COUNT; i++) {
                if (lstrcmpA((LPCSTR)nm->Name, g_hooks[i].name) != 0) continue;
                if (write_thunk((void**)&addrs->u1.Function, g_hooks[i].hook))
                    n++;
                break;
            }
        }
    }
    return n;
}

/*
 * True if this module contains the code of any function we hook.
 *
 * This guard is what keeps the hooks from recursing into themselves. On
 * current Windows, kernel32's exported Sleep, QueryPerformanceCounter and
 * GetTickCount64 are not implementations but forwarding thunks shaped like
 * `jmp [__imp_Sleep]`, resolved through kernel32's OWN import table into
 * kernelbase. GetProcAddress hands back the address of that thunk. Patch
 * kernel32's import table as well and real_Sleep jumps straight back into
 * h_Sleep -- an infinite recursion that ends as a stack overflow a few
 * milliseconds after injection.
 *
 * Testing the address range instead of matching module names also covers
 * kernelbase, winmm and whichever api-set stub a given Windows build puts
 * the implementation in.
 */
static BOOL hosts_real_function(BYTE *base, DWORD size)
{
    size_t i;
    for (i = 0; i < HOOK_COUNT; i++) {
        BYTE *p = (BYTE*)*g_hooks[i].real;
        if (p && p >= base && p < base + size) return TRUE;
    }
    return FALSE;
}

/*
 * The address check above catches the module a real function lives in, but
 * a forward can take two hops (kernel32 thunk -> kernelbase thunk -> ntdll),
 * and only the first hop is visible as an address. These modules forward and
 * implement the API set; none of them is a caller whose sense of time we
 * want to change, so they are never patched.
 */
static BOOL is_api_host(const WCHAR *name)
{
    static const WCHAR *const names[] = {
        L"ntdll.dll", L"kernel32.dll", L"kernelbase.dll", L"win32u.dll",
    };
    size_t i;
    for (i = 0; i < sizeof(names) / sizeof(names[0]); i++)
        if (lstrcmpiW(name, names[i]) == 0) return TRUE;

    /* api-ms-win-*.dll and ext-ms-win-*.dll are api-set forwarding stubs. */
    return (name[0] == L'a' || name[0] == L'A' ||
            name[0] == L'e' || name[0] == L'E')
           && (CompareStringOrdinal(name, 7, L"api-ms-", 7, TRUE) == CSTR_EQUAL
            || CompareStringOrdinal(name, 7, L"ext-ms-", 7, TRUE) == CSTR_EQUAL);
}

static int patch_all_modules(void)
{
    static BOOL first = TRUE;   /* log the skip list once, not every pass */
    HANDLE snap;
    MODULEENTRY32W me;
    int n = 0;

    snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
                                   GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE) return 0;

    me.dwSize = sizeof(me);
    if (Module32FirstW(snap, &me)) {
        do {
            int k;
            if (is_api_host(me.szModule)
                || hosts_real_function(me.modBaseAddr, me.modBaseSize)) {
                if (first) gsp_log("  skip %S (api host)", me.szModule);
                continue;
            }
            k = patch_module(me.hModule);
            if (k) gsp_log("  patched %d thunk(s) in %S", k, me.szModule);
            n += k;
        } while (Module32NextW(snap, &me));
    }
    CloseHandle(snap);
    first = FALSE;
    return n;
}

/* ------------------------------------------------------------------ */
/* Crash diagnostics: report what faulted and inside which module.     */
/* ------------------------------------------------------------------ */

#ifndef GSP_NO_LOG
static LONG CALLBACK gsp_veh(EXCEPTION_POINTERS *ep)
{
    EXCEPTION_RECORD *r = ep->ExceptionRecord;
    HMODULE owner = NULL;
    char name[MAX_PATH] = "?";

    /* Only first-chance faults that actually kill a process are interesting;
     * C++/SEH flow control noise (0x406d1388, 0xe06d7363) is not. */
    if (r->ExceptionCode == 0x406D1388 || r->ExceptionCode == 0xE06D7363)
        return EXCEPTION_CONTINUE_SEARCH;

    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                       | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCSTR)r->ExceptionAddress, &owner);
    if (owner) GetModuleFileNameA(owner, name, sizeof(name));

    gsp_log("!! exception %08lx at %p in %s (params=%lu %p)",
            (unsigned long)r->ExceptionCode, r->ExceptionAddress, name,
            (unsigned long)r->NumberParameters,
            r->NumberParameters > 1 ? (void*)r->ExceptionInformation[1] : NULL);
    return EXCEPTION_CONTINUE_SEARCH;
}
#endif

/* ------------------------------------------------------------------ */
/* Startup and control loop                                            */
/* ------------------------------------------------------------------ */

static void open_shared(void)
{
    WCHAR name[64];
    wsprintfW(name, L"Local\\GameSpeedCtl_%lu", GetCurrentProcessId());

    /* The GUI creates the block before injecting, so its DACL -- and the
     * question of who may steer this process -- belongs to the GUI. */
    g_shm_handle = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!g_shm_handle) return;
    g_shm = (GspShared*)MapViewOfFile(g_shm_handle, FILE_MAP_ALL_ACCESS,
                                      0, 0, sizeof(GspShared));
    if (g_shm && g_shm->magic != GSP_MAGIC) {   /* someone else's mapping */
        UnmapViewOfFile(g_shm);
        g_shm = NULL;
    }
}

static BOOL init_clock(void)
{
    LARGE_INTEGER f, c;
    FILETIME ft;

    real_QPC            = (fn_QPC)resolve("kernel32.dll", "QueryPerformanceCounter");
    real_QPF            = (fn_QPF)resolve("kernel32.dll", "QueryPerformanceFrequency");
    real_GetTickCount   = (fn_GetTickCount)resolve("kernel32.dll", "GetTickCount");
    real_GetTickCount64 = (fn_GetTickCount64)resolve("kernel32.dll", "GetTickCount64");
    real_GetSystemTimeAsFileTime =
        (fn_GetSysTimeAsFileTime)resolve("kernel32.dll", "GetSystemTimeAsFileTime");
    real_GetSystemTimePreciseAsFileTime =
        (fn_GetSysTimeAsFileTime)resolve("kernel32.dll", "GetSystemTimePreciseAsFileTime");
    real_Sleep          = (fn_Sleep)resolve("kernel32.dll", "Sleep");
    real_SleepEx        = (fn_SleepEx)resolve("kernel32.dll", "SleepEx");
    real_GetProcAddress = (fn_GetProcAddress)resolve("kernel32.dll", "GetProcAddress");
    real_timeGetTime    = (fn_timeGetTime)resolve("winmm.dll", "timeGetTime");

    if (!real_QPC || !real_QPF || !real_GetTickCount || !real_Sleep
        || !real_GetProcAddress)
        return FALSE;

    if (!real_QPF(&f) || f.QuadPart <= 0) return FALSE;
    real_QPC(&c);
    real_GetSystemTimeAsFileTime(&ft);

    InitializeCriticalSection(&g.cs);
    g.qpf         = f.QuadPart;
    g.real_anchor = c.QuadPart;
    g.virt_qpc    = c.QuadPart;
    g.virt_origin = c.QuadPart;
    g.frac        = 0.0;
    g.mult        = 1.0;
    g.flags       = FLAG_SLEEP;
    g.tick_base   = real_GetTickCount();
    g.tick64_base = real_GetTickCount64 ? real_GetTickCount64()
                                        : (ULONGLONG)g.tick_base;
    g.mm_base     = real_timeGetTime ? real_timeGetTime() : g.tick_base;
    g.ft_base     = ((ULONGLONG)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    g.ready       = TRUE;
    return TRUE;
}

static DWORD WINAPI worker(LPVOID unused)
{
    LONGLONG last_real, last_virt;
    UINT32 last_seq = 0xFFFFFFFFu;
    LARGE_INTEGER c;
    int ticks = 0;
    (void)unused;

    gsp_log("worker: start");
#ifndef GSP_NO_LOG
    AddVectoredExceptionHandler(1, gsp_veh);
#endif
    if (!init_clock()) { gsp_log("worker: init_clock FAILED"); return 0; }
    gsp_log("worker: clock ready, qpf=%ld", (long)g.qpf);

    open_shared();
    gsp_log("worker: shm=%p", (void*)g_shm);

    g_patched = patch_all_modules();
    gsp_log("worker: first pass patched %d thunks", (int)g_patched);
    if (g_shm) {
        g_shm->patched = (UINT32)g_patched;
        g_shm->backend = BACKEND_CLOCK;
        g_shm->state   = 1;
    }

    real_QPC(&c);
    last_real = c.QuadPart;
    last_virt = virt_qpc_now();
    gsp_log("worker: entering loop");

    for (;;) {
        real_Sleep(250);   /* real sleep: our own loop must not be scaled */

        if (g_shm) {
            double want_m   = g_shm->mult;
            UINT32 want_fl  = g_shm->flags;
            UINT32 want_seq = g_shm->seq;

            if (want_m < 0.01)   want_m = 0.01;
            if (want_m > 1000.0) want_m = 1000.0;

            /* The sequence number alone is not enough to spot a change: a
             * replacement controller starts its counter over and can repeat a
             * number already seen, so the values are compared as well. This
             * thread is the only writer of g.mult and g.flags, so reading
             * them here without the lock is safe. */
            if (want_seq != last_seq || want_m != g.mult || want_fl != g.flags) {
                virt_qpc_now();            /* settle the old rate first */
                EnterCriticalSection(&g.cs);
                g.mult  = want_m;
                g.flags = want_fl;
                LeaveCriticalSection(&g.cs);
                last_seq = want_seq;
                gsp_log("rate -> %d/100, flags %lu",
                        (int)(want_m * 100), (unsigned long)want_fl);
            }
            /* Report the ratio actually achieved, which is what the user can
             * compare against the figure they asked for. */
            {
                LONGLONG rd, vd;
                real_QPC(&c);
                vd = virt_qpc_now() - last_virt;
                rd = c.QuadPart - last_real;
                if (rd > 0) g_shm->actual = (double)vd / (double)rd;
                last_real = c.QuadPart;
                last_virt += vd;
            }
        }

        if (ticks < 4) gsp_log("loop %d: mult=%d/100 actual=%d/100 patched=%d",
                               (int)ticks, (int)(cur_mult() * 100),
                               g_shm ? (int)(g_shm->actual * 100) : -1,
                               (int)g_patched);
        ticks++;

        /* Modules load late (renderer, mono, plugin DLLs). Re-patch so that
         * import tables arriving later are covered too. */
        g_patched += patch_all_modules();
        if (g_shm) g_shm->patched = (UINT32)g_patched;
    }
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = (HMODULE)inst;
        gsp_log("--- DllMain attach, self=%p ---", (void*)inst);
        DisableThreadLibraryCalls(inst);
        CreateThread(NULL, 0, worker, NULL, 0, NULL);
    }
    return TRUE;
}
