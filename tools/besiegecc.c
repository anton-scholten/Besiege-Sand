/* Runs Besiege's own in-game C# compiler (mcs.dll) offline, via the Mono
   embedding API, so mod compile errors can be found without launching the game.
   Usage: besiegecc <mcs args...> */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef void  (*set_dirs_t)(const char *, const char *);
typedef void  (*set_asm_path_t)(const char *);
typedef void *(*jit_init_version_t)(const char *, const char *);
typedef void *(*domain_assembly_open_t)(void *, const char *);
typedef void *(*assembly_get_image_t)(void *);
typedef void *(*class_from_name_t)(void *, const char *, const char *);
typedef void *(*class_get_method_from_name_t)(void *, const char *, int);
typedef void *(*runtime_invoke_t)(void *, void *, void **, void **);
typedef void *(*array_new_t)(void *, void *, unsigned long);
typedef char *(*array_addr_with_size_t)(void *, int, unsigned long);
typedef void *(*string_new_t)(void *, const char *);
typedef void *(*get_string_class_t)(void);
typedef void *(*get_corlib_t)(void);
typedef void *(*object_unbox_t)(void *);
typedef char *(*string_to_utf8_t)(void *);

static void *H;
static void *sym(const char *n) {
    void *p = dlsym(H, n);
    if (!p) { fprintf(stderr, "missing symbol %s\n", n); exit(2); }
    return p;
}

/* Reads an exception by poking at its fields rather than calling ToString().
   When a type initializer has failed the runtime is already unhappy, and
   invoking more managed code (ToString, which is what mono_object_to_string
   does) tends to fail too -- taking the diagnostic with it. Field reads do not
   run any managed code, so they still work. Walks the InnerException chain,
   which for a TypeInitializationException is where the real cause lives. */
static void print_exception(void *domain, void *exc) {
    void *(*object_get_class)(void *) = (void *(*)(void *))dlsym(H, "mono_object_get_class");
    const char *(*class_get_name)(void *) = (const char *(*)(void *))dlsym(H, "mono_class_get_name");
    const char *(*class_get_ns)(void *) = (const char *(*)(void *))dlsym(H, "mono_class_get_namespace");
    void *(*class_get_parent)(void *) = (void *(*)(void *))dlsym(H, "mono_class_get_parent");
    void *(*field_from_name)(void *, const char *) =
        (void *(*)(void *, const char *))dlsym(H, "mono_class_get_field_from_name");
    void *(*field_get_value_object)(void *, void *, void *) =
        (void *(*)(void *, void *, void *))dlsym(H, "mono_field_get_value_object");
    string_to_utf8_t to_utf8 = (string_to_utf8_t)dlsym(H, "mono_string_to_utf8");

    if (!object_get_class || !class_get_name || !field_from_name ||
        !field_get_value_object || !to_utf8) {
        fprintf(stderr, "(the mono build here does not expose enough to read it)\n");
        return;
    }

    for (int depth = 0; exc && depth < 8; depth++) {
        void *klass = object_get_class(exc);
        const char *ns = class_get_ns ? class_get_ns(klass) : "";
        fprintf(stderr, "%*s%s%s%s%s", depth * 2, "",
                depth ? "caused by " : "",
                ns && ns[0] ? ns : "", ns && ns[0] ? "." : "",
                class_get_name(klass));

        /* Fields live on System.Exception, so search up from the actual type. */
        void *msg_field = NULL, *inner_field = NULL, *type_field = NULL;
        for (void *k = klass; k && !(msg_field && inner_field); k = class_get_parent ? class_get_parent(k) : NULL) {
            if (!msg_field)   msg_field   = field_from_name(k, "message");
            if (!inner_field) inner_field = field_from_name(k, "inner_exception");
            if (!type_field)  type_field  = field_from_name(k, "type_name");
        }

        if (type_field) {
            void *s = field_get_value_object(domain, type_field, exc);
            if (s) fprintf(stderr, " (initialising %s)", to_utf8(s));
        }
        if (msg_field) {
            void *s = field_get_value_object(domain, msg_field, exc);
            fprintf(stderr, ": %s\n", s ? to_utf8(s) : "(no message)");
        } else {
            fprintf(stderr, "\n");
        }

        void *inner = inner_field ? field_get_value_object(domain, inner_field, exc) : NULL;
        if (!inner) break;
        exc = inner;
    }
}

