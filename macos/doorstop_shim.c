/*
 * doorstop_shim — minimal UnityDoorstop replacement for macOS.
 *
 * Why this exists (two separate macOS/Unity-6 problems):
 *
 * 1. UnityDoorstop 4.5.0 fails on Unity 6 / macOS 26 binaries: its plthook
 *    parses LC_DYLD_CHAINED_FIXUPS by hand and aborts with
 *    "unknown imports format 0". This shim avoids Mach-O parsing entirely:
 *    it uses dyld's native __interpose mechanism (applied by dyld itself at
 *    bind time, format-agnostic) to intercept the Unity player's dlsym()
 *    lookup of mono_jit_init_version, then runs the Doorstop managed
 *    entrypoint (Doorstop.Entrypoint.Start) after the Mono runtime starts.
 *
 * 2. THE System.Native TRAP. Unity's player installs Mono's dllmap table
 *    (mono_config_parse, which maps "System.Native" ->
 *    "$mono_libdir/libmono-native.dylib") only *after* mono_jit_init_version
 *    returns. BepInEx's DoorstopEntrypoint.Start() calls DateTime.Now on its
 *    very first line; on Unix that walks TimeZoneInfo -> File.Exists ->
 *    Interop.Sys, whose static constructor P/Invokes into System.Native.
 *    Running that one line too early makes the lookup fail, and a failed
 *    static constructor is cached for the life of the AppDomain — so every
 *    later File.Exists in the *game itself* re-throws
 *    TypeInitializationException and the game hangs on the intro screen.
 *    (This never bites on Windows, where System.IO uses Win32 directly.)
 *
 *    Fix: register the native dllmaps ourselves, with absolute paths, before
 *    invoking the managed entrypoint. Unity's later mono_config_parse simply
 *    prepends equivalent entries; both point at the same dylib.
 *
 * Reads the same DOORSTOP_* environment variables as UnityDoorstop and
 * exports the same managed-side variables, so BepInEx's
 * BepInEx.Unity.Mono.Preloader.dll works unchanged.
 *
 * Build: see build.sh (clang -arch arm64 -arch x86_64 -dynamiclib).
 */

#include <dlfcn.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <mach-o/dyld.h>

#define LOG(...) do { \
    fprintf(stderr, "[doorstop_shim] " __VA_ARGS__); \
    fprintf(stderr, "\n"); \
} while (0)

typedef void MonoDomain;
typedef void MonoAssembly;
typedef void MonoImage;
typedef void MonoClass;
typedef void MonoMethod;
typedef void MonoObject;

static MonoDomain *(*real_mono_jit_init_version)(const char *, const char *);

static char *(*p_mono_assembly_getrootdir)(void);
static void (*p_mono_set_assemblies_path)(const char *);
static MonoAssembly *(*p_mono_domain_assembly_open)(MonoDomain *, const char *);
static MonoImage *(*p_mono_assembly_get_image)(MonoAssembly *);
static MonoClass *(*p_mono_class_from_name)(MonoImage *, const char *, const char *);
static MonoMethod *(*p_mono_class_get_method_from_name)(MonoClass *, const char *, int);
static MonoObject *(*p_mono_runtime_invoke)(MonoMethod *, void *, void **, MonoObject **);
static void (*p_mono_print_unhandled_exception)(MonoObject *);
static void (*p_mono_dllmap_insert)(MonoImage *, const char *, const char *,
                                    const char *, const char *);
static void (*p_mono_config_parse)(const char *);
static void (*p_mono_domain_set_config)(MonoDomain *, const char *, const char *);
static void *(*p_mono_thread_current)(void);
static void (*p_mono_thread_set_main)(void *);

static int entry_done = 0;

static void resolve_mono_funcs(void *handle)
{
#define RESOLVE(var, name) var = dlsym(handle, name)
    RESOLVE(p_mono_assembly_getrootdir, "mono_assembly_getrootdir");
    RESOLVE(p_mono_set_assemblies_path, "mono_set_assemblies_path");
    RESOLVE(p_mono_domain_assembly_open, "mono_domain_assembly_open");
    RESOLVE(p_mono_assembly_get_image, "mono_assembly_get_image");
    RESOLVE(p_mono_class_from_name, "mono_class_from_name");
    RESOLVE(p_mono_class_get_method_from_name, "mono_class_get_method_from_name");
    RESOLVE(p_mono_runtime_invoke, "mono_runtime_invoke");
    RESOLVE(p_mono_print_unhandled_exception, "mono_print_unhandled_exception");
    RESOLVE(p_mono_dllmap_insert, "mono_dllmap_insert");
    RESOLVE(p_mono_config_parse, "mono_config_parse");
    RESOLVE(p_mono_domain_set_config, "mono_domain_set_config");
    RESOLVE(p_mono_thread_current, "mono_thread_current");
    RESOLVE(p_mono_thread_set_main, "mono_thread_set_main");
#undef RESOLVE
}

