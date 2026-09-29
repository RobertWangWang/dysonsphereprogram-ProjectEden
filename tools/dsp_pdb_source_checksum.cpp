// Read source checksums via the installed Microsoft DIA SDK. Never executes the target.
#include <windows.h>
#include <atlbase.h>
#include <dia2.h>
#include <cstdio>
#include <cwchar>
#include <vector>

static void check(HRESULT value) {
    if (FAILED(value)) { std::fprintf(stderr, "DIA error: %08lx\n", static_cast<unsigned long>(value)); std::exit(2); }
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) { std::fprintf(stderr,"Usage: dsp_pdb_source_checksum <pdb> <filename suffix>\n"); return 2; }
    check(CoInitialize(nullptr));
    int count=0;
    {
        CComPtr<IDiaDataSource> source;
        check(CoCreateInstance(__uuidof(DiaSource),nullptr,CLSCTX_INPROC_SERVER,__uuidof(IDiaDataSource),reinterpret_cast<void**>(&source)));
        check(source->loadDataFromPdb(argv[1]));
        CComPtr<IDiaSession> session;check(source->openSession(&session));
        CComPtr<IDiaEnumSourceFiles> files;check(session->findFile(nullptr,nullptr,nsNone,&files));
        while (true) {
            CComPtr<IDiaSourceFile> file;ULONG fetched=0;check(files->Next(1,&file,&fetched));if(!fetched)break;
            CComBSTR name;check(file->get_fileName(&name));
            size_t n=wcslen(name),suffix=wcslen(argv[2]);if(n<suffix || _wcsicmp(name+n-suffix,argv[2]))continue;
            DWORD type=0,size=0,id=0;check(file->get_checksumType(&type));check(file->get_uniqueId(&id));check(file->get_checksum(0,&size,nullptr));
            std::vector<BYTE> bytes(size);DWORD actual=0;if(size)check(file->get_checksum(size,&actual,bytes.data()));if(actual!=size)return 3;
            std::printf("%lu\t%lu\t",static_cast<unsigned long>(id),static_cast<unsigned long>(type));
            for(BYTE b:bytes)std::printf("%02x",b);
            int needed=WideCharToMultiByte(CP_UTF8,0,name,-1,nullptr,0,nullptr,nullptr);std::vector<char> utf8(needed);
            WideCharToMultiByte(CP_UTF8,0,name,-1,utf8.data(),needed,nullptr,nullptr);std::printf("\t%s\n",utf8.data());++count;
        }
    }
    CoUninitialize();return count ? 0 : 4;
}
