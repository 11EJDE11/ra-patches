; Adapter for package ra95.dat SHA256 3d9eef850b9d59191330d3dc4a60ca1a1741663d62bf4c817f1203db813af60e.
; These addresses belong to this package's already-linked spawner, not Westwood.
; Normal source builds use the corresponding changes in src/spawn.asm instead.
%include "macros/patch.inc"
%include "macros/datatypes.inc"
cextern Replay_Load_Header

@SET 0x006F381B, db 2              ; playback, not attract
@CLEAR 0x006F3809, 0x90, 0x006F3813 ; postpone header until final options
@CALL 0x006F3861, Replay_Load_Header
@LJMP 0x006F3866, Replay_Header_Loaded
@CALL 0x006F3CAC, Replay_Start_Scenario
@CALL 0x006F3CCF, Replay_Create_Connections
@LJMP 0x006F387C, Replay_Open_Failed

[section .text]
Replay_Header_Loaded:
    test eax, eax
    jz Replay_Open_Failed
    xor eax, eax
    mov edx, -3
    jmp 0x006F386D

Replay_Start_Scenario:
    test byte [0x00680151], 1
    jz .start
    pushad
    mov eax, 0x006800F1
    call 0x004F8718
    popad
.start:
    jmp 0x0053A0A4

Replay_Create_Connections:
    test byte [0x00680151], 2
    jz .live
    mov eax, 1
    ret
.live:
    jmp 0x0054A4F8

Replay_Open_Failed:
    mov byte [0x00680151], 0
    mov dword [0x00669924], 0
    xor eax, eax
    jmp 0x006F3D39
