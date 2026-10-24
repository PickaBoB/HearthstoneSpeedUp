/*
 * testclock.exe -- a stand-in for a game, used to verify the hook.
 *
 * It must be native C, not .NET: a C program imports the timer functions
 * through its import table, which is exactly what gsphook patches, while
 * .NET resolves a DllImport lazily and caches the raw pointer, so an
 * injection arriving later never reaches it. Games are native here.
 *
 * Every column is a multiplier against real time, so an unhooked run reads
 * x1.00 across the board. The reference clock is GetSystemTimeAsFileTime,
 * which the hook leaves alone unless the wall-clock flag is set.
 *
 *   zig cc -target x86_64-windows-gnu -O2 -o testclock.exe testclock.c \
 *          -lkernel32 -lwinmm
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>

/* WIN32_LEAN_AND_MEAN drops mmsystem.h, and timeapi.h is not in every
 * toolchain, so declare the one multimedia function we use. */
DWORD WINAPI timeGetTime(void);

static ULONGLONG wall_100ns(void)
{
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    return ((ULONGLONG)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}

int main(void)
{
    LARGE_INTEGER freq, qpc, prev_qpc;
    ULONGLONG tick, prev_tick, wall, prev_wall, wall0;
    DWORD mm, prev_mm;

    setvbuf(stdout, NULL, _IONBF, 0);   /* output is usually redirected */

    if (!QueryPerformanceFrequency(&freq) || freq.QuadPart <= 0) {
        fprintf(stderr, "QueryPerformanceFrequency failed\n");
        return 1;
    }

    QueryPerformanceCounter(&prev_qpc);
    prev_tick = GetTickCount64();
    prev_mm   = timeGetTime();
    prev_wall = wall0 = wall_100ns();

    printf("PID %lu   %s   qpf=%lld\n",
           (unsigned long)GetCurrentProcessId(),
           sizeof(void*) == 8 ? "x64" : "x86",
           (long long)freq.QuadPart);
    printf("Inject the hook into this PID and change the speed.\n");
    printf("Columns are multipliers vs real time; expect x1.00 with no hook.\n\n");
    printf("  real s        QPC   GetTickCount64   timeGetTime\n");

    for (;;) {
        double real_ms, r_qpc, r_tick, r_mm;

        /* Wait one real second. Sleep() is scaled by the hook, so the wall
         * clock decides when the second is up, not the sleep itself. */
        {
            ULONGLONG deadline = prev_wall + 10000000ull;
            while (wall_100ns() < deadline) Sleep(5);
        }

        wall = wall_100ns();
        QueryPerformanceCounter(&qpc);
        tick = GetTickCount64();
        mm   = timeGetTime();

        real_ms = (double)(wall - prev_wall) / 10000.0;
        if (real_ms <= 0.0) continue;

        r_qpc  = (double)(qpc.QuadPart - prev_qpc.QuadPart)
                 * 1000.0 / (double)freq.QuadPart / real_ms;
        r_tick = (double)(tick - prev_tick) / real_ms;
        r_mm   = (double)(DWORD)(mm - prev_mm) / real_ms;

        printf("  %6.1f   x%8.2f   x%12.2f   x%9.2f\n",
               (double)(wall - wall0) / 10000000.0, r_qpc, r_tick, r_mm);

        prev_qpc = qpc; prev_tick = tick; prev_mm = mm; prev_wall = wall;
    }
}
