; Native replay queue repairs for Westwood 3.03. See tools/replay/README.md.
%include "macros/patch.inc"
%include "macros/datatypes.inc"

cextern recording_mode
cextern recording_file
cglobal Replay_Load_Header

gstring recording_filename, "ratest.rarec"

; A replay keeps the recorded house's sidebar. Only attract/demo playback
; suppresses activation, matching CO2's Session.Attract source-level repair.
; Cameo clicks are handled separately below; outgoing viewer orders are also
; discarded by Queue_Playback.
@PATCH 0x0054DA88
    test byte [recording_mode], 4
@ENDPATCH

; Record commands only, never the original player's view or selection.
@CLEAR 0x004A7EDD, 0x90, 0x004A7EE2
@CLEAR 0x004A53E7, 0x90, 0x004A53EC

; Restore the seed fields removed from the package binary's native header.
@PATCH 0x004F874F
    mov ebx, 4
    mov esi, [ecx]
    mov edx, 0x00680654
    mov eax, ecx
    call [esi+0x30]
@ENDPATCH
@PATCH 0x004F87FB
    mov ebx, 4
    mov esi, [ecx]
    mov edx, 0x00680654
    mov eax, ecx
    call [esi+0x24]
@ENDPATCH

; Only multiplayer consumes an RNG value in Compute_Game_CRC. Match its live
; schedule, including frame zero and frames between network send periods.
hack 0x0052BE87, 0x0052BE8C
    cmp byte [0x0067F2B4], 0
    je .done
    cmp byte [0x0067F2B4], 5
    je .done
    call 0x0052C044
.done:
    jmp hackend

; Skirmish, like campaign, records frame zero and executes just one house.
hack 0x0052BECB, 0x0052BED8
    cmp byte [0x0067F2B4], 0
    je .singleplayer
    cmp byte [0x0067F2B4], 5
    jne 0x0052C039
.singleplayer:
    jmp hackend

hack 0x0052BFCD, 0x0052BFD6
    cmp byte [0x0067F2B4], 0
    je .singleplayer
    cmp byte [0x0067F2B4], 5
    jne 0x0052C006
.singleplayer:
    jmp hackend

; Discard locally generated viewer commands and reset the whole outgoing FIFO.
hack 0x0052BDEC, 0x0052BDF1
    mov dword [0x0066AB5C], 0
    mov dword [0x0066AB60], 0
    mov dword [0x0066AB64], 0
    push ebp
    mov ebp, esp
    push ebx
    push ecx
    jmp hackend

; Reject corrupt lengths before reading or silently dropping a full queue.
hack 0x0052BF3A, 0x0052BF3F
    mov edx, [ebp-0x1C]
    test edx, edx
    js 0x0052BFB1
    mov ecx, [0x0066B068]
    add ecx, edx
    jc 0x0052BFB1
    cmp ecx, 4096
    ja 0x0052BFB1
    xor ecx, ecx
    jmp hackend

; EventClass is 20 bytes. Frame/house/executed share the dword at offset one.
hack 0x0052BF67, 0x0052BF75
    cmp byte [ebp-0x30], 32
    ja 0x0052BFAA
    mov eax, [ebp-0x2F]
    and eax, 0x03FFFFFF
    cmp eax, [0x006680C4]
    jne 0x0052BFAA
    mov eax, [ebp-0x2F]
    shr eax, 26
    and eax, 31
    cmp eax, 20
    jae 0x0052BFAA
    and byte [ebp-0x2C], 0x7F
    cmp dword [0x0066B068], 4096
    jmp hackend

; FRAMEINFO contains a peer's transport checksum/history, including the live
; startup grace period. It is not a playback-state hash. Consume the heartbeat
; without entering the live OOS/disconnect path; keep live validation untouched.
hack 0x0052BB10, 0x0052BB15
    test byte [recording_mode], 2
    jnz 0x0052BC63
    mov ebx, [ebp-0x38]
    test ebx, ebx
    jmp hackend

; For remote EXIT, use the same patched departure handler as the live match.
; The spawner can destroy the departing army instead of giving it to the AI;
; the original playback-only AI conversion does not reproduce that behavior.
hack 0x0052BABA, 0x0052BAC1
    cmp byte [0x0067F2B4], 3
    je .network
    cmp byte [0x0067F2B4], 4
    je .network
    and byte [eax+0x42], 0xFD
    mov edx, [ebp-0x24]
    jmp hackend
