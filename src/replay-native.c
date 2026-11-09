/* Native replay container IO. No launcher, CLR, imported CRT, or scratch game.
 * Client metadata is the existing YRRP envelope; the RA1N body is RA1-specific.
 * All integers in the file are little endian. See tools/replay/NATIVE-FORMAT.md.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "replay-native.h"
/* Keep this cdecl SHA implementation separate from the game's existing API. */
#define Sha256_Init NativeReplay_Sha256_Init
#define Sha256_Update NativeReplay_Sha256_Update
#define Sha256_Final NativeReplay_Sha256_Final
#include "Sha256.c"
#include "vendor/miniz/miniz.h"

#define HEADER_SIZE 1312
#define SETUP_LIMIT (32u*1024*1024)
#define EVENTS_LIMIT (256u*1024*1024)
#define CHECKS_LIMIT (64u*1024*1024)
#define U32(p) (*(unsigned *)(p))

void *nr_memcpy(void *d, const void *s, size_t n) { unsigned char *a=d; const unsigned char *b=s; while(n--) *a++=*b++; return d; }
void *nr_memset(void *d, int v, size_t n) { unsigned char *a=d; while(n--) *a++=(unsigned char)v; return d; }
void *nr_memmove(void *d, const void *s, size_t n) { unsigned char *a=d; const unsigned char *b=s; if(a>b && a<b+n) { a+=n; b+=n; while(n--) *--a=*--b; } else nr_memcpy(d,s,n); return d; }
int nr_memcmp(const void *a, const void *b, size_t n) { const unsigned char *x=a,*y=b; while(n--) { if(*x!=*y) return *x-*y; x++; y++; } return 0; }
size_t nr_strlen(const char *p) { const char *s=p; while(*p) p++; return p-s; }

typedef struct {
    HANDLE (WINAPI *CreateFileA)(LPCSTR,DWORD,DWORD,LPSECURITY_ATTRIBUTES,DWORD,DWORD,HANDLE);
    HANDLE (WINAPI *CreateFileW)(LPCWSTR,DWORD,DWORD,LPSECURITY_ATTRIBUTES,DWORD,DWORD,HANDLE);
    BOOL (WINAPI *ReadFile)(HANDLE,LPVOID,DWORD,LPDWORD,LPOVERLAPPED);
    BOOL (WINAPI *WriteFile)(HANDLE,LPCVOID,DWORD,LPDWORD,LPOVERLAPPED);
    BOOL (WINAPI *CloseHandle)(HANDLE);
    DWORD (WINAPI *GetFileSize)(HANDLE,LPDWORD);
    DWORD (WINAPI *SetFilePointer)(HANDLE,LONG,PLONG,DWORD);
    HANDLE (WINAPI *GetProcessHeap)(void);
    LPVOID (WINAPI *HeapAlloc)(HANDLE,DWORD,SIZE_T);
    LPVOID (WINAPI *HeapReAlloc)(HANDLE,DWORD,LPVOID,SIZE_T);
    BOOL (WINAPI *HeapFree)(HANDLE,DWORD,LPVOID);
    BOOL (WINAPI *CreateDirectoryW)(LPCWSTR,LPSECURITY_ATTRIBUTES);
    int (WINAPI *MultiByteToWideChar)(UINT,DWORD,LPCCH,int,LPWSTR,int);
    DWORD (WINAPI *GetModuleFileNameA)(HMODULE,LPSTR,DWORD);
    void (WINAPI *GetSystemTime)(LPSYSTEMTIME);
    BOOL (WINAPI *FlushFileBuffers)(HANDLE);
    void (WINAPI *ExitProcess)(UINT);
} NativeAPI;
static NativeAPI api;
static HANDLE heap;
int NativeReplay_ModeValue;
int NativeReplay_Bound;
int NativeReplay_ClientRequest;

typedef struct {
    HANDLE file;
    unsigned char header[HEADER_SIZE];
    unsigned char *events, *checks;
    unsigned event_size, event_pos, check_size, check_cap, check_pos;
    unsigned last_frame;
    int failed, closed;
    tdefl_compressor *compressor;
    CSha256 event_hash;
} ReplayState;
static ReplayState *state;

