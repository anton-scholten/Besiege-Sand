/* Minimal host for Besiege's embedded Mono, so we can run the game's own mcs.dll
   compiler offline instead of discovering compile errors by launching the game. */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>

typedef void (*set_dirs_t)(const char *, const char *);
typedef void (*set_asm_path_t)(const char *);
typedef void *(*jit_init_version_t)(const char *, const char *);
typedef void *(*assembly_open_t)(const char *, int *);
typedef int (*jit_exec_t)(void *, void *, int, char **);
typedef void (*jit_cleanup_t)(void *);

int main(int argc, char **argv) {
    /* See the same call in besiegecc.c: Mono's System.Console initializer blows
       up on a modern terminfo entry when stderr is a TTY, and anything run here
       writes to the console. Only reproduces in a real terminal. */
    setenv("TERM", "dumb", 1);

    const char *libmono = getenv("LIBMONO");
    const char *managed = getenv("MANAGED");
    const char *etcdir  = getenv("MONOETC");
    const char *target  = getenv("TARGET_ASM");
    void *h = dlopen(libmono, RTLD_NOW | RTLD_GLOBAL);
    if (!h) { fprintf(stderr, "dlopen failed: %s\n", dlerror()); return 2; }

    set_dirs_t set_dirs           = (set_dirs_t)dlsym(h, "mono_set_dirs");
    set_asm_path_t set_asm_path   = (set_asm_path_t)dlsym(h, "mono_set_assemblies_path");
    jit_init_version_t jit_init   = (jit_init_version_t)dlsym(h, "mono_jit_init_version");
    assembly_open_t asm_open      = (assembly_open_t)dlsym(h, "mono_assembly_open");
    jit_exec_t jit_exec           = (jit_exec_t)dlsym(h, "mono_jit_exec");
    jit_cleanup_t jit_cleanup     = (jit_cleanup_t)dlsym(h, "mono_jit_cleanup");
    if (!set_dirs || !jit_init || !asm_open || !jit_exec) {
        fprintf(stderr, "missing mono symbols\n"); return 2;
    }

    set_dirs(managed, etcdir);
    if (set_asm_path) set_asm_path(managed);

    void *domain = jit_init(target, "v2.0.50727");
    if (!domain) { fprintf(stderr, "jit_init failed\n"); return 2; }

    int status = 0;
    void *asmb = asm_open(target, &status);
    if (!asmb) { fprintf(stderr, "could not open %s (status %d)\n", target, status); return 2; }

    /* argv[0] must be the assembly path for Mono's arg handling. */
    argv[0] = (char *)target;
    int rc = jit_exec(domain, asmb, argc, argv);
    if (jit_cleanup) jit_cleanup(domain);
    return rc;
}