.network:
    pushad
    mov eax, [ebp-0x24]
    mov eax, [eax+1]              ; HouseClass::ID
    xor edx, edx                 ; normal departure, not connection loss
    call 0x00506624               ; Destroy_Connection (including package patches)
    popad
    jmp 0x0052BB02

; Checked writes disable recording on disk failure without affecting the match.
@LJMP 0x0052BD20, Replay_Queue_Record

[section .text]
Replay_Queue_Record:
    pushad
    sub esp, 4
    xor esi, esi
    mov dword [esp], 0
.count:
    cmp esi, [0x0066B068]
    jge .write_count
    call .event_address
    inc esi
    mov eax, [edx+1]
    test eax, 0x80000000
    jnz .count
    and eax, 0x03FFFFFF
    cmp eax, [0x006680C4]
    jne .count
    inc dword [esp]
    jmp .count
.write_count:
    mov edx, esp
    mov eax, recording_file
    mov ebx, 4
    call 0x00462860
    cmp eax, 4
    jne .failed
    xor esi, esi
.write_events:
    cmp esi, [0x0066B068]
    jge .done
    call .event_address
    inc esi
    mov eax, [edx+1]
    test eax, 0x80000000
    jnz .write_events
    and eax, 0x03FFFFFF
    cmp eax, [0x006680C4]
    jne .write_events
    mov eax, recording_file
    mov ebx, 20
    call 0x00462860
    cmp eax, 20
    jne .failed
    jmp .write_events
.failed:
    and byte [recording_mode], 0xFE
    mov eax, recording_file
    call 0x00462AA8
.done:
    add esp, 4
    popad
    ret

.event_address:
    mov edx, [0x0066B06C]
    add edx, esi
    and edx, 4095
    imul edx, 20
    add edx, 0x0066B074
    ret

; Native Session.Load appends Players. Release the spawner's old nodes first,
; retain the vector capacity, then load and seed playback from the header.
Replay_Load_Header:
    push eax
    call Replay_Validate_Header
    test eax, eax
    pop eax
    jz .invalid
    pushad
    mov esi, [0x0068043A]
    mov edi, [0x0068044A]
.release:
    test edi, edi
    jz .load
    dec edi
    mov eax, [esi+edi*4]
    call 0x005C3945                 ; free(void *), Watcom register convention
    jmp .release
.load:
    mov dword [0x0068044A], 0
    popad
    call 0x004F87C4
    push eax
    call 0x004F5EC4                 ; Init_Random AFTER restoring Seed
    pop eax
    ret
.invalid:
    xor eax, eax
    ret

; Native Session.Load trusts its file. Check its fixed prefix and file length
; before it can allocate an unbounded player array or load a zero send rate.
; Always rewind, even on failure. EAX is the CCFileClass; result is 0/1.
Replay_Validate_Header:
    pushad
    sub esp, 60
    mov esi, eax
    mov dword [esp+56], 0
    mov edx, esp
    mov ebx, 53
    call 0x004628B0
    cmp eax, 53
    jne .rewind
    cmp byte [esp], 7
    ja .rewind
    cmp byte [esp+1], 2
    ja .rewind
    cmp dword [esp+2], 1
    jl .rewind
    cmp dword [esp+2], 60
    jg .rewind
    mov edi, [esp+49]
    test edi, edi
    js .rewind
    cmp edi, 8
    ja .rewind
    test edi, edi
    jnz .players_ready
    cmp byte [esp], 0
    jne .rewind
.players_ready:
    imul edi, 29
    add edi, 754
    mov eax, esi
    mov ecx, [eax]
    call [ecx+0x2C]              ; FileClass::Size
    cmp eax, edi
    jl .rewind
    mov dword [esp+56], 1
.rewind:
    mov eax, esi
    mov ecx, [eax]
    xor edx, edx
    xor ebx, ebx                 ; SEEK_SET
    call [ecx+0x28]              ; FileClass::Seek
    test eax, eax
    jz .result
    mov dword [esp+56], 0
.result:
    mov eax, [esp+56]
    mov [esp+88], eax             ; pushad's saved EAX
    add esp, 60
    popad
    ret

; Like YR's SidebarCameoClick_IgnoreDuringPlayback, ignore production/placement
; clicks before they touch factory or pending-object state. Discarding OutList
; alone does not undo those local side effects. Keep ControlClass::Action's
; normal button/key cleanup and the native handled=true return.
hack 0x0054ECE4, 0x0054ECED
    mov [ebp-0x1C], eax
    mov [ebp-0x18], edx
    mov [ebp-0x30], ebx
    test byte [recording_mode], 2
    jnz 0x0054F363
    jmp hackend