static int initialize_api(void) {
    typedef HMODULE (WINAPI *LoadLib)(LPCSTR);
    typedef FARPROC (WINAPI *GetProc)(HMODULE,LPCSTR);
    LoadLib load=*(LoadLib *)0x5E663C;
    GetProc get=*(GetProc *)0x5E65FC;
    HMODULE k=load("kernel32.dll");
#define RESOLVE(name) api.name=(void *)get(k,#name); if(!api.name) return 0
    RESOLVE(CreateFileA); RESOLVE(CreateFileW); RESOLVE(ReadFile); RESOLVE(WriteFile);
    RESOLVE(CloseHandle); RESOLVE(GetFileSize); RESOLVE(SetFilePointer);
    RESOLVE(GetProcessHeap); RESOLVE(HeapAlloc); RESOLVE(HeapReAlloc); RESOLVE(HeapFree);
    RESOLVE(CreateDirectoryW); RESOLVE(MultiByteToWideChar); RESOLVE(GetModuleFileNameA);
    RESOLVE(GetSystemTime); RESOLVE(FlushFileBuffers);
    RESOLVE(ExitProcess);
#undef RESOLVE
    heap=api.GetProcessHeap(); return heap!=0;
}
static void *alloc(unsigned n) { return api.HeapAlloc(heap,HEAP_ZERO_MEMORY,n?n:1); }
static void release(void *p) { if(p) api.HeapFree(heap,0,p); }
static void log_line(const char *s) {
    HANDLE f=api.CreateFileA("replay-native.log",GENERIC_WRITE,FILE_SHARE_READ,0,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,0);
    DWORD n; if(f==INVALID_HANDLE_VALUE) return;
    api.SetFilePointer(f,0,0,FILE_END); api.WriteFile(f,s,nr_strlen(s),&n,0);
    api.WriteFile(f,"\r\n",2,&n,0); api.CloseHandle(f);
}
static void number_line(const char *s, unsigned value) {
    char b[96], digits[12]; unsigned n=nr_strlen(s),i=0;
    if(n>70) n=70;
    nr_memcpy(b,s,n);
    do { digits[i++]=(char)('0'+value%10); value/=10; } while(value);
    while(i) b[n++]=digits[--i];
    b[n]=0; log_line(b);
}
static int write_bytes(HANDLE f, const void *p, unsigned n) { DWORD done=0; return api.WriteFile(f,p,n,&done,0) && done==n; }
static unsigned char *read_handle(HANDLE f, unsigned *n, unsigned limit) {
    DWORD high=0,done=0; unsigned char *p; *n=api.GetFileSize(f,&high);
    if(high || !*n || *n>limit) return 0;
    p=alloc(*n+1); if(!p) return 0;
    if(!api.ReadFile(f,p,*n,&done,0) || done!=*n) { release(p); return 0; } return p;
}
static unsigned char *read_path(const char *path, unsigned *n, unsigned limit) {
    unsigned char *p; HANDLE f=api.CreateFileA(path,GENERIC_READ,FILE_SHARE_READ,0,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,0);
    if(f==INVALID_HANDLE_VALUE) return 0;
    p=read_handle(f,n,limit); api.CloseHandle(f); return p;
}
static int write_path(const char *path,const void *p,unsigned n) {
    int ok; HANDLE f=api.CreateFileA(path,GENERIC_WRITE,0,0,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,0);
    if(f==INVALID_HANDLE_VALUE) return 0;
    ok=write_bytes(f,p,n); api.CloseHandle(f); return ok;
}
static unsigned lower(unsigned c) { return c>='A'&&c<='Z'?c+32:c; }
static int equal(const char *s,unsigned n,const char *key) {
    unsigned i; if(n!=nr_strlen(key)) return 0;
    for(i=0;i<n;i++) if(lower((unsigned char)s[i])!=lower((unsigned char)key[i])) return 0;
    return 1;
}
static int ini(const unsigned char *text,const char *section,const char *key,char *out,unsigned cap) {
    const char *p=(const char *)text; int selected=0; out[0]=0;
    while(*p) {
        const char *start=p,*end,*eq,*v; unsigned n;
        while(*p && *p!='\n' && *p!='\r') p++;
        end=p;
        while(*p=='\n'||*p=='\r') p++;
        while(start<end && (*start==' '||*start=='\t')) start++;
        while(end>start && (end[-1]==' '||end[-1]=='\t')) end--;
        if(start==end || *start==';') continue;
        if(*start=='[' && end[-1]==']') { selected=equal(start+1,end-start-2,section); continue; }
        if(!selected) continue;
        for(eq=start;eq<end && *eq!='=';eq++) {}
        if(eq==end) continue;
        v=eq+1; while(eq>start && (eq[-1]==' '||eq[-1]=='\t')) eq--;
        if(!equal(start,eq-start,key)) continue;
        while(v<end && (*v==' '||*v=='\t')) v++;
        n=end-v; if(n>=cap) return 0; nr_memcpy(out,v,n); out[n]=0; return 1;
    } return 0;
}
static int truth(const char *s) { return equal(s,nr_strlen(s),"yes")||equal(s,nr_strlen(s),"true")||equal(s,nr_strlen(s),"1"); }
static unsigned decimal(const char *s) { unsigned n=0; while(*s>='0'&&*s<='9') n=n*10+*s++-'0'; return n; }
static int relative(const char *p) {
    const char *s=p; if(!*p || *p=='/' || *p=='\\') return 0;
    while(*p) { if(*p==':' || (unsigned char)*p<32) return 0;
        if(*p=='/'||*p=='\\') { if(p==s || p[-1]=='.' || p[-1]==' ') return 0; s=p+1; } p++; }
    return p>s && p[-1]!='.' && p[-1]!=' ';
}
static HANDLE replay_file(const char *path,int record) {
    wchar_t w[1024]; unsigned i;
    if(!relative(path) || !api.MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,path,-1,w,1024)) return INVALID_HANDLE_VALUE;
    if(record) for(i=0;w[i];i++) if(w[i]=='/'||w[i]=='\\') { wchar_t old=w[i]; w[i]=0; api.CreateDirectoryW(w,0); w[i]=old; }
    return api.CreateFileW(w,record?GENERIC_WRITE:GENERIC_READ,FILE_SHARE_READ,0,record?CREATE_NEW:OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,0);
}
static void digest(const void *p,unsigned n,unsigned char *out) { CSha256 h; Sha256_Init(&h); Sha256_Update(&h,p,n); Sha256_Final(&h,out); }
static int engine_hash(unsigned char *out) {
    char path[MAX_PATH]; unsigned n; unsigned char *p;
    if(!api.GetModuleFileNameA(0,path,sizeof(path))) return 0;
    p=read_path(path,&n,32*1024*1024); if(!p) return 0; digest(p,n,out); release(p); return 1;
}
static int compressed_write(const void *p,int n,void *user) {
    ReplayState *r=user; if(n<0 || !write_bytes(r->file,p,(unsigned)n)) { r->failed=1; return 0; } return 1;
}
static int start_compressor(void) {
    return tdefl_init(state->compressor,compressed_write,state,TDEFL_DEFAULT_MAX_PROBES)==TDEFL_STATUS_OKAY;
}
static int compressed(const void *p,unsigned n,int finish) {
    tdefl_status s=tdefl_compress_buffer(state->compressor,p,n,finish?TDEFL_FINISH:TDEFL_NO_FLUSH);
    return finish?s==TDEFL_STATUS_DONE:s==TDEFL_STATUS_OKAY;
}
static unsigned position(void) { return api.SetFilePointer(state->file,0,0,FILE_CURRENT); }
static unsigned char *utf8(const unsigned char *p,unsigned n,unsigned *size) {
    unsigned i,j=0; unsigned char *out=alloc(n*2+1); if(!out) return 0;
    for(i=0;i<n;i++) { if(p[i]<128) out[j++]=p[i]; else { out[j++]=0xc0|(p[i]>>6); out[j++]=0x80|(p[i]&63); } }
    *size=j; return out;
}
static unsigned timestamp(void) {
    SYSTEMTIME t; int year,era,yoe,doy,days; unsigned month;
    api.GetSystemTime(&t); year=t.wYear; month=t.wMonth; year-=month<=2; era=year/400; yoe=year-era*400;
    doy=(153*(month+(month>2?-3:9))+2)/5+t.wDay-1;
    days=era*146097+yoe*365+yoe/4-yoe/100+doy-719468;
    return (unsigned)days*86400+t.wHour*3600+t.wMinute*60+t.wSecond;
}
static int record_setup(unsigned char *spawn,unsigned spawn_size,const char *out) {
    char scenario[1024],value[64]; unsigned map_size,utf_size,start;
    unsigned char *map=0,*front=0; int ok=0;
    if(!ini(spawn,"Settings","Scenario",scenario,sizeof(scenario)) || !equal(scenario,nr_strlen(scenario),"spawnmap.ini")) return 0;
    map=read_path(scenario,&map_size,SETUP_LIMIT); if(!map) return 0;
    digest(map,map_size,state->header+1272);
    state->file=replay_file(out,1); if(state->file==INVALID_HANDLE_VALUE) goto done;
    state->compressor=alloc(sizeof(tdefl_compressor)); if(!state->compressor) goto done;
    U32(state->header)=0x50525259; U32(state->header+4)=1; U32(state->header+8)=HEADER_SIZE;
    U32(state->header+12)=1;
    ini(spawn,"Settings","Seed",value,sizeof(value)); U32(state->header+20)=decimal(value);
    ini(spawn,"Settings","GameSpeed",value,sizeof(value));
    /* The client displays YR speed indices; RA1 stores a 60/fps divisor. */
    { unsigned speed=decimal(value); U32(state->header+1040)=speed<=1?0:speed==2?2:speed==3?3:speed==4?4:speed==5?5:6; }
    U32(state->header+1044)=timestamp();
    U32(state->header+1096)=0x4e314152; U32(state->header+1100)=2; U32(state->header+1104)=1;
    if(!engine_hash(state->header+1208) || !write_bytes(state->file,state->header,HEADER_SIZE)) goto done;
    front=utf8(spawn,spawn_size,&utf_size); if(!front) goto done;
    start=position(); if(!start_compressor()||!compressed(front,utf_size,1)) goto done;
    U32(state->header+1032)=position()-start; U32(state->header+1084)=utf_size;
    release(front); front=utf8(map,map_size,&utf_size); if(!front) goto done;
    start=position(); if(!start_compressor()||!compressed(front,utf_size,1)) goto done;
    U32(state->header+1036)=position()-start; U32(state->header+1088)=utf_size;
    U32(state->header+1108)=position(); U32(state->header+1112)=spawn_size;
    digest(spawn,spawn_size,state->header+1240);
    if(!write_bytes(state->file,spawn,spawn_size)) goto done;
    U32(state->header+1116)=position();
    if(!start_compressor()) goto done;
    start=position(); api.SetFilePointer(state->file,0,0,FILE_BEGIN);
    if(!write_bytes(state->file,state->header,HEADER_SIZE)) goto done;
    api.SetFilePointer(state->file,start,0,FILE_BEGIN);
    Sha256_Init(&state->event_hash); NativeReplay_ModeValue=1; ok=1;
done:
    release(front); release(map); return ok;
}
static int span(unsigned off,unsigned n,unsigned total) { return off<=total && n<=total-off; }
static unsigned char *inflate(const unsigned char *file,unsigned total,unsigned off,unsigned stored,unsigned raw,unsigned limit) {
    unsigned char *p; if(!raw||raw>limit||!stored||!span(off,stored,total)) return 0;
    p=alloc(raw+1); if(!p) return 0;
    if(tinfl_decompress_mem_to_mem(p,raw,file+off,stored,0)!=raw) { release(p); return 0; } return p;
}
static int native_events_valid(const unsigned char *p,unsigned n) {
    unsigned players,pos,count,i,word;
    if(n<754 || p[0]>7 || p[1]>2 || U32(p+2)<1 || U32(p+2)>60) return 0;
    players=U32(p+49); if(players>8 || (!players&&p[0])) return 0;
    pos=754+29*players; if(pos>n) return 0;
    while(pos<n) {
        if(n-pos<4) return 0;
        count=U32(p+pos); pos+=4;
        if(count>4096||count>(n-pos)/20) return 0;
        for(i=0;i<count;i++,pos+=20) {
            word=U32(p+pos+1);
            if(p[pos]>32 || ((word>>26)&31)>=20 || (word&0x80000000)) return 0;
        }
    } return pos==n;
}
static int playback_setup(const char *path) {
    HANDLE f; unsigned char *file=0,*map=0,*spawn=0; unsigned n,map_raw; unsigned char hash[32]; int ok=0;
    unsigned *h=(unsigned *)state->header; /* File bounds are checked before every payload access. */
    f=replay_file(path,0); if(f==INVALID_HANDLE_VALUE) return 0;
    file=read_handle(f,&n,512u*1024*1024); api.CloseHandle(f);
    if(!file||n<HEADER_SIZE) goto done;
    nr_memcpy(state->header,file,HEADER_SIZE);
    if(h[0]!=0x50525259 || h[1]!=1 || h[2]!=HEADER_SIZE || h[1096/4]!=0x4e314152 || h[1100/4]!=2 || h[1104/4]!=1 || !(h[1056/4]&1)) goto done;
    if(!engine_hash(hash)||nr_memcmp(hash,file+1208,32)) { log_line("Replay engine fingerprint differs."); goto done; }
    if(h[1032/4]>SETUP_LIMIT*2||h[1036/4]>SETUP_LIMIT*2 ||
       h[1108/4]!=HEADER_SIZE+h[1032/4]+h[1036/4] ||
       !h[1112/4]||h[1112/4]>SETUP_LIMIT||!span(h[1108/4],h[1112/4],n) ||
       h[1116/4]!=h[1108/4]+h[1112/4] || !span(h[1116/4],h[1120/4],n) ||
       h[1128/4]!=h[1116/4]+h[1120/4] || !span(h[1128/4],h[1132/4],n) ||
       h[1128/4]+h[1132/4]!=n) goto done;
    digest(file+h[1108/4],h[1112/4],hash); if(nr_memcmp(hash,file+1240,32)) goto done;
    state->event_size=h[1124/4]; state->check_size=h[1136/4];
    state->events=inflate(file,n,h[1116/4],h[1120/4],state->event_size,EVENTS_LIMIT);
    state->checks=inflate(file,n,h[1128/4],h[1132/4],state->check_size,CHECKS_LIMIT);
    if(!state->events||!state->checks || state->check_size%12 || !native_events_valid(state->events,state->event_size)) goto done;
    digest(state->events,state->event_size,hash); if(nr_memcmp(hash,file+1144,32)) goto done;
    digest(state->checks,state->check_size,hash); if(nr_memcmp(hash,file+1176,32)) goto done;
    map_raw=h[1088/4]; map=inflate(file,n,HEADER_SIZE+h[1032/4],h[1036/4],map_raw,SETUP_LIMIT*2);
    if(!map) goto done;
    /* The client already extracts the frontend metadata. Restore the byte-exact
       game setup here before its INI parser runs; never replay viewer settings. */
    spawn=alloc(h[1112/4]+1); if(!spawn) goto done;
    nr_memcpy(spawn,file+h[1108/4],h[1112/4]);
    /* The map metadata is UTF-8. Reverse the lossless Latin-1 conversion. */
    { unsigned i=0,j=0; while(i<map_raw) { unsigned c=map[i++];
        if(c>=0xc2&&c<=0xc3) { if(i==map_raw||(map[i]&0xc0)!=0x80) goto done; c=((c&3)<<6)|(map[i++]&63); }
        else if(c>=128) goto done;
        map[j++]=(unsigned char)c; }
      { char scenario[1024];
        digest(map,j,hash); if(nr_memcmp(hash,file+1272,32)) goto done;
        if(!ini(spawn,"Settings","Scenario",scenario,sizeof(scenario))||!equal(scenario,nr_strlen(scenario),"spawnmap.ini") ||
           !write_path(scenario,map,j) || !write_path("spawn.ini",spawn,h[1112/4])) goto done; }
    }
    NativeReplay_ModeValue=2; ok=1;
done:
    release(file); release(map); release(spawn); return ok;
}

