; Bridges between Westwood's Watcom register ABI and the native C container.
%include "macros/patch.inc"
%include "macros/datatypes.inc"
cextern NativeReplay_Bootstrap
cextern NativeReplay_ModeValue
cextern NativeReplay_Bound
cextern NativeReplay_ClientRequest
cextern NativeReplay_Read
cextern NativeReplay_Write
cextern NativeReplay_Seek
cextern NativeReplay_Size
cextern NativeReplay_Close
cextern NativeReplay_Frame
cextern Replay_Load_Header
%ifdef REPLAY_TRACE
cextern Trace_Frame
%endif
cglobal Replay_Native_Setup

; Only the recording object is virtualized. All other game and MIX IO is native.
%macro FILE_HOOK 4
@LJMP %1, %2
[section .text]
%2:
    pushfd
    cmp eax, 0x6800F1
    jne %%original
    cmp dword [NativeReplay_Bound], 0
    je %%original
    pushad
%if %4 == 2
    push ebx
    push edx
%endif
    call %3
%if %4 == 2
    add esp, 8
%endif
    mov [esp+28], eax
    popad
    popfd
    ret
%%original:
    popfd
    push ebp
    mov ebp, esp
%if %1 == 0x462860 || %1 == 0x4628B0 || %1 == 0x462958
    push ecx
    push esi
%else
    push ebx
    push ecx
%endif
    jmp %1+5
%endmacro

FILE_HOOK 0x462860, Replay_Native_Write, NativeReplay_Write, 2
FILE_HOOK 0x4628B0, Replay_Native_Read, NativeReplay_Read, 2
FILE_HOOK 0x462958, Replay_Native_Seek, NativeReplay_Seek, 2
FILE_HOOK 0x4629CC, Replay_Native_Size, NativeReplay_Size, 0
FILE_HOOK 0x462AA8, Replay_Native_Close, NativeReplay_Close, 0

; One frame probe owns this hook, including optional development trace output.
hack 0x528EDC, 0x528EE6
    pushfd
    pushad
%ifdef REPLAY_TRACE
    call Trace_Frame
%endif
    cmp dword [NativeReplay_ModeValue], 0
    je .continue
    push dword [0x6678E8]
    push dword [0x68785C]
    call 0x52C044
    mov edx, [0x68785C]
    pop dword [0x68785C]
    pop dword [0x6678E8]
    push edx
    push dword [0x6678E8]
    push dword [0x6680C4]
    call NativeReplay_Frame
    add esp, 12
    test eax, eax
    jnz .continue
    mov dword [0x669924], 0
    popad
    popfd
    ret
.continue:
    popad
    popfd
    push ebp
    mov ebp, esp
    test byte [0x680151], 2
    jmp hackend

[section .text]
; Called after native settings and RNG initialization, before starting scenario.
; Returns 0 for legacy raw mode, 1 for native container, -1 on invalid playback.
Replay_Native_Setup:
    mov eax, [NativeReplay_ModeValue]
    test eax, eax
    jnz .active
    cmp dword [NativeReplay_ClientRequest], 0
    je .done
    mov byte [0x680151], 0
    mov eax, 1
    ret
.active:
    pushad
    mov edx, .name
    mov eax, 0x6800F1
    call 0x4627D4
    mov dword [NativeReplay_Bound], 1
    mov eax, [NativeReplay_ModeValue]
    mov [0x680151], al
    cmp eax, 2
    jne .success
    mov eax, 0x6800F1
    call Replay_Load_Header
    test eax, eax
    jz .failure
.success:
    mov dword [esp+28], 1
    popad
.done:
    ret
.failure:
    call NativeReplay_Close
    mov dword [esp+28], -1
    popad
    ret
[section .rdata]
.name: db "native-replay", 0