/*
 * Map the P/Invoke module names the BCL uses on macOS onto the dylibs Unity
 * actually ships, using absolute paths. Must run before any managed code
 * touches Interop.Sys — see note 2 at the top of this file.
 *
 * The dylibs live next to the Mono runtime (Contents/Frameworks), which we
 * locate from the address of mono_jit_init_version rather than guessing.
 */
static void register_native_dllmaps(void)
{
    static const struct { const char *scope; const char *lib; } maps[] = {
        { "System.Native",                              "libmono-native.dylib"     },
        { "System.Net.Security.Native",                 "libmono-native.dylib"     },
        { "System.Security.Cryptography.Native.Apple",  "libmono-native.dylib"     },
        { "MonoPosixHelper",                            "libMonoPosixHelper.dylib" },
    };

    Dl_info info;
    char dir[PATH_MAX];
    char *slash;
    size_t i;

    if (!p_mono_dllmap_insert) {
        LOG("mono_dllmap_insert unavailable; falling back to mono_config_parse");
        if (p_mono_config_parse)
            p_mono_config_parse(NULL);
        return;
    }

    if (!real_mono_jit_init_version ||
        !dladdr((void *)real_mono_jit_init_version, &info) || !info.dli_fname) {
        LOG("cannot locate the mono runtime dylib; skipping dllmap setup");
        return;
    }

    if (strlen(info.dli_fname) >= sizeof(dir)) {
        LOG("mono runtime path too long; skipping dllmap setup");
        return;
    }
    strcpy(dir, info.dli_fname);
    slash = strrchr(dir, '/');
    if (!slash) {
        LOG("unexpected mono runtime path: %s", info.dli_fname);
        return;
    }
    *slash = '\0';

    for (i = 0; i < sizeof(maps) / sizeof(maps[0]); i++) {
        char target[PATH_MAX];
        if (snprintf(target, sizeof(target), "%s/%s", dir, maps[i].lib) >= (int)sizeof(target))
            continue;
        if (access(target, F_OK) != 0) {
            LOG("skipping %s: %s not found", maps[i].scope, target);
            continue;
        }
        p_mono_dllmap_insert(NULL, maps[i].scope, NULL, target, NULL);
        LOG("dllmap %s -> %s", maps[i].scope, target);
    }
}

/*
 * Give the root domain an application base and a config-file name, the way
 * UnityDoorstop does. Unity itself never sets these, which leaves
 * AppDomain.CurrentDomain.SetupInformation.ConfigurationFile null; the first
 * managed access to System.Diagnostics.Trace then walks into
 * ConfigurationManager and dies with
 *   ConfigurationErrorsException -> "The 'ExeConfigFilename' argument cannot be null."
 * BepInEx hits exactly that in TraceLogSource.CreateSource() during
 * UnityPreloader.Run(). The file itself does not have to exist -- Mono only
 * requires the name to be non-null.
 */
static void setup_domain_config(MonoDomain *domain)
{
    char exe_path[PATH_MAX];
    char resolved[PATH_MAX];
    char app_dir[PATH_MAX];
    char config_file[PATH_MAX];
    uint32_t size = sizeof(exe_path);
    char *slash;

    if (!p_mono_domain_set_config) {
        LOG("mono_domain_set_config unavailable; skipping domain config");
        return;
    }

    if (_NSGetExecutablePath(exe_path, &size) != 0) {
        LOG("cannot determine executable path; skipping domain config");
        return;
    }
    if (!realpath(exe_path, resolved)) {
        if (strlen(exe_path) >= sizeof(resolved))
            return;
        strcpy(resolved, exe_path);
    }

    strcpy(app_dir, resolved);
    slash = strrchr(app_dir, '/');
    if (!slash)
        return;
    *slash = '\0';

    if (snprintf(config_file, sizeof(config_file), "%s.config", resolved) >= (int)sizeof(config_file))
        return;

    p_mono_domain_set_config(domain, app_dir, config_file);
    LOG("domain config: base=%s config=%s", app_dir, config_file);

    /* Doorstop also marks this thread as Mono's main thread. */
    if (p_mono_thread_set_main && p_mono_thread_current) {
        void *thread = p_mono_thread_current();
        if (thread)
            p_mono_thread_set_main(thread);
    }
}

