# miniz

The single-file C/header and license are copied from
`11EJDE11/yrpp-spawner`, `src/Vendor/miniz`, commit
`1239f3c084eb246c7c89c9dd4bfbe5cd68f03a68` (the same copy used by YR replays).
That vendor directory identifies its upstream pin as miniz 3.0.2.
Only trailing whitespace was removed from the upstream code.

RA1 uses the low-level raw-deflate compressor/inflater. Archive, stdio, zlib
wrappers and internal allocation are disabled. Private cdecl memory helpers
avoid calls into Westwood's Watcom CRT. The game supplies compressor storage
through Win32 heap allocation. See LICENSE for upstream notices.
