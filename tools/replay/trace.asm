; Optional diagnostic build only: frame + synchronized RNG + native state CRC.
; Computing the CRC normally draws RNG. Save/restore it and GameCRC, so this
; probe does not change simulation state. Trace files close with the process.
%include "macros/patch.inc"

hack 0x00528EDC, 0x00528EE6
    pushfd
    pushad
    call Trace_Frame
    popad
    popfd
    push ebp
    mov ebp, esp
    test byte [0x00680151], 2
    jmp hackend

[section .data]
trace_handle: dd 0
trace_bytes: dd 0
trace_frame: times 3 dd 0
record_name: db "replay-record.trace", 0
play_name: db "replay-play.trace", 0

[section .text]
Trace_Frame:
    test byte [0x00680151], 3
    jz .done
    cmp dword [trace_handle], 0
    jne .opened
    mov eax, record_name
    test byte [0x00680151], 2
    jz .name_ready
    mov eax, play_name
.name_ready:
    push 0
    push 0x80
    push 2
    push 0
    push 1
    push 0x40000000
    push eax
    call [0x005E658C]             ; CreateFileA
    mov [trace_handle], eax
.opened:
    cmp dword [trace_handle], -1
    je .done
    push dword [0x006678E8]
    push dword [0x0068785C]
    mov eax, [0x006680C4]
    mov [trace_frame], eax
    mov eax, [0x006678E8]
    mov [trace_frame+4], eax
    call 0x0052C044
    mov eax, [0x0068785C]
    mov [trace_frame+8], eax
    pop dword [0x0068785C]
    pop dword [0x006678E8]
    push 0
    push trace_bytes
    push 12
    push trace_frame
    push dword [trace_handle]
    call [0x005E6684]             ; WriteFile
.done:
    ret
