#include <windows.h>
#include <cstring>
#include <string>

struct DomainSearch
{
    void* unityRoot = nullptr;
    void* unityChild = nullptr;
    const char* (*name)(void*);
};

static void FindDomain(void* domain, void* state)
{
    auto search = static_cast<DomainSearch*>(state);
    auto name = search->name(domain);
    if (!name) return;
    if (std::strcmp(name, "Unity Root Domain") == 0) search->unityRoot = domain;
    if (std::strcmp(name, "Unity Child Domain") == 0) search->unityChild = domain;
}

static std::string Utf8(const std::wstring& value)
{
    int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, &result[0], size, nullptr, nullptr);
    return result;
}

static void WriteException(HMODULE mono, void* exception, const std::wstring& connectionPath)
{
    auto stringify = reinterpret_cast<void* (*)(void*, void**)>(GetProcAddress(mono, "mono_object_to_string"));
    auto utf8 = reinterpret_cast<char* (*)(void*)>(GetProcAddress(mono, "mono_string_to_utf8"));
    auto release = reinterpret_cast<void (*)(void*)>(GetProcAddress(mono, "mono_free"));
    if (!stringify || !utf8 || !release) return;
    void* formattingError = nullptr;
    auto textObject = stringify(exception, &formattingError);
    if (!textObject || formattingError) return;
    auto text = utf8(textObject);
    auto file = CreateFileW((connectionPath + L".error").c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        DWORD written;
        WriteFile(file, text, static_cast<DWORD>(std::strlen(text)), &written, nullptr);
        CloseHandle(file);
    }
    release(text);
}

extern "C" __declspec(dllexport) unsigned long long GetDomainEpoch() { return 0; }
extern "C" __declspec(dllexport) void SetRecoveryEnabled(int) { }

extern "C" __declspec(dllexport) DWORD WINAPI Bootstrap(void* argument)
{
    HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    if (!mono) mono = GetModuleHandleW(L"mono-2.0-sgen.dll");
    if (!mono) return 1;
#define API(name, type) auto name = reinterpret_cast<type>(GetProcAddress(mono, #name)); if (!name) return 2
    API(mono_get_root_domain, void* (*)());
    API(mono_thread_attach, void* (*)(void*));
    API(mono_thread_detach, void (*)(void*));
    API(mono_domain_foreach, void (*)(void (*)(void*, void*), void*));
    API(mono_domain_set, int (*)(void*, int));
    API(mono_domain_assembly_open, void* (*)(void*, const char*));
    API(mono_assembly_get_image, void* (*)(void*));
    API(mono_class_from_name, void* (*)(void*, const char*, const char*));
    API(mono_class_get_method_from_name, void* (*)(void*, const char*, int));
    API(mono_string_new, void* (*)(void*, const char*));
    API(mono_runtime_invoke, void* (*)(void*, void*, void**, void**));

    auto domainName = reinterpret_cast<const char* (*)(void*)>(GetProcAddress(mono, "mono_domain_get_friendly_name"));
    if (!domainName) return 2;
    std::wstring input(static_cast<wchar_t*>(argument));
    auto first = input.find(L'\n');
    if (first == std::wstring::npos) return 3;
    auto payloadPath = Utf8(input.substr(0, first));
    auto config = input.substr(first + 1);
    auto configUtf8 = Utf8(config);
    auto configSeparator = config.find(L'\n');
    auto connectionPath = configSeparator == std::wstring::npos ? config : config.substr(0, configSeparator);

    auto root = mono_get_root_domain();
    auto thread = mono_thread_attach(root);
    if (!thread) return 8;
    DomainSearch search { nullptr, nullptr, domainName };
    mono_domain_foreach(FindDomain, &search);
    auto domain = search.unityRoot ? search.unityRoot : (search.unityChild ? search.unityChild : root);
    DWORD status = 4;
    if (domain && mono_domain_set(domain, 0))
    {
        auto assembly = mono_domain_assembly_open(domain, payloadPath.c_str());
        status = 5;
        if (assembly)
        {
            auto klass = mono_class_from_name(mono_assembly_get_image(assembly), "AutoGame", "Entry");
            auto method = klass ? mono_class_get_method_from_name(klass, "Initialize", 1) : nullptr;
            status = 6;
            if (method)
            {
                void* args[] = { mono_string_new(domain, configUtf8.c_str()) };
                void* exception = nullptr;
                mono_runtime_invoke(method, nullptr, args, &exception);
                status = exception ? 7 : 0;
                if (exception) WriteException(mono, exception, connectionPath);
            }
        }
    }
    mono_thread_detach(thread);
    return status;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