int NativeReplay_Bootstrap(void) {
    unsigned size; unsigned char *spawn; char request[1024],value[64]; int playback=0,ok=1;
    if(!initialize_api()) return 0;
    spawn=read_path("spawn.ini",&size,SETUP_LIMIT); if(!spawn) return 1;
    ini(spawn,"Settings","ReplayFile",request,sizeof(request));
    if(*request) playback=1;
    else {
        NativeReplay_ClientRequest=ini(spawn,"Settings","EnableReplayRecording",value,sizeof(value)); if(!truth(value)) goto done;
        ini(spawn,"Settings","LoadSaveGame",value,sizeof(value)); if(truth(value)) goto done;
        ini(spawn,"Settings","IsSinglePlayer",value,sizeof(value)); if(truth(value)) goto done;
        ini(spawn,"Settings","ReplayFileOut",request,sizeof(request)); if(!*request) goto done;
    }
    state=alloc(sizeof(ReplayState)); if(!state) { ok=!playback; goto done; }
    NativeReplay_ClientRequest=1;
    state->file=INVALID_HANDLE_VALUE;
    ok=playback?playback_setup(request):record_setup(spawn,size,request);
    if(ok) log_line(playback?"Native replay playback prepared.":"Native replay recording prepared.");
    else {
        log_line(playback?"Native replay rejected: invalid, incomplete, incompatible, or unreadable file.":"Native replay recording could not start; match will continue.");
        if(state->file!=INVALID_HANDLE_VALUE) api.CloseHandle(state->file);
        release(state->events); release(state->checks); release(state->compressor);
        state->events=state->checks=0; state->compressor=0;
        state->closed=1; NativeReplay_ModeValue=0; ok=!playback;
    }
done:
    release(spawn);
    /* A rejected -SPAWN playback must return to the client, not Westwood's menu. */
    if(!ok && playback) api.ExitProcess(1);
    return ok;
}

