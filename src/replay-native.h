#ifndef RA_REPLAY_NATIVE_H
#define RA_REPLAY_NATIVE_H
#include <stddef.h>
/* Private CRT replacements: the game uses Watcom's register calling convention. */
void *nr_memcpy(void *, const void *, size_t);
void *nr_memmove(void *, const void *, size_t);
void *nr_memset(void *, int, size_t);
int nr_memcmp(const void *, const void *, size_t);
size_t nr_strlen(const char *);
#endif