static void set_managed_env(const char *target, const char *root, const char *search_dirs)
{
    char exe_path[4096];
    uint32_t size = sizeof(exe_path);

    setenv("DOORSTOP_INITIALIZED", "TRUE", 1);
    setenv("DOORSTOP_INVOKE_DLL_PATH", target, 1);
    if (root)
        setenv("DOORSTOP_MANAGED_FOLDER_DIR", root, 1);
    if (search_dirs)
        setenv("DOORSTOP_DLL_SEARCH_DIRS", search_dirs, 1);
    if (_NSGetExecutablePath(exe_path, &size) == 0)
        setenv("DOORSTOP_PROCESS_PATH", exe_path, 1);

    Dl_info info;
    if (real_mono_jit_init_version &&
        dladdr((void *)real_mono_jit_init_version, &info) && info.dli_fname)
        setenv("DOORSTOP_MONO_LIB_PATH", info.dli_fname, 1);
}

static void run_entrypoint(MonoDomain *domain, const char *target)
{
    if (!p_mono_domain_assembly_open || !p_mono_assembly_get_image ||
        !p_mono_class_from_name || !p_mono_class_get_method_from_name ||
        !p_mono_runtime_invoke) {
        LOG("missing mono functions, cannot run entrypoint");
        return;
    }

    MonoAssembly *assembly = p_mono_domain_assembly_open(domain, target);
    if (!assembly) {
        LOG("failed to open target assembly: %s", target);
        return;
    }

    MonoImage *image = p_mono_assembly_get_image(assembly);
    MonoClass *klass = image ? p_mono_class_from_name(image, "Doorstop", "Entrypoint") : NULL;
    MonoMethod *method = klass ? p_mono_class_get_method_from_name(klass, "Start", 0) : NULL;
    if (!method) {
        LOG("Doorstop.Entrypoint.Start not found in %s", target);
        return;
    }

    MonoObject *exc = NULL;
    p_mono_runtime_invoke(method, NULL, NULL, &exc);
    if (exc) {
        LOG("Doorstop.Entrypoint.Start threw a managed exception");
        if (p_mono_print_unhandled_exception)
            p_mono_print_unhandled_exception(exc);
    } else {
        LOG("Doorstop.Entrypoint.Start executed");
    }
}

static MonoDomain *my_mono_jit_init_version(const char *name, const char *version)
{
    const char *target = getenv("DOORSTOP_TARGET_ASSEMBLY");
    const char *override = getenv("DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE");
    char *search_dirs = NULL;

    LOG("mono_jit_init_version(%s, %s)", name ? name : "?", version ? version : "?");

    const char *root = p_mono_assembly_getrootdir ? p_mono_assembly_getrootdir() : NULL;
    if (override && *override && p_mono_set_assemblies_path) {
        if (root)
            asprintf(&search_dirs, "%s:%s", override, root);
        else
            search_dirs = strdup(override);
        p_mono_set_assemblies_path(search_dirs);
        LOG("assembly search path: %s", search_dirs);
    }

    if (target)
        set_managed_env(target, root, search_dirs ? search_dirs : root);
    free(search_dirs);

    MonoDomain *domain = real_mono_jit_init_version(name, version);
    if (!domain) {
        LOG("mono_jit_init_version returned NULL");
        return domain;
    }

    if (target && !entry_done) {
        entry_done = 1;
        /* Order matters: both of these must happen before managed code runs. */
        register_native_dllmaps();
        setup_domain_config(domain);
        run_entrypoint(domain, target);
    }
    return domain;
}

/* Interposed dlsym: everything passes through untouched except the Unity
 * player's lookup of mono_jit_init_version, which gets our wrapper. */
static void *my_dlsym(void *handle, const char *symbol)
{
    void *res = dlsym(handle, symbol);

    if (res && symbol && strcmp(symbol, "mono_jit_init_version") == 0 &&
        !real_mono_jit_init_version) {
        const char *enabled = getenv("DOORSTOP_ENABLED");
        if (!enabled || strcmp(enabled, "1") != 0)
            return res;
        real_mono_jit_init_version = (MonoDomain *(*)(const char *, const char *))res;
        resolve_mono_funcs(handle);
        LOG("hooked mono_jit_init_version");
        return (void *)my_mono_jit_init_version;
    }
    return res;
}

__attribute__((used, section("__DATA,__interpose")))
static struct { const void *replacement; const void *replacee; }
interposers[] = {
    { (const void *)my_dlsym, (const void *)dlsym },
};

__attribute__((constructor))
static void shim_init(void)
{
    const char *enabled = getenv("DOORSTOP_ENABLED");
    if (enabled && strcmp(enabled, "1") == 0)
        LOG("loaded, waiting for mono runtime");
}
