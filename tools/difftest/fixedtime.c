/* fixedtime.c : LD_PRELOAD shim that freezes the wall clock seen through time(2).
 *
 * MAME (running_machine) takes its base date/time from time(NULL); the Epson R4543 RTC of the System 12 board
 * (read by the H8/3002 sound CPU) starts from it, and the game stores that date/time in the NVRAM on its first boot.
 * Without this shim a first boot from an EMPTY nvram is not reproducible (three runs, three different EEPROM images,
 * differing in the stored timestamp and in 800+ bytes derived from it).
 * Only time() is overridden (nothing sleeps on it); clock_gettime/gettimeofday and the monotonic clocks are untouched.
 *
 *   gcc -shared -fPIC -O2 -o fixedtime.so fixedtime.c
 *   DT_FIXED_TIME=1767225600 LD_PRELOAD=./fixedtime.so mame ...      (default 1767225600 = 2026-01-01 00:00:00 UTC)
 */
#include <stdlib.h>
#include <time.h>

time_t time(time_t *t)
{
    static time_t fixed = 0;
    if (!fixed) {
        const char *e = getenv("DT_FIXED_TIME");
        fixed = e ? (time_t)atoll(e) : (time_t)1767225600;
    }
    if (t) *t = fixed;
    return fixed;
}