int main(int argc, char **argv) {
    /* Force a dumb terminal before Mono starts.
       Mono's System.Console static initializer builds a terminfo-driven console
       driver when stderr is a TTY, and the ancient runtime Besiege ships cannot
       parse modern terminfo entries -- TERM=xterm-256color makes it throw, which
       surfaces as a TypeInitializationException out of the compiler and looks
       for all the world like a broken build. It only reproduces in a real
       terminal, so piping the output hides it.
       A batch compiler has no use for terminal capabilities, so opt out. This
       only affects the Mono runtime hosted in this process. */
    setenv("TERM", "dumb", 1);

    const char *libmono = getenv("LIBMONO");
    const char *managed = getenv("MANAGED");
    const char *etcdir  = getenv("MONOETC");
    char mcs[1024];
    snprintf(mcs, sizeof mcs, "%s/mcs.dll", managed);

    H = dlopen(libmono, RTLD_NOW | RTLD_GLOBAL);
    if (!H) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 2; }

    ((set_dirs_t)sym("mono_set_dirs"))(managed, etcdir);
    ((set_asm_path_t)sym("mono_set_assemblies_path"))(managed);

    void *domain = ((jit_init_version_t)sym("mono_jit_init_version"))(mcs, "v2.0.50727");
    if (!domain) { fprintf(stderr, "jit_init failed\n"); return 2; }

    void *asmb = ((domain_assembly_open_t)sym("mono_domain_assembly_open"))(domain, mcs);
    if (!asmb) { fprintf(stderr, "cannot open mcs.dll\n"); return 2; }
    void *image = ((assembly_get_image_t)sym("mono_assembly_get_image"))(asmb);

    void *klass = ((class_from_name_t)sym("mono_class_from_name"))(
        image, "Mono.CSharp", "CompilerCallableEntryPoint");
    if (!klass) { fprintf(stderr, "no CompilerCallableEntryPoint\n"); return 2; }
    void *method = ((class_get_method_from_name_t)sym("mono_class_get_method_from_name"))(
        klass, "InvokeCompiler", 2);
    if (!method) { fprintf(stderr, "no InvokeCompiler(string[],TextWriter)\n"); return 2; }

    /* string[] args */
    array_new_t array_new = (array_new_t)sym("mono_array_new");
    array_addr_with_size_t addr = (array_addr_with_size_t)sym("mono_array_addr_with_size");
    string_new_t string_new = (string_new_t)sym("mono_string_new");
    void *str_class = ((get_string_class_t)sym("mono_get_string_class"))();

    int n = argc - 1;
    void *arr = array_new(domain, str_class, (unsigned long)n);
    for (int i = 0; i < n; i++) {
        void *s = string_new(domain, argv[i + 1]);
        memcpy(addr(arr, (int)sizeof(void *), (unsigned long)i), &s, sizeof(void *));
    }

    /* Console.Error as the TextWriter to receive diagnostics */
    void *corlib = ((get_corlib_t)sym("mono_get_corlib"))();
    void *console = ((class_from_name_t)sym("mono_class_from_name"))(corlib, "System", "Console");
    void *get_error = ((class_get_method_from_name_t)sym("mono_class_get_method_from_name"))(
        console, "get_Error", 0);
    runtime_invoke_t invoke = (runtime_invoke_t)sym("mono_runtime_invoke");
    void *exc = NULL;
    void *writer = invoke(get_error, NULL, NULL, &exc);
    if (exc) {
        fprintf(stderr, "\n[besiegecc] could not obtain Console.Error:\n");
        print_exception(domain, exc);
        return 3;
    }

    void *params_[2];
    params_[0] = arr;
    params_[1] = writer;
    exc = NULL;
    void *res = invoke(method, NULL, params_, &exc);
    if (exc) {
        /* Print what actually went wrong. Without this the caller only learns
           that "something" threw, which is useless -- and the usual causes
           (output file locked by a running game, unwritable path) are ones the
           message names outright. */
        fprintf(stderr, "\n[besiegecc] the compiler threw a managed exception:\n");
        print_exception(domain, exc);
        return 3;
    }

    unsigned char ok = *(unsigned char *)((object_unbox_t)sym("mono_object_unbox"))(res);
    fprintf(stderr, "\n[besiegecc] compile %s\n", ok ? "SUCCEEDED" : "FAILED");
    return ok ? 0 : 1;
}