int NativeReplay_Read(void *out,unsigned n) {
    if(!state||state->closed||NativeReplay_ModeValue!=2) return 0;
    if(n>state->event_size-state->event_pos) n=state->event_size-state->event_pos;
    nr_memcpy(out,state->events+state->event_pos,n); state->event_pos+=n; return n;
}
int NativeReplay_Write(const void *p,unsigned n) {
    if(!state||state->closed||state->failed||NativeReplay_ModeValue!=1) return 0;
    if(n>EVENTS_LIMIT-state->event_size || !compressed(p,n,0)) { state->failed=1; return 0; }
    /* Save_Recording_Values runs after all spawner settings, before scenario RNG. */
    if(!state->event_size) U32(state->header+20)=*(unsigned *)0x680654;
    Sha256_Update(&state->event_hash,p,n); state->event_size+=n; return n;
}
int NativeReplay_Seek(int offset,int origin) {
    unsigned base=origin==0?0:origin==1?state->event_pos:state->event_size;
    if(origin<0||origin>2 || (offset<0 && 0u-(unsigned)offset>base) || (offset>=0 && (unsigned)offset>state->event_size-base)) return -1;
    state->event_pos=base+offset; return state->event_pos;
}
int NativeReplay_Size(void) { return state?state->event_size:0; }
int NativeReplay_Frame(unsigned frame,unsigned seed,unsigned crc) {
    unsigned sample[3]; sample[0]=frame; sample[1]=seed; sample[2]=crc;
    if(!state||state->closed) return 1;
    if(NativeReplay_ModeValue==1) {
        if(state->failed) return 1;
        if(state->check_size+12>state->check_cap) {
            unsigned cap=state->check_cap?state->check_cap*2:65536; void *p;
            if(cap>CHECKS_LIMIT) cap=CHECKS_LIMIT;
            p=cap>=state->check_size+12?(state->checks?api.HeapReAlloc(heap,0,state->checks,cap):alloc(cap)):0;
            if(!p) { state->failed=1; log_line("Native replay check buffer exhausted; recording is incomplete."); return 1; }
            state->checks=p; state->check_cap=cap;
        }
        nr_memcpy(state->checks+state->check_size,sample,12); state->check_size+=12; state->last_frame=frame;
    } else if(NativeReplay_ModeValue==2) {
        if(state->check_pos==state->check_size) return 0;
        if(nr_memcmp(sample,state->checks+state->check_pos,12)) {
            state->failed=1; number_line("Native replay state mismatch at frame ",frame); return 0;
        }
        state->check_pos+=12; state->last_frame=frame;
    }
    return 1;
}
int NativeReplay_Close(void) {
    unsigned start; int ok;
    if(!state||state->closed) return 0;
    state->closed=1;
    if(NativeReplay_ModeValue==1) {
        ok=!state->failed&&state->event_size&&state->check_size&&compressed(0,0,1);
        U32(state->header+1120)=position()-U32(state->header+1116);
        U32(state->header+1124)=state->event_size; Sha256_Final(&state->event_hash,state->header+1144);
        start=position(); U32(state->header+1128)=start;
        if(ok) ok=start_compressor()&&compressed(state->checks,state->check_size,1);
        U32(state->header+1132)=position()-start; U32(state->header+1136)=state->check_size;
        digest(state->checks,state->check_size,state->header+1176);
        U32(state->header+1052)=state->last_frame; U32(state->header+1056)=ok?1:0;
        api.SetFilePointer(state->file,0,0,FILE_BEGIN);
        ok=write_bytes(state->file,state->header,HEADER_SIZE)&&ok;
        api.FlushFileBuffers(state->file); api.CloseHandle(state->file);
        log_line(ok?"Native replay recording finalized.":"Native replay recording incomplete.");
        number_line("Recorded frame/RNG/CRC samples: ",state->check_size/12);
    } else {
        ok=!state->failed && state->check_pos==state->check_size;
        log_line(ok?"Native replay playback: all frame/RNG/CRC samples match.":"Native replay playback ended before all samples matched.");
        number_line("Matched frame/RNG/CRC samples: ",state->check_pos/12);
    }
    release(state->events); release(state->checks); release(state->compressor);
    state->events=state->checks=0; state->compressor=0; NativeReplay_Bound=0; return 0;
}
